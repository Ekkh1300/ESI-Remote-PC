using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using Esi.Protocol;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Capture;

/// <summary>One JPEG-encoded tile ready to go on the wire.</summary>
public readonly record struct EncodedTile(ushort Col, ushort Row, ushort Width, ushort Height, byte[] Jpeg);

public sealed class EncodedFrame
{
    public required uint FrameIndex { get; init; }
    public required ushort DisplayId { get; init; }
    public required int ScreenWidth { get; init; }
    public required int ScreenHeight { get; init; }
    public required ushort GridCols { get; init; }
    public required ushort GridRows { get; init; }
    public required bool IsKeyframe { get; init; }
    public required IReadOnlyList<EncodedTile> Tiles { get; init; }
    public required int TotalBytes { get; init; }

    /// <summary>Serialises into the fixed binary layout documented in PROTOCOL.md §7.5.</summary>
    public byte[] ToWire()
    {
        var descriptorBytes = Tiles.Count * TileDescriptor.Size;
        var total = ScreenFrameHeader.Size + descriptorBytes + TotalBytes;

        var buffer = new byte[total];
        var header = IsKeyframe
            ? ScreenFrameHeader.Keyframe(FrameIndex, DisplayId, ScreenWidth, ScreenHeight, GridCols, GridRows, (ushort)Tiles.Count, (uint)TotalBytes)
            : ScreenFrameHeader.TileUpdate(FrameIndex, DisplayId, ScreenWidth, ScreenHeight, GridCols, GridRows, (ushort)Tiles.Count, (uint)TotalBytes);

        header.ToBytes().CopyTo(buffer, 0);

        var offset = ScreenFrameHeader.Size;
        foreach (var tile in Tiles)
        {
            var descriptor = new TileDescriptor
            {
                Col = tile.Col,
                Row = tile.Row,
                TileWidth = tile.Width,
                TileHeight = tile.Height,
                TileLength = (uint)tile.Jpeg.Length,
            };
            descriptor.ToBytes().CopyTo(buffer, offset);
            offset += TileDescriptor.Size;
        }

        foreach (var tile in Tiles)
        {
            tile.Jpeg.CopyTo(buffer, offset);
            offset += tile.Jpeg.Length;
        }

        return buffer;
    }
}

/// <summary>
/// Turns successive captures into a bandwidth-efficient tile delta stream.
/// <para>
/// <b>Design intent.</b> This is deliberately not "RDP, but worse". The pipeline is
/// structured so the pieces a real remote-desktop codec needs are already in place:
/// </para>
/// <list type="number">
///   <item><description>Display enumeration and geometry come from a provider interface,
///   so multi-monitor is a provider change, not a protocol change.</description></item>
///   <item><description>Capture is provider-abstracted, so DXGI Desktop Duplication
///   drops in without touching the encoder.</description></item>
///   <item><description>Frames are content-addressed by tile, so a future real
///   frame-prediction or RFX-style codec replaces the JPEG step but keeps the tile
///   bookkeeping, the resync path and the wire format.</description></item>
///   <item><description>A periodic forced keyframe bounds the worst case after packet
///   loss, which is what makes lossy Wi-Fi usable rather than a permanently corrupt
///   image.</description></item>
/// </list>
/// </summary>
public sealed class ScreenStreamEncoder(ILogger<ScreenStreamEncoder> log)
{
    private readonly Dictionary<int, TileState> _states = [];
    private uint _frameIndex;
    private long _lastChangeUnixMs;
    private int _framesSinceKeyframe;

    /// <summary>
    /// How long a keyframe may be withheld before one is forced, in milliseconds.
    /// <para>
    /// This is the heartbeat of the stream (D46). Without it, a desktop that is not changing
    /// produces no frame at all: the encoder hashes each tile, finds every one unchanged, and
    /// returns null. The phone then holds a single correct-but-frozen picture forever, which it
    /// cannot distinguish from a stream that died - and the only way out was for the user to stop
    /// and start again.
    /// </para>
    /// <para>
    /// Measured in time, not in frames, and that is the part that matters. A frame counter only
    /// advances when a frame is produced, so counting frames guaranteed the heartbeat could never
    /// fire on exactly the screens that needed it. Two seconds is comfortably under the
    /// keep-alive and well above the rate at which a redundant full frame is noticeable.
    /// </para>
    /// </summary>
    private long _keyframeIntervalMs = 2000;

