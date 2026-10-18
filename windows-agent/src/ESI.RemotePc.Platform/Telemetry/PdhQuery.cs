using System.Runtime.InteropServices;
using System.Text;
using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Telemetry;

/// <summary>
/// A live PDH query with a set of counters.
/// <para>
/// Two documented behaviours drive the design here:
/// </para>
/// <list type="bullet">
///   <item><description><b>Rate counters need two samples.</b> <c>PdhCollectQueryData</c>
///   must be called at least twice before a rate counter yields a displayable value;
///   the first call only establishes a baseline.</description></item>
///   <item><description><b>Counters are localised.</b> <c>PdhAddCounter</c> paths are
///   language-dependent, so a machine with a non-English Windows would fail to open
///   <c>\Processor(_Total)\% Processor Time</c>. <c>PdhAddEnglishCounter</c> is the
///   documented language-neutral alternative and is used throughout.</description></item>
/// </list>
/// <para>
/// Every read checks <c>CStatus</c> first, because PDH happily returns a struct whose
/// value is meaningless when the status says so.
/// </para>
/// </summary>
public sealed unsafe class PdhQuery : IDisposable
{
    private readonly ILogger<PdhQuery> _log;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _query;
    private int _sampleCount;
    private bool _disposed;

    public PdhQuery(ILogger<PdhQuery> log)
    {
        _log = log;
        var status = PDH.PdhOpenQueryW(null, UIntPtr.Zero, out _query);
        if (status != WinConst.ERROR_SUCCESS)
        {
            _log.LogDebug("PdhOpenQuery failed with {Status}; performance counters are unavailable", status);
            _query = IntPtr.Zero;
        }
    }

    public bool IsOpen => _query != IntPtr.Zero;

    /// <summary>Number of successful <c>PdhCollectQueryData</c> calls.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>Adds a counter. Returns <c>false</c> when the counter path does not exist here.</summary>
    public bool AddCounter(string englishPath, string key)
    {
        if (_query == IntPtr.Zero || _counters.ContainsKey(key) || _missing.Contains(key))
            return _counters.ContainsKey(key);

        var status = PDH.PdhAddEnglishCounterW(_query, englishPath, UIntPtr.Zero, out var handle);
        if (status != WinConst.ERROR_SUCCESS)
        {
            // Very common: the counter simply does not exist on this SKU (e.g. GPU Engine
            // on a machine with no supported GPU driver). Remember that so we stop trying.
            _missing.Add(key);
            _log.LogDebug("Counter {Path} is unavailable (status {Status})", englishPath, status);
            return false;
        }

        _counters[key] = handle;
        return true;
    }

    /// <summary>
    /// Adds a wildcard counter (for example <c>\GPU Engine(*)\Utilization Percentage</c>)
    /// and remembers its handle so <see cref="ReadWildcardDouble"/> can enumerate instances.
    /// </summary>
    public bool AddWildcardValueCounter(string englishWildcardPath, string key)
    {
        if (_query == IntPtr.Zero || _wildcardHandles.ContainsKey(key) || _missing.Contains(key))
            return _wildcardHandles.ContainsKey(key);

        var status = PDH.PdhAddEnglishCounterW(_query, englishWildcardPath, UIntPtr.Zero, out var handle);
        if (status != WinConst.ERROR_SUCCESS)
        {
            _missing.Add(key);
            _log.LogDebug("Wildcard counter {Path} unavailable ({Status})", englishWildcardPath, status);
            return false;
        }

        _wildcardHandles[key] = handle;
        return true;
    }

