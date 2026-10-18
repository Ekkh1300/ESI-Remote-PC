using Esi.Platform.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D87: when the phone carries the PC's sound, the PC's own speakers are silenced, and the
/// machine returns to the state the user left it in afterwards - or after a crash.
///
/// <para>
/// The mechanism was measured before it was designed: on the machine that asked, the endpoint
/// mute takes effect after the loopback tap ( Equal peaks of 5143 muted and unmuted while a
/// tone played), so muting silences the room without silencing the stream. The decisions that
/// matter are therefore not device ones; they are "whose mute is this", "what happens to a
/// mute the process never got to undo", and "does a refusal leave a lie in the ledger". Those
/// are pinned here against fakes, without a sound card.
/// </para>
/// </summary>
public sealed class LocalOutputSilencerTests
{
    private readonly ITestOutputHelper _out;

    public LocalOutputSilencerTests(ITestOutputHelper output) => _out = output;

    private sealed class FakeEndpoint(bool startMuted = false) : IEndpointMute
    {
        private bool _mute = startMuted;

        /// <summary>When true, setting <see cref="Mute"/> throws, as a device gone away would.</summary>
        public bool FailOnSet { get; set; }

        public bool Mute
        {
            get => _mute;
            set
            {
                if (FailOnSet)
                    throw new InvalidOperationException("the endpoint is gone for now");

                _mute = value;
            }
        }
    }

    private sealed class FakeLedger : ISilencerLedger
    {
        public bool? Value { get; private set; }

        public int Writes { get; private set; }

        public bool? Read() => Value;

        public void Write(bool value)
        {
            Value = value;
            Writes++;
        }

        public void Clear() => Value = null;
    }

    private static LocalOutputSilencer Make(FakeEndpoint endpoint, FakeLedger ledger)
        => new(endpoint, ledger, NullLogger<LocalOutputSilencer>.Instance);

    [Fact]
    public void Engage_mutes_an_unmuted_endpoint_and_records_the_commitment()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        var silencer = Make(endpoint, ledger);

        silencer.Engage();

        Assert.True(endpoint.Mute);
        Assert.True(ledger.Read());
        Assert.Equal(1, ledger.Writes);
    }

    [Fact]
    public void Double_engage_mutes_once_and_commits_once()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        var silencer = Make(endpoint, ledger);

        silencer.Engage();
        silencer.Engage();

        Assert.True(endpoint.Mute);
        Assert.Equal(1, ledger.Writes);
    }

    [Fact]
    public void Engage_on_an_already_muted_endpoint_commits_nothing_and_release_restores_nothing()
    {
        // The user muted this themselves. Our release must not put sound on a machine they
        // deliberately silenced, and our ledger must not claim a mute we did not take.
        var endpoint = new FakeEndpoint(startMuted: true);
        var ledger = new FakeLedger();
        var silencer = Make(endpoint, ledger);

        silencer.Engage();
        silencer.Release();

        Assert.True(endpoint.Mute, "the user's own mute stands");
        Assert.Null(ledger.Read());
    }

    [Fact]
    public void Release_lifts_our_mute_and_clears_the_ledger()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        var silencer = Make(endpoint, ledger);

        silencer.Engage();
        silencer.Release();

        Assert.False(endpoint.Mute);
        Assert.False(ledger.Read());
    }

    [Fact]
    public void Release_before_any_engage_is_a_no_op()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        Make(endpoint, ledger).Release();

        Assert.False(endpoint.Mute);
        Assert.Equal(0, ledger.Writes);
    }

    [Fact]
    public void An_engage_refusal_leaves_no_ledger_trace()
    {
        var endpoint = new FakeEndpoint { FailOnSet = true };
        var ledger = new FakeLedger();
        var silencer = Make(endpoint, ledger);

        silencer.Engage();
        silencer.Release();

        Assert.Null(ledger.Read());
        Assert.False(endpoint.Mute);
    }

    [Fact]
    public void A_failed_unmute_keeps_the_ledger_set_so_a_later_run_retries()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        var silencer = Make(endpoint, ledger);

        silencer.Engage();

        // The endpoint goes away at exactly the wrong moment: unmute refused.
        endpoint.FailOnSet = true;
        silencer.Release();

        Assert.True(ledger.Read(), "the commit stands: the user's speakers are still silenced by us");

        // The next run - a healthy endpoint, the ledger still naming what we left behind.
        var healthy = new FakeEndpoint();
        var nextRun = Make(healthy, ledger);
        nextRun.Recover();

        Assert.False(healthy.Mute);
        Assert.False(ledger.Read());
    }

    [Fact]
    public void Recover_lifts_a_mute_the_ledger_names_and_clears_it()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        ledger.Write(true); // what a dead run left behind

        Make(endpoint, ledger).Recover();

        Assert.False(endpoint.Mute);
        Assert.False(ledger.Read());
    }

    [Fact]
    public void Recover_with_no_outstanding_commitment_touches_the_endpoint_not_at_all()
    {
        var endpoint = new FakeEndpoint();
        var ledger = new FakeLedger();
        ledger.Clear();

        Make(endpoint, ledger).Recover();

        Assert.False(endpoint.Mute);
        Assert.Equal(0, ledger.Writes);

        // A finished session's ledger says "false" - no mute outstanding - and recovery
        // trusts it rather than unmuting something that never was.
        ledger.Write(false);
        Make(endpoint, ledger).Recover();

        Assert.False(endpoint.Mute);
        Assert.Equal(1, ledger.Writes);
    }
}
