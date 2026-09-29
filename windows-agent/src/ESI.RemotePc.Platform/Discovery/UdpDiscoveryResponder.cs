using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Esi.Protocol;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Discovery;

public sealed class DiscoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = ProtocolConstants.DefaultDiscoveryPort;

    /// <summary>Cap on how many probes a single source address may send per minute.</summary>
    public int MaxProbesPerMinutePerSource { get; set; } = 30;
}

/// <summary>
/// Answers UDP broadcast discovery probes.
/// <para>
/// Broadcast is the primary discovery mechanism because it works on every Android version
/// without extra runtime permissions, unlike <c>NsdManager</c> (which needs
/// <c>NEARBY_WIFI_DEVICES</c> from API 33 and the <c>ACCESS_LOCAL_NETWORK</c> permission
/// from API 37). mDNS is layered on top for tools that speak DNS-SD.
/// </para>
/// <para>
/// The reply is the same <see cref="HandshakeResponse"/> the HTTPS handshake returns, so
/// the phone learns the device id, API port and TLS pin in one datagram. Everything in it
/// is a hint: identity is only established cryptographically at pairing time, because an
/// mDNS/UDP advertisement is trivially spoofable on a shared network.
/// </para>
/// </summary>
public sealed class UdpDiscoveryResponder : IDisposable
{
    private const int MaxDatagramBytes = 9000;

    private readonly ILogger<UdpDiscoveryResponder> _log;
    private readonly DiscoveryOptions _options;
    private readonly Func<HandshakeResponse> _handshakeFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, SlidingWindow> _rateLimits = new(StringComparer.Ordinal);
    private readonly Lock _rateLock = new();

    private UdpClient? _socket;
    private Task? _loop;

    public UdpDiscoveryResponder(
        ILogger<UdpDiscoveryResponder> log,
        DiscoveryOptions options,
        Func<HandshakeResponse> handshakeFactory)
    {
        _log = log;
        _options = options;
        _handshakeFactory = handshakeFactory;
    }

    public bool IsRunning => _socket is not null;

    public void Start()
    {
        if (_socket is not null)
            return;

        try
        {
            var socket = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
            };

            // SO_REUSEADDR so a second agent instance (or a leftover socket after a crash)
            // does not prevent startup.
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, _options.Port));

            _socket = socket;
            _loop = Task.Run(() => ListenAsync(_cts.Token));
            _log.LogInformation("UDP discovery responder listening on port {Port}", _options.Port);
        }
        catch (SocketException ex)
        {
            _log.LogWarning(ex, "Could not bind the discovery UDP port {Port}. " +
                               "The agent still answers mDNS and accepts connections on its known IP address.", _options.Port);
            _socket?.Dispose();
            _socket = null;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "Discovery socket error");
                continue;
            }

            try
            {
                await HandleProbeAsync(received, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Discovery probe handling failed");
            }
        }
    }

    private async Task HandleProbeAsync(UdpReceiveResult received, CancellationToken ct)
    {
        var probe = Encoding.UTF8.GetString(received.Buffer).Trim('\0', ' ', '\r', '\n');

        if (!probe.StartsWith(ProtocolConstants.DiscoveryProbe, StringComparison.Ordinal))
            return;

        // Rate limit per source. An unauthenticated UDP endpoint is a trivial amplification
        // target, so the reply volume is capped independently of the HTTPS rate limiter.
        if (!AllowProbe(((IPEndPoint)received.RemoteEndPoint).Address.ToString()))
            return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(_handshakeFactory(), ProtocolJson.Options);

        if (payload.Length > MaxDatagramBytes)
            return;

        try
        {
            // Reply to the source address and port, unicast. Discovery replies are not
            // multicast: the querier already knows where it sent from.
            await _socket!.SendAsync(payload, new IPEndPoint(((IPEndPoint)received.RemoteEndPoint).Address, ((IPEndPoint)received.RemoteEndPoint).Port), ct).ConfigureAwait(false);
            _log.LogTrace("Answered a discovery probe from {Address}", ((IPEndPoint)received.RemoteEndPoint).Address);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "Could not answer the discovery probe from {Address}", ((IPEndPoint)received.RemoteEndPoint).Address);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private bool AllowProbe(string source)
    {
        lock (_rateLock)
        {
            if (!_rateLimits.TryGetValue(source, out var window))
            {
                window = new SlidingWindow(DateTimeOffset.UtcNow);
                _rateLimits[source] = window;
            }

            if (!window.TryConsume(DateTimeOffset.UtcNow, _options.MaxProbesPerMinutePerSource))
                return false;

            // Bound the dictionary so a spoofed source sweep cannot grow it without limit.
            if (_rateLimits.Count > 4096)
            {
                var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
                foreach (var key in _rateLimits.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
                    _rateLimits.Remove(key);
            }

            return true;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // Nothing to do.
        }

        _socket = null;
        _cts.Dispose();
    }

    private sealed class SlidingWindow(DateTimeOffset created)
    {
        private readonly Queue<DateTimeOffset> _hits = new();
        public DateTimeOffset LastSeen { get; private set; } = created;

        public bool TryConsume(DateTimeOffset now, int limitPerMinute)
        {
            var cutoff = now - TimeSpan.FromMinutes(1);

            while (_hits.Count > 0 && _hits.Peek() < cutoff)
                _hits.Dequeue();

            if (_hits.Count >= limitPerMinute)
            {
                LastSeen = now;
                return false;
            }

            _hits.Enqueue(now);
            LastSeen = now;
            return true;
        }
    }
}
