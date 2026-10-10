using Esi.Platform.Capture;
using Esi.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D46: the screen stream sent exactly one frame and then went silent, on a still desktop.
/// <para>
/// The encoder is a tile-delta codec. It hashes each tile, skips any that is unchanged, and
/// returns <c>null</c> when they all match. A still desktop is the normal case, so the phone held
/// one correct-but-frozen picture forever, and nothing on either side could tell that apart from
/// a dead stream. The only way out was for the user to press Stop and Start.
/// </para>
/// <para>
/// The first attempt at a fix counted frames and passed its own tests, and was still broken in
/// production - which is why these tests are built the way they are. A frame counter can only
/// advance inside code that runs when a frame is produced, and this method returns before that
/// code is reached whenever nothing changed. Counting frames therefore guaranteed the heartbeat
/// could never fire on exactly the screens that needed it. Every test here drives the clock
/// instead, because that is what a real capture does.
/// </para>
/// </summary>
public class ScreenStreamHeartbeatTests
{
    private const int Width = 320;
    private const int Height = 180;

    /// <summary>
    /// A capture at <paramref name="atUnixMs"/>. The pixels do not change unless
    /// <paramref name="seed"/> does, so passing the same seed repeatedly is a still desktop.
    /// </summary>
    private static CapturedFrame Frame(long atUnixMs, byte seed = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            pixels[i] = (byte)((x + seed) & 0xFF);
            pixels[i + 1] = (byte)(y & 0xFF);
            pixels[i + 2] = seed;
            pixels[i + 3] = 0xFF;
        }

