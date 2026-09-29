using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Esi.Platform.Machine;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Power;

/// <summary>
/// Sends Wake-on-LAN "magic packets".
/// <para>
/// Wire format (102 bytes): six <c>0xFF</c> bytes followed by the target 6-byte MAC
/// address repeated sixteen times. Sent as a UDP datagram — conventionally to port 9
/// (discard) or port 7 (echo). Because NIC firmware scans for the byte pattern rather
/// than parsing a protocol stack, the payload works over any port, and sending to both
/// 9 and 7 is standard practice.
/// </para>
/// <para>
/// Reliability notes that matter in practice:
/// </para>
/// <list type="bullet">
///   <item><description>Sending 2-3 copies is standard: a NIC in WoL state runs minimal
///   firmware and can miss a single datagram, and switch MAC tables expire.</description></item>
///   <item><description>Both the limited broadcast (<c>255.255.255.255</c>) and the
///   subnet-directed broadcast (e.g. <c>192.168.1.255</c>) should be tried; many access
///   points and mobile stacks rate-limit or drop the limited broadcast.</description></item>
///   <item><description>This is a layer-2 broadcast and will not cross a router. Client
///   isolation / AP isolation / guest networks break it silently.</description></item>
///   <item><description>There is no delivery feedback. Reporting "sent" is honest;
///   reporting "woken up" is not.</description></item>
/// </list>
/// </summary>
public sealed class WakeOnLanSender(ILogger<WakeOnLanSender> log)
{
    public const int DefaultPort = 9;
    public const int EchoPort = 7;
    public const int MagicPacketLength = 102;
    public const int DefaultRepeat = 3;

    /// <summary>Parses "AA:BB:CC:DD:EE:FF", "aa-bb-cc-dd-ee-ff" or "aabb.ccdd.eeff" into 6 bytes.</summary>
    public static bool TryParseMac(string? input, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var cleaned = new string(input.Where(c => Uri.IsHexDigit(c)).ToArray());
        if (cleaned.Length != 12)
            return false;

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }

        mac = bytes;
        return true;
    }

    public static string FormatMac(byte[] mac)
        => mac.Length == 6 ? string.Join(':', mac.Select(b => b.ToString("X2"))) : string.Empty;

    /// <summary>Builds the canonical 102-byte magic packet.</summary>
    public static byte[] BuildMagicPacket(byte[] mac)
    {
        if (mac.Length != 6)
            throw new ArgumentException("A MAC address must be exactly 6 bytes.", nameof(mac));

        var packet = new byte[MagicPacketLength];
        for (var i = 0; i < 6; i++)
            packet[i] = 0xFF;

        for (var repetition = 0; repetition < 16; repetition++)
            Buffer.BlockCopy(mac, 0, packet, 6 + (repetition * 6), 6);

        return packet;
    }

    /// <summary>
    /// Sends the magic packet to every supplied broadcast address on every supplied port.
    /// </summary>
    public async Task<(int Sent, List<string> Targets, List<int> Ports)> SendAsync(
        byte[] mac,
        IReadOnlyList<IPAddress> broadcastAddresses,
        IReadOnlyList<int>? ports = null,
        int repeat = DefaultRepeat,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mac);
        if (mac.Length != 6)
            throw new ArgumentException("A MAC address must be exactly 6 bytes.", nameof(mac));

        ports = ports is { Count: > 0 } ? ports : [DefaultPort, EchoPort];
        repeat = Math.Clamp(repeat, 1, 10);

        var packet = BuildMagicPacket(mac);
        var targets = new List<string>();
        var sent = 0;

        // A single socket handles every destination: SO_BROADCAST is set once and
        // each SendTo targets a specific broadcast address.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch (SocketException)
        {
            // ReuseAddress is a nicety, not a requirement: another instance may hold 48211.
        }

        foreach (var address in broadcastAddresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                continue;

            var endPoint = new IPEndPoint(address, 0);

            foreach (var port in ports)
            {
                endPoint.Port = port;

                for (var attempt = 0; attempt < repeat; attempt++)
                {
                    if (ct.IsCancellationRequested)
                        return (sent, targets, [.. ports]);

                    try
                    {
                        await socket.SendToAsync(packet, SocketFlags.None, endPoint, ct).ConfigureAwait(false);
                        sent++;
                    }
                    catch (SocketException ex)
                    {
                        log.LogDebug(ex, "Magic packet to {Address}:{Port} failed", address, port);
                        break; // further attempts to the same endpoint will also fail
                    }

                    if (attempt < repeat - 1)
                        await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }

            targets.Add($"{address}:{string.Join(',', ports)}");
        }

        log.LogInformation("Sent {Count} Wake-on-LAN magic packets for {Mac} to {Targets}",
            sent, FormatMac(mac), string.Join(", ", targets));

        return (sent, targets, [.. ports]);
    }

    /// <summary>
    /// Sends to the limited broadcast plus the subnet-directed broadcast of every
    /// interface that is up, non-loopback and has an IPv4 address. This is the
    /// "just work" entry point; callers that know the target subnet should pass it in.
    /// </summary>
    public async Task<(int Sent, List<string> Targets, List<int> Ports)> SendToLocalNetworkAsync(
        byte[] mac,
        IReadOnlyList<IPAddress>? explicitBroadcasts = null,
        CancellationToken ct = default)
    {
        var addresses = new List<IPAddress>();

        if (explicitBroadcasts is { Count: > 0 })
        {
            addresses.AddRange(explicitBroadcasts);
        }
        else
        {
            // The limited broadcast. Not always forwarded, but harmless to try.
            addresses.Add(IPAddress.Broadcast);

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var directed = SubnetBroadcast(unicast.Address, unicast.IPv4Mask) ??
                                   SubnetBroadcast(unicast.Address, unicast.PrefixLength);
                    if (directed is not null && !addresses.Contains(directed))
                        addresses.Add(directed);
                }
            }
        }

        return await SendAsync(mac, addresses, null, DefaultRepeat, ct).ConfigureAwait(false);
    }

    /// <summary>Computes the subnet-directed broadcast address from an address and mask.</summary>
    public static IPAddress? SubnetBroadcast(IPAddress address, IPAddress? netmask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || netmask is null)
            return null;
        if (netmask.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var addrBytes = address.GetAddressBytes();
        var maskBytes = netmask.GetAddressBytes();
        if (addrBytes.Length != 4 || maskBytes.Length != 4)
            return null;

        var result = new byte[4];
        for (var i = 0; i < 4; i++)
            result[i] = (byte)(addrBytes[i] | ~maskBytes[i]);

        return result[0] == 0 || result[0] >= 224 ? null : new IPAddress(result);
    }

    public static IPAddress? SubnetBroadcast(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
            return null;

        var maskBytes = new byte[4];
        var fullBytes = prefixLength / 8;
        var remainderBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
            maskBytes[i] = 0xFF;

        if (remainderBits > 0)
            maskBytes[fullBytes] = (byte)(0xFF << (8 - remainderBits));

        return SubnetBroadcast(address, new IPAddress(maskBytes));
    }
}
