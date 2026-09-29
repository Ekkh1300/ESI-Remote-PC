using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Esi.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Esi.Core.Security;

/// <summary>
/// Token-bucket rate limiting, applied per device and per source address.
/// <para>
/// A token bucket rather than a fixed window: a remote-control API benefits from allowing
/// a short burst (a dashboard refresh is several requests at once) while still bounding the
/// sustained rate. The refill is continuous, so there is no boundary artefact where a
/// client gets 2x the budget.
/// </para>
/// </summary>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefillUnixMs;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _denials = new(StringComparer.Ordinal);

    /// <summary>Per-minute budget for ordinary authenticated calls.</summary>
    public int RequestsPerMinute { get; set; } = 240;

    /// <summary>Burst size. Set to roughly one dashboard refresh so bursts are not penalised.</summary>
    public int Burst { get; set; } = 60;

    /// <summary>Per-minute budget for unauthenticated endpoints, which is much tighter.</summary>
    public int UnauthenticatedPerMinute { get; set; } = 30;

    public int UnauthenticatedBurst { get; set; } = 10;

    /// <summary>Per-minute budget for power actions, which are the most dangerous to replay.</summary>
    public int PowerActionsPerMinute { get; set; } = 10;

    public int PowerActionsBurst { get; set; } = 3;

    /// <summary>
    /// Per-hour budget for pairing requests from one source address.
    /// <para>
    /// Deliberately an hour, not a minute. Pairing is the only unauthenticated write endpoint
    /// on the agent, and it is what an attacker on the LAN would reach for. Five attempts a
    /// minute is a desktop prompt every twelve seconds; five an hour is a nuisance that still
    /// lets a real user pair a phone they keep re-pairing.
    /// </para>
    /// </summary>
    public int PairingPerHour { get; set; } = 5;

    /// <summary>
    /// How many pairing requests may be made back to back before the hourly budget starts
    /// draining.
    /// <para>
    /// Defaults to the hourly budget rather than something smaller. A burst of two means a
    /// user who pairs a phone, re-pairs it after clearing app data, and re-pairs again is
    /// locked out for the best part of an hour with no explanation - which reads as a broken
    /// product rather than as a throttle. Since the threat is an attacker generating desktop
    /// prompts, the number of prompts per hour is the thing that actually matters.
    /// </para>
    /// </summary>
    public int PairingBurst { get; set; } = 5;

    /// <summary>
    /// How many times a phone may poll one pairing attempt before the sustained budget kicks
    /// in, and how many it may poll per minute.
    /// <para>
    /// This budget exists because the protocol tells the phone to poll
    /// <c>GET /api/v1/pair/session</c> every <c>pollIntervalMs</c> for as long as
    /// <c>pairingWindowSeconds</c> lasts, and that route is unauthenticated. With the
    /// general unauthenticated budget of 30 a minute, a phone following the protocol
    /// exactly gets throttled about a quarter of the way through the window, so any user
    /// who takes more than half a minute to approve a pairing sees it fail. That is a
    /// product that cannot be paired slowly, which is most of the time.
    /// </para>
    /// <para>
    /// The numbers are derived from the protocol constants rather than guessed, and the
    /// defaults here are recomputed from them in <c>AgentHostedService</c> so the budget
    /// cannot fall behind the window again if either changes. The headroom over the
    /// protocol's own worst case absorbs a phone that polls a little faster than asked, and
    /// a client that ignores <c>pollIntervalMs</c> and spins is still bounded.
    /// </para>
    /// </summary>
    public int PairingPollsPerMinute { get; set; } = 240;

    public int PairingPollBurst { get; set; } = 20;

    /// <summary>
    /// The polling budget implied by the protocol: one poll per <paramref name="intervalMs"/>
    /// for the length of the window, with headroom, expressed per minute.
    /// </summary>
    public static int RequiredPairingPollsPerMinute(int windowSeconds, int intervalMs)
    {
        if (intervalMs <= 0) return 240;
        var pollsPerWindow = (int)Math.Ceiling(windowSeconds * 1000.0 / intervalMs);
        // 2x headroom, and never below a handful per second, so a phone that polls at the
        // protocol's rate is never throttled by its own agent.
        return Math.Clamp(pollsPerWindow * 2, 60, 100_000);
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Consumes one token for <paramref name="key"/>. Returns <c>false</c> when the budget
    /// is exhausted, in which case the caller should answer 429 with a Retry-After.
    /// </summary>
    /// <param name="key">The bucket identity, e.g. <c>addr:192.168.1.5:pair</c>.</param>
    /// <param name="capacity">Tokens regained per <paramref name="period"/>.</param>
    /// <param name="burst">Maximum tokens the bucket can hold.</param>
    /// <param name="period">The refill period the capacity is expressed in.</param>
    public bool TryConsume(string key, int capacity, int burst, TimeSpan period)
    {
        if (capacity <= 0 || burst <= 0 || period <= TimeSpan.Zero)
        {
            // A misconfigured budget must not mean "unlimited". Failing closed on the
            // security-relevant categories is the safe direction.
            return false;
        }

        var now = NowMs();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket
        {
            Tokens = burst,
            LastRefillUnixMs = now,
        });

        lock (bucket)
        {
            var elapsedMs = Math.Max(0, now - bucket.LastRefillUnixMs);
            bucket.LastRefillUnixMs = now;

            // Refill continuously, capped at the burst size. The period is explicit rather
            // than assumed to be a minute: an earlier version hard-coded 60000 and silently
            // turned a "per hour" limit into a "per minute" one, which is five times the
            // intended budget on the one endpoint where that matters most.
            bucket.Tokens = Math.Min(burst, bucket.Tokens + (elapsedMs * capacity / period.TotalMilliseconds));

            if (bucket.Tokens < 1.0)
            {
                _denials.AddOrUpdate(key, 1, (_, v) => v + 1);
                return false;
            }

            bucket.Tokens -= 1.0;
            return true;
        }
    }

    /// <summary>Per-minute variant, for the ordinary authenticated budget.</summary>
    public bool TryConsumePerMinute(string key, int perMinute, int burst)
        => TryConsume(key, perMinute, burst, TimeSpan.FromMinutes(1));

    /// <summary>Per-hour variant, for the pairing budget.</summary>
    public bool TryConsumePerHour(string key, int perHour, int burst)
        => TryConsume(key, perHour, burst, TimeSpan.FromHours(1));

    public bool TryConsumeForDevice(string deviceId, string category)
    {
        var (perMinute, burst) = category switch
        {
            "power" => (PowerActionsPerMinute, PowerActionsBurst),
            "unauth" => (UnauthenticatedPerMinute, UnauthenticatedBurst),
            _ => (RequestsPerMinute, Burst),
        };

        return TryConsumePerMinute($"device:{deviceId}:{category}", perMinute, burst);
    }

    public bool TryConsumeForAddress(IPAddress? address, string category)
    {
        var key = $"addr:{address?.ToString() ?? "unknown"}:{category}";

        if (category == "pair")
            return TryConsumePerHour(key, PairingPerHour, PairingBurst);

        var (perMinute, burst) = category switch
        {
            "unauth" => (UnauthenticatedPerMinute, UnauthenticatedBurst),
            // The poll route gets its own budget, sized to the protocol's own poll rate.
            // See PairingPollsPerMinute for why the general unauthenticated one is wrong
            // for it.
            "pairpoll" => (PairingPollsPerMinute, PairingPollBurst),
            _ => (RequestsPerMinute * 2, Burst * 2),
        };

        return TryConsumePerMinute(key, perMinute, burst);
    }

    /// <summary>Seconds until at least one token is available again.</summary>
    public int RetryAfterSeconds(string key, int perMinute)
    {
        // Zero for a bucket that does not exist: nothing was ever throttled for this key, and
        // telling a client to wait when it was not limited makes a working request look like
        // a slow one. The caller only reaches this after a denial, where the bucket exists.
        if (!_buckets.TryGetValue(key, out var bucket))
            return 0;

        lock (bucket)
        {
            if (bucket.Tokens >= 1.0)
                return 0;

            var missing = 1.0 - bucket.Tokens;
            var seconds = (int)Math.Ceiling(missing * 60000.0 / Math.Max(1, perMinute));
            return Math.Clamp(seconds, 1, 60);
        }
    }

    public IReadOnlyDictionary<string, long> DenialCounts => _denials;

    /// <summary>Drops buckets that have fully refilled, so a long uptime does not leak memory.</summary>
    public void Sweep()
    {
        var now = NowMs();

        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                if (now - bucket.LastRefillUnixMs > 10 * 60 * 1000)
                    _buckets.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>
/// Decides whether a remote address is allowed to talk to the agent at all.
/// <para>
/// The default posture is local-network-only, and this is the enforcement point for it.
/// The check is deliberately stricter than "is it a private IP": a private address
/// arriving from outside is impossible on a normal network, but a misconfigured or hostile
/// setup could produce one, so the configured subnet list is the authority.
/// </para>
/// <para>
/// Loopback is always permitted: the per-user session agent talks to the agent over
/// <c>127.0.0.1</c>, and a developer running the agent in a console needs it too.
/// </para>
/// </summary>
public sealed class LocalNetworkGuard
{
    private readonly ILogger<LocalNetworkGuard> _log;
    private readonly List<(IPAddress Network, int PrefixLength)> _allowed = [];
    private bool _localOnly = true;

    public LocalNetworkGuard(ILogger<LocalNetworkGuard> log) => _log = log;

    public bool LocalNetworkOnly => _localOnly;

    public void Configure(bool localNetworkOnly, IEnumerable<string> allowedSubnets)
    {
        _localOnly = localNetworkOnly;
        _allowed.Clear();

        foreach (var cidr in allowedSubnets ?? [])
        {
            var trimmed = cidr.Trim();
            if (trimmed.Length == 0)
                continue;

            var slash = trimmed.IndexOf('/');
            var addressPart = slash > 0 ? trimmed[..slash] : trimmed;
            var prefixPart = slash > 0 && int.TryParse(trimmed[(slash + 1)..], out var p) ? p : -1;

            if (!IPAddress.TryParse(addressPart, out var address))
            {
                _log.LogWarning("Ignoring malformed subnet {Cidr}", cidr);
                continue;
            }

            var prefix = address.AddressFamily == AddressFamily.InterNetwork
                ? (prefixPart >= 0 ? prefixPart : 32)
                : (prefixPart >= 0 ? prefixPart : 64);

            if (prefix < 0 || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
            {
                _log.LogWarning("Ignoring subnet {Cidr} with an out-of-range prefix length", cidr);
                continue;
            }

            _allowed.Add((address, prefix));
        }

        _log.LogInformation(
            "Local network guard: localOnly={LocalOnly}, {Count} allowed subnet(s): {Subnets}",
            _localOnly,
            _allowed.Count,
            string.Join(", ", _allowed.Select(entry => $"{entry.Network}/{entry.PrefixLength}")));
    }

    /// <summary>Whether a request from this address may proceed at all.</summary>
    public bool IsAllowed(IPAddress? address)
    {
        if (!_localOnly)
            return true;

        if (address is null)
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv6 ULA (fc00::/7) is the local equivalent; anything else is rejected
            // because the agent is IPv4-only by default.
            if (address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC)
                return true;

            return false;
        }

        var bytes = address.GetAddressBytes();

        foreach (var (network, prefix) in _allowed)
        {
            if (network.AddressFamily != address.AddressFamily)
                continue;

            if (InSubnet(bytes, network.GetAddressBytes(), prefix))
                return true;
        }

        return false;
    }

    private static bool InSubnet(byte[] address, byte[] network, int prefixLength)
    {
        if (address.Length != network.Length)
            return false;

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != network[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }

    /// <summary>True when the address is in one of the RFC 1918 / link-local ranges.</summary>
    public static bool IsPrivateLan(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

        var b = address.GetAddressBytes();

        return b[0] switch
        {
            10 => true,                                     // 10.0.0.0/8
            172 => b[1] is >= 16 and <= 31,                 // 172.16.0.0/12
            192 => b[1] == 168,                             // 192.168.0.0/16
            169 => b[1] == 254,                             // 169.254.0.0/16 link-local
            _ => false,
        };
    }
}