    /// <summary>When the last keyframe was produced, on the capture clock.</summary>
    private long _lastKeyframeUnixMs;

    private sealed class TileState
    {
        public required long Hash { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    /// <summary>Resets all state; call on start, on stop and on resync.</summary>
    public void Reset()
    {
        lock (_states)
        {
            _states.Clear();
            _frameIndex = 0;
            _lastKeyframeUnixMs = 0;
            _lastChangeUnixMs = 0;
        }
    }

    /// <summary>Forces the next encoded frame to be a full keyframe.</summary>
    public void RequestKeyframe()
    {
        lock (_states)
        {
            // Zero means "no keyframe has been sent yet", which forces the next frame to be a full
            // one. A frame counter cannot express a request the way a clock can, because the
            // method that would observe it only runs when a frame is produced (D46).
            _lastKeyframeUnixMs = 0;
        }
    }

    /// <summary>
    /// Encodes a capture. When <paramref name="forceKeyframe"/> is false, only tiles whose
    /// content hash changed since the previous frame are encoded and sent.
    /// </summary>
    public EncodedFrame? Encode(CapturedFrame frame, int displayId, int gridCols, int gridRows, int quality, bool forceKeyframe)
    {
        gridCols = Math.Clamp(gridCols, 1, 32);
        gridRows = Math.Clamp(gridRows, 1, 32);
        quality = Math.Clamp(quality, 1, 100);

        var tileWidth = frame.Width / gridCols;
        var tileHeight = frame.Height / gridRows;
        if (tileWidth <= 0 || tileHeight <= 0)
            return null;

        // The last column/row absorbs the remainder so the grid always covers the screen.
        List<EncodedTile> changed = [];
        var isKeyframe = forceKeyframe;
        var totalBytes = 0;
        var encoder = ResolveEncoder(quality);

        lock (_states)
        {
            // D46: a keyframe has to arrive on a timer, not on a frame count.
            //
            // A frame count does not work here, which is the whole difficulty and it cost a long
            // afternoon to find. The counter could only be incremented on a frame that was
            // produced - the method returns null when nothing changed, and the caller never
            // reaches the code that would count it. So on a still desktop the counter never moved,
            // never reached its threshold, and no keyframe was ever due. The stream produced one
            // frame and then nothing, forever, and every layer above reported health.
            //
            // Measured against wall-clock time it behaves: the timestamp is on the captured
            // frame, so it advances whether or not anything is sent.
            var sinceKeyframe = _lastKeyframeUnixMs == 0
                ? long.MaxValue
                : frame.CapturedAtUnixMs - _lastKeyframeUnixMs;

            var mustKeyframe = isKeyframe ||
                               _states.Count == 0 ||
                               _states.Count != gridCols * gridRows ||
                               sinceKeyframe >= _keyframeIntervalMs;

            for (var row = 0; row < gridRows; row++)
            {
                for (var col = 0; col < gridCols; col++)
                {
                    var x = col * tileWidth;
                    var y = row * tileHeight;
                    var w = col == gridCols - 1 ? frame.Width - x : tileWidth;
                    var h = row == gridRows - 1 ? frame.Height - y : tileHeight;

                    if (w <= 0 || h <= 0)
                        continue;

                    var hash = HashTile(frame, x, y, w, h);

                    if (!mustKeyframe)
                    {
                        if (_states.TryGetValue(row * gridCols + col, out var state) && state.Hash == hash)
                            continue; // unchanged, do not transmit
                    }

                    var jpeg = EncodeTile(frame, x, y, w, h, encoder);
                    if (jpeg is null || jpeg.Length == 0)
                        continue;

                    changed.Add(new EncodedTile((ushort)col, (ushort)row, (ushort)w, (ushort)h, jpeg));
                    totalBytes += jpeg.Length;
                    _states[row * gridCols + col] = new TileState { Hash = hash, Width = w, Height = h };
                }
            }

            var now = frame.CapturedAtUnixMs;
            if (changed.Count > 0)
            {
                _lastChangeUnixMs = now;
                isKeyframe = mustKeyframe;
            }

            // Only a frame that actually goes out resets the heartbeat. Counting a keyframe that
            // was then dropped would push the next one further away, which is how a timer like
            // this quietly stops firing (D46).
            if (isKeyframe && changed.Count > 0)
                _lastKeyframeUnixMs = now;
        }

        if (changed.Count == 0)
            return null;

        return new EncodedFrame
        {
            FrameIndex = unchecked(_frameIndex++),
            DisplayId = (ushort)displayId,
            ScreenWidth = frame.Width,
            ScreenHeight = frame.Height,
            GridCols = (ushort)gridCols,
            GridRows = (ushort)gridRows,
            IsKeyframe = isKeyframe,
            Tiles = changed,
            TotalBytes = totalBytes,
        };
    }

    /// <summary>Milliseconds since the last frame that actually contained a change.</summary>
    public long MillisecondsSinceChange(long nowUnixMs) => nowUnixMs - _lastChangeUnixMs;

    // -----------------------------------------------------------------------------
    // Tile hashing and encoding
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Fast 64-bit content hash over a tile. A rolling FNV-1a over the raw BGRA bytes is
    /// roughly 20x cheaper than encoding the tile, which is the entire point: the hash
    /// decides whether to do the expensive work at all.
    /// </summary>
    private static long HashTile(CapturedFrame frame, int x, int y, int width, int height)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        var rowBytes = width * 4;

        for (var row = 0; row < height; row++)
        {
            var start = (y + row) * frame.Stride + (x * 4);
            var span = frame.Pixels.AsSpan(start, rowBytes);

            for (var i = 0; i < span.Length; i++)
            {
                hash ^= span[i];
                hash *= prime;
            }
        }

        return unchecked((long)hash);
    }

