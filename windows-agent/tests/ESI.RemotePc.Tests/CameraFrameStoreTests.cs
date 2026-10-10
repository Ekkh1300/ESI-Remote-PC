using System.Drawing;
using System.Drawing.Imaging;
using Esi.Platform.Camera;
using Esi.Protocol;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D44: a phone's camera, streamed up to the PC and served to anything that speaks MJPEG.
/// <para>
/// There is no virtual webcam device behind this and there cannot be: registering one as a real
/// camera needs a signed kernel-mode driver, and iVCam's <c>iVCam.sys</c> carries the Microsoft
/// Windows Hardware Compatibility Publisher certificate precisely because that is what it takes.
/// So the frames are published over HTTP as <c>multipart/x-mixed-replace</c>, which OBS, VLC and
/// ffmpeg all open as an ordinary network source.
/// </para>
/// <para>
/// These tests cover the parts that are pure logic - the one-frame store and the wire header - so
/// they run without a phone. The endpoint itself is exercised by the integration fixture.
/// </para>
/// </summary>
public class CameraFrameStoreTests
{
    private static CameraFrameStore NewStore(DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        return new CameraFrameStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraFrameStore>.Instance,
            () => clock);
    }

    [Fact]
    public void A_store_with_nothing_in_it_has_no_frame()
    {
        var store = NewStore();

        Assert.Null(store.Latest("nobody"));
        Assert.False(store.IsLive("nobody"));
    }

    [Fact]
    public void A_published_frame_is_readable_with_its_dimensions()
    {
        var store = NewStore();
        store.Publish("phone", new byte[] { 1, 2, 3 }, width: 640, height: 480);

        var frame = store.Latest("phone");

        Assert.NotNull(frame);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame!.Jpeg);
        Assert.Equal(640, frame.Width);
        Assert.Equal(480, frame.Height);
        Assert.True(store.IsLive("phone"));
    }

    [Fact]
    public void A_newer_frame_replaces_the_old_one_instead_of_joining_a_queue()
    {
        // The whole design rests on this. A camera makes frames faster than anyone watches them,
        // so holding a backlog would turn into growing latency, and a live view of the wrong ten
        // seconds is worse than a live view that skips.
        var store = NewStore();

        store.Publish("phone", new byte[] { 1 }, 10, 10);
        store.Publish("phone", new byte[] { 2 }, 20, 20);

        var frame = store.Latest("phone");

        Assert.NotNull(frame);
        Assert.Equal(new byte[] { 2 }, frame!.Jpeg);
        Assert.Equal(20, frame.Width);
    }

    [Fact]
    public void An_empty_payload_is_ignored()
    {
        // A zero-length "frame" would replace a good picture with nothing.
        var store = NewStore();
        store.Publish("phone", new byte[] { 9 }, 10, 10);

        store.Publish("phone", ReadOnlyMemory<byte>.Empty, 10, 10);

        var frame = store.Latest("phone");
        Assert.NotNull(frame);
        Assert.Equal(new byte[] { 9 }, frame!.Jpeg);
    }

    [Fact]
    public void A_stale_frame_is_not_served()
    {
        // A phone that stops streaming leaves its last picture behind. Serving it forever would
        // freeze a viewer on a picture that stopped moving, with nothing to say so - the same
        // silent-success failure that made play and pause look like they worked in D43.
        //
        // The clock moves instead of the test sleeping, so this is both instant and exact: right
        // up to the timeout the frame is still served, and one tick past it, it is not.
        var start = DateTimeOffset.UtcNow;
        var clock = start;
        var store = new CameraFrameStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraFrameStore>.Instance,
            () => clock);

        store.Publish("phone", new byte[] { 1 }, 10, 10);
        Assert.NotNull(store.Latest("phone"));

        clock = start + CameraFrameStore.FrameTimeout + TimeSpan.FromMilliseconds(1);

        Assert.Null(store.Latest("phone"));
        Assert.False(store.IsLive("phone"));
    }

    [Fact]
    public void A_forgotten_device_keeps_nothing()
    {
        var store = NewStore();
        store.Publish("phone", new byte[] { 1 }, 10, 10);

        store.Forget("phone");

        Assert.Null(store.Latest("phone"));
        Assert.False(store.IsLive("phone"));
    }

    [Fact]
    public void Two_phones_do_not_see_each_others_frames()
    {
        var store = NewStore();
        store.Publish("alice", new byte[] { 1 }, 10, 10);
        store.Publish("bob", new byte[] { 2 }, 20, 20);

        Assert.Equal(new byte[] { 1 }, store.Latest("alice")!.Jpeg);
        Assert.Equal(new byte[] { 2 }, store.Latest("bob")!.Jpeg);
    }
}

/// <summary>
/// The binary wire header that carries a camera frame up from the phone.
/// </summary>
public class CameraFrameHeaderTests
{
    [Fact]
    public void A_camera_header_round_trips_through_its_fixed_layout()
    {
        var header = ScreenFrameHeader.Camera(index: 42, width: 1280, height: 720, payloadLen: 512);

        var bytes = header.ToBytes();

        Assert.Equal(ScreenFrameHeader.Size, bytes.Length);
        Assert.Equal(ScreenFrameHeader.ExpectedMagic, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(FrameTypes.CameraFrame, BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(1280, BitConverter.ToInt32(bytes, 16));
        Assert.Equal(720, BitConverter.ToInt32(bytes, 20));
        Assert.Equal(512u, BitConverter.ToUInt32(bytes, 28));
    }

    [Fact]
    public void A_camera_frame_is_distinguishable_from_a_screen_frame()
    {
        // Both travel as binary on the same socket, so a receiver has to be able to tell them
        // apart by one comparison. If these ever collided the app would decode a camera picture
        // as a desktop tile and produce nonsense instead of an error.
        var camera = ScreenFrameHeader.Camera(1, 640, 480, 100);
        var screen = ScreenFrameHeader.Keyframe(1, 0, 1920, 1080, 4, 4, 16, 100);

        Assert.NotEqual(FrameTypes.CameraFrame, FrameTypes.Keyframe);
        Assert.NotEqual(FrameTypes.CameraFrame, FrameTypes.TileUpdate);
        Assert.Equal(FrameTypes.CameraFrame, BitConverter.ToUInt16(camera.ToBytes(), 4));
        Assert.Equal(FrameTypes.Keyframe, BitConverter.ToUInt16(screen.ToBytes(), 4));
    }

    [Fact]
    public void A_camera_frame_declares_exactly_one_image_and_no_grid()
    {
        var header = ScreenFrameHeader.Camera(1, 640, 480, 100);

        // TileCount 1 because a camera picture is one whole image, and the grid fields must be
        // zero: leaving stale tile geometry in them would invite a receiver to read tiles that
        // are not there.
        Assert.Equal(1, BitConverter.ToUInt16(header.ToBytes(), 12));
        Assert.Equal(0, BitConverter.ToUInt16(header.ToBytes(), 24));
        Assert.Equal(0, BitConverter.ToUInt16(header.ToBytes(), 26));
    }
}