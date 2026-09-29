using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Telemetry;

/// <summary>
/// NVIDIA Management Library (NVML) access, loaded dynamically.
/// <para>
/// NVML is NVIDIA's documented C interface for GPU telemetry. It is loaded with
/// <c>NativeLibrary.TryLoad</c> rather than a static <c>DllImport</c> so the agent runs
/// unchanged on machines with no NVIDIA GPU, no driver, or a driver that is too old to
/// install nvml.dll.
/// </para>
/// <para>
/// The DLL is looked for in the documented locations, in order:
/// <c>%SystemRoot%\System32\nvml.dll</c>, then
/// <c>%ProgramFiles%\NVIDIA Corporation\NVSMI\nvml.dll</c>.
/// </para>
/// <para>
/// Only stable, documented entry points are used: <c>nvmlInit_v2</c>,
/// <c>nvmlDeviceGetCount_v2</c>, <c>nvmlDeviceGetHandleByIndex_v2</c>,
/// <c>nvmlDeviceGetName</c>, <c>nvmlDeviceGetUtilizationRates</c>,
/// <c>nvmlDeviceGetTemperature</c>, <c>nvmlDeviceGetFanSpeed</c>,
/// <c>nvmlDeviceGetMemoryInfo</c>, <c>nvmlDeviceGetPowerUsage</c>,
/// <c>nvmlDeviceGetClockInfo</c> and <c>nvmlShutdown</c>. Every entry point is bound to an
/// explicitly declared <c>[UnmanagedFunctionPointer]</c> signature, so a driver exporting
/// something different simply fails to bind and is reported as unavailable instead of
/// corrupting the stack.
/// </para>
/// </summary>
public sealed class NvmlProvider : IDisposable
{
    private const int NVML_SUCCESS = 0;

    /// <summary>NVML_TEMPERATURE_GPU, the sensor selector for the GPU die temperature.</summary>
    private const int NVML_TEMPERATURE_GPU = 0;

    private const int NameBufferBytes = 96;

    private readonly ILogger<NvmlProvider> _log;

    private nint _module;
    private bool _initialised;
    private bool _shutdown;
    private bool _unavailable;
    private string? _unavailableReason;

    // ---- NVML entry points -------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetHandleByIndex(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetName(nint device, [Out] byte[] name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetUtilization(nint device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetTemperature(nint device, int sensor, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetUint(nint device, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetMemory(nint device, out NvmlMemory memory);

    private NvmlInit? _init;
    private NvmlShutdown? _shutdownFn;
    private NvmlGetCount? _getCount;
    private NvmlGetHandleByIndex? _getHandleByIndex;
    private NvmlGetName? _getName;
    private NvmlGetUtilization? _getUtilization;
    private NvmlGetTemperature? _getTemperature;
    private NvmlGetUint? _getFanSpeed;
    private NvmlGetMemory? _getMemoryInfo;
    private NvmlGetUint? _getPowerUsage;
    private NvmlGetUint? _getClockInfo;

    // ---- NVML ABI ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        /// <summary>Percent of the last sample period the GPU was busy, 0-100.</summary>
        public uint gpu;

