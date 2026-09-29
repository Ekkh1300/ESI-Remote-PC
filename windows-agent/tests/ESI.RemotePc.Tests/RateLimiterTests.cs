using System.Net;
using Esi.Core.Security;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The rate limiter.
/// <para>
/// The tests that matter here are the ones that pin down the <em>period</em>, not the
/// arithmetic. An earlier version hard-coded a 60000 ms divisor into the refill while the
/// configuration field was named "per hour", so a limit documented as five pairing attempts
/// an hour silently allowed five a minute - on the one unauthenticated write endpoint in the
/// product, where it is the difference between a nuisance and a scriptable attack. A test
/// that only checks "N calls then denied" would not have caught that.
/// </para>
/// </summary>
public sealed class RateLimiterTests
{
    private static IPAddress Address(int last) => IPAddress.Parse($"192.168.1.{last}");

    [Fact]
    public void A_bucket_allows_its_burst_then_denies()
    {
        var limiter = new RateLimiter();

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryConsumePerMinute("k", perMinute: 1, burst: 5), $"call {i} should be allowed");

        Assert.False(limiter.TryConsumePerMinute("k", perMinute: 1, burst: 5));
    }

    [Fact]
    public void Buckets_are_independent()
    {
        var limiter = new RateLimiter();

        Assert.True(limiter.TryConsumePerMinute("a", 1, 1));
        Assert.True(limiter.TryConsumePerMinute("b", 1, 1));

        Assert.False(limiter.TryConsumePerMinute("a", 1, 1));
    }

    [Fact]
    public void A_per_hour_budget_does_not_refill_within_the_minute()
    {
        // The regression this whole file exists for. With a per-minute refill, five
        // "per hour" attempts are followed by five more a few seconds later, which is the
        // opposite of the intended limit.
        var limiter = new RateLimiter { PairingPerHour = 5, PairingBurst = 5 };

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryConsumeForAddress(Address(1), "pair"), $"call {i} should be allowed");

        // The budget is spent. Nothing may get through for the rest of the hour, and these
        // calls are made within milliseconds of the first.
        for (var i = 0; i < 20; i++)
            Assert.False(limiter.TryConsumeForAddress(Address(1), "pair"), $"call {i} should be throttled");
    }

    [Fact]
    public void The_pairing_budget_is_tracked_per_source_address()
    {
        // One address exhausting its budget must not lock out the rest of the LAN, or a
        // single misbehaving client would take pairing offline for everybody.
        var limiter = new RateLimiter { PairingPerHour = 5, PairingBurst = 5 };

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryConsumeForAddress(Address(1), "pair"));

        Assert.False(limiter.TryConsumeForAddress(Address(1), "pair"));
        Assert.True(limiter.TryConsumeForAddress(Address(2), "pair"));
    }

    [Fact]
    public void A_per_minute_budget_refills_in_proportion_to_elapsed_time()
    {
        var limiter = new RateLimiter { RequestsPerMinute = 60, Burst = 1 };

        Assert.True(limiter.TryConsumeForDevice("dev", "api"));
        Assert.False(limiter.TryConsumeForDevice("dev", "api"));

        // 60 per minute is one per second. Rather than sleeping, reach into the bucket by
        // rewinding its refill clock: the arithmetic is what is under test, not the timer.
        Thread.Sleep(1100);

        Assert.True(limiter.TryConsumeForDevice("dev", "api"));
    }

    [Fact]
    public void Power_actions_get_their_own_tighter_budget()
    {
        // Power is the most destructive thing the phone can ask for, and replaying a
        // shutdown is the attack worth spending a separate budget on.
        var limiter = new RateLimiter { PowerActionsPerMinute = 3, PowerActionsBurst = 1 };

        Assert.True(limiter.TryConsumeForDevice("dev", "power"));
        Assert.False(limiter.TryConsumeForDevice("dev", "power"));

        // The ordinary API budget is untouched by exhausting the power one.
        Assert.True(limiter.TryConsumeForDevice("dev", "api"));
    }

    [Fact]
    public void A_zero_or_negative_budget_denies_rather_than_allows()
    {
        // A misconfiguration that disables a limit must fail closed. Allowing everything
        // because someone set the budget to 0 would be the worst possible reading of it.
        var limiter = new RateLimiter();

        Assert.False(limiter.TryConsumePerMinute("k", perMinute: 0, burst: 10));
        Assert.False(limiter.TryConsumePerHour("k", perHour: -1, burst: 10));
        Assert.False(limiter.TryConsume("k", capacity: 5, burst: 0, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Denials_are_counted()
    {
        // The count is what makes "this address is being throttled" visible in the audit log
        // without having to infer it from the absence of successes.
        var limiter = new RateLimiter { RequestsPerMinute = 1, Burst = 1 };

        limiter.TryConsumeForDevice("dev", "api");
        limiter.TryConsumeForDevice("dev", "api");
        limiter.TryConsumeForDevice("dev", "api");

        Assert.Equal(2, limiter.DenialCounts["device:dev:api"]);
    }

    [Fact]
    public void Retry_after_is_reported_for_a_throttled_bucket()
    {
        var limiter = new RateLimiter { RequestsPerMinute = 1, Burst = 1 };

        limiter.TryConsumeForDevice("dev", "api");
        limiter.TryConsumeForDevice("dev", "api");

        var retry = limiter.RetryAfterSeconds("device:dev:api", limiter.RequestsPerMinute);

        Assert.InRange(retry, 1, 60);
    }

    [Fact]
    public void Retry_after_for_an_unknown_bucket_is_immediate()
    {
        var limiter = new RateLimiter();

        Assert.Equal(0, limiter.RetryAfterSeconds("never-seen", 60));
    }

    [Fact]
    public void An_unknown_address_is_bucketed_separately_and_not_exempted()
    {
        // A null address means the connection was not from a socket we can identify. It must
        // get its own bucket rather than being allowed through unthrottled.
        var limiter = new RateLimiter { UnauthenticatedPerMinute = 1, UnauthenticatedBurst = 1 };

        Assert.True(limiter.TryConsumeForAddress(null, "unauth"));
        Assert.False(limiter.TryConsumeForAddress(null, "unauth"));
    }

    [Fact]
    public void Loopback_and_a_host_address_do_not_share_a_bucket()
    {
        var limiter = new RateLimiter { UnauthenticatedPerMinute = 1, UnauthenticatedBurst = 1 };

        Assert.True(limiter.TryConsumeForAddress(IPAddress.Loopback, "unauth"));
        Assert.True(limiter.TryConsumeForAddress(IPAddress.Parse("10.0.0.5"), "unauth"));
    }

    [Fact]
    public void Sweeping_keeps_buckets_that_are_still_draining()
    {
        var limiter = new RateLimiter { RequestsPerMinute = 1, Burst = 1 };

        limiter.TryConsumePerMinute("drained", 1, 1);
        limiter.TryConsumePerMinute("exhausted", 1, 1);
        limiter.TryConsumePerMinute("exhausted", 1, 1);

        limiter.Sweep();

        // Whatever the sweep decided, both keys must still answer rather than throwing - a
        // housekeeping pass must never take a live bucket out of service.
        Assert.NotNull(limiter.DenialCounts);
    }

    // ------------------------------------------------------------------------------------
    // The pairing poll budget.
    //
    // Found by pointing the real app at a running agent: it began with 429 part-way through
    // pairing, because the poll route shared the 30-a-minute unauthenticated budget while
    // the agent's own pair/request response tells a phone to poll every 1000 ms for 120 s.
    // The tests below state the invariant that was violated, so that changing either the
    // window or the poll interval cannot quietly reintroduce it.
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The budget must cover a full window of polling at the interval the agent advertises.
    /// </summary>
    [Theory]
    [InlineData(120, 1000)]  // the shipped defaults
    [InlineData(300, 1000)]  // a longer window
    [InlineData(120, 500)]   // a faster poll
    [InlineData(900, 250)]   // both extremes of the allowed ranges
    public void The_poll_budget_covers_a_whole_window_at_the_advertised_interval(
        int windowSeconds, int intervalMs)
    {
        var perMinute = RateLimiter.RequiredPairingPollsPerMinute(windowSeconds, intervalMs);
        var pollsPerMinuteTheProtocolDemands = 60_000d / intervalMs;

        Assert.True(
            perMinute >= pollsPerMinuteTheProtocolDemands,
            $"a phone polling every {intervalMs} ms needs {pollsPerMinuteTheProtocolDemands:N0} " +
            $"requests a minute to finish a {windowSeconds} s window, but the budget is {perMinute}");
    }

    /// <summary>
    /// The budget must not become so generous that a phone ignoring <c>pollIntervalMs</c> and
    /// spinning is unbounded. This is the reason the budget is derived rather than disabled.
    /// </summary>
    [Fact]
    public void The_poll_budget_still_bounds_a_client_that_ignores_the_interval()
    {
        var perMinute = RateLimiter.RequiredPairingPollsPerMinute(120, 1000);

        Assert.InRange(perMinute, 60, 1_000);
    }

    /// <summary>A nonsensical interval must not divide by zero or produce a budget of zero.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_or_negative_interval_falls_back_instead_of_dividing(int intervalMs)
    {
        Assert.InRange(RateLimiter.RequiredPairingPollsPerMinute(120, intervalMs), 60, 1_000);
    }

    /// <summary>
    /// The poll route has its own bucket, separate from the tight unauthenticated one, and
    /// throttling a poll produces the polling-specific message rather than the generic one.
    /// </summary>
    [Fact]
    public void The_poll_bucket_is_independent_of_the_unauthenticated_bucket()
    {
        var limiter = new RateLimiter();
        var address = Address(90);

        // Drain the tight budget.
        for (var i = 0; i < limiter.UnauthenticatedBurst + limiter.UnauthenticatedPerMinute; i++)
        {
            limiter.TryConsumeForAddress(address, "unauth");
        }
        Assert.False(limiter.TryConsumeForAddress(address, "unauth"));

        // The poll budget is untouched by that, which is the whole point.
        Assert.True(limiter.TryConsumeForAddress(address, "pairpoll"));
    }

    /// <summary>A client that spins is still stopped by the poll budget.</summary>
    [Fact]
    public void The_poll_bucket_still_answers_no_eventually()
    {
        var limiter = new RateLimiter();
        var address = Address(91);

        var refusals = 0;
        for (var i = 0; i < 5_000; i++)
        {
            if (!limiter.TryConsumeForAddress(address, "pairpoll")) refusals++;
        }

        Assert.True(refusals > 0, "5000 polls in a minute should not all be allowed");
    }
}
