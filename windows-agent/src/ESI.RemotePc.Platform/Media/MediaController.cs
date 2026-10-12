using System.Runtime.InteropServices;
using Esi.Protocol;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Media;

/// <summary>
/// Controls transport keys and the system volume.
/// <para>
/// Two mechanisms, deliberately combined:
/// </para>
/// <list type="bullet">
///   <item><description><b>Volume and mute</b> use <c>SendInput</c> with the documented
///   multimedia virtual keys (<c>VK_VOLUME_UP</c> 0xAF, <c>VK_VOLUME_DOWN</c> 0xAE,
///   <c>VK_VOLUME_MUTE</c> 0xAD). These are global, work from any session, and cannot
///   desynchronise the system from a cached local value.</description></item>
///   <item><description><b>Volume state</b> (read-back) uses Core Audio's
///   <c>IAudioEndpointVolume</c> through late-bound COM. Reading the real value matters
///   because the user may have changed volume with a physical key.</description></item>
/// </list>
/// <para>
/// Transport keys (<c>VK_MEDIA_PLAY_PAUSE</c> 0xB3 and friends) are injected as real
/// input so they are consumed by whatever is actually playing — Spotify, a browser, a
/// game — which is what users expect from a media remote. They are session-bound, so they
/// are forwarded to the interactive session agent rather than injected from session 0.
/// </para>
/// </summary>
public sealed class MediaController(ILogger<MediaController> log)
{
    private const int DefaultVolumeStep = 5;
    private const uint MaxVolume = 100;

    /// <summary>
    /// Presses per <c>SendInput</c> call. A ceiling per call, not per command: a walk of a
    /// hundred presses is simply several calls rather than one truncated one.
    /// </summary>
    private const int MaxVolumeStep = 25;

    // ---- Core Audio -----------------------------------------------------------------

    /// <summary>
    /// The default render endpoint this controller reads from, or null where there is none.
    /// <para>
    /// Null inside a Windows service, always: it runs in session 0, which has no interactive
    /// desktop and gets no endpoint from Core Audio at all (D43). Every read is then "unknown",
    /// and unknown is the honest answer. The previous version reported it as a confident 0,
    /// which made a PC sitting at 100% read as silent - and made "move to 30" a walk of 30
    /// volume-up presses from a base that had never been measured, so the volume never arrived
    /// anywhere near the number the phone had asked for.
    /// </para>
    /// </summary>
    private CoreAudioEndpoint? _endpoint;

    private bool _connectFailed;

    public bool CoreAudioAvailable => _endpoint is not null;

    public string? DefaultDeviceName => _endpoint?.FriendlyName;

    /// <summary>
    /// Opens the default render endpoint. Failure is non-fatal: the volume keys still work,
    /// only the read-back is unavailable - and unavailable is reported as unavailable.
    /// </summary>
    public bool TryConnect()
    {
        if (_endpoint is not null)
            return true;

        // Cached rather than retried. Inside a service this fails forever and identically, so
        // retrying it on every poll is a warning line in the log every three seconds for a
        // fact that does not change.
        if (_connectFailed)
            return false;

        _endpoint = CoreAudioEndpoint.TryOpen();

        if (_endpoint is null)
        {
            _connectFailed = true;
            log.LogWarning(
                "Core Audio gave this process no default render endpoint, so the volume cannot " +
                "be read back. Expected inside the Windows service (session 0); the session " +
                "agent reads the volume on its behalf.");
            return false;
        }

        log.LogInformation("Core Audio endpoint volume connected: {Device}", _endpoint.FriendlyName);
        return true;
    }

    /// <summary>The master volume, 0-100, or null when it cannot be read.</summary>
    private int? ReadVolume()
    {
        if (!TryConnect() || _endpoint is null)
            return null;

        var scalar = _endpoint.ReadVolumeScalar();
        return scalar is null ? null : (int)Math.Round(scalar.Value * MaxVolume);
    }

    /// <summary>Whether the default output is muted, or null when that cannot be read.</summary>
    private bool? ReadMuted()
    {
        if (!TryConnect() || _endpoint is null)
            return null;

        return _endpoint.ReadMuted();
    }

