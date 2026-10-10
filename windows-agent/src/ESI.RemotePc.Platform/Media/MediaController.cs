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

    // ---- Core Audio late-bound COM ------------------------------------------------

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const uint ERender = 0;
    private const uint DeviceStateActive = 0x1;
    private const uint CLSCTX_ALL = 0x17;

    private object? _enumerator;
    private object? _endpointVolume;
    private string? _deviceName;

    public bool CoreAudioAvailable => _endpointVolume is not null;

    public string? DefaultDeviceName => _deviceName;

    /// <summary>
    /// Resolves the default render endpoint's volume interface. Failure is non-fatal:
    /// the volume keys still work, only the read-back is unavailable.
    /// </summary>
    public bool TryConnect()
    {
        if (_endpointVolume is not null)
            return true;

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator, throwOnError: true);
            _enumerator = Activator.CreateInstance(type!);
            if (_enumerator is null)
                return false;

            // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole, &device)
            var getDefault = _enumerator.GetType().GetMethod("GetDefaultAudioEndpoint");
            if (getDefault is null)
                return false;

            object? device = null;
            var args = new[] { (object)ERender, (object)0u, null };
            var result = getDefault.Invoke(_enumerator, args);
            device = args[2];

            if (result is not null && (int)result != 0)
                return false;
            if (device is null)
                return false;

            // IMMDevice::Activate(IID_IAudioEndpointVolume)
            var activate = device.GetType().GetMethod("Activate");
            if (activate is null)
                return false;

            object? volume = null;
            var activateArgs = new[] { (object)IidIAudioEndpointVolume, (object)CLSCTX_ALL, IntPtr.Zero, (object)0u, null };
            var activateResult = activate.Invoke(device, activateArgs);
            volume = activateArgs[4];

            if (activateResult is not null && (int)activateResult != 0)
                return false;
            if (volume is null)
                return false;

            _endpointVolume = volume;
            _deviceName = ReadFriendlyName(device);
            log.LogInformation("Core Audio endpoint volume connected: {Device}", _deviceName ?? "default render endpoint");
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Core Audio is unavailable; volume read-back will be disabled");
            return false;
        }
    }

    private string? ReadFriendlyName(object device)
    {
        try
        {
            var store = device.GetType().GetMethod("OpenPropertyStore")?.Invoke(device, [0u]);
            if (store is null)
                return null;

            // {a45c254e-df1c-4efd-8020-67d146a850e0}0 = PKEY_Device_FriendlyName
            var getValue = store.GetType().GetMethod("GetValue");
            var fromId = store.GetType().GetMethod("GetValue")?.GetParameters();
            if (getValue is null || fromId is null)
                return null;

            var key = new PropertyKey(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 0);
            var value = getValue.Invoke(store, [key]);
            if (value is null)
                return null;

            var data = value.GetType().GetProperty("Value")?.GetValue(value);
            if (data is string s)
                return s;

            return data?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private int? ReadVolume()
    {
        if (!TryConnect() || _endpointVolume is null)
            return null;

        try
        {
            var getMaster = _endpointVolume.GetType().GetMethod("GetMasterVolumeLevelScalar");
            if (getMaster is null)
                return null;

            var args = new[] { (object)0f };
            getMaster.Invoke(_endpointVolume, args);
            var scalar = Convert.ToSingle(args[0]);
            return (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * MaxVolume);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read the master volume level");
            return null;
        }
    }

    private bool? ReadMuted()
    {
        if (!TryConnect() || _endpointVolume is null)
            return null;

        try
        {
            var getMute = _endpointVolume.GetType().GetMethod("GetMute");
            if (getMute is null)
                return null;

            var args = new[] { (object)0f };
            getMute.Invoke(_endpointVolume, args);
            return Convert.ToSingle(args[0]) > 0.5f;
        }
        catch
        {
            return null;
        }
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
    {
        var repeats = Math.Clamp(step <= 0 ? DefaultVolumeStep : step, 1, 25);
        return SendVolumeKey(WinConst.VK_VOLUME_UP, repeats, "VolumeUp");
    }

    public MediaActionResponse VolumeDown(int step)
    {
        var repeats = Math.Clamp(step <= 0 ? DefaultVolumeStep : step, 1, 25);
        return SendVolumeKey(WinConst.VK_VOLUME_DOWN, repeats, "VolumeDown");
    }

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
    public static MediaActionResponse SendVolumeKeyFromSession(ushort vk, int repeats, string action)
    {
        var clamped = Math.Clamp(repeats <= 0 ? DefaultVolumeStep : repeats, 1, 25);
        var events = new List<INPUT>(clamped * 2);
        for (var i = 0; i < clamped; i++)
        {
            events.Add(KeyEvent(vk, down: true));
            events.Add(KeyEvent(vk, down: false));
        }

        var sent = Native.USER32.SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<INPUT>());
        return new MediaActionResponse
        {
            Accepted = sent == events.Count,
            Action = action,
            Detail = sent == events.Count
                ? null
                : $"SendInput delivered {sent} of {events.Count} events from the session agent " +
                  $"(error {Marshal.GetLastWin32Error()}).",
        };
    }

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

    private MediaActionResponse SendVolumeKey(ushort vk, int repeats, string action)
    {
        var events = new List<INPUT>(repeats * 2);
        for (var i = 0; i < repeats; i++)
        {
            events.Add(KeyEvent(vk, down: true));
            events.Add(KeyEvent(vk, down: false));
        }

        var sent = Native.USER32.SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != events.Count)
        {
            return new MediaActionResponse
            {
                Accepted = false,
                Action = action,
                Detail = $"SendInput delivered {sent} of {events.Count} events (error {Marshal.GetLastWin32Error()}). " +
                         "Volume keys must be injected from the interactive desktop.",
            };
        }

        return new MediaActionResponse
        {
            Accepted = true,
            Action = action,
            Volume = ReadVolume(),
            IsMuted = ReadMuted(),
        };
    }

    public MediaActionResponse Mute(bool muted)
    {
        var current = ReadMuted();
        if (current == muted)
        {
            return new MediaActionResponse { Accepted = true, Action = muted ? "Mute" : "Unmute", IsMuted = current, Detail = "Already in the requested state." };
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

        // Walk to the target with real key presses so every consumer sees the change.
        var delta = target - current.Value;
        var step = delta > 0 ? VolumeUp(Math.Min(25, delta)) : VolumeDown(Math.Min(25, -delta));

        return new MediaActionResponse
        {
            Accepted = step.Accepted,
            Action = "volumeSet",
            Volume = ReadVolume(),
            IsMuted = ReadMuted(),
            Detail = step.Accepted ? null : step.Detail,
        };
    }

    public MediaStateResponse GetState()
    {
        TryConnect();

        return new MediaStateResponse
        {
            Volume = ReadVolume() ?? 0,
            IsMuted = ReadMuted() ?? false,
            DefaultDevice = _deviceName,
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

    // ---- PROPERTYKEY helper ------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public PropertyKey(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid)
        {
            fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            this.pid = pid;
        }
    }
}
