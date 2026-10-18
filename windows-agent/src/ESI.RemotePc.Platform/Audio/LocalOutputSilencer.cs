using Microsoft.Extensions.Logging;

namespace Esi.Platform.Audio;

/// <summary>
/// The mute bit of one audio endpoint, in whatever shape the host can drive it.
/// </summary>
/// <remarks>
/// The session agent binds this to the MMDevice endpoint the loopback capture taps, so the
/// abstraction is exercised by tests without a sound card and the binding stays thin. A
/// machine whose driver applies the endpoint volume digitally inside the audio engine makes
/// this bit silence the loopback tap too - measured on one machine (17 October) the engine's
/// mix delivered the same peak muted and unmuted, which is the behaviour D87 is built on, and
/// the phone's own silence meter is what would catch the other behaviour in the field.
/// </remarks>
public interface IEndpointMute
{
    /// <summary>True while the endpoint is muted. Reading it and setting it are both real.</summary>
    bool Mute { get; set; }
}

/// <summary>
/// The one-bit record that lets a later session agent undo the mute of a crashed one.
/// </summary>
/// <remarks>
/// A process that dies between muting and restoring leaves the machine's speakers silenced,
/// and nothing about the mute survives the process to name it as ours. The ledger does: a
/// single per-user bit, written while we hold the mute and cleared after we give it up, so a
/// later run restores what the dead run committed. The host binds it to HKCU.
/// </remarks>
public interface ISilencerLedger
{
    /// <summary>True while a mute this feature is responsible for is outstanding; null when never written.</summary>
    bool? Read();

    void Write(bool value);
}

/// <summary>
/// Silences the PC's own output device while the phone is the one carrying its sound, and
/// puts it back afterwards - the "speaker is the phone" arrangement working like a wireless
/// speaker the PC has plugged into. Everything that decides whether this is *our* mute lives
/// here, so the semantics are testable without Core Audio and the device binding is a
/// property-set away.
/// </summary>
/// <remarks>
/// <para>
/// The distinction the whole class enforces: a mute is restored only when we caused it. In
/// particular, an endpoint that was already muted at engage time is left as it was - the mute
/// belonged to the user, and unmuting it at stream's end would put sound onto a machine the
/// user had deliberately silenced. Also, if the endpoint was already muted, the ledger is
/// never written: there is no restore commitment of ours to recover.
/// </para>
/// <para>
/// Already-muted also answers the only failure of D87's mechanism worth naming: some users
/// like their PC muted anyway, and the feature is then a no-op both at engage and at release,
/// which is the correct behaviour for a request that says "make sure the PC's speakers are
/// silent" rather than "mute and unmute".
/// </para>
/// </remarks>
public sealed class LocalOutputSilencer
{
    private readonly IEndpointMute _endpoint;
    private readonly ISilencerLedger _ledger;
    private readonly ILogger _log;

    private bool _engagedByUs;

    public LocalOutputSilencer(IEndpointMute endpoint, ISilencerLedger ledger, ILogger log)
    {
        _endpoint = endpoint;
        _ledger = ledger;
        _log = log;
    }

    /// <summary>
    /// Mutes the endpoint if it was not already muted, and records the commitment so a crash
    /// before the release is recoverable. Safe to call again while engaged; the second call
    /// does not re-mute or re-commit.
    /// </summary>
    public void Engage()
    {
        if (_engagedByUs)
            return;

        bool wasMuted;
        try
        {
            wasMuted = _endpoint.Mute;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "The endpoint mute state could not be read; the PC's own speakers are left as they are.");
            return;
        }

        if (wasMuted)
        {
            // The user muted this themselves; our release must not undo their choice.
            _log.LogInformation(
                "The PC's output was already muted before the phone asked for audio - leaving it as it is.");
            return;
        }

        try
        {
            _endpoint.Mute = true;
        }
        catch (Exception ex)
        {
            // The capture is not affected: it taps the mix, not the device. The DS87 request
            // degrades to "no silencing", the same behaviour as the toggle being off.
            _log.LogWarning(
                ex,
                "The endpoint refused to be muted; the PC's own speakers stay audible to whoever is in the room.");
            return;
        }

        _ledger.Write(true);
        _engagedByUs = true;
        _log.LogInformation(
            "The PC's own speakers are muted while the phone carries the sound; they return when the stream ends.");
    }

    /// <summary>
    /// Unmutes only if <see cref="Engage"/> was the thing that muted, and clears the ledger
    /// only when the unmute goes through. Safe to call when never engaged.
    /// </summary>
    /// <remarks>
    /// A failed unmute keeps the ledger set, because the failure means the user's speakers are
    /// still silenced by our mute and a later session agent must have another try.
    /// </remarks>
    public void Release()
    {
        if (!_engagedByUs)
            return;

        _engagedByUs = false;

        bool unmuted;
        try
        {
            _endpoint.Mute = false;
            unmuted = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The endpoint mute could not be lifted; a later agent run will retry.");
            unmuted = false;
        }

        if (unmuted)
        {
            _ledger.Write(false);
            _log.LogInformation("The PC's own speakers are unmuted again.");
        }
    }

    /// <summary>
    /// Undoes whatever a previous run of this same feature left behind, at startup. Called
    /// before any new engage, when the ledger says a mute is outstanding.
    /// </summary>
    /// <remarks>
    /// Restoring to *unmuted* rather than to a recorded previous value is deliberate: the
    /// previous value was already muted (the ledger would not say otherwise), so an unmute is
    /// the only restoration that tells the user's hands apart from ours.
    /// </remarks>
    public void Recover()
    {
        if (_ledger.Read() != true)
            return;

        try
        {
            _endpoint.Mute = false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "A mute a previous session left behind could not be lifted here; the ledger stays set so a later run retries.");
            return;
        }

        _ledger.Write(false);
        _log.LogInformation(
            "A mute the previous session agent left behind was lifted at startup - it died after silencing the PC and before restoring it.");
    }
}
