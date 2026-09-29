using System.Net;
using System.Net.Sockets;
using System.Text;
using Esi.Protocol;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Discovery;

// ---------------------------------------------------------------------------------
// Minimal DNS wire format (RFC 1035), which mDNS reuses unchanged (RFC 6762 §18).
// Only the record types the agent needs are decoded.
// ---------------------------------------------------------------------------------

internal enum DnsType : ushort
{
    A = 1,
    PTR = 12,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    ANY = 255,
    NSEC = 47,
}

internal enum DnsClass : ushort
{
    IN = 1,
    ANY = 255,
}

internal static class Dns
{
    public const int HeaderSize = 12;

    public static void WriteHeader(Span<byte> buffer, ushort id, ushort flags, ushort questions, ushort answers,
        ushort authorities, ushort additionals)
    {
        BinaryPrimitivesCompat.WriteUInt16(buffer, id, 0);
        BinaryPrimitivesCompat.WriteUInt16(buffer, flags, 2);
        BinaryPrimitivesCompat.WriteUInt16(buffer, questions, 4);
        BinaryPrimitivesCompat.WriteUInt16(buffer, answers, 6);
        BinaryPrimitivesCompat.WriteUInt16(buffer, authorities, 8);
        BinaryPrimitivesCompat.WriteUInt16(buffer, additionals, 10);
    }

    /// <summary>Writes a DNS name in label form. Names are not compressed on send.</summary>
    public static int WriteName(Span<byte> buffer, int offset, string name)
    {
        var start = offset;

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0)
                continue;
            if (label.Length > 63)
                throw new ArgumentException("A DNS label cannot exceed 63 octets.", nameof(name));

            buffer[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, buffer[offset..]);
        }

        buffer[offset++] = 0;
        return offset - start;
    }

    /// <summary>Reads a (possibly compressed) name, returning the offset just past it.</summary>
    public static int ReadName(ReadOnlySpan<byte> buffer, int offset, out string name, out int consumed)
    {
        var labels = new List<string>();
        var cursor = offset;
        var next = -1;

        while (cursor < buffer.Length)
        {
            var length = buffer[cursor];

            if (length == 0)
            {
                cursor++;
                break;
            }

            // Top two bits set = a compression pointer. Follow it exactly once: RFC 6762
            // §18.14 forbids pointer loops and mDNS caps name length anyway.
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= buffer.Length)
                    break;

                var pointer = ((length & 0x3F) << 8) | buffer[cursor + 1];
                if (next < 0)
                    next = cursor + 2;

                if (pointer >= buffer.Length)
                    break;

                cursor = pointer;
                continue;
            }

            cursor++;
            if (cursor + length > buffer.Length)
                break;

            labels.Add(Encoding.ASCII.GetString(buffer.Slice(cursor, length)));
            cursor += length;
        }

        name = string.Join('.', labels);
        consumed = next < 0 ? cursor - offset : next - offset;
        return cursor;
    }

    public static int WriteRecordHeader(Span<byte> buffer, int offset, string name, DnsType type,
        bool cacheFlush, uint ttl, int rdLength)
    {
        offset += WriteName(buffer, offset, name);
        BinaryPrimitivesCompat.WriteUInt16(buffer, (ushort)type, offset);
        offset += 2;

        // The top bit of CLASS is the mDNS "cache flush" bit (RFC 6762 §10.2).
        var dnsClass = (ushort)DnsClass.IN;
        if (cacheFlush)
            dnsClass |= 0x8000;

        BinaryPrimitivesCompat.WriteUInt16(buffer, dnsClass, offset);
        offset += 2;
        BinaryPrimitivesCompat.WriteUInt32(buffer, ttl, offset);
        offset += 4;
        BinaryPrimitivesCompat.WriteUInt16(buffer, (ushort)rdLength, offset);
        return offset + 2;
    }
}

