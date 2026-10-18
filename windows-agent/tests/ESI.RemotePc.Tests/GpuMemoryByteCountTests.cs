using Esi.Platform.Native;
using Esi.Platform.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D91: the per-adapter GPU memory read served zero on every machine, with the keys present
/// and the values "parsed" — the read format and the union member had drifted apart. The
/// adapter-memory path reads with <c>PDH_FMT_LARGE</c> (bytes), but the wildcard reader
/// decoded the union through its <c>doubleValue</c> member no matter what format the call
/// asked for. A byte count such as 845,787,136 lives entirely in the low 32 bits of the
/// union, and those same 8 bytes re-read as an IEEE double are a subnormal (~1e-315):
/// "greater than zero" passes the sanity gate, and the (long) cast lands on exactly 0.
/// typeperf measured 845,787,136 bytes on this machine's card a second apart from our zero.
///
/// <para>
/// These pins hold the decode to the format that asked for it, and one live pin holds the
/// end-to-end memory read to a rendering desktop's real VRAM figure.
/// </para>
/// </summary>
public class GpuMemoryByteCountTests
{
    private readonly ITestOutputHelper _log;

    public GpuMemoryByteCountTests(ITestOutputHelper log) => _log = log;

    [Theory]
    [InlineData(845_787_136)] // measured on this machine: the desktop's dedicated usage
    [InlineData(4_096)]
    [InlineData(4_293_918_720)] // the WMI 4 GiB cap, at the uint32 boundary
    public void A_large_format_read_decodes_the_integer_member(long bytes)
    {
        var value = new PDH_FMT_COUNTERVALUE
        {
            CStatus = WinConst.PDH_CSTATUS_VALID_DATA,
            largeValue = bytes,
        };

        var decoded = PdhQuery.DecodeCounterValue(WinConst.PDH_FMT_LARGE, value);
        Assert.Equal((double)bytes, decoded);
        Assert.Equal(bytes, (long)decoded); // the byte identity that (long)-casting needs
    }

    [Theory]
    [InlineData(845_787_136)]
    [InlineData(4_293_918_720)]
    public void Re_reading_a_large_value_as_a_double_is_the_precise_old_defect(long bytes)
    {
        var value = new PDH_FMT_COUNTERVALUE
        {
            CStatus = WinConst.PDH_CSTATUS_VALID_DATA,
            largeValue = bytes,
        };

        // For any realistic VRAM count (< 2^32) the pattern's exponent field is zero, so a
        // double re-read is a subnormal: it passes "raw > 0" and (long)-casts to zero. This
        // is not an accident of one machine; it is what the old path served everywhere.
        var reinterpreted = value.doubleValue;
        Assert.True(reinterpreted > 0);
        Assert.Equal(0, (long)reinterpreted);
    }

    [Fact]
    public void A_double_format_read_still_decodes_the_double_member()
    {
        var value = new PDH_FMT_COUNTERVALUE
        {
            CStatus = WinConst.PDH_CSTATUS_VALID_DATA,
            doubleValue = 16.32,
        };

        // The load-path reads are double-format and carry no cap; the decode must remain
        // the plain double for them.
        var decoded = PdhQuery.DecodeCounterValue(
            WinConst.PDH_FMT_DOUBLE | WinConst.PDH_FMT_NOCAP100, value);
        Assert.Equal(16.32, decoded, precision: 9);
    }

    [Fact]
    public async Task The_desktops_dedicated_vram_reads_as_real_bytes()
    {
        // The live half, on a rendering desktop: a composited desktop keeps dedicated VRAM
        // busy (measured 845,787,136 bytes with nothing but the desktop open), so after the
        // two-sample warm-up the per-adapter memory must not read zero. A machine whose
        // only display is the Basic fallback has no engine counter at all; skip the pin
        // there rather than demand hardware nobody has.
        var logFactory = new Microsoft.Extensions.Logging.LoggerFactory();
        using var sampler = new SystemSampler(
            logFactory.CreateLogger<SystemSampler>(),
            new PdhQuery(logFactory.CreateLogger<PdhQuery>()),
            collectGpu: true,
            collectTemperatures: false);

        sampler.Collect();
        await Task.Delay(1000);
        sampler.Collect();

        var loads = sampler.SampleGpuEngineLoad();
        var memories = sampler.SampleGpuMemory();
        _log.WriteLine($"engine rows: {loads.Count}; memory keys: {memories.Count}");
        foreach (var kv in memories)
            _log.WriteLine($"  mem luid={kv.Key} bytes={kv.Value}");

        if (loads.Count == 0)
        {
            _log.WriteLine("no GPU engine counter on this machine; the memory pin has nothing to read");
            return;
        }

        // The dominant adapter is the one the inventory reports; its memory must be real.
        var dominant = loads[0].AdapterLuid;
        Assert.True(memories.TryGetValue(dominant, out var bytes),
            $"no memory row for the dominant adapter {dominant}");
        _log.WriteLine($"dominant {dominant}: {bytes} bytes dedicated+shared");
        Assert.True(bytes > 0,
            "the desktop is rendering, so dedicated VRAM cannot read zero - this is the D91 shape");
    }
}
