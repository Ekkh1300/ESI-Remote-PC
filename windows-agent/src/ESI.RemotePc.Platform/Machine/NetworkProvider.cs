using System.Net.NetworkInformation;
using System.Net.Sockets;
using Esi.Protocol;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Machine;

/// <summary>
/// Enumerates network interfaces, addresses, gateways and DNS.
/// <para>
/// Implemented on <see cref="NetworkInterface"/> from
/// <c>System.Net.NetworkInformation</c> rather than hand-marshalling
/// <c>GetAdaptersAddresses</c>/<c>GetIfTable2</c>. The BCL wraps exactly those documented
/// APIs and gets the <c>IP_ADAPTER_ADDRESSES_LH</c> linked-list marshalling right for us;
/// hand-rolling it is a well-known source of silent, process-local corruption, and the
/// linked list is explicitly documented as possibly containing padding before the first
/// entry.
/// </para>
/// </summary>
public sealed class NetworkProvider(ILogger<NetworkProvider> log)
{
    /// <summary>All interfaces, translated to the wire contract.</summary>
    public List<NetworkInterfaceMetrics> GetInterfaces()
    {
        var result = new List<NetworkInterfaceMetrics>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            log.LogWarning(ex, "Could not enumerate network interfaces");
            return result;
        }

        foreach (var nic in interfaces)
        {
            try
            {
                result.Add(Translate(nic));
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Skipping interface {Name}", nic.Name);
            }
        }

        return result;
    }

    private static NetworkInterfaceMetrics Translate(NetworkInterface nic)
    {
        var metrics = new NetworkInterfaceMetrics
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            Type = MapType(nic.NetworkInterfaceType),
            IsUp = nic.OperationalStatus == OperationalStatus.Up,
            MacAddress = FormatMac(nic.GetPhysicalAddress()),
            LinkSpeedBitsPerSecond = (long)nic.Speed,
        };

        try
        {
            metrics.Mtu = nic.GetIPProperties().GetIPv4Properties()?.Mtu ?? 0;
        }
        catch (NetworkInformationException)
        {
            // Some virtual adapters throw; leave MTU at 0.
        }

        var properties = nic.GetIPProperties();

        foreach (var unicast in properties.UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                continue;

            metrics.Ipv4 ??= unicast.Address.ToString();

            if (unicast.IPv4Mask is not null)
                metrics.Netmask ??= unicast.IPv4Mask.ToString();
        }

        // Gateways are only exposed through the per-interface property bag on the
        // BCL surface; UnicastIPAddressInformation does not carry one.
        foreach (var gateway in properties.GatewayAddresses)
        {
            if (gateway.Address is { } address && address.AddressFamily == AddressFamily.InterNetwork)
            {
                metrics.Gateway ??= address.ToString();
                break;
            }
        }

        foreach (var dns in properties.DnsAddresses)
        {
            if (dns.AddressFamily == AddressFamily.InterNetwork && metrics.Dns.Count < 4)
                metrics.Dns.Add(dns.ToString());
        }

        try
        {
            var stats = nic.GetIPv4Statistics();
            metrics.RxTotalBytes = (long)stats.BytesReceived;
            metrics.TxTotalBytes = (long)stats.BytesSent;
        }
        catch (NetworkInformationException)
        {
            // Non-fatal.
        }

        return metrics;
    }

    /// <summary>
    /// Picks the interface the agent should treat as primary: operational, non-loopback,
    /// with an IPv4 address, preferring the one carrying default traffic. Link speed is
    /// used as a tiebreaker because it is a decent proxy for "the real network link".
    /// </summary>
    public NetworkInterfaceMetrics? GetPrimaryInterface()
    {
        var candidates = GetInterfaces()
            .Where(i => i.IsUp &&
                        !string.Equals(i.Type, "loopback", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(i.Ipv4))
            .OrderByDescending(i => i.LinkSpeedBitsPerSecond)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.FirstOrDefault();
    }

    /// <summary>The subnet-directed broadcast address of the primary interface.</summary>
    public string[] GetRecommendedBroadcastAddresses()
    {
        var addresses = new List<string> { "255.255.255.255" };

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var directed = Power.WakeOnLanSender.SubnetBroadcast(unicast.Address, unicast.IPv4Mask) ??
                               Power.WakeOnLanSender.SubnetBroadcast(unicast.Address, unicast.PrefixLength);

                if (directed is not null)
                    addresses.Add(directed.ToString());
            }
        }

        return addresses.Distinct().ToArray();
    }

    /// <summary>Every non-loopback adapter MAC address, for Wake-on-LAN.</summary>
    public string[] GetMacAddresses()
    {
        var macs = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var mac = FormatMac(nic.GetPhysicalAddress());
            if (!string.IsNullOrEmpty(mac))
                macs.Add(mac);
        }

        return macs.Distinct().ToArray();
    }

    /// <summary>Adapters that are wired, i.e. the ones Wake-on-LAN can realistically work on.</summary>
    public List<(string Name, string Mac, string Type)> GetWakeCapableAdapters()
    {
        var result = new List<(string, string, string)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var mac = FormatMac(nic.GetPhysicalAddress());
            if (string.IsNullOrEmpty(mac))
                continue;

            result.Add((nic.Name, mac, MapType(nic.NetworkInterfaceType)));
        }

        return result;
    }

    private static string MapType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or
        NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Isdn or
        NetworkInterfaceType.BasicIsdn => "ethernet",
        NetworkInterfaceType.Wireless80211 => "wifi",
        NetworkInterfaceType.Loopback => "loopback",
        NetworkInterfaceType.Tunnel => "virtual",
        NetworkInterfaceType.Ppp => "ppp",
        _ => "other",
    };

    public static string FormatMac(PhysicalAddress? address)
    {
        if (address is null)
            return string.Empty;

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0)
            return string.Empty;

        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
