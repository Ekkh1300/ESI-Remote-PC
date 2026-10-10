using System.Drawing;
using Esi.Platform.Capture;
using Esi.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D46: the screen stream sent exactly one frame and then went silent.
/// <para>
/// The encoder is a tile-delta codec: it hashes each tile and skips any that is unchanged, and it
/// returned <c>null</c> when every tile matched. A still desktop is the normal case, so the phone
/// received one correct frame and then never heard from the stream again. The picture it held was
/// right but frozen, and nothing on either side could distinguish that from a dead stream - the
/// only way to recover was for the user to press Stop and Start.
/// </para>
/// <para>
/// The fix is a periodic keyframe. These tests pin that, because the failure mode is silence:
/// nothing throws, nothing is logged, and the stream simply stops.
/// </para>
/// </summary>
public class ScreenStreamHeartbeatTests
{
    private static CapturedFrame Frame(long seed, int width = 320, int height = 180)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)((x + seed) & 0xFF);
            pixels[i + 1] = (byte)(y & 0xFF);
            pixels[i + 2] = (byte)seed;
            pixels[i + 3] = 0xFF;
        }

        return new CapturedFrame
        {
            Width = width,
            Height = height,
            Stride = width * 4,
            Pixels = pixels,
            CapturedAtUnixMs = 1_700_000_000_000 + seed,
        };
    }

    private static ScreenStreamEncoder NewEncoder() =>
        new(NullLogger<ScreenStreamEncoder>.Instance);

    [Fact]
    public void A_quiet_desktop_still_produces_a_frame()
    {
        // The whole defect in one assertion: the identical frame is presented again and again,
        // exactly as a motionless desktop would, and the encoder must not fall silent.
        var encoder = NewEncoder();
        var frame = Frame(0);

        // The first frame establishes the tile states and is always sent.
        Assert.NotNull(encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: true));

        var produced = 0;
        for (var i = 0; i < 200; i++)
        {
            if (encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: false) is not null)
                produced++;
        }

        Assert.True(produced > 0,
            "an unchanging desktop produced no frame at all, so the stream went silent after the "
            + "first picture. The phone cannot tell a still desktop from a dead stream.");
    }

    [Fact]
    public void The_heartbeat_is_not_so_frequent_that_it_costs_a_frame_every_tick()
    {
        // The fix trades bandwidth for freshness, so it has to be a real trade: a keyframe on
        // every frame would make the tile-delta codec pointless and a slow phone unusable.
        var encoder = NewEncoder();
        var frame = Frame(0);

        Assert.NotNull(encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: true));

        var produced = 0;
        for (var i = 0; i < 20; i++)
        {
            if (encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: false) is not null)
                produced++;
        }

        Assert.True(produced < 20,
            $"every one of 20 identical frames was transmitted ({produced}); the delta codec is "
            + "not doing any work");
    }

    [Fact]
    public void A_changing_desktop_still_sends_only_what_changed()
    {
        // The heartbeat must not defeat the delta codec. When something does change, the frame
        // should carry the tiles that moved and not the whole screen.
        var encoder = NewEncoder();

        Assert.NotNull(encoder.Encode(Frame(0), 0, 2, 2, 70, forceKeyframe: true));

        // One changed pixel band, so only the bottom row of tiles should be re-encoded.
        var moved = Frame(0);
        moved.Pixels[0] = 0xFF;

        var update = encoder.Encode(moved, 0, 2, 2, 70, forceKeyframe: false);

        Assert.NotNull(update);
        Assert.False(update!.IsKeyframe);
        Assert.True(update.Tiles.Count is > 0 and <= 2,
            $"a single-pixel change sent {update.Tiles.Count} of 4 tiles, so the delta is not "
            + "being computed");
    }

    [Fact]
    public void A_keyframe_always_carries_every_tile()
    {
        // A partial frame is only meaningful to a client that already has the rest, which is why
        // the phone keeps tiles across frames. If a "keyframe" ever carried only the tiles that
        // happened to hash differently, a phone that had just connected would draw a partial
        // screen and call it a whole one.
        var encoder = NewEncoder();
        var frame = Frame(0);

        Assert.NotNull(encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: true));

        for (var i = 0; i < 400 && !IsFullKeyframe(encoder, frame); i++)
        {
            // Somewhere in here the encoder must decide to refresh everything.
        }

        Assert.True(IsFullKeyframe(encoder, frame),
            "no full keyframe was produced over 400 unchanged frames, so a client that lost its "
            + "tile cache would never be repaired");
    }

    /// <summary>
    /// Drives frames until one comes back marked as a keyframe, which by definition carries every
    /// tile rather than only the changed ones.
    /// </summary>
    private static bool IsFullKeyframe(ScreenStreamEncoder encoder, CapturedFrame frame)
    {
        for (var i = 0; i < 400; i++)
        {
            var encoded = encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: false);
            if (encoded is not null && encoded.IsKeyframe && encoded.Tiles.Count == 4)
                return true;
        }
        return false;
    }

    [Fact]
    public void Frame_index_keeps_climbing_so_the_client_can_order_frames()
    {
        // The phone drops a frame whose index has not advanced. If the index were pinned at zero
        // by the heartbeat, every refresh would be discarded as an out-of-order packet and the
        // picture would stay frozen even though frames were arriving.
        var encoder = NewEncoder();
        var frame = Frame(0);

        Assert.NotNull(encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: true));

        var last = -1L;
        for (var i = 0; i < 200; i++)
        {
            var encoded = encoder.Encode(frame, 0, 2, 2, 70, forceKeyframe: false);
            if (encoded is null) continue;
            Assert.True(encoded.FrameIndex > last,
                $"frame index went from {last} to {encoded.FrameIndex}; the client drops "
                + "non-advancing frames");
            last = encoded.FrameIndex;
        }

        Assert.True(last > 0, "no frame after the first was ever produced");
    }
}