internal static class BinaryPrimitivesCompat
{
    public static void WriteUInt16(Span<byte> buffer, ushort value, int offset)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    public static void WriteUInt32(Span<byte> buffer, uint value, int offset)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
        => ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
           ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
}

// ---------------------------------------------------------------------------------

/// <summary>One resource record the responder owns.</summary>
internal sealed record MdnsRecord(
    string Name,
    DnsType Type,
    string? Target,          // PTR target, or SRV host
    ushort Port,             // SRV
    IReadOnlyList<string> Txt, // TXT
    IPAddress? Address,      // A / AAAA
    uint Ttl,
    bool CacheFlush,
    bool Unique)
{
    /// <summary>Serialises the rdata (everything after the record header).</summary>
    public int WriteRData(Span<byte> buffer, int offset)
    {
        var start = offset;

        switch (Type)
        {
            case DnsType.PTR:
                offset += Dns.WriteName(buffer, offset, Target ?? string.Empty);
                break;

            case DnsType.TXT:
                foreach (var entry in Txt)
                {
                    var bytes = Encoding.UTF8.GetBytes(entry);
                    if (bytes.Length > 255)
                        continue; // A TXT entry cannot exceed 255 octets.

                    buffer[offset++] = (byte)bytes.Length;
                    offset += bytes.Length;
                }
                break;

            case DnsType.SRV:
                BinaryPrimitivesCompat.WriteUInt16(buffer, 0, offset); // priority
                offset += 2;
                BinaryPrimitivesCompat.WriteUInt16(buffer, 0, offset); // weight
                offset += 2;
                BinaryPrimitivesCompat.WriteUInt16(buffer, Port, offset);
                offset += 2;
                offset += Dns.WriteName(buffer, offset, Target ?? string.Empty);
                break;

            case DnsType.A:
            {
                var bytes = Address?.GetAddressBytes() ?? [];
                bytes.CopyTo(buffer[offset..]);
                offset += 4;
                break;
            }

            case DnsType.AAAA:
            {
                var bytes = Address?.GetAddressBytes() ?? [];
                bytes.CopyTo(buffer[offset..]);
                offset += 16;
                break;
            }

            case DnsType.NSEC:
                // Explicit "no such type" answer: bitmap is empty, next domain name is
                // the owner itself.
                buffer[offset++] = 0; // no window
                buffer[offset++] = 0; // no bitmap length
                offset += Dns.WriteName(buffer, offset, Name);
                break;
        }

        return offset - start;
    }

    public int RDataLength
    {
        get
        {
            var buffer = new byte[512];
            return WriteRData(buffer, 0);
        }
    }
}

