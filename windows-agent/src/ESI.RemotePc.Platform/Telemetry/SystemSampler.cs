using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Telemetry;

/// <summary>Snapshot of the CPU time accounting from <c>GetSystemTimes</c>.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User)
{
    /// <summary>
    /// Busy percentage between two samples.
    /// <para>
    /// The subtlety that matters: <c>lpKernelTime</c> <b>already includes idle time</b>.
    /// The correct formula therefore subtracts idle from kernel before comparing against
    /// the total, and the result is <em>not</em> divided by the core count because the
    /// values are already summed across all processors.
    /// </para>
    /// </summary>
    public double BusyPercentSince(in CpuTimes previous)
    {
        var dIdle = Idle - previous.Idle;
        var dKernel = Kernel - previous.Kernel;
        var dUser = User - previous.User;

        if (dIdle == 0 && dKernel == 0 && dUser == 0)
            return 0;

        var dBusy = (dKernel - dIdle) + dUser;
        var dTotal = dKernel + dUser;

        if (dTotal == 0)
            return 0;

        var pct = 100.0 * dBusy / dTotal;
        return Math.Clamp(pct, 0, 100);
    }

    public double UserPercentSince(in CpuTimes previous)
    {
        var dUser = User - previous.User;
        var dTotal = (Kernel - previous.Kernel) + (User - previous.User);
        return dTotal == 0 ? 0 : Math.Clamp(100.0 * dUser / dTotal, 0, 100);
    }

    public double KernelPercentSince(in CpuTimes previous)
    {
        var dIdle = Idle - previous.Idle;
        var dKernel = (Kernel - previous.Kernel) - dIdle;
        var dTotal = (Kernel - previous.Kernel) + (User - previous.User);
        return dTotal == 0 ? 0 : Math.Clamp(100.0 * dKernel / dTotal, 0, 100);
    }
}

