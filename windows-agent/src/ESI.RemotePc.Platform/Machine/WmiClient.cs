using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Machine;

/// <summary>
/// Thin, defensive wrapper over WMI/CIM.
/// <para>
/// WMI is the documented, supported way to read hardware inventory on Windows
/// (https://learn.microsoft.com/en-us/windows/win32/wmisdk/using-wmi). Two namespaces
/// are used:
/// </para>
/// <list type="bullet">
///   <item><description><c>root\cimv2</c> — the <c>Win32_*</c> classes.</description></item>
///   <item><description><c>root\wmi</c> — ACPI thermal zones and <c>MSPower_*</c>.</description></item>
/// </list>
/// <para>
/// Every query here is cached: WMI calls are slow (10-100 ms each) and the agent
/// must not hammer the WMI service from a metrics loop. Callers get a snapshot from
/// the cache and <see cref="Invalidate"/> forces a refresh after a hardware change.
/// </para>
/// </summary>
public sealed class WmiClient : IDisposable
{
    private readonly ILogger<WmiClient> _log;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WmiClient(ILogger<WmiClient> log, TimeSpan? cacheTtl = null)
    {
        _log = log;
        _ttl = cacheTtl ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Forces the next read of every cached query to hit WMI again.</summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>
    /// Runs a WQL query and maps each row through <paramref name="selector"/>. Returns an
    /// empty list on any failure — a WMI hiccup must never take down the metrics pipeline.
    /// </summary>
    public IReadOnlyList<T> Query<T>(string wql, string scope, Func<ManagementBaseObject, T> selector, Func<T>? fallback = null)
    {
        var cacheKey = scope + "|" + wql;
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < _ttl)
        {
            if (hit.Value is IReadOnlyList<T> cached)
                return cached;
        }

        var results = new List<T>();
        try
        {
            // Read-only, no writes, no impersonation escalation: the agent only ever queries.
            var options = new ConnectionOptions
            {
                EnablePrivileges = false,
                Timeout = TimeSpan.FromSeconds(5),
            };

            // The ManagementScope carries the ConnectionOptions, so the two-argument
            // searcher overload still connects with our restricted options. There is no
            // ManagementObjectSearcher overload that accepts ConnectionOptions directly.
            var scopeObj = new ManagementScope(scope, options);
            using var searcher = new ManagementObjectSearcher(scopeObj, new ObjectQuery(wql));
            using var rows = searcher.Get();

            foreach (ManagementBaseObject row in rows)
            {
                using (row)
                {
                    try
                    {
                        var mapped = selector(row);
                        if (mapped is not null)
                            results.Add(mapped);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Skipping a WMI row that could not be mapped (query: {Query})", wql);
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            _log.LogDebug(ex, "WMI query failed ({Query} in {Scope}); returning fallback", wql, scope);
            if (fallback is not null)
                return [fallback()];
        }
        catch (COMException ex)
        {
            _log.LogDebug(ex, "WMI query failed with a COM error ({Query}); returning fallback", wql);
            if (fallback is not null)
                return [fallback()];
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogDebug(ex, "WMI query denied ({Query}); returning fallback", wql);
            if (fallback is not null)
                return [fallback()];
        }

        _cache[cacheKey] = (DateTimeOffset.UtcNow, results);
        return results;
    }

    /// <summary>Runs a scalar WQL query, returning the first row's value or <c>null</c>.</summary>
    public T? QueryScalar<T>(string wql, string scope, Func<ManagementBaseObject, T?> selector) where T : class
    {
        var rows = Query(wql, scope, row => selector(row)!);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string wql, string scope, Func<ManagementBaseObject, T> selector, CancellationToken ct = default)
    {
        // WMI itself is synchronous and internally threaded; offloading to the thread pool
        // keeps the caller's request pipeline responsive.
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Query(wql, scope, selector);
        }, ct).ConfigureAwait(false);
    }

    public static string? GetString(ManagementBaseObject o, string property)
    {
        try
        {
            var v = o[property];
            if (v is null)
                return null;
            var s = v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        catch
        {
            return null;
        }
    }

    public static T? GetValue<T>(ManagementBaseObject o, string property) where T : struct
    {
        try
        {
            var v = o[property];
            if (v is null)
                return null;
            return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    public static ulong? GetUInt64(ManagementBaseObject o, string property)
    {
        var v = GetValue<ulong>(o, property);
        return v;
    }

    public static DateTimeOffset? GetDate(ManagementBaseObject o, string property)
    {
        try
        {
            // WMI returns DMTF strings like 20260928120000.000000-060 for CIM_DATETIME.
            var raw = o[property];
            if (raw is null)
                return null;

            switch (raw)
            {
                case DateTime dt:
                    return new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero);
                case string s:
                    // ManagementDateTimeConverter returns an unspecified-kind DateTime from
                    // a DMTF string; treat it as UTC because WMI CIM_DATETIME is that.
                    return DateTime.SpecifyKind(ManagementDateTimeConverter.ToDateTime(s), DateTimeKind.Utc);
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    public const string CimV2 = @"\\.\root\cimv2";
    public const string WmiNamespace = @"\\.\root\wmi";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
        _cache.Clear();
    }
}
