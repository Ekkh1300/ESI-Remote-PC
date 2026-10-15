using System.Runtime.InteropServices;

namespace Esi.Platform.Native;

// ---------------------------------------------------------------------------------
// Core Audio interop surface, written by hand against the published vtable layouts.
//
//   mmdeviceapi.h   IMMDeviceEnumerator, IMMDevice
//   endpointvolume.h   IAudioEndpointVolume
//   propidl.h       PROPVARIANT, PropertyKey
//
// The reason this is hand-written rather than reached for with reflection is the reason it
// exists at all. The previous version built the COM objects with Activator.CreateInstance and
// then called them with `GetType().GetMethod("GetDefaultAudioEndpoint").Invoke(...)`. That
// pattern works for automation servers and for nothing else, and Core Audio is not one:
// every interface here derives from IUnknown only, so
//
//     enumerator.GetType().GetMethod("GetDefaultAudioEndpoint")   => null
//     enumerator.GetType().InvokeMember(...)                     => "COM target does not implement IDispatch"
//
// Both of those were measured on a live machine, not inferred. The call returned null before
// Core Audio was ever touched, which meant the volume could never be read, which meant
// GetState() reported 0 for a PC sitting at 100 - and, worse, that "move to 30" was computed
// as a walk of 30 volume-up presses from a base of 0, so the volume never arrived anywhere
// near the number the phone had asked for.
//
// Every method is [PreserveSig] so the native HRESULT comes back as the return value instead
// of being turned into an exception by the marshaler. The read paths want to *know* that a
// call failed; converting that knowledge into a throw and catching it again is how the old
// code ended up silently reporting 0.
// ---------------------------------------------------------------------------------

/// <summary>Where an endpoint renders. Only Render (0) is used here.</summary>
internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

/// <summary>Which role the endpoint is the default for.</summary>
internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

    /// <summary>The default endpoint for eRender/eConsole - the one the volume slider drives.</summary>
    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>
    /// The one call needed here: ask the endpoint for a specific interface by IID.
    /// <paramref name="clsCtx"/> is <c>CLSCTX_ALL</c>.
    /// </summary>
    [PreserveSig]
    int Activate(
        ref Guid iid,
        uint clsCtx,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore store);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

/// <summary>
/// The master volume and mute state of an endpoint.
/// <para>
/// Declared in full rather than trimmed to the two members that are read, because a COM
/// interface is a vtable and a vtable is positional: a missing declaration shifts every
/// method after it, and the shift shows up as a plausible wrong answer rather than as an
/// error. Same reason <see cref="IMMDevice"/> lists its unused members.
/// </para>
/// </summary>
[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float level);

    /// <summary>The default output device's volume, 0.0 to 1.0. This is the number the phone shows.</summary>
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute(int mute, IntPtr eventContext);

    /// <summary>Non-zero when muted. BOOL is 4 bytes, so this is an int and not a bool.</summary>
    [PreserveSig] int GetMute(out int mute);

    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(IntPtr eventContext);
    [PreserveSig] int VolumeStepDown(IntPtr eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float step);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

/// <summary><c>REFPROPERTYKEY</c>: a format GUID plus a property id.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid Fmtid;
    public uint Pid;

    public PropertyKey(Guid fmtid, uint pid)
    {
        Fmtid = fmtid;
        Pid = pid;
    }

    /// <summary><c>PKEY_Device_FriendlyName</c> - the name Windows shows for the device.</summary>
    public static readonly PropertyKey DeviceFriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
}

/// <summary>
/// <c>PROPVARIANT</c>: a 16-byte tag followed by a union.
/// <para>
/// Explicit layout rather than automatic, because automatic layout cannot express "a tag at 0
/// and a value at 8". Only the two shapes this file reads are modelled: VT_LPWSTR (a string
/// pointer) and the numeric types the tag selects. Anything else is reported as unreadable
/// rather than misread.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;
    [FieldOffset(8)] public float floatValue;
    [FieldOffset(8)] public int intValue;

    /// <summary><c>VT_LPWSTR</c>, the variant type of a device friendly name.</summary>
    public const ushort VtLpwStr = 31;
}

/// <summary>
/// Opens the PC's default audio output endpoint and reads its volume and mute state.
/// <para>
/// A read-only window onto one endpoint, opened once and held. Holding it is deliberate: the
/// endpoint is the *default* endpoint at the moment of the call, and re-opening on every read
/// would silently start reporting a different device after the user switched outputs - which
/// would show as the slider jumping rather than as anything obviously wrong.
/// </para>
/// </summary>
internal sealed class CoreAudioEndpoint : IDisposable
{
    private const uint ClsCtxAll = 0x17;
    private const uint StgmRead = 0x00000000;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private readonly IAudioEndpointVolume _volume;
    private readonly IMMDevice _device;

    private CoreAudioEndpoint(
        IMMDevice device,
        IAudioEndpointVolume volume,
        string? friendlyName,
        string? deviceId)
    {
        _device = device;
        _volume = volume;
        FriendlyName = friendlyName;
        DeviceId = deviceId;
    }

    /// <summary>The user-visible name of the endpoint, e.g. "Speakers (Realtek(R) Audio)".</summary>
    public string? FriendlyName { get; }

    /// <summary>
    /// Core Audio's stable device identifier, or null when it could not be read.
    /// <para>
    /// This is what makes an endpoint comparable to the *current* default. The friendly name
    /// is not: two devices can share one, and a headset's name changes with its connection
    /// state.
    /// </para>
    /// </summary>
    public string? DeviceId { get; }