    private readonly Dictionary<int, ImageCodecInfo> _encoders = [];

    private ImageCodecInfo ResolveEncoder(int quality)
    {
        if (_encoders.TryGetValue(quality, out var cached))
            return cached;

        ImageCodecInfo? jpeg = null;
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
            {
                jpeg = codec;
                break;
            }
        }

        if (jpeg is null)
            throw new InvalidOperationException("No JPEG encoder is available in this process.");

        // System.Drawing.Encoders.EncoderParameters is mutable and not thread safe, so a
        // fresh set is built per call; caching the codec itself is the useful part.
        _encoders[quality] = jpeg;
        return jpeg;
    }

    private byte[]? EncodeTile(CapturedFrame frame, int x, int y, int width, int height, ImageCodecInfo codec)
    {
        try
        {
            using var bitmap = ToBitmap(frame, x, y, width, height);
            using var ms = new MemoryStream(capacity: width * height / 2);

            var quality = Math.Clamp(_lastQuality, 1, 100);
            var parameters = new EncoderParameters(1);
            try
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bitmap.Save(ms, codec, parameters);
            }
            finally
            {
                parameters.Dispose();
            }

            return ms.ToArray();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not JPEG-encode tile {X},{Y} ({W}x{H})", x, y, width, height);
            return null;
        }
    }

    private int _lastQuality = 70;

    /// <summary>Updates the JPEG quality used by subsequent frames.</summary>
    public void SetQuality(int quality) => _lastQuality = Math.Clamp(quality, 1, 100);

    /// <summary>
    /// Copies a sub-rectangle out of the captured frame into a standalone 24-bit bitmap.
    /// 24-bit rather than 32-bit because the alpha channel is always fully opaque and
    /// skipping it shrinks every tile by a quarter.
    /// </summary>
    private static Bitmap ToBitmap(CapturedFrame frame, int x, int y, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = width * 3;
            var target = new byte[rowBytes];
            var source = frame.Pixels;

            for (var row = 0; row < height; row++)
            {
                var sourceOffset = (y + row) * frame.Stride + (x * 4);
                for (var col = 0; col < width; col++)
                {
                    // Source is BGRA; target is BGR.
                    target[col * 3 + 0] = source[sourceOffset + (col * 4) + 0];
                    target[col * 3 + 1] = source[sourceOffset + (col * 4) + 1];
                    target[col * 3 + 2] = source[sourceOffset + (col * 4) + 2];
                }

                System.Runtime.InteropServices.Marshal.Copy(target, 0,
                    IntPtr.Add(data.Scan0, row * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