/// <summary>
/// Samples CPU, memory, network and process counters at a fixed cadence.
/// <para>
/// Two facts from the Win32 documentation shape this class:
/// </para>
/// <list type="bullet">
///   <item><description>Rate counters need two samples before they mean anything, so the
///   first <c>Collect()</c> only establishes a baseline and returns zeroes.</description></item>
///   <item><description>PDH is unavailable to non-elevated callers in some
///   configurations (<c>PdhCollectQueryData</c> returns <c>PDH_NO_DATA</c>), so every
///   PDH-sourced value degrades to <c>null</c> and the native Win32 paths are used instead
///   wherever one exists.</description></item>
/// </list>
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private readonly ILogger<SystemSampler> _log;
    private readonly PdhQuery _pdh;
    private readonly bool _collectGpu;
    private readonly bool _collectTemperatures;

    private CpuTimes _previousCpu;
    private bool _hasCpuBaseline;

    // Monotonic baselines for interface byte counters; NICStatistics gives cumulative
    // totals, so a rate needs a previous sample just like a PDH rate counter.
    private readonly Dictionary<string, (long Rx, long Tx)> _previousNicCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Read, long Write)> _previousDiskCounters = new(StringComparer.OrdinalIgnoreCase);

    private bool _gpuEngineAvailable;
    private bool _gpuEngineProbed;

    public SystemSampler(
        ILogger<SystemSampler> log,
        PdhQuery pdh,
        bool collectGpu = true,
        bool collectTemperatures = true)
    {
        _log = log;
        _pdh = pdh;
        _collectGpu = collectGpu;
        _collectTemperatures = collectTemperatures;

        _pdh.AddCounter(PdhCounters.TotalProcessorTime, "cpu.total");
        _pdh.AddCounter(PdhCounters.AvailableMBytes, "mem.available");
        _pdh.AddCounter(PdhCounters.CacheBytes, "mem.cache");
        _pdh.AddWildcardValueCounter(PdhCounters.NetworkBytesSent, "net.tx");
        _pdh.AddWildcardValueCounter(PdhCounters.NetworkBytesReceived, "net.rx");
        _pdh.AddCounter(PdhCounters.DiskReadBytes, "disk.read");
        _pdh.AddCounter(PdhCounters.DiskWriteBytes, "disk.write");
        _pdh.AddWildcardValueCounter(PdhCounters.DiskReadBytesAll, "disk.readAll");
        _pdh.AddWildcardValueCounter(PdhCounters.DiskWriteBytesAll, "disk.writeAll");

        // A rate counter needs a core to be attributable to; on a multi-core box the
        // aggregate is the average across cores, which is what the dashboard wants.
        var coreCount = Environment.ProcessorCount;
        for (var i = 0; i < coreCount && i < 256; i++)
        {
            _pdh.AddCounter(string.Format(PdhCounters.ProcessorTimeTemplate, i.ToString()), "cpu.core." + i);
        }
    }

    /// <summary>Number of samples taken so far. The first sample returns zeroes.</summary>
    public bool HasBaseline => _hasCpuBaseline;

    /// <summary>Takes a PDH sample. Call once per sampling interval, before reading values.</summary>
    public void Collect()
    {
        if (_collectGpu)
            ProbeGpuEngineOnce();

        _pdh.Collect();
    }

    /// <summary>
    /// <c>\GPU Engine(*)\Utilization Percentage</c> is not documented on
    /// learn.microsoft.com even though Windows ships it, so its availability is probed at
    /// runtime rather than assumed.
    /// </summary>
    private void ProbeGpuEngineOnce()
    {
        if (_gpuEngineProbed)
            return;

        _gpuEngineProbed = true;

        // Availability is the add call itself: PdhAddEnglishCounter succeeds exactly where
        // the counter object exists, in any locale, and that is the whole question. The
        // wildcard *expand* is not the probe, because measured on this machine it reports
        // one row where the same counter's read enumerates 541 - a call whose answer varies
        // that far from the thing it describes decides nothing. It stays in the log only.
        _gpuEngineAvailable = _pdh.AddWildcardValueCounter(PdhCounters.GpuEngineUtilization, "gpu.engine");
        if (_gpuEngineAvailable)
        {
            _pdh.AddWildcardValueCounter(PdhCounters.GpuAdapterMemoryShared, "gpu.sharedMemory");
            _pdh.AddWildcardValueCounter(PdhCounters.GpuAdapterMemoryDedicated, "gpu.dedicatedMemory");
        }

        _log.LogInformation(
            "GPU Engine performance counter: {State}. GPU load will {Fallback}.",
            _gpuEngineAvailable ? "available" : "not available",
            _gpuEngineAvailable ? "come from the per-engine counters" : "come from vendor APIs (NVML) or be reported as unavailable");
    }

    public bool IsGpuEngineCounterAvailable => _gpuEngineAvailable;

    // -----------------------------------------------------------------------------
    // CPU
    // -----------------------------------------------------------------------------

    public double SampleCpuBusyPercent(out double userPercent, out double kernelPercent)
    {
        userPercent = 0;
        kernelPercent = 0;

        if (!Native.KERNEL32.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var current = new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());

        if (!_hasCpuBaseline)
        {
            _previousCpu = current;
            _hasCpuBaseline = true;
            return 0;
        }

        var busy = current.BusyPercentSince(_previousCpu);
        userPercent = current.UserPercentSince(_previousCpu);
        kernelPercent = current.KernelPercentSince(_previousCpu);
        _previousCpu = current;

        return busy;
    }

    public List<double> SamplePerCorePercentages()
    {
        var result = new List<double>();
        for (var i = 0; i < 256; i++)
        {
            var value = _pdh.ReadDouble("cpu.core." + i);
            if (value is null)
                break;
            result.Add(Math.Clamp(value.Value, 0, 100));
        }

        return result;
    }

    // -----------------------------------------------------------------------------
    // Memory
    // -----------------------------------------------------------------------------

    public MemorySnapshot SampleMemory()
    {
        var status = new MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>(),
        };

        if (!Native.KERNEL32.GlobalMemoryStatusEx(ref status))
            return new MemorySnapshot();

        var total = (long)status.ullTotalPhys;
        var available = (long)status.ullAvailPhys;
        var used = Math.Max(0, total - available);

        // Commit figures come from GetPerformanceInfo, which is the documented way to get
        // system-wide commit limits; GlobalMemoryStatusEx's page-file numbers are
        // per-process for the virtual fields.
        long? committed = null;
        long? commitLimit = null;
        if (Native.KERNEL32.GetPerformanceInfo(out var perf, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        {
            committed = (long)perf.CommitTotal;
            commitLimit = (long)perf.CommitLimit;
        }

        long? cache = _pdh.ReadLarge("mem.cache");

        return new MemorySnapshot
        {
            TotalBytes = total,
            UsedBytes = used,
            AvailableBytes = available,
            UsedPercent = total > 0 ? Math.Round(used * 100.0 / total, 2) : 0,
            CachedBytes = cache,
            CommittedBytes = committed,
            CommitLimitBytes = commitLimit,
            LoadPercent = status.dwMemoryLoad,
        };
    }

    public readonly record struct MemorySnapshot
    {
        public long TotalBytes { get; init; }
        public long UsedBytes { get; init; }
        public long AvailableBytes { get; init; }
        public double UsedPercent { get; init; }
        public long? CachedBytes { get; init; }
        public long? CommittedBytes { get; init; }
        public long? CommitLimitBytes { get; init; }
        public uint LoadPercent { get; init; }
    }

    public ProcessAndThreadCounts SampleProcessCounts()
    {
        if (Native.KERNEL32.GetPerformanceInfo(out var perf, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            return new ProcessAndThreadCounts((int)perf.ProcessCount, (int)perf.ThreadCount);

        return new ProcessAndThreadCounts(0, 0);
    }

    public readonly record struct ProcessAndThreadCounts(int Processes, int Threads);

    public long? SampleUptimeSeconds() => (long)TimeSpan.FromMilliseconds(Native.KERNEL32.GetTickCount64()).TotalSeconds;

    // -----------------------------------------------------------------------------
    // Network rates
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Per-interface upload/download rates in bytes per second, derived by differencing
    /// the cumulative <see cref="IPv4InterfaceStatistics"/> counters.
    /// </summary>
    public Dictionary<string, (long Down, long Up, long RxTotal, long TxTotal)> SampleNetworkRates(
        TimeSpan interval)
    {
        var seconds = Math.Max(0.001, interval.TotalSeconds);
        var result = new Dictionary<string, (long, long, long, long)>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPv4InterfaceStatistics stats;
            try
            {
                stats = nic.GetIPv4Statistics();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            long down = 0;
            long up = 0;

            if (_previousNicCounters.TryGetValue(nic.Id, out var previous))
            {
                down = Math.Max(0, stats.BytesReceived - previous.Rx);
                up = Math.Max(0, stats.BytesSent - previous.Tx);
            }

            _previousNicCounters[nic.Id] = (stats.BytesReceived, stats.BytesSent);

            result[nic.Id] = (
                (long)(down / seconds),
                (long)(up / seconds),
                stats.BytesReceived,
                stats.BytesSent);
        }

        return result;
    }

    /// <summary>Per-instance network throughput straight from the PDH counters, when available.</summary>
    public IReadOnlyDictionary<string, double> SamplePdhNetworkRates()
        => _pdh.ReadWildcardDouble("net.rx");

    // -----------------------------------------------------------------------------
    // Disk rates
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Per-physical-disk read/write rates in bytes per second, from the documented
    /// <c>\PhysicalDisk(*)\Disk Read Bytes/sec</c> and <c>Disk Write Bytes/sec</c>
    /// counters. There is no managed equivalent: <c>DriveInfo</c> exposes capacity but not
    /// I/O totals.
    /// </summary>
    public IReadOnlyDictionary<string, DiskRate> SampleDiskRates()
    {
        var reads = _pdh.ReadWildcardDouble("disk.readAll", WinConst.PDH_FMT_DOUBLE);
        var writes = _pdh.ReadWildcardDouble("disk.writeAll", WinConst.PDH_FMT_DOUBLE);

        var result = new Dictionary<string, DiskRate>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instance, read) in reads)
        {
            writes.TryGetValue(instance, out var write);
            result[ExtractDiskKey(instance)] = new DiskRate((long)read, (long)write);
        }

        return result;
    }

    /// <summary>Aggregate disk I/O for the whole machine, in bytes per second.</summary>
    public DiskRate SampleAggregateDiskRate() => new(
        (long)(_pdh.ReadDouble("disk.read") ?? 0),
        (long)(_pdh.ReadDouble("disk.write") ?? 0));

    /// <summary>Strips the instance-number prefix from a PDH PhysicalDisk instance name.</summary>
    public static string ExtractDiskKey(string instanceName)
    {
        // e.g. "0 C:\" or "_Total"
        var space = instanceName.IndexOf(' ');
        return space > 0 ? instanceName[(space + 1)..] : instanceName;
    }

    public readonly record struct DiskRate(long ReadBytesPerSecond, long WriteBytesPerSecond);

    // -----------------------------------------------------------------------------
    // GPU
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Per-adapter GPU load aggregated from the <c>\GPU Engine(*)\Utilization
    /// Percentage</c> instances.
    /// <para>
    /// Instance names look like <c>luid_0x00000000_0x0000ABCD_engtype_3D</c> or
    /// <c>luid_0x00000000_0x0000ABCD_eng_3</c>. The first two LUIDs identify the physical
    /// adapter, so instances are grouped by that prefix and summed per adapter.
    /// <c>PDH_FMT_NOCAP100</c> is essential: multi-engine instances routinely exceed 100 %
    /// and must not be clamped before summing.
    /// </para>
    /// </summary>
    public List<(string AdapterLuid, double UsagePercent)> SampleGpuEngineLoad()
    {
        if (!_gpuEngineAvailable)
            return [];

        var instances = _pdh.ReadWildcardDouble("gpu.engine");
        if (instances.Count == 0)
            return [];

        var perAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instanceName, value) in instances)
        {
            var luid = ExtractAdapterLuid(instanceName);
            if (luid is null)
                continue;

            perAdapter.TryGetValue(luid, out var current);
            perAdapter[luid] = current + value;
        }

        return perAdapter
            .Select(kv => (kv.Key, UsagePercent: Math.Clamp(kv.Value, 0, 100)))
            .OrderByDescending(x => x.UsagePercent)
            .ToList();
    }

    /// <summary>
    /// Extracts the adapter's LUID key from a GPU Engine instance name.
    /// Returns <c>null</c> for anything that does not name a LUID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instance family changed shape across Windows versions, and both spellings are
    /// alive in the field. The one the documentation describes leads with the adapter:
    /// <c>luid_0x00000000_0x0000ABCD_engtype_3D</c>. Every Windows 10 2004+ machine leads
    /// with the process that owns the engine instead - this machine's counters, read
    /// directly on 17 October, spell it <c>pid_10864_luid_0x00000000_0x00012997_phys_0_eng_0_engtype_3D</c>
    /// across 541 rows - and the parser that assumed the documented shape rejected all of
    /// them, which is why GPU load read zero here forever.
    /// </para>
    /// <para>
    /// The adapter is named by the two hex tokens after the <c>luid_</c> marker in both
    /// families, so the parser finds that marker wherever it stands instead of trusting the
    /// name's first token. The same rule keys the adapter-memory rows, which carry the same
    /// pair, so a load row and its memory row find each other.
    /// </para>
    /// </remarks>
    public static string? ExtractAdapterLuid(string instanceName)
    {
        var at = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        var start = at + "luid_".Length;
        var first = instanceName.IndexOf('_', start);
        if (first < 0)
            return instanceName[at..];

        var second = instanceName.IndexOf('_', first + 1);
        return second < 0 ? instanceName[at..] : instanceName[at..second];
    }

    /// <summary>Per-adapter VRAM usage from <c>\GPU Adapter Memory(*)</c>, when available.</summary>
    public IReadOnlyDictionary<string, long> SampleGpuMemory()
    {
        if (!_gpuEngineAvailable)
            return new Dictionary<string, long>();

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instance, bytes) in _pdh.ReadWildcardDouble("gpu.sharedMemory", WinConst.PDH_FMT_LARGE))
        {
            if (!TryParseLargeValue(bytes, out var value))
                continue;

            var luid = ExtractAdapterLuid(instance) ?? instance;
            result.TryGetValue(luid, out var existing);
            result[luid] = existing + (long)value;
        }

        foreach (var (instance, bytes) in _pdh.ReadWildcardDouble("gpu.dedicatedMemory", WinConst.PDH_FMT_LARGE))
        {
            if (!TryParseLargeValue(bytes, out var value))
                continue;

            var luid = ExtractAdapterLuid(instance) ?? instance;
            result.TryGetValue(luid, out var existing);
            result[luid] = existing + (long)value;
        }

        return result;
    }

    private static bool TryParseLargeValue(double raw, out long value)
    {
        value = (long)raw;
        return raw > 0 && raw < long.MaxValue;
    }

    public void Dispose() => _pdh.Dispose();
}
