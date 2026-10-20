using System.Collections.Concurrent;
using Esi.Platform.Machine;
using Esi.Platform.Native;
using Esi.Platform.Telemetry;

namespace Esi.Core;

/// <summary>
/// Assembles a <see cref="MetricsResponse"/> from the platform samplers and keeps a short
/// rolling history for the dashboard graphs.
/// <para>
/// Runs on a background cadence rather than per request. A dashboard refresh is several
/// requests; sampling once and sharing the result keeps the cost flat regardless of how
/// many phones are watching, and means every device sees the same numbers at the same
/// instant, which matters when two people are looking at the same screen.
/// </para>
/// </summary>
public sealed class MetricsService : IDisposable
{
    private readonly ILogger<MetricsService> _log;
    private readonly SystemSampler _sampler;
    private readonly SystemInfoProvider _systemInfo;
    private readonly NetworkProvider _network;
    private readonly TemperatureProvider _temperatures;
    private readonly NvmlProvider _nvml;
    private readonly WmiClient _wmi;

    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly List<LoadPoint> _history = [];
    private readonly Lock _historyLock = new();

    private MetricsResponse? _latest;
    private DateTimeOffset _lastSampleUtc = DateTimeOffset.MinValue;
    private TimeSpan _lastSampleInterval = TimeSpan.FromSeconds(1);
    private CpuInfo? _cpuInfo;
    private Dictionary<int, (long Used, long Total)> _gpuMemoryCache = [];

    public MetricsService(
        ILogger<MetricsService> log,
        SystemSampler sampler,
        SystemInfoProvider systemInfo,
        NetworkProvider network,
        TemperatureProvider temperatures,
        NvmlProvider nvml,
        WmiClient wmi)
    {
        _log = log;
        _sampler = sampler;
        _systemInfo = systemInfo;
        _network = network;
        _temperatures = temperatures;
        _nvml = nvml;
        _wmi = wmi;
    }

    /// <summary>Number of samples in the rolling history.</summary>
    public int HistoryLimit { get; set; } = 300;

    /// <summary>
    /// Reads the page files via <c>Win32_PageFileUsage</c> and sums them into the wire's
    /// <c>swap</c> fields (D98). Zero on any failure - a machine with the page file
    /// disabled IS a zero and must read as one, not as an unreported dash.
    /// </summary>
    private SwapMetrics SampleSwapMetrics()
    {
        var rows = _wmi.Query(
            "SELECT Name, AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage",
            WmiClient.CimV2,
            o => new SwapMetrics
            {
                TotalBytes = (WmiClient.GetValue<uint>(o, "AllocatedBaseSize") ?? 0) * 1024L * 1024L,
                UsedBytes = (WmiClient.GetValue<uint>(o, "CurrentUsage") ?? 0) * 1024L * 1024L,
            });

        var result = new SwapMetrics();
        foreach (var row in rows)
        {
            result = new SwapMetrics
            {
                TotalBytes = result.TotalBytes + row.TotalBytes,
                UsedBytes = result.UsedBytes + row.UsedBytes,
            };
        }

        return result;
    }

