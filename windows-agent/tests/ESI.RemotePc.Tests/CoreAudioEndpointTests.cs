using Esi.Platform.Native;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// An opened audio endpoint records <i>which</i> device it opened, and can say whether that
/// device is still the one Windows would send a volume key to.
/// <para>
/// This is the whole of the "the volume slider only affects one output" defect. The controller
/// used to open the default render endpoint once and hold it for the life of the process. That
/// is fine right up until a Bluetooth headset connects: Windows moves the default to the
/// headset, while the controller keeps writing to the speakers. The volume keys were never
/// wrong - they go to whatever is default *now* - so the phone moved a number that belonged to
/// a device nobody was listening to, and the headset you were actually hearing stayed where it
/// was.
/// </para>
/// <para>
/// Both tests below are conditional on the machine actually having a render endpoint. A build
/// agent in session 0, and a CI runner with no sound card, have none, and there is nothing to
/// compare there. They are not silently passing: each asserts the two halves agree about the
/// absence.
/// </para>
/// </summary>
public sealed class CoreAudioEndpointTests
{
    [Fact]
    public void An_opened_endpoint_names_the_device_it_opened_and_is_still_the_default()
    {
        var defaultId = CoreAudioEndpoint.TryDefaultDeviceId();
        var endpoint = CoreAudioEndpoint.TryOpen();

        if (endpoint is null)
        {
            // No endpoint: then there is no default id either. If these ever disagree, the
            // two halves of this file have drifted and the conditional in every consumer is
            // deciding on the wrong one.
            Assert.Null(defaultId);
            return;
        }

        try
        {
            // Without a recorded id there is nothing to compare against, and IsStillDefault
            // would have to answer "yes" to everything - which is the bug.
            Assert.NotNull(endpoint.DeviceId);
            Assert.Equal(defaultId, endpoint.DeviceId);

            // True at the moment of opening, by construction. The endpoint only goes stale
            // once the default moves away from it, and this is the baseline that makes a
            // later move detectable.
            Assert.True(endpoint.IsStillDefault());
        }
        finally
        {
            endpoint.Release();
        }
    }

    [Fact]
    public void The_default_device_id_is_the_same_on_two_consecutive_reads()
    {
        // An unchanged machine must give the same answer twice. If it did not,
        // IsStillDefault would report a device change on every call and the controller
        // would reopen its endpoint forever, which is a different bug wearing this one's
        // clothes.
        var first = CoreAudioEndpoint.TryDefaultDeviceId();
        var second = CoreAudioEndpoint.TryDefaultDeviceId();

        Assert.Equal(first, second);
        Assert.True(first is null || first.Length > 0);
    }
}