/// <summary>
/// A minimal but conformant mDNS / DNS-SD responder advertising the agent (RFC 6762,
/// RFC 6763).
/// <para>
/// Behaviours that matter and are implemented here:
/// </para>
/// <list type="bullet">
///   <item><description>The response ID is always 0 and the QR bit is set (RFC 6762 §18).</description></item>
///   <item><description>Answers are delayed by a random 20-120 ms so several queries can be
///   aggregated into one packet (RFC 6762 §6).</description></item>
///   <item><description>Unique record sets (SRV/TXT/A/AAAA) carry the cache-flush bit;
///   the shared PTR record never does (RFC 6762 §10.2).</description></item>
///   <item><description>A record is never multicasted more than once per second
///   (RFC 6762 §6).</description></item>
///   <item><description>A goodbye packet with TTL 0 is sent for every record on a clean
///   shutdown (RFC 6762 §10.1).</description></item>
///   <item><description>A query whose source is not on a local subnet is ignored
///   (RFC 6762 §11).</description></item>
///   <item><description>Unknown questions get an explicit NSEC negative answer for the
///   types the agent owns (RFC 6762 §6.1).</description></item>
/// </list>
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    public const int MdnsPort = 5353;
    public const string MulticastAddress = "224.0.0.251";

    private const ushort FlagResponse = 0x8000;
    private const ushort FlagAuthoritative = 0x0400;
    private const int MaxPacketSize = 9000; // comfortably inside the 1500 MTU path via IP fragmentation
    private const int TtlA = 120;          // RFC 6762 §10 recommended for host-name records
    private const int TtlShared = 4500;    // RFC 6762 §10 recommended for other records (75 min)

    private readonly ILogger<MdnsResponder> _log;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _socket;
    private Task? _loop;
    private readonly List<MdnsRecord> _records = [];
    private readonly Dictionary<string, DateTime> _lastMulticast = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _jitter = Random.Shared;

    private volatile string _serviceInstance = "ESI Remote PC";
    private volatile string _hostName = "esi-remotepc";
    private volatile int _port = ProtocolConstants.DefaultHttpsPort;
    private volatile string? _deviceId;
    private volatile string? _spkiPin;
    private volatile IPAddress[] _addresses = [];

    public MdnsResponder(ILogger<MdnsResponder> log) => _log = log;

    public bool IsRunning => _socket is not null;

    /// <summary>Replaces the advertised identity. Safe to call at any time.</summary>
    public void Update(string serviceInstance, string hostName, int port, string deviceId, string? spkiPin, IPAddress[] addresses)
    {
        _serviceInstance = SanitiseInstanceName(serviceInstance);
        _hostName = SanitiseHostName(hostName);
        _port = port;
        _deviceId = deviceId;
        _spkiPin = spkiPin;
        _addresses = addresses;
    }

    public void Start()
    {
        if (_socket is not null)
            return;

        try
        {
            var socket = new UdpClient(AddressFamily.InterNetwork);

            // SO_REUSEADDR + exclusive address use so the responder can coexist with any
            // other mDNS implementation on the machine.
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

            _socket = socket;
            _loop = Task.Run(() => ListenAsync(_cts.Token));
            _log.LogInformation("mDNS responder listening on {Address}:{Port} for {ServiceType}",
                MulticastAddress, MdnsPort, ProtocolConstants.MdnsServiceType);
        }
        catch (SocketException ex)
        {
            // A port conflict with another mDNS responder is common and non-fatal:
            // UDP broadcast discovery still works.
            _log.LogWarning(ex,
                "Could not start the mDNS responder on port {Port}. UDP broadcast discovery is unaffected.",
                MdnsPort);
            _socket?.Dispose();
            _socket = null;
        }
    }

    /// <summary>Builds the current record set. Called fresh so a TXT change is picked up.</summary>
    private List<MdnsRecord> BuildRecords()
    {
        var records = new List<MdnsRecord>();
        var instanceName = $"{_serviceInstance}.{ProtocolConstants.MdnsServiceType}";
        var hostTarget = $"{_hostName}.{ProtocolConstants.MdnsDomain}";

        // PTR: shared record set, so no cache-flush bit.
        records.Add(new MdnsRecord(
            ProtocolConstants.MdnsServiceType, DnsType.PTR, instanceName, 0, [], null, TtlShared, CacheFlush: false, Unique: false));

        // SRV: unique record set, cache-flush set.
        records.Add(new MdnsRecord(
            instanceName, DnsType.SRV, hostTarget, (ushort)_port, [], null, TtlA, CacheFlush: true, Unique: true));

        var txt = new List<string> { $"v={ProtocolConstants.Version}" };
        if (!string.IsNullOrEmpty(_deviceId))
        {
            txt.Add($"id={_deviceId}");
            txt.Add($"name={_serviceInstance}");
        }
        if (!string.IsNullOrEmpty(_spkiPin))
        {
            // The pin travels in the advertisement so a client can pin before its first
            // request. It is public information, exactly like a certificate fingerprint.
            txt.Add($"pin={_spkiPin}");
        }

        records.Add(new MdnsRecord(
            instanceName, DnsType.TXT, null, 0, txt, null, TtlShared, CacheFlush: true, Unique: true));

        foreach (var address in _addresses)
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                records.Add(new MdnsRecord(
                    hostTarget, DnsType.A, null, 0, [], address, TtlA, CacheFlush: true, Unique: true));
            }
        }

        return records;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var multicast = IPAddress.Parse(MulticastAddress);

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
                _log.LogDebug(ex, "mDNS receive failed");
                continue;
            }

            try
            {
                await HandleQueryAsync(received, multicast, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "mDNS query handling failed");
            }
        }
    }

    private async Task HandleQueryAsync(UdpReceiveResult received, IPAddress multicast, CancellationToken ct)
    {
        var packet = received.Buffer;
        if (packet.Length < Dns.HeaderSize)
            return;

        var flags = BinaryPrimitivesCompat.ReadUInt16(packet, 2);

        // RFC 6762 §18: QR MUST be 1 in responses and MUST be ignored on receipt; we only
        // act on queries, which is what §6 describes.
        if ((flags & FlagResponse) != 0)
            return;

        var questions = BinaryPrimitivesCompat.ReadUInt16(packet, 4);
        if (questions == 0)
            return;

        // RFC 6762 §11: a responder must silently ignore a direct-unicast query whose
        // source is not on a local subnet / on-link prefix.
        if (!IsOnLocalLink(((IPEndPoint)received.RemoteEndPoint).Address))
            return;

        var records = BuildRecords();
        var answered = new List<MdnsRecord>();
        var offset = Dns.HeaderSize;
        var now = DateTime.UtcNow;

        for (var i = 0; i < questions; i++)
        {
            if (offset >= packet.Length)
                break;

            var cursor = Dns.ReadName(packet, offset, out var qname, out var consumed);
            if (consumed <= 0)
                break;

            offset = cursor;
            if (offset + 4 > packet.Length)
                break;

            var qtype = (DnsType)BinaryPrimitivesCompat.ReadUInt16(packet, offset);
            var qclassRaw = BinaryPrimitivesCompat.ReadUInt16(packet, offset + 2);
            offset += 4;

            // The top bit of QCLASS is the unicast-response bit (RFC 6762 §5.4). We do not
            // implement unicast responses, so a request with that bit set is skipped.
            var wantsUnicast = (qclassRaw & 0x8000) != 0;
            var qclass = qclassRaw & 0x7FFF;

            if (qclass != (ushort)DnsClass.IN && qclass != (ushort)DnsClass.ANY)
                continue;

            if (wantsUnicast)
                continue;

            var isServiceQuery = qname.Equals(ProtocolConstants.MdnsServiceType, StringComparison.OrdinalIgnoreCase) ||
                                 qname.Equals(ProtocolConstants.MdnsServiceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

            if (!isServiceQuery)
            {
                // Explicit negative answer for the types the agent owns, so a querier can
                // distinguish "does not exist" from "not answered yet" (RFC 6762 §6.1).
                if (qname.EndsWith(ProtocolConstants.MdnsDomain, StringComparison.OrdinalIgnoreCase) ||
                    qname.EndsWith(ProtocolConstants.MdnsServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    answered.Add(new MdnsRecord(
                        qname, DnsType.NSEC, null, 0, [], null, TtlShared, CacheFlush: true, Unique: true));
                }

                continue;
            }

            foreach (var record in records)
            {
                if (qtype != DnsType.ANY && record.Type != qtype)
                    continue;

                // RFC 6762 §6: a record must not be multicasted more than once per second.
                var key = record.Name + "|" + record.Type;
                if (_lastMulticast.TryGetValue(key, out var last) && (now - last) < TimeSpan.FromSeconds(1))
                    continue;

                answered.Add(record);
                _lastMulticast[key] = now;
            }
        }

        if (answered.Count == 0)
            return;

        var response = BuildResponse(answered, ((IPEndPoint)received.RemoteEndPoint).Address);
        if (response is null)
            return;

        // RFC 6762 §6: a 20-120 ms random delay lets several queries be answered together.
        var delayMs = _jitter.Next(20, 121);
        await Task.Delay(delayMs, ct).ConfigureAwait(false);

        try
        {
            await _socket!.SendAsync(response, new IPEndPoint(multicast, MdnsPort), ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            _log.LogDebug(ex, "mDNS multicast send failed");
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private static byte[]? BuildResponse(List<MdnsRecord> answers, IPAddress querier)
    {
        var buffer = new byte[MaxPacketSize];

        // ID 0 and QR set: both are mandatory for mDNS (RFC 6762 §18).
        Dns.WriteHeader(buffer, 0, FlagResponse | FlagAuthoritative, questions: 0,
            answers: (ushort)answers.Count, authorities: 0, additionals: 0);

        var offset = Dns.HeaderSize;

        foreach (var record in answers)
        {
            if (offset + record.Name.Length + 16 > buffer.Length)
                break;

            var rdataLength = record.RDataLength;
            offset = Dns.WriteRecordHeader(buffer, offset, record.Name, record.Type,
                record.CacheFlush, record.Ttl, rdataLength);

            if (offset + rdataLength > buffer.Length)
                return null;

            offset += record.WriteRData(buffer.AsSpan(0), offset);
        }

        return buffer[..offset];
    }

    private bool IsOnLocalLink(IPAddress remote)
    {
        // Multicast queries arrive from a link-local source; unicast queries must come
        // from one of the local interface prefixes.
        if (remote.IsIPv6LinkLocal || remote.Equals(IPAddress.Loopback) || remote.Equals(IPAddress.Broadcast))
            return true;

        foreach (var local in _addresses)
        {
            if (local.AddressFamily != remote.AddressFamily)
                continue;

            if (local.Equals(remote) || IsSameSubnet(local, remote))
                return true;
        }

        return false;
    }

    private static bool IsSameSubnet(IPAddress a, IPAddress b)
    {
        var aBytes = a.GetAddressBytes();
        var bBytes = b.GetAddressBytes();

        if (aBytes.Length != 4 || bBytes.Length != 4)
            return false;

        // /24 comparison is a pragmatic on-link test; the address set is only ever
        // populated from this machine's own interfaces.
        return aBytes[0] == bBytes[0] && aBytes[1] == bBytes[1] && aBytes[2] == bBytes[2];
    }

    /// <summary>
    /// Sends a goodbye packet (TTL 0) for every advertised record, so queriers drop the
    /// service immediately instead of waiting out the TTL (RFC 6762 §10.1).
    /// </summary>
    public void SendGoodbye()
    {
        var socket = _socket;
        if (socket is null)
            return;

        var records = BuildRecords();
        var goodbye = new List<MdnsRecord>(records.Count);

        foreach (var record in records)
            goodbye.Add(record with { Ttl = 0 });

        var response = BuildResponse(goodbye, IPAddress.Any);
        if (response is null)
            return;

        try
        {
            socket.Send(response, new IPEndPoint(IPAddress.Parse(MulticastAddress), MdnsPort));
            _log.LogInformation("Sent mDNS goodbye packets for {Count} records", goodbye.Count);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not send mDNS goodbye packets");
        }
    }

    /// <summary>
    /// RFC 6763 §4.1: the instance label may contain anything except ASCII control
    /// characters, up to 63 octets. Dots are allowed but the collision-avoidance suffix
    /// rule (§9.4) then applies.
    /// </summary>
    public static string SanitiseInstanceName(string? name)
    {
        var cleaned = new string((name ?? "ESI Remote PC")
            .Where(c => c > 0x20 && c != 0x7F)
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "ESI Remote PC";

        return cleaned.Length <= 63 ? cleaned : cleaned[..63];
    }

    /// <summary>Host labels must be valid DNS labels, so dots and spaces are replaced.</summary>
    public static string SanitiseHostName(string? name)
    {
        var cleaned = new string((name ?? "esi-remotepc")
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray())
            .Trim('-');

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "esi-remotepc";

        return cleaned.Length <= 63 ? cleaned : cleaned[..63];
    }

    public void Dispose()
    {
        SendGoodbye();
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
}
