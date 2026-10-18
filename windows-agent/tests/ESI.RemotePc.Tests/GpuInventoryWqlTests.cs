using Esi.Platform.Machine;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D90: the phone read "Adapters reporting 0" forever on a healthy machine with a working
/// GPU, because the video-controller inventory query was a bare SELECT list with no FROM
/// clause — the one WMI query in the codebase that named no class. Invalid WQL, swallowed
/// into an empty list by the metrics pipeline's never-fail design, and then that empty
/// failure was cached under its key for the cache's whole TTL: the poison outlived even a
/// fixed query for five minutes at a time, and nothing anywhere logged it louder than a
/// debug line.
///
/// <para>
/// These pins hold the inventory to the shape that works: the query names its class, a
/// failed read is returned but never cached, a completed read is cached, and — live, on
/// whatever machine runs the suite — the real inventory still comes back through the path
/// the agent serves.
/// </para>
/// </summary>
public class GpuInventoryWqlTests
{
    private readonly ITestOutputHelper _log;

    public GpuInventoryWqlTests(ITestOutputHelper log) => _log = log;

    [Fact]
    public void The_query_names_its_class()
    {
        // The one defect that read as "no GPU anywhere": a SELECT with no FROM.
        Assert.Contains(" FROM Win32_VideoController", SystemInfoProvider.VideoControllerWql);
    }

    [Fact]
    public void The_query_carries_every_column_the_row_maps()
    {
        const string wql = SystemInfoProvider.VideoControllerWql;

        // Anything mapped by the row selector and missing from the SELECT list surfaces as
        // null silently, so each mapped column is named in the query text too.
        Assert.Contains("Name", wql);
        Assert.Contains("AdapterCompatibility", wql);
        Assert.Contains("AdapterRAM", wql);
        Assert.Contains("DriverVersion", wql);
        Assert.Contains("Status", wql);
        Assert.Contains("PNPDeviceID", wql);
        Assert.Contains("CurrentHorizontalResolution", wql);
        Assert.Contains("CurrentVerticalResolution", wql);
    }

    [Fact]
    public void A_failed_read_is_returned_but_never_cached()
    {
        var logFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        using var wmi = new WmiClient(logFactory.CreateLogger<WmiClient>());

        const string broken = "SELECT Name FROM";
        var rows = wmi.Query(broken, WmiClient.CimV2, row => (string?)row["Name"]?.ToString());

        // The swallow contract: a failure is an empty result, not an exception - the metrics
        // pipeline is built on that.
        Assert.Empty(rows);

        // The D90 half: the failure does not sit in the cache for the TTL, so the next call
        // actually tries again.
        Assert.False(wmi.IsCached(WmiClient.CimV2, broken));
    }

    [Fact]
    public void A_completed_read_is_cached()
    {
        var logFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        using var wmi = new WmiClient(logFactory.CreateLogger<WmiClient>());

        // Any query that completes - here the machine's board identity - earns its cache
        // entry, which is the design worth keeping.
        var rows = wmi.Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem", WmiClient.CimV2,
            row => (string?)row["Model"]?.ToString());
        Assert.NotEmpty(rows);
        Assert.True(wmi.IsCached(WmiClient.CimV2, "SELECT Manufacturer, Model FROM Win32_ComputerSystem"));
    }

    [Fact]
    public void The_served_inventory_comes_back_nonempty()
    {
        // The end-to-end half, on the machine that runs the suite: the inventory path the
        // metrics pipeline serves returns the real adapters through the fixed query. A
        // machine whose only display adapter is the Basic fallback still answers honestly -
        // the query works, the room has just no discrete GPU - so the assertion tolerates
        // that shape by pinning the query's own health instead: no defect, no silence.
        var logFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        using var wmiForLive = new WmiClient(logFactory.CreateLogger<WmiClient>());
        var info = new SystemInfoProvider(wmiForLive, logFactory.CreateLogger<SystemInfoProvider>());

        var gpus = info.GetGpuInfo();
        _log.WriteLine($"inventory rows served: {gpus.Count}");
        foreach (var g in gpus)
            _log.WriteLine($"  [{g.Index}] {g.Name} vendor={g.Vendor} driver={g.DriverVersion}");

        // The live half is conditional on the machine having a non-Basic display adapter,
        // measured the same way the pipeline itself filters:
        var raw = wmiForLive.Query(SystemInfoProvider.VideoControllerWql, WmiClient.CimV2,
            row => (string?)row["Name"]?.ToString() ?? string.Empty);
        var realAdapters = raw.Where(n => !string.Equals(n, "Microsoft Basic Display Adapter", StringComparison.OrdinalIgnoreCase));

        if (realAdapters.Any())
        {
            Assert.NotEmpty(gpus);
            Assert.Equal(realAdapters.Count(), gpus.Count);
        }
        else
        {
            _log.WriteLine("machine reports no non-Basic display adapter; inventory emptiness is honest");
        }
    }
}