    /// <summary>
    /// Expands a wildcard path to its concrete instance paths. Used at startup to decide
    /// whether an optional counter (notably GPU Engine) exists on this machine at all.
    /// </summary>
    public IReadOnlyList<string> ExpandWildcardInstances(string englishWildcardPath)
    {
        if (_query == IntPtr.Zero)
            return [];

        // Two-pass: pass 0 asks for the required buffer size, then the real call fills it.
        var required = 0u;
        var status = PDH.PdhExpandWildCardPathW(null, englishWildcardPath, null, ref required, WinConst.PDH_REFRESH_COUNTERS);

        if (status != WinConst.PDH_MORE_DATA && status != WinConst.ERROR_SUCCESS)
            return [];

        if (required == 0)
            return [];

        var buffer = new StringBuilder((int)required);
        var capacity = required;
        status = PDH.PdhExpandWildCardPathW(null, englishWildcardPath, buffer, ref capacity, WinConst.PDH_REFRESH_COUNTERS);

        if (status != WinConst.ERROR_SUCCESS)
            return [];

        // The result is a double-NUL-terminated list of counter paths.
        return buffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Takes a sample. The first call only establishes the baseline for rate counters.</summary>
    public void Collect()
    {
        if (_query == IntPtr.Zero)
            return;

        if (PDH.PdhCollectQueryData(_query) == WinConst.ERROR_SUCCESS)
            _sampleCount++;
    }

    /// <summary>Reads a double-formatted counter. Returns <c>null</c> when unavailable.</summary>
    public double? ReadDouble(string key, uint format = WinConst.PDH_FMT_DOUBLE | WinConst.PDH_FMT_NOCAP100)
    {
        if (_query == IntPtr.Zero || !_counters.TryGetValue(key, out var counter) || counter == IntPtr.Zero)
            return null;

        // Rate counters are meaningless until two samples exist.
        if (_sampleCount < 2)
            return null;

        var status = PDH.PdhGetFormattedCounterValue(counter, format, out _, out var value);

        // CStatus must be checked before trusting the union; PDH_INVALID_DATA is what a
        // rate counter returns before its first real interval.
        if (status != WinConst.ERROR_SUCCESS || value.CStatus is not (WinConst.PDH_CSTATUS_VALID_DATA or WinConst.PDH_CSTATUS_NEW_DATA))
            return null;

        return value.doubleValue;
    }

    /// <summary>Reads a large-integer counter (bytes, octets).</summary>
    public long? ReadLarge(string key)
    {
        if (_query == IntPtr.Zero || !_counters.TryGetValue(key, out var counter) || counter == IntPtr.Zero)
            return null;

        var status = PDH.PdhGetFormattedCounterValue(counter, WinConst.PDH_FMT_LARGE, out _, out var value);
        if (status != WinConst.ERROR_SUCCESS)
            return null;

        if (value.CStatus is not (WinConst.PDH_CSTATUS_VALID_DATA or WinConst.PDH_CSTATUS_NEW_DATA))
            return null;

        // PDH_FMT_LARGE populates the 64-bit union member.
        return value.largeValue;
    }

    /// <summary>
    /// Reads every instance of a wildcard counter. Returns instance name to value.
    /// <para>
    /// Uses <c>PdhGetFormattedCounterArrayW</c>, which is a two-pass call: the first call
    /// with a null buffer reports the required buffer size, the second fills an
    /// <c>PDH_FMT_COUNTERVALUE_ITEM_W</c> array (a <c>LPCWSTR</c> pointer followed by a
    /// <c>PDH_FMT_COUNTERVALUE</c>). The name strings follow the item array in the same
    /// buffer, which is why the name pointer is dereferenced rather than the string being
    /// read in place.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, double> ReadWildcardDouble(string key, uint format = WinConst.PDH_FMT_DOUBLE | WinConst.PDH_FMT_NOCAP100)
    {
        if (_query == IntPtr.Zero || !_wildcardHandles.TryGetValue(key, out var handle) || _sampleCount < 2)
            return new Dictionary<string, double>();

        // Pass 1: required size.
        var bufferSize = 0u;
        var status = PDH.PdhGetFormattedCounterArrayW(handle, format, ref bufferSize, out _, IntPtr.Zero);

        if (status != WinConst.PDH_MORE_DATA || bufferSize == 0)
            return new Dictionary<string, double>();

        // Pass 2.
        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            var count = 0u;
            status = PDH.PdhGetFormattedCounterArrayW(handle, format, ref bufferSize, out count, buffer);

            if (status != WinConst.ERROR_SUCCESS || count == 0)
                return new Dictionary<string, double>();

            var result = new Dictionary<string, double>((int)count, StringComparer.OrdinalIgnoreCase);
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();

            for (var i = 0; i < count; i++)
            {
                // Reading the element as a typed struct, rather than hand-computing offsets,
                // is what makes the 8-byte pointer + 16-byte value layout correct on x64.
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(IntPtr.Add(buffer, i * itemSize));

                if (item.pszName == IntPtr.Zero)
                    continue;

                var name = Marshal.PtrToStringUni(item.pszName);
                if (string.IsNullOrEmpty(name))
                    continue;

                var value = item.PdhFmtValue;
                if (value.CStatus is not (WinConst.PDH_CSTATUS_VALID_DATA or WinConst.PDH_CSTATUS_NEW_DATA))
                    continue;

                result[name] = DecodeCounterValue(format, value);
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PdhGetFormattedCounterArrayW failed for {Key}", key);
            return new Dictionary<string, double>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Decodes one formatted counter value according to the read format (D91). The native
    /// value is a C union: <c>PDH_FMT_LARGE</c> fills the 64-bit integer member and
    /// <c>PDH_FMT_DOUBLE</c> fills the double member - so reading <c>doubleValue</c> on a
    /// LARGE read turns every byte count into a subnormal double that (long)-casts to zero.
    /// That is exactly what the GPU adapter-memory path served while typeperf measured
    /// 845,787,136 bytes on the same counter a second apart: keys present, every value
    /// parsed-but-zero, the phone reading "0 bytes used" on a card mid-render.
    /// </summary>
    internal static double DecodeCounterValue(uint format, PDH_FMT_COUNTERVALUE value) =>
        (format & WinConst.PDH_FMT_LARGE) != 0 ? value.largeValue : value.doubleValue;

    private readonly Dictionary<string, IntPtr> _wildcardHandles = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_query != IntPtr.Zero)
        {
            PDH.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        _counters.Clear();
        _wildcardHandles.Clear();
        _missing.Clear();
    }
}

/// <summary>Well-known English PDH counter paths used by the agent.</summary>
public static class PdhCounters
{
    public const string TotalProcessorTime = @"\Processor(_Total)\% Processor Time";
    public const string ProcessorTimeTemplate = @"\Processor({0})\% Processor Time";
    public const string AvailableMBytes = @"\Memory\Available MBytes";
    public const string PageFileBytes = @"\Paging File(_Total)\% Usage";
    public const string CacheBytes = @"\Memory\Cache Bytes";
    public const string PhysicalDiskTime = @"\PhysicalDisk(_Total)\% Disk Time";
    public const string DiskReadBytes = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    public const string DiskWriteBytes = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    public const string DiskReadBytesAll = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    public const string DiskWriteBytesAll = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    public const string NetworkBytesSent = @"\Network Interface(*)\Bytes Sent/sec";
    public const string NetworkBytesReceived = @"\Network Interface(*)\Bytes Received/sec";

    /// <summary>
    /// GPU Engine utilisation. Windows exposes one instance per (adapter, engine, engine type)
    /// with names like <c>luid_0x00000000_0x0000ABCD_engtype_3D</c>. It is not documented on
    /// learn.microsoft.com, so the agent verifies the counter exists at runtime with
    /// <c>PdhExpandWildCardPath</c> and degrades gracefully when it does not.
    /// </summary>
    public const string GpuEngineUtilization = @"\GPU Engine(*)\Utilization Percentage";

    public const string GpuAdapterMemoryShared = @"\GPU Adapter Memory(*)\Shared Usage";
    public const string GpuAdapterMemoryDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
}
