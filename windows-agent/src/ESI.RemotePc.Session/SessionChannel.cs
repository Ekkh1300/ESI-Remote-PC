using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Esi.Platform.Native;
using Esi.Platform.Session;

namespace Esi.Session;

/// <summary>
/// The client half of the service ↔ session-agent channel.
/// <para>
/// The protocol is newline-delimited JSON in both directions, which is the right trade here:
/// the control traffic is low-rate and human-readable (so it can be diagnosed by logging one
/// line), and the only high-rate item — screen frames — is base64 inside a JSON message,
/// which costs about a third more on the wire than a binary frame would. That is an
/// acceptable price for a channel that never desynchronises and needs no framing state on
/// either side.
/// </para>
/// <para>
/// Writes are serialised through a semaphore: <see cref="PipeStream"/> permits only one
/// outstanding write, and the capture loop, the heartbeat and command results all write
/// concurrently.
/// </para>
/// </summary>
internal sealed class SessionChannel : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Serilog.ILogger _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// How long a single write may take before the channel is considered broken. A capture
    /// frame is the largest thing that goes down this pipe, and the service reads
    /// continuously, so five seconds is generous; hanging forever is not an option because
    /// the semaphore below would then be held by a write nobody is ever going to finish.
    /// </summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private volatile bool _connected;

    /// <summary>
    /// Messages dropped because the channel was not usable, for the D46 diagnostic above. Reset
    /// to zero by the first successful write, so it counts the current outage rather than the
    /// process lifetime.
    /// </summary>
    private int _droppedSends;

    public SessionChannel(string pipeName, Serilog.ILogger log)
    {
        _pipeName = pipeName;
        _log = log;
    }

    public bool IsConnected => _connected;

    /// <summary>Raised for each command received from the service, on a background thread.</summary>
    public event Func<SessionCommandEnvelope, Task>? CommandReceived;

    /// <summary>
    /// Connects and pumps until the service goes away, then returns so the caller can retry.
    /// Never throws for an expected disconnection: the service restarts independently of
    /// logon, and a long-lived agent must survive that.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // Expected while the service is stopped, or before the pipe's DACL has been
                // installed. The service polls, so quietly retrying is the correct behaviour.
                _log.Debug("The command channel is not available yet ({Error}); retrying", ex.Message);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "The command channel failed; retrying");
            }
            finally
            {
                await TearDownAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(
            ".",                      // the local computer: the service is in session 0 on this machine
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        _pipe = pipe;

        // 10s: long enough that a busy PC still connects, short enough that a stopped service
        // is noticed promptly rather than after the agent looks healthy but is not.
        await pipe.ConnectAsync(10_000, ct).ConfigureAwait(false);

        _reader = new StreamReader(pipe, Encoding.UTF8, false, 32 * 1024, leaveOpen: true);
        _connected = true;
        _log.Information("Connected to the agent service on pipe {Pipe}", _pipeName);

        await SendAsync(new SessionAgentMessage
        {
            T = SessionChannelProtocol.Events.Ready,
            SessionId = Esi.Platform.Native.KERNEL32.WTSGetActiveConsoleSessionId(),
            ProcessId = Environment.ProcessId,
            ProcessName = "ESI.RemotePc.Session",
        }).ConfigureAwait(false);

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0)
                continue;

            SessionCommandEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SessionCommandEnvelope>(line, SessionJson.Options);
            }
            catch (JsonException ex)
            {
                _log.Warning(ex, "Discarding a malformed command from the service");
                continue;
            }

            if (envelope is null)
                continue;

            var handler = CommandReceived;
            if (handler is null)
                continue;

            // Commands are handled one at a time and in order. A pairing prompt must not be
            // reordered behind an input event, and a capture start must not overtake the stop
            // that preceded it.
            try
            {
                await handler(envelope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Handling {Command} failed", envelope.T);
            }
        }
    }

    /// <summary>Sends one message. Safe to call from any thread.</summary>
    public async Task SendAsync(SessionAgentMessage message)
    {
        var pipe = _pipe;
        if (pipe is null || !_connected || !pipe.IsConnected)
        {
            // D46: this was a bare `return`. A frame dropped here is indistinguishable from one
            // that was never built, so the agent saw a stream that "started" and produced exactly
            // one frame, the session agent logged "Capturing display 0", and the pump never
            // reported a single thing - because nothing on this path ever said anything.
            //
            // The other half of the problem is that this method is used for every message type,
            // including the heartbeats that keep the service from declaring the agent dead, so the
            // sample is coarse on purpose: a burst of dropped frames should be one line, not one
            // per frame.
            var dropped = Interlocked.Increment(ref _droppedSends);
            if (dropped == 1 || dropped % 120 == 0)
            {
                _log.Warning(
                    "Dropped a {Type} message: the command channel is not usable " +
                    "(pipe={Pipe}, connected={Connected}, pipeConnected={PipeConnected}); {Total} dropped so far",
                    message.T,
                    pipe is null ? "none" : "present",
                    _connected,
                    pipe?.IsConnected,
                    dropped);
            }

            return;
        }

        Interlocked.Exchange(ref _droppedSends, 0);

        var json = JsonSerializer.Serialize(message, SessionJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The bound has to come from the async path. `PipeStream.WriteTimeout` is not
            // supported on a handle opened with `PipeOptions.Asynchronous`: assigning it throws
            // InvalidOperationException("Timeouts are not supported on this stream"), and this
            // catch used to swallow that at Debug level - below the Information floor this
            // process logs at - so the failure was invisible and every send quietly returned
            // without writing a byte.
            //
            // The consequence was not a small one. `_connected` went false on the first send
            // after connecting, so no heartbeat, no ready and no pairing result ever reached
            // the service; the service saw silence, declared the agent dead 45 s later and
            // tore the pipe down, and the agent reconnected 3 s after that. The service log
            // showed 1675 "Session agent connected" and zero "Session agent ready" before this
            // was found - a half-duplex channel that had looked connected from both ends
            // since the first release. A write that cannot be bounded is better than no write
            // at all, so the timeout is a cancellation rather than a property.
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
            or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            // Warning, not Debug: this is the whole channel going dark, and at Debug nobody
            // would ever see it. That is what let it stay broken for three days.
            _connected = false;
            _log.Warning("Could not write to the command channel: {Error}", ex.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task SendAsync(string type) => SendAsync(new SessionAgentMessage { T = type });

    private async Task TearDownAsync()
    {
        _connected = false;

        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // Nothing useful to do.
        }

        _reader = null;

        try
        {
            if (_pipe is not null)
                await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Nothing useful to do.
        }

        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