    // ---------------------------------------------------------------------------
    // Volume
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Changes the volume by sending the real multimedia key. This is used instead of
    /// <c>IAudioEndpointVolume::SetMasterVolumeLevelScalar</c> so that the OSD, lock
    /// screen and any per-app mixer all stay consistent with what the user sees.
    /// </summary>
    public MediaActionResponse VolumeUp(int step)
        => SendVolumeKey(WinConst.VK_VOLUME_UP, step <= 0 ? DefaultVolumeStep : step, "VolumeUp");

    public MediaActionResponse VolumeDown(int step)
        => SendVolumeKey(WinConst.VK_VOLUME_DOWN, step <= 0 ? DefaultVolumeStep : step, "VolumeDown");

    /// <summary>
    /// Injects a volume or mute key from the interactive desktop, on behalf of the service.
    /// <para>
    /// The service cannot do this itself. It runs in session 0, which has no interactive
    /// desktop and no audio endpoint: <c>SendInput</c> there either fails outright or goes to a
    /// desktop nobody is looking at, and Core Audio's <c>GetDefaultAudioEndpoint</c> returns
    /// nothing, so the volume cannot even be read back. Every volume key therefore has to be
    /// pressed by the session agent, which runs as the signed-in user (D43).
    /// </para>
    /// <para>
    /// This is the same pairing that already existed for the transport keys. The comment on that
    /// path said they "are forwarded to the interactive session agent", but nothing forwarded
    /// them - <see cref="SendTransport"/> ran in the service too, so the note described an intent
    /// the code did not carry out. Volume and mute were worse: they were not even checked, so
    /// the phone got a bare <c>409</c> with no reason and had no way to tell a refusal from a
    /// bug.
    /// </para>
    /// </summary>
    public static MediaActionResponse SendVolumeKeyFromSession(ushort vk, int presses, string action)
        => SendVolumeKey(vk, presses, action);

    /// <summary>
    /// The virtual key for a volume or mute action, for the session agent to inject.
    /// </summary>
    public static ushort? VolumeVirtualKey(MediaAction action) => action switch
    {
        MediaAction.VolumeUp => WinConst.VK_VOLUME_UP,
        MediaAction.VolumeDown => WinConst.VK_VOLUME_DOWN,
        MediaAction.Mute => WinConst.VK_VOLUME_MUTE,
        MediaAction.Unmute => WinConst.VK_VOLUME_MUTE,
        MediaAction.ToggleMute => WinConst.VK_VOLUME_MUTE,
        _ => null,
    };

    /// <summary>
    /// The batched key path. One batch is capped at <see cref="MaxVolumeStep"/> presses; a walk
    /// longer than that is simply sent as several batches rather than being truncated. Before
    /// this existed, moving from 0 to 100 sent 25 presses, stopped at 25, and every reply still
    /// reported the volume as delivered - so a phone that asked for 60 and got 25 had no way to
    /// notice that the two had never met.
    /// </summary>
    private static MediaActionResponse SendVolumeKey(ushort vk, int presses, string action)
    {
        var attempted = 0;
        var delivered = 0;
        var total = VolumeKeyBatches(presses).Sum();

        foreach (var size in VolumeKeyBatches(presses))
        {
            var events = new List<INPUT>(size * 2);
            for (var i = 0; i < size; i++)
            {
                events.Add(KeyEvent(vk, down: true));
                events.Add(KeyEvent(vk, down: false));
            }

            var sent = Native.USER32.SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<INPUT>());
            attempted += (int)sent;
            delivered += (int)sent;

            if (sent != events.Count)
            {
                return new MediaActionResponse
                {
                    Accepted = false,
                    Action = action,
                    Detail = $"SendInput delivered {attempted} of {total * 2} events " +
                             $"(error {Marshal.GetLastWin32Error()}). Volume keys must be injected " +
                             "from the interactive desktop.",
                };
            }
        }