        return new CapturedFrame
        {
            Width = Width,
            Height = Height,
            Stride = Width * 4,
            Pixels = pixels,
            CapturedAtUnixMs = atUnixMs,
        };
    }

    private static ScreenStreamEncoder NewEncoder() =>
        new(NullLogger<ScreenStreamEncoder>.Instance);

    [Fact]
    public void A_still_desktop_keeps_producing_keyframes()
    {
        // The defect, stated as one assertion: ten seconds of identical captures at the idle rate
        // produced exactly one frame, and everything downstream reported a healthy stream.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var produced = 0;
        for (var i = 0; i < 30; i++)
        {
            // 3 fps, which is what CapturePump uses when the screen has been still for 400 ms.
            var at = start + (long)(i * (1000.0 / 3));
            if (encoder.Encode(Frame(at), 0, 2, 2, 70, forceKeyframe: false) is not null)
                produced++;
        }

        Assert.True(produced >= 4,
            $"ten seconds of an unchanging desktop produced {produced} frames. The phone freezes "
            + "on one picture and cannot tell a still screen from a dead stream.");
    }

    [Fact]
    public void The_heartbeat_is_measured_in_time_and_not_in_frames()
    {
        // The guard that matters. If someone re-introduces a frame counter, this fails - and it is
        // the version that passed while the product was broken, because its test fed the encoder
        // the same timestamp every time, so no amount of counting could ever have elapsed.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // One frame, then a long silence, then one more: the timestamps are what must carry the
        // heartbeat, not the number of calls.
        Assert.NotNull(encoder.Encode(Frame(start), 0, 2, 2, 70, forceKeyframe: false));

        Assert.Null(encoder.Encode(Frame(start + 100), 0, 2, 2, 70, forceKeyframe: false));
        Assert.NotNull(encoder.Encode(Frame(start + 5000), 0, 2, 2, 70, forceKeyframe: false));
    }

    [Fact]
    public void A_quiet_screen_does_not_resend_a_frame_every_tick()
    {
        // The heartbeat must not defeat the delta codec. Sub-second samples of an unchanged screen
        // are the case where the codec earns its keep, and a frame per tick would make a 15 fps
        // stream a full-frame stream on every screen that is not animating.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.NotNull(encoder.Encode(Frame(start), 0, 2, 2, 70, forceKeyframe: false));

        var produced = 0;
        for (var i = 1; i <= 20; i++)
        {
            // 20 samples across 660 ms - inside one heartbeat interval.
            if (encoder.Encode(Frame(start + (i * 33)), 0, 2, 2, 70, forceKeyframe: false) is not null)
                produced++;
        }

        Assert.True(produced < 20,
            $"every one of 20 sub-second samples was transmitted ({produced}); the delta codec is "
            + "not doing any work");
    }

    [Fact]
    public void A_changing_desktop_sends_only_what_changed()
    {
        // The heartbeat must not turn every frame into a full one when something does move, which
        // is the whole point of tiling.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.NotNull(encoder.Encode(Frame(start), 0, 2, 2, 70, forceKeyframe: true));

        // Change only the bottom-left pixel, so exactly one of the four tiles moves. `seed` is
        // applied to every pixel, which would move all of them and prove nothing about the delta.
        var moved = Frame(start + 33);
        var corner = ((Height - 1) * Width + (Width - 1)) * 4;
        moved.Pixels[corner] ^= 0xFF;

        var update = encoder.Encode(moved, 0, 2, 2, 70, forceKeyframe: false);

        Assert.NotNull(update);
        Assert.False(update!.IsKeyframe);
        Assert.Equal(1, update.Tiles.Count);
        Assert.Equal(1, update.Tiles[0].Col);
        Assert.Equal(1, update.Tiles[0].Row);
    }

    [Fact]
    public void A_keyframe_carries_every_tile()
    {
        // A partial frame is only meaningful to a client that already has the rest. If a
        // "keyframe" ever carried only the tiles that happened to hash differently, a phone that
        // had just connected would draw a partial screen and call it a whole one.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.NotNull(encoder.Encode(Frame(start), 0, 2, 2, 70, forceKeyframe: true));

        var sawFull = false;
        for (var i = 1; i <= 40 && !sawFull; i++)
        {
            var at = start + (long)(i * (1000.0 / 3));
            var encoded = encoder.Encode(Frame(at), 0, 2, 2, 70, forceKeyframe: false);
            if (encoded is not null && encoded.IsKeyframe)
                sawFull = encoded.Tiles.Count == 4;
        }

        Assert.True(sawFull,
            "no full keyframe was produced over 13 seconds of an unchanged screen, so a client "
            + "that lost its tile cache would never be repaired");
    }

    [Fact]
    public void Frame_index_keeps_climbing_so_the_client_can_order_frames()
    {
        // The phone drops a frame whose index has not advanced. If the index were pinned while the
        // heartbeat was sending, every refresh would be discarded as out-of-order and the picture
        // would stay frozen even though frames were arriving.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.NotNull(encoder.Encode(Frame(start), 0, 2, 2, 70, forceKeyframe: true));

        var last = -1L;
        for (var i = 1; i <= 30; i++)
        {
            var at = start + (long)(i * (1000.0 / 3));
            var encoded = encoder.Encode(Frame(at), 0, 2, 2, 70, forceKeyframe: false);
            if (encoded is null) continue;

            Assert.True(encoded.FrameIndex > last,
                $"frame index went from {last} to {encoded.FrameIndex}; the client drops "
                + "non-advancing frames");
            last = encoded.FrameIndex;
        }

        Assert.True(last > 0, "no frame after the first was ever produced");
    }

    [Fact]
    public void Requesting_a_keyframe_forces_the_next_frame_to_be_a_full_one()
    {
        // Start and every resync go through this, and the phone depends on it: after a resync the
        // client has dropped its tiles, so a partial frame would leave it with a partly blank
        // screen that no later frame would necessarily repair.
        var encoder = NewEncoder();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.NotNull(encoder.Encode(Frame(start), 0, 2, 2, 70, forceKeyframe: false));
        Assert.Null(encoder.Encode(Frame(start + 33), 0, 2, 2, 70, forceKeyframe: false));

        encoder.RequestKeyframe();

        var forced = encoder.Encode(Frame(start + 66), 0, 2, 2, 70, forceKeyframe: false);

        Assert.NotNull(forced);
        Assert.True(forced!.IsKeyframe, "RequestKeyframe did not force a full frame");
        Assert.Equal(4, forced.Tiles.Count);
    }
}