    /// <summary>Returns the most recent sample, taking a fresh one when the cached one is stale.</summary>
    public async Task<MetricsResponse> GetAsync(int sampleWindowMs = 1000, CancellationToken ct = default)
    {
        var window = TimeSpan.FromMilliseconds(Math.Clamp(sampleWindowMs, 250, 10000));

        await _sampleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var age = DateTimeOffset.UtcNow - _lastSampleUtc;

            // A wider requested window forces a fresh sample; otherwise the cached one is
            // reused until it is older than the requested window.
            if (_latest is not null && age <= window)
                return _latest;

            _lastSampleInterval = _lastSampleUtc == DateTimeOffset.MinValue ? window : age;
            _lastSampleUtc = DateTimeOffset.UtcNow;
            _latest = await SampleAsync(_lastSampleInterval, ct).ConfigureAwait(false);
            return _latest;
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    /// <summary>Forces a fresh sample. Used by the metrics loop and after a state change.</summary>
    public async Task<MetricsResponse> SampleNowAsync(TimeSpan? window = null, CancellationToken ct = default)
    {
        await _sampleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastSampleUtc = DateTimeOffset.UtcNow;
            _latest = await SampleAsync(window ?? TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            return _latest;
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    private async Task<MetricsResponse> SampleAsync(TimeSpan window, CancellationToken ct)
    {
        // PDH rate counters need a sample taken at the start of the interval, so the
        // collection happens before the reads.
        _sampler.Collect();

        var response = new MetricsResponse
        {
            CapturedAt = AgentDatabase.NowUnix(),
            SampleWindowMs = (int)window.TotalMilliseconds,
        };

        // ---- CPU ---------------------------------------------------------------
        var busy = _sampler.SampleCpuBusyPercent(out var user, out var kernel);
        _cpuInfo ??= _systemInfo.GetCpuInfo();

        response.Cpu = new CpuMetrics
        {
            UsagePercent = Math.Round(busy, 2),
            UserPercent = Math.Round(user, 2),
            KernelPercent = Math.Round(kernel, 2),
            PerCore = _sampler.SamplePerCorePercentages(),
            MaxClockMhz = _cpuInfo.MaxClockMhz,
            TemperatureC = _temperatures.CpuTemperature().Celsius,
        };

        var counts = _sampler.SampleProcessCounts();
        response.Cpu.ProcessCount = counts.Processes;
        response.Cpu.ThreadCount = counts.Threads;

        // ---- Memory ------------------------------------------------------------
        var memory = _sampler.SampleMemory();
        response.Memory = new MemoryMetrics
        {
            TotalBytes = memory.TotalBytes,
            UsedBytes = memory.UsedBytes,
            AvailableBytes = memory.AvailableBytes,
            UsedPercent = memory.UsedPercent,
            CachedBytes = memory.CachedBytes,
            CommittedBytes = memory.CommittedBytes,
            CommitLimitBytes = memory.CommitLimitBytes,
            // D98: the page-file rows used to stay at the contract's zero default, so the
            // phone read "Page file 0 B" on a machine whose page file was carrying 600 MB
            // of live commit. Win32_PageFileUsage reports MB; the read is cached by
            // WmiClient's TTL, so a resize propagates within minutes, not per sample.
            Swap = SampleSwapMetrics(),
        };

        // ---- GPU ---------------------------------------------------------------
        var gpus = await SampleGpuAsync(ct).ConfigureAwait(false);
        response.Gpu = gpus;
        response.GpuAggregate = new GpuAggregate
        {
            UsagePercent = gpus.Count > 0 ? Math.Round(gpus.Max(g => g.UsagePercent), 2) : 0,
            MaxTemperatureC = gpus.Count > 0 ? gpus.Max(g => g.TemperatureC) : null,
            DominantIndex = gpus.Count > 0 ? gpus.OrderByDescending(g => g.UsagePercent).First().Index : 0,
        };

        // ---- Storage -----------------------------------------------------------
        var volumes = _systemInfo.GetStorageVolumes();
        var diskRates = _sampler.SampleDiskRates();
        var aggregateDisk = _sampler.SampleAggregateDiskRate();

        response.Storage = volumes.Select(v =>
        {
            diskRates.TryGetValue(v.DeviceId, out var rate);

            return new StorageMetrics
            {
                DeviceId = v.DeviceId,
                UsedPercent = v.UsedPercent,
                UsedBytes = v.TotalBytes - v.FreeBytes,
                FreeBytes = v.FreeBytes,
                TotalBytes = v.TotalBytes,
                // Only the system volume gets the machine-wide I/O figure; attributing
                // total disk throughput to every volume would double-count it.
                ReadBytesPerSecond = v.IsSystem ? aggregateDisk.ReadBytesPerSecond : rate.ReadBytesPerSecond,
                WriteBytesPerSecond = v.IsSystem ? aggregateDisk.WriteBytesPerSecond : rate.WriteBytesPerSecond,
            };
        }).ToList();

        // ---- Network -----------------------------------------------------------
        var rates = _sampler.SampleNetworkRates(window);
        var interfaces = _network.GetInterfaces();
        var primaryId = _network.GetPrimaryInterface()?.Name;

        foreach (var nic in interfaces)
        {
            if (rates.TryGetValue(nic.Id, out var rate))
            {
                nic.DownloadBytesPerSecond = rate.Down;
                nic.UploadBytesPerSecond = rate.Up;
                nic.RxTotalBytes = rate.RxTotal;
                nic.TxTotalBytes = rate.TxTotal;
            }
        }

        var primary = interfaces.FirstOrDefault(i => i.Name == primaryId);
        if (primary is not null)
        {
            primary.IsPrimary = true;

            // The primary row duplicates the interface entry plus a flat DNS list, which
            // is what the dashboard's network section binds to.
            response.Network.Primary = new NetworkInterfaceMetrics
            {
                Name = primary.Name,
                Description = primary.Description,
                Type = primary.Type,
                IsUp = primary.IsUp,
                MacAddress = primary.MacAddress,
                Ipv4 = primary.Ipv4,
                Netmask = primary.Netmask,
                Gateway = primary.Gateway,
                Dns = [.. primary.Dns],
                LinkSpeedBitsPerSecond = primary.LinkSpeedBitsPerSecond,
                Mtu = primary.Mtu,
                DownloadBytesPerSecond = primary.DownloadBytesPerSecond,
                UploadBytesPerSecond = primary.UploadBytesPerSecond,
                RxTotalBytes = primary.RxTotalBytes,
                TxTotalBytes = primary.TxTotalBytes,
                IsPrimary = true,
            };
        }

        response.Network.Interfaces = interfaces;

        // ---- History -----------------------------------------------------------
        lock (_historyLock)
        {
            _history.Add(new LoadPoint
            {
                Ts = response.CapturedAt,
                Cpu = response.Cpu.UsagePercent,
                Gpu = response.GpuAggregate.UsagePercent,
                Ram = response.Memory.UsedPercent,
            });

            if (_history.Count > HistoryLimit)
                _history.RemoveRange(0, _history.Count - HistoryLimit);

            response.Load = [.. _history];
        }

        return response;
    }

    private async Task<List<GpuMetrics>> SampleGpuAsync(CancellationToken ct)
    {
        var result = new List<GpuMetrics>();
        var inventory = _systemInfo.GetGpuInfo();

        // ---- 1. NVML is authoritative where present -----------------------------
        var nvmlDevices = _nvml.GetDevices();
        var pdhLoad = _sampler.SampleGpuEngineLoad();
        var pdhMemory = _sampler.SampleGpuMemory();

        for (var i = 0; i < inventory.Count; i++)
        {
            var info = inventory[i];
            var nvml = i < nvmlDevices.Length ? nvmlDevices[i] : null;

            var row = new GpuMetrics
            {
                Index = i,
                Name = nvml?.Name ?? info.Name,
                Vendor = info.Vendor,
                TemperatureC = nvml?.TemperatureC > 0 ? nvml.TemperatureC : null,
                FanRpm = nvml?.FanPercent is > 0 ? (int)nvml.FanPercent : null,
                PowerWatts = nvml?.PowerWatts,
                Source = "unavailable",
            };

            if (nvml is not null)
            {
                row.UsagePercent = nvml.UsagePercent;
                row.MemoryUsedBytes = nvml.MemoryUsedBytes > 0 ? nvml.MemoryUsedBytes : null;
                row.MemoryTotalBytes = nvml.MemoryTotalBytes > 0 ? nvml.MemoryTotalBytes : null;
                row.Source = "nvml";
            }
            else if (pdhLoad.Count > 0)
            {
                // The PDH GPU Engine counters are per adapter, keyed by the adapter LUID.
                // They cannot be matched to a WMI adapter by name, so the dominant adapter
                // is reported first and the rest follow in order.
                var position = Math.Min(i, pdhLoad.Count - 1);
                row.UsagePercent = Math.Round(pdhLoad[position].UsagePercent, 2);
                row.Source = "pdh";

                if (pdhMemory.TryGetValue(pdhLoad[position].AdapterLuid, out var memoryBytes))
                    row.MemoryUsedBytes = memoryBytes;

                row.MemoryTotalBytes = info.VramBytes;
            }
            else
            {
                // No vendor SDK and no PDH counter: the load is genuinely unknown. It is
                // reported as 0 with source "unavailable" rather than guessed.
                row.MemoryTotalBytes = info.VramBytes;
            }

            result.Add(row);
        }

        await Task.CompletedTask;
        return result;
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _sampleGate.Dispose();
    }
}