        return new MediaActionResponse { Accepted = true, Action = action };
    }

    /// <summary>
    /// How many presses go into each <c>SendInput</c> call for a walk of <paramref name="total"/>
    /// presses.
    /// <para>
    /// Split out of <see cref="SendVolumeKey"/> because the rule it encodes is the part worth
    /// testing, and the part that was wrong: one call is capped at <see cref="MaxVolumeStep"/>
    /// presses, and the cap used to be applied to the *whole* walk instead of to one call.
    /// Moving from 0 to 100 therefore sent 25 presses, stopped at 25, and reported the volume as
    /// delivered. A phone that asked for 60 and got 25 had no way to notice that the two had
    /// never met, because the reply never carried the number the PC landed on.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<int> VolumeKeyBatches(int total)
    {
        var remaining = Math.Max(1, total);
        var batches = new List<int>();

        while (remaining > 0)
        {
            var size = Math.Min(MaxVolumeStep, remaining);
            batches.Add(size);
            remaining -= size;
        }

        return batches;
    }

    public MediaActionResponse Mute(bool muted)
    {
        var current = ReadMuted();
        if (current == muted)
        {
            return new MediaActionResponse
            {
                Accepted = true,
                Action = muted ? "Mute" : "Unmute",
                IsMuted = current,
                Detail = "Already in the requested state.",
            };
        }

        // Write the flag rather than press the mute key. A key is a toggle, so pressing it while
        // already muted unmutes - and a process that cannot read the flag has no way to know
        // which half of that toggle it is standing in.
        if (TryConnect() && _endpoint is not null && _endpoint.WriteMuted(muted))
        {
            var wrote = ReadMuted();
            if (wrote is not null)
            {
                return new MediaActionResponse
                {
                    Accepted = wrote == muted,
                    Action = muted ? "Mute" : "Unmute",
                    IsMuted = wrote,
                    Detail = wrote == muted
                        ? "Mute state written through Core Audio."
                        : "The mute flag was written but did not land in the requested state.",
                };
            }
        }

        var events = new[] { KeyEvent(WinConst.VK_VOLUME_MUTE, down: true), KeyEvent(WinConst.VK_VOLUME_MUTE, down: false) };
        var sent = Native.USER32.SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());

        return new MediaActionResponse
        {
            Accepted = sent == events.Length,
            Action = muted ? "Mute" : "Unmute",
            IsMuted = muted,
            Detail = sent == events.Length ? null : "SendInput could not deliver the mute key.",
        };
    }

    public MediaActionResponse ToggleMute() => Mute(!(ReadMuted() ?? false));

    public MediaActionResponse SetVolume(int volume)
    {
        var target = (int)Math.Clamp(volume, 0, MaxVolume);
        var current = ReadVolume();

        if (current is null)
        {
            return new MediaActionResponse
            {
                Accepted = false,
                Action = "volumeSet",
                Detail = "Core Audio is unavailable, so the exact volume cannot be set. Use volumeUp/volumeDown instead.",
            };
        }

        // One COM call, not a walk. See CoreAudioEndpoint.WriteVolumeScalar for why the walk
        // moved twice as far as asked and left the reply describing a volume still in motion.
        var wrote = TryWriteVolume(target);
        var afterWrite = ReadVolume();

        if (wrote && afterWrite is not null && Math.Abs(afterWrite.Value - target) <= 1)
        {
            return new MediaActionResponse
            {
                Accepted = true,
                Action = "volumeSet",
                Volume = afterWrite,
                IsMuted = ReadMuted(),
                Detail = $"Set to {afterWrite.Value}% through Core Audio.",
            };
        }

        // The write failed, or the level did not land within a rounding step of the target.
        // Fall back to the key path - but now the press count comes from the endpoint's own
        // step count, so the distance it travels is measured rather than assumed.
        var delta = target - ((afterWrite ?? current) ?? current).Value;
        var presses = VolumePressesForDelta(delta);

        var step = delta == 0
            ? new MediaActionResponse { Accepted = true, Action = "volumeSet", Volume = afterWrite ?? current, IsMuted = ReadMuted() }
            : SendVolumeKey(delta > 0 ? WinConst.VK_VOLUME_UP : WinConst.VK_VOLUME_DOWN, presses, "volumeSet");

        return new MediaActionResponse
        {
            Accepted = step.Accepted,
            Action = "volumeSet",
            Volume = ReadVolume(),
            IsMuted = ReadMuted(),
            Detail = step.Accepted
                ? $"Core Audio would not take the level directly; moved it with {presses} key presses instead."
                : step.Detail,
        };
    }

    /// <summary>
    /// One direct write to an exact percentage, succeeding only if the level that came back is
    /// within a rounding step of the one asked for.
    /// <para>
    /// The readback is the point. A write that returns success and leaves the volume somewhere
    /// else is the same failure the walk had, just quieter, so the caller only believes the write
    /// when the endpoint agrees it happened.
    /// </para>
    /// </summary>
    internal bool TryWriteVolume(int targetPercent)
    {
        if (!TryConnect() || _endpoint is null)
            return false;

        return _endpoint.WriteVolumeScalar(targetPercent / (float)MaxVolume);
    }

    /// <summary>Same, with the step count read from the live endpoint.</summary>
    internal int VolumePressesForDelta(int delta)
        => VolumePressesForDelta(delta, _endpoint?.ReadStepCount());

    /// <summary>
    /// How many key presses move the volume by <paramref name="delta"/> percentage points when
    /// the endpoint reports <paramref name="stepCount"/> steps across its range.
    /// <para>
    /// Not <c>|delta|</c>, which is what this used to be. A press moves 100/stepCount points -
    /// about 1.96 on this machine, which reports 51 steps - so pressing |delta| times travels
    /// roughly twice the intended distance. Asking for 55 from 100 pressed 45 times and landed
    /// at 10.
    /// </para>
    /// <para>
    /// A null or zero step count means the step could not be measured, and the old arithmetic is
    /// kept: an unmeasured press is still worth making, and an exact number of presses is better
    /// than no movement at all.
    /// </para>
    /// </summary>
    internal static int VolumePressesForDelta(int delta, int? stepCount)
    {
        if (delta == 0)
            return 0;

        if (stepCount is not { } steps || steps <= 0)
            return Math.Abs(delta);

        return Math.Max(1, (int)Math.Round(Math.Abs(delta) * steps / (double)MaxVolume));
    }

    public MediaStateResponse GetState()
    {
        TryConnect();

        // A failed read is not the number zero. The old `ReadVolume() ?? 0` made "this process
        // cannot see an audio endpoint" indistinguishable from "the PC is silent", and the phone
        // then spent every poll showing a confident 0 next to a slider the user had just moved.
        var volume = ReadVolume();
        var muted = ReadMuted();

        return new MediaStateResponse
        {
            Volume = volume ?? 0,
            VolumeKnown = volume is not null,
            IsMuted = muted ?? false,
            IsMuteKnown = muted is not null,
            DefaultDevice = _endpoint?.FriendlyName,
            SessionPeak = 100,
            Supported = true,
            AvailableActions =
            [
                "play", "pause", "playPause", "stop", "next", "previous",
                "volumeUp", "volumeDown", "volumeSet", "mute", "unmute", "toggleMute",
            ],
        };
    }

    // ---------------------------------------------------------------------------
    // Transport
    // ---------------------------------------------------------------------------

    /// <summary>Virtual key for a transport action, for the session agent to inject.</summary>
    public static ushort? TransportVirtualKey(MediaAction action) => action switch
    {
        MediaAction.Play => WinConst.VK_MEDIA_PLAY_PAUSE,
        MediaAction.Pause => WinConst.VK_MEDIA_PLAY_PAUSE,
        MediaAction.PlayPause => WinConst.VK_MEDIA_PLAY_PAUSE,
        MediaAction.Stop => WinConst.VK_MEDIA_STOP,
        MediaAction.Next => WinConst.VK_MEDIA_NEXT_TRACK,
        MediaAction.Previous => WinConst.VK_MEDIA_PREV_TRACK,
        _ => null,
    };

    /// <summary>
    /// Sends a transport key from this process. Only valid on the interactive desktop;
    /// the service routes these through the session agent instead.
    /// </summary>
    public MediaActionResponse SendTransport(MediaAction action)
    {
        if (TransportVirtualKey(action) is not { } vk)
        {
            return new MediaActionResponse
            {
                Accepted = false,
                Action = action.ToString(),
                Detail = $"{action} is not a transport action.",
            };
        }

        var events = new[] { KeyEvent(vk, down: true), KeyEvent(vk, down: false) };
        var sent = Native.USER32.SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());

        return new MediaActionResponse
        {
            Accepted = sent == events.Length,
            Action = action.ToString(),
            Detail = sent == events.Length
                ? null
                : "SendInput could not deliver the media key; it must be injected from the interactive desktop.",
        };
    }

    private static INPUT KeyEvent(ushort vk, bool down) => new()
    {
        type = WinConst.INPUT_KEYBOARD,
        U = new InputUnionUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = down ? 0 : WinConst.KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