    /// <summary>
    /// The default render endpoint, or null when there is none.
    /// <para>
    /// Null is the expected outcome for a service in session 0: it has no interactive desktop
    /// and Core Audio hands it no endpoint at all (D43). Every caller treats null as "unknown",
    /// which is the whole point - the previous code turned unknown into a confident 0.
    /// </para>
    /// </summary>
    public static CoreAudioEndpoint? TryOpen()
    {
        try
        {
            var coClass = Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;

            if (Activator.CreateInstance(coClass) is not IMMDeviceEnumerator enumerator)
                return null;

            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device) != 0
                || device is null)
                return null;

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var raw) != 0
                || raw is not IAudioEndpointVolume volume)
                return null;

            return new CoreAudioEndpoint(device, volume, ReadFriendlyName(device), ReadDeviceId(device));
        }
        catch
        {
            // No endpoint, no endpoint volume interface, or not on Windows at all. The caller
            // reports the volume as unknown; that is a fact about the machine, not a fault.
            return null;
        }
    }

    /// <summary>
    /// The machine's current default render device id, or null when there is none.
    /// </summary>
    internal static string? TryDefaultDeviceId()
    {
        try
        {
            var coClass = Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;

            if (Activator.CreateInstance(coClass) is not IMMDeviceEnumerator enumerator)
                return null;

            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device) != 0
                || device is null)
                return null;

            return ReadDeviceId(device);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this endpoint is still the device Windows would send a volume key to.
    /// </summary>
    /// <para>
    /// The default device is not a constant for the life of a process. A Bluetooth headset
    /// that connects takes over as the default; unplugging a USB DAC hands it back to the
    /// speakers. An endpoint opened before that still names the old device, so moving *its*
    /// volume changes a device nobody is listening to - which is what "the volume only affects
    /// one output" looks like from the phone, where the headset is the one being heard.
    /// </para>
    public bool IsStillDefault()
    {
        var current = TryDefaultDeviceId();
        return current is not null
            && DeviceId is not null
            && string.Equals(current, DeviceId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Releases the two COM references this endpoint holds, so a machine that swaps devices
    /// often does not accumulate runtime callable wrappers until the process is restarted.
    /// </summary>
    public void Release()
    {
        try
        {
            Marshal.ReleaseComObject(_volume);
        }
        catch
        {
            // Already released, or the device was unplugged out from under it.
        }

        try
        {
            Marshal.ReleaseComObject(_device);
        }
        catch
        {
            // Same.
        }
    }

    private static string? ReadDeviceId(IMMDevice device)
    {
        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The master volume, 0.0 to 1.0, or null if the call failed.</summary>
    public float? ReadVolumeScalar()
    {
        try
        {
            return _volume.GetMasterVolumeLevelScalar(out var level) == 0
                ? Math.Clamp(level, 0f, 1f)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether the endpoint is muted, or null if the call failed.</summary>
    public bool? ReadMuted()
    {
        try
        {
            return _volume.GetMute(out var mute) == 0 && mute != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the master volume directly, 0.0 to 1.0.
    /// <para>
    /// This is how an exact level is reached, and it replaced a walk of N volume-key presses for
    /// an N-point change. The walk was wrong twice over, in a way that matched exactly what the
    /// user reported as the volume "jumping":
    /// </para>
    /// <list type="number">
    /// <item>It assumed one press is one percentage point. Windows reports 51 steps across
    /// 0-100 on this machine, so one press moves ~1.96 points and a 45-press walk covers ~88.
    /// A request for 55 from 100 landed at 10 - roughly double the intended distance.</item>
    /// <item>It took long enough, and the level kept moving long enough afterwards, that the
    /// read *after* the presses saw a volume still in motion. The phone asked for 55, was told
    /// 82, and the PC settled on 10.</item>
    /// </list>
    /// <para>
    /// One COM call has neither problem: it lands in one step and the next read is already
    /// final.
    /// </para>
    /// <para>
    /// A side effect worth naming: no key press, so no Windows volume overlay. Every consumer
    /// still sees the change, because this is the endpoint's own volume rather than a private
    /// copy of it - which is more than the walk could honestly claim if it had overshot.
    /// </para>
    /// </summary>
    public bool WriteVolumeScalar(float level)
    {
        try
        {
            return _volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), IntPtr.Zero) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the endpoint's mute flag directly.
    /// <para>
    /// A mute *key* is a toggle, so pressing it while already muted unmutes - and the old code
    /// could not tell, because a process that cannot read the flag cannot compare it to the
    /// requested state. Writing the flag makes the request idempotent regardless of what the
    /// read does.
    /// </para>
    /// </summary>
    public bool WriteMuted(bool muted)
    {
        try
        {
            return _volume.SetMute(muted ? 1 : 0, IntPtr.Zero) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// How many volume steps the endpoint reports across its whole range, or null if the call
    /// failed. This is what makes a *key walk* measurable rather than assumed: 100 divided by
    /// this number is how far one press actually moves.
    /// </summary>
    public int? ReadStepCount()
    {
        try
        {
            return _volume.GetVolumeStepInfo(out _, out var count) == 0 && count > 0
                ? (int)count
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0 || store is null)
            return null;

        var key = PropertyKey.DeviceFriendlyName;
        PropVariant value = default;
        try
        {
            if (store.GetValue(ref key, out value) != 0)
                return null;

            // VT_LPWSTR is the only type a device name arrives as. Anything else means the
            // property store did something this file did not expect, and saying so is better
            // than printing the union's memory.
            return value.vt == PropVariant.VtLpwStr && value.pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.pointerValue)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            // The variant owns an unmanaged string; leaving it to the GC leaks it, and this
            // runs on every connect.
            PropVariantClear(ref value);
        }
    }

    public void Dispose()
    {
        if (Marshal.IsComObject(_volume))
            Marshal.ReleaseComObject(_volume);
        if (Marshal.IsComObject(_device))
            Marshal.ReleaseComObject(_device);
    }
}
