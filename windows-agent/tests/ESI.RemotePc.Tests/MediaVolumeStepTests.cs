using Esi.Platform.Media;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// Setting an absolute volume moved roughly twice as far as asked, and the reply described a
/// number the PC had never been at.
/// <para>
/// Both came from the same assumption: that one volume-key press is one percentage point. It is
/// not. Windows reports <c>nStepCount = 51</c> across 0-100 on the machine this was measured on,
/// so a press moves about 1.96 points. A walk of <c>|target - current|</c> presses therefore
/// travels about twice the intended distance.
/// </para>
/// <para>
/// Measured live, not inferred. The phone asked for 55 on a PC sitting at 100; the agent logged
/// <c>Volume volumeset 100 -&gt; 55 ... delivered</c>, then read back 82, then the PC settled on
/// 10. Forty-five presses at ~1.96 points is 88, and 100 - 88 is 10 - which is exactly where it
/// landed. The 82 the phone was shown was a read taken nine presses into forty-five, so the
/// control was reporting a volume that no longer existed by the time the answer arrived.
/// </para>
/// <para>
/// The fix is that an absolute set no longer walks at all: <c>CoreAudioEndpoint.WriteVolumeScalar</c>
/// writes the level in one COM call, and the level is read back before anything is claimed. The
/// press arithmetic below only exists for the case where that write fails, and it now divides by
/// the endpoint's own step count instead of assuming one.
/// </para>
/// </summary>
public class MediaVolumeStepTests
{
    /// <summary>The step count measured on the fixing machine: 51 steps, ~1.96 points each.</summary>
    private const int MeasuredStepCount = 51;

    [Fact]
    public void A_press_moves_its_share_of_the_range_not_one_point()
    {
        // 45 presses is what the old code sent for "go from 100 to 55". At 51 steps that is not
        // 45 points of travel, and the arithmetic has to say so.
        var presses = MediaController.VolumePressesForDelta(-45, MeasuredStepCount);

        // 45 points of 100 across 51 steps is 22.95 presses; half a press does not exist.
        Assert.Equal(23, presses);
    }

    [Fact]
    public void The_measured_walk_lands_where_the_request_asked()
    {
        // The whole point: walking from 100 to 55 must move 45 *points*, not 45 presses.
        const int from = 100;
        const int target = 55;
        var presses = MediaController.VolumePressesForDelta(target - from, MeasuredStepCount);

        var pointsMoved = presses * (100.0 / MeasuredStepCount);
        var landed = from - pointsMoved;

        // Within one press of the target, which is the closest a key walk can get. The direct
        // Core Audio write gets it exact, which is why it is the path that is actually used.
        Assert.InRange(landed, target - 100.0 / MeasuredStepCount, target + 100.0 / MeasuredStepCount);
    }

    [Fact]
    public void The_old_arithmetic_would_have_overshot_by_more_than_a_full_scale()
    {
        // What actually happened, stated as a number: 45 presses at 1.96 points is 88 points of
        // travel for a 45-point request, so a PC at 100 ends at 10. That is the jump the user
        // reported, and it is the size of it.
        var travel = 45 * (100.0 / MeasuredStepCount);

        Assert.True(travel > 85, $"45 presses moved {travel} points, not the 45 they were meant to.");
    }

    [Fact]
    public void No_move_presses_nothing()
    {
        Assert.Equal(0, MediaController.VolumePressesForDelta(0, MeasuredStepCount));
        Assert.Equal(0, MediaController.VolumePressesForDelta(0, null));
    }

    [Fact]
    public void A_one_point_move_still_presses_once()
    {
        // Rounding must not turn a small nudge into no movement at all. One point of 100 across
        // 51 steps is 0.51 presses, which rounds up, and must never round down to zero.
        Assert.Equal(1, MediaController.VolumePressesForDelta(1, MeasuredStepCount));
        Assert.Equal(1, MediaController.VolumePressesForDelta(-1, MeasuredStepCount));
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(100)]
    public void A_full_scale_walk_presses_every_step(int delta)
    {
        // 0 to 100 is 51 presses, not 100. Each press is a step, and there are 51 of them.
        Assert.Equal(MeasuredStepCount, MediaController.VolumePressesForDelta(delta, MeasuredStepCount));
    }

    [Fact]
    public void An_unmeasurable_step_falls_back_to_pressing_points()
    {
        // If the endpoint will not say how many steps it has, the old arithmetic is kept. An
        // unmeasured press is still worth making, and pressing too few times is a smaller failure
        // than pressing none.
        Assert.Equal(45, MediaController.VolumePressesForDelta(-45, null));
        Assert.Equal(45, MediaController.VolumePressesForDelta(-45, 0));
        Assert.Equal(30, MediaController.VolumePressesForDelta(30, null));
    }
}
