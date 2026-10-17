using Esi.Platform.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D85: GPU load read zero on every modern Windows machine, and the reason was one string
/// assumption. The parser only accepted the instance name the documentation spells - leading
/// with the adapter's <c>luid_</c> - while Windows 10 2004+ leads with the owning process
/// instead, <c>pid_10864_luid_0x00000000_0x00012997_phys_0_eng_0_engtype_3D</c>, which was
/// measured directly on this machine's own counters (541 engine rows, all pid_-prefixed).
/// Every such name returned null from the extractor, every row was skipped, the per-adapter
/// aggregate was an empty list, and the phone's GPU channel showed nothing forever.
///
/// <para>
/// These pins hold the extractor to the names that actually exist in the field, including
/// the two adapter-memory spellings, so a load row and its memory row key identically and
/// the metrics pairing that joins them never splits.
/// </para>
/// </summary>
public sealed class GpuInstanceNameTests
{
    private readonly ITestOutputHelper _out;

    public GpuInstanceNameTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Accepts_the_process_leading_shape_modern_windows_writes()
    {
        // Measured verbatim from \GPU Engine(*)\Utilization Percentage on this machine.
        var key = SystemSampler.ExtractAdapterLuid(
            "pid_10864_luid_0x00000000_0x00012997_phys_0_eng_0_engtype_3D");

        Assert.Equal("luid_0x00000000_0x00012997", key);
    }

    [Fact]
    public void Accepts_the_documented_adapter_leading_shape()
    {
        var key = SystemSampler.ExtractAdapterLuid("luid_0x00000000_0x0000ABCD_engtype_3D");

        Assert.Equal("luid_0x00000000_0x0000ABCD", key);
    }

    [Fact]
    public void Keys_the_adapter_memory_row_the_same_as_its_engine_row()
    {
        // \GPU Adapter Memory's instances carry the same LUID pair with a phys segment and no
        // engine segment. The metrics service joins a load row to its memory row by this key,
        // so the two spellings must land on one string or the join silently drops.
        var engine = SystemSampler.ExtractAdapterLuid(
            "pid_10864_luid_0x00000000_0x00012997_phys_0_eng_0_engtype_3D");
        var memory = SystemSampler.ExtractAdapterLuid("luid_0x00000000_0x00012997_phys_0");

        Assert.Equal(engine, memory);
    }

    [Fact]
    public void Falls_back_to_the_whole_name_when_only_one_luid_token_exists()
    {
        // Some builds spell the memory row with the LUID pair truncated to its high part.
        // There is no pair to extract, so the name itself is the key - degraded but never
        // a null that would drop the row.
        var key = SystemSampler.ExtractAdapterLuid("luid_0x00000000");

        Assert.Equal("luid_0x00000000", key);
    }

    [Fact]
    public void Returns_null_for_a_name_without_a_luid()
    {
        Assert.Null(SystemSampler.ExtractAdapterLuid("phys_0_eng_3"));
        Assert.Null(SystemSampler.ExtractAdapterLuid(""));
    }

    [Fact]
    public void Is_case_insensitive_like_pdh_instance_names_are_in_practice()
    {
        var key = SystemSampler.ExtractAdapterLuid(
            "PID_10864_LUID_0x00000000_0x00012997_phys_0_eng_0_engtype_3D");

        Assert.Equal("LUID_0x00000000_0x00012997", key);
    }
}