        /// <summary>Percent of the last sample period the memory controller was busy, 0-100.</summary>
        public uint memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong total;
        public ulong free;
        public ulong used;
    }

    public NvmlProvider(ILogger<NvmlProvider> log) => _log = log;

    /// <summary>Whether NVML is loaded and initialised.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureInitialised();
            return _initialised;
        }
    }

    /// <summary>Why NVML is unavailable, surfaced in the agent's diagnostics.</summary>
    public string? UnavailableReason => _unavailableReason;

    /// <summary>Telemetry for one NVIDIA device.</summary>
    public sealed class DeviceInfo
    {
        public int Index { get; init; }
        public string? Name { get; set; }
        public uint UsagePercent { get; set; }
        public uint MemoryUsagePercent { get; set; }
        public uint TemperatureC { get; set; }
        public uint FanPercent { get; set; }
        public long MemoryUsedBytes { get; set; }
        public long MemoryTotalBytes { get; set; }
        public long MemoryFreeBytes { get; set; }
        public double? PowerWatts { get; set; }
        public int? ClockMhz { get; set; }
    }

    // ---- Public API --------------------------------------------------------------

    public DeviceInfo[] GetDevices()
    {
        if (!EnsureInitialised() || _shutdown)
            return [];

        if (_getCount is null || _getHandleByIndex is null)
            return [];

        try
        {
            if (_getCount(out var count) != NVML_SUCCESS || count == 0)
                return [];

            var devices = new List<DeviceInfo>((int)count);

            for (uint i = 0; i < count; i++)
            {
                if (_getHandleByIndex(i, out var handle) != NVML_SUCCESS || handle == IntPtr.Zero)
                    continue;

                var info = new DeviceInfo { Index = (int)i };

                // nvmlDeviceGetName writes a NUL-terminated ASCII name into a caller buffer.
                if (_getName is not null)
                {
                    var nameBuffer = new byte[NameBufferBytes];
                    if (_getName(handle, nameBuffer, NameBufferBytes) == NVML_SUCCESS)
                    {
                        var nul = Array.IndexOf(nameBuffer, (byte)0);
                        var bytes = nul >= 0 ? nameBuffer.AsSpan(0, nul) : nameBuffer.AsSpan();
                        if (!bytes.IsEmpty)
                            info.Name = Encoding.ASCII.GetString(bytes);
                    }
                }

                if (_getUtilization is not null &&
                    _getUtilization(handle, out var util) == NVML_SUCCESS)
                {
                    // Both fields reading zero means the driver has no sample yet, which
                    // is normal in the first second after load or after a GPU reset.
                    if (util.gpu <= 100)
                        info.UsagePercent = util.gpu;
                    if (util.memory <= 100)
                        info.MemoryUsagePercent = util.memory;
                }

                if (_getTemperature is not null &&
                    _getTemperature(handle, NVML_TEMPERATURE_GPU, out var temp) == NVML_SUCCESS &&
                    temp is > 0 and < 200)
                {
                    info.TemperatureC = temp;
                }

                if (_getFanSpeed is not null &&
                    _getFanSpeed(handle, out var fan) == NVML_SUCCESS &&
                    fan is > 0 and <= 100)
                {
                    info.FanPercent = fan;
                }

                if (_getMemoryInfo is not null &&
                    _getMemoryInfo(handle, out var memory) == NVML_SUCCESS)
                {
                    info.MemoryUsedBytes = (long)memory.used;
                    info.MemoryTotalBytes = (long)memory.total;
                    info.MemoryFreeBytes = (long)memory.free;
                }

                if (_getPowerUsage is not null &&
                    _getPowerUsage(handle, out var milliwatts) == NVML_SUCCESS &&
                    milliwatts > 0)
                {
                    info.PowerWatts = milliwatts / 1000.0;
                }

                if (_getClockInfo is not null &&
                    _getClockInfo(handle, out var clockMhz) == NVML_SUCCESS &&
                    clockMhz > 0)
                {
                    info.ClockMhz = (int)clockMhz;
                }

                devices.Add(info);
            }

            return devices.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NVML telemetry collection failed; the agent will fall back to PDH GPU counters");
            return [];
        }
    }

    // ---- Dynamic loading ---------------------------------------------------------

    private bool EnsureInitialised()
    {
        if (_initialised)
            return true;
        if (_unavailable)
            return false;

        try
        {
            var path = LocateNvml();
            if (path is null)
            {
                _unavailable = true;
                _unavailableReason = "nvml.dll not found: no NVIDIA driver, or the driver does not install it.";
                return false;
            }

            if (!NativeLibrary.TryLoad(path, out var module))
            {
                _unavailable = true;
                _unavailableReason = $"nvml.dll at {path} could not be loaded.";
                return false;
            }

            _module = module;

            if (!BindAll())
            {
                _unavailable = true;
                _unavailableReason = "The installed NVIDIA driver does not export the documented NVML entry points this agent uses.";
                ReleaseModule();
                return false;
            }

            if (_init!() != NVML_SUCCESS)
            {
                _unavailable = true;
                _unavailableReason = "nvmlInit_v2 did not report success.";
                ReleaseModule();
                return false;
            }

            _initialised = true;
            _log.LogInformation("NVML initialised from {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _unavailableReason = ex.Message;
            _log.LogDebug(ex, "NVML is unavailable; GPU telemetry will use PDH counters only");
            return false;
        }
    }

    private bool BindAll()
    {
        // Every one of these is a documented, stable NVML entry point. If any is missing
        // the whole provider is disabled rather than partially working, so the caller
        // gets one unambiguous answer. A && chain would leave the later locals unassigned
        // on an early failure, so each result is accumulated instead.
        var allBound = true;

        allBound &= TryBind<NvmlInit>("nvmlInit_v2", out var init);
        allBound &= TryBind<NvmlShutdown>("nvmlShutdown", out var shutdown);
        allBound &= TryBind<NvmlGetCount>("nvmlDeviceGetCount_v2", out var count);
        allBound &= TryBind<NvmlGetHandleByIndex>("nvmlDeviceGetHandleByIndex_v2", out var handleByIndex);
        allBound &= TryBind<NvmlGetName>("nvmlDeviceGetName", out var name);
        allBound &= TryBind<NvmlGetUtilization>("nvmlDeviceGetUtilizationRates", out var utilization);
        allBound &= TryBind<NvmlGetTemperature>("nvmlDeviceGetTemperature", out var temperature);
        allBound &= TryBind<NvmlGetUint>("nvmlDeviceGetFanSpeed", out var fan);
        allBound &= TryBind<NvmlGetMemory>("nvmlDeviceGetMemoryInfo", out var memory);
        allBound &= TryBind<NvmlGetUint>("nvmlDeviceGetPowerUsage", out var power);
        allBound &= TryBind<NvmlGetUint>("nvmlDeviceGetClockInfo", out var clock);

        if (!allBound)
            return false;

        _init = init;
        _shutdownFn = shutdown;
        _getCount = count;
        _getHandleByIndex = handleByIndex;
        _getName = name;
        _getUtilization = utilization;
        _getTemperature = temperature;
        _getFanSpeed = fan;
        _getMemoryInfo = memory;
        _getPowerUsage = power;
        _getClockInfo = clock;
        return true;
    }

    private bool TryBind<T>(string exportName, out T? typed) where T : Delegate
    {
        typed = null;
        if (!NativeLibrary.TryGetExport(_module, exportName, out var address) || address == IntPtr.Zero)
        {
            _log.LogDebug("nvml.dll does not export {Export}", exportName);
            return false;
        }

        typed = Marshal.GetDelegateForFunctionPointer<T>(address);
        return true;
    }

    private static string? LocateNvml()
    {
        var candidates = new List<string>();

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system))
            candidates.Add(Path.Combine(system, "nvml.dll"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, @"NVIDIA Corporation\NVSMI\nvml.dll"));
            candidates.Add(Path.Combine(programFiles, @"NVIDIA Corporation\NVSMI\nvml64.dll"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private void ReleaseModule()
    {
        if (_module == IntPtr.Zero)
            return;

        try
        {
            NativeLibrary.Free(_module);
        }
        catch
        {
            // Nothing useful to do.
        }

        _module = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_initialised && !_shutdown)
        {
            try
            {
                _shutdownFn?.Invoke();
            }
            catch
            {
                // Nothing useful to do; the handle is released regardless.
            }
        }

        _shutdown = true;
        _initialised = false;
        ReleaseModule();
    }
}
