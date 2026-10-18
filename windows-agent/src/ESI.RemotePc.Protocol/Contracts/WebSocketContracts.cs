using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Esi.Protocol;

/// <summary>
/// Client → server WebSocket control frame. Mirrors docs/PROTOCOL.md §7.2.
/// </summary>
public sealed class WsClientFrame
{
    [JsonPropertyName("p")] public int P { get; set; } = ProtocolConstants.Version;
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ts")] public long? Ts { get; set; }
    [JsonPropertyName("channels")] public List<string>? Channels { get; set; }
    [JsonPropertyName("displayId")] public int? DisplayId { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("quality")] public int? Quality { get; set; }
    [JsonPropertyName("grid")] public int[]? Grid { get; set; }
    [JsonPropertyName("inputEnabled")] public bool? InputEnabled { get; set; }
    [JsonPropertyName("intervalMs")] public int? IntervalMs { get; set; }

    /// <summary>
    /// True when the phone asked the PC to silence its own speakers while the phone carries
    /// its sound (D87). Absent from an older phone, which the PC reads as "no silencing".
    /// </summary>
    [JsonPropertyName("silenceLocal")] public bool? SilenceLocal { get; set; }
    [JsonPropertyName("input")] public RemoteInputRequest? Input { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("down")] public bool? Down { get; set; }
    [JsonPropertyName("mods")] public List<string>? Mods { get; set; }
    [JsonPropertyName("sequence")] public long? Sequence { get; set; }
}

/// <summary>Server → client WebSocket control frame. Mirrors docs/PROTOCOL.md §7.5.</summary>
public sealed class WsServerFrame
{
    [JsonPropertyName("p")] public int P { get; set; } = ProtocolConstants.Version;
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ts")] public long? Ts { get; set; }
    [JsonPropertyName("data")] public object? Data { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    public static WsServerFrame Of(string type, string? id = null, object? data = null)
        => new() { T = type, Id = id, Data = data, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
}

/// <summary>
/// Fixed 32-byte header that prefixes every binary screen-stream frame.
/// Layout is fixed and little-endian; see docs/PROTOCOL.md §7.5.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ScreenFrameHeader
{
    /// <summary>'E','S','I','V' in that byte order.</summary>
    public uint Magic;              // offset 0
    public ushort FrameType;        // offset 4
    public ushort DisplayId;        // offset 6
    public uint FrameIndex;         // offset 8
    public ushort TileCount;        // offset 12
    public ushort Codec;            // offset 14
    public int ScreenWidth;         // offset 16
    public int ScreenHeight;        // offset 20
    public ushort GridCols;         // offset 24
    public ushort GridRows;         // offset 26
    public uint PayloadLength;      // offset 28

    public const uint ExpectedMagic = 0x56495345; // "ESIV" little-endian
    public const int Size = 32;

    public static ScreenFrameHeader Keyframe(uint index, ushort displayId, int w, int h, ushort cols, ushort rows, ushort tiles, uint payloadLen)
        => new() { Magic = ExpectedMagic, FrameType = FrameTypes.Keyframe, DisplayId = displayId, FrameIndex = index,
                   TileCount = tiles, Codec = Codecs.Jpeg, ScreenWidth = w, ScreenHeight = h,
                   GridCols = cols, GridRows = rows, PayloadLength = payloadLen };

    public static ScreenFrameHeader TileUpdate(uint index, ushort displayId, int w, int h, ushort cols, ushort rows, ushort tiles, uint payloadLen)
        => new() { Magic = ExpectedMagic, FrameType = FrameTypes.TileUpdate, DisplayId = displayId, FrameIndex = index,
                   TileCount = tiles, Codec = Codecs.Jpeg, ScreenWidth = w, ScreenHeight = h,
                   GridCols = cols, GridRows = rows, PayloadLength = payloadLen };

    public static ScreenFrameHeader ResyncRequest(uint index, ushort displayId, ushort tiles, uint payloadLen)
        => new() { Magic = ExpectedMagic, FrameType = FrameTypes.ResyncRequest, DisplayId = displayId, FrameIndex = index,
                   TileCount = tiles, Codec = Codecs.Jpeg, PayloadLength = payloadLen };

    /// <summary>
    /// A camera picture pushed up by the phone. <paramref name="width"/> and
    /// <paramref name="height"/> carry the picture's real dimensions, and there are no grid
    /// fields to read: a camera frame is one image, not a grid of tiles.
    /// </summary>
    public static ScreenFrameHeader Camera(uint index, int width, int height, uint payloadLen)
        => new() { Magic = ExpectedMagic, FrameType = FrameTypes.CameraFrame, FrameIndex = index,
                   TileCount = 1, Codec = Codecs.Jpeg, ScreenWidth = width, ScreenHeight = height,
                   PayloadLength = payloadLen };

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), Magic);
        BitConverter.TryWriteBytes(b.AsSpan(4, 2), FrameType);
        BitConverter.TryWriteBytes(b.AsSpan(6, 2), DisplayId);
        BitConverter.TryWriteBytes(b.AsSpan(8, 4), FrameIndex);
        BitConverter.TryWriteBytes(b.AsSpan(12, 2), TileCount);
        BitConverter.TryWriteBytes(b.AsSpan(14, 2), Codec);
        BitConverter.TryWriteBytes(b.AsSpan(16, 4), ScreenWidth);
        BitConverter.TryWriteBytes(b.AsSpan(20, 4), ScreenHeight);
        BitConverter.TryWriteBytes(b.AsSpan(24, 2), GridCols);
        BitConverter.TryWriteBytes(b.AsSpan(26, 2), GridRows);
        BitConverter.TryWriteBytes(b.AsSpan(28, 4), PayloadLength);
        return b;
    }
}

public static class FrameTypes
{
    public const ushort Keyframe = 1;
    public const ushort TileUpdate = 2;
    public const ushort ResyncRequest = 3;

    /// <summary>
    /// A camera frame from the phone, going the other way: the phone pushes it up and the PC
    /// serves it as MJPEG.
    /// <para>
    /// It reuses this header rather than inventing one. The magic, the codec field and the
    /// width/height/payload trio already say everything a camera frame needs, and a client can
    /// therefore tell the two apart with a single comparison on <see cref="FrameType"/> - which
    /// matters because both arrive on the same binary WebSocket.
    /// </para>
    /// <para>
    /// There are no tiles: a camera picture is one whole image, so <c>TileCount</c> is always 1
    /// and the payload is a single JPEG rather than a descriptor list.
    /// </para>
    /// </summary>
    public const ushort CameraFrame = 4;

    /// <summary>
    /// Audio from the PC to the phone, so the phone can play what the machine is playing.
    ///
    /// <para>
    /// It arrives on the same binary WebSocket as the screen and the camera, which is why it is
    /// a <c>FrameType</c> on the same header rather than a channel of its own: the phone's
    /// decoder already reads a 32-byte header off the front of every binary message, and giving
    /// audio a second framing would mean a second reader and a second place for the two to
    /// disagree.
    /// </para>
    ///
    /// <para>
    /// The audio fields reuse width, height and tile count rather than adding members, because
    /// the header is a fixed 32 bytes and both ends read it by offset (PROTOCOL.md §7.5). For
    /// audio, <c>ScreenWidth</c> is the sample rate in Hz, <c>ScreenHeight</c> is the channel
    /// count, and <c>TileCount</c> is unused because µ-law frames carry no block structure at
    /// all - a chunk is just a run of samples. The names are wrong for what they carry and that
    /// is a deliberate, documented lie: widening the struct would move every later field and
    /// break the screen path that is already deployed.
    /// </para>
    /// </summary>
    public const ushort AudioChunk = 5;
}

public static class Codecs
{
    public const ushort Jpeg = 1;
    /// <summary>Reserved for the future: a real RDP graphics pipeline / RemoteFX style codec.</summary>
    public const ushort RdpGraphicsPipeline = 2;

    /// <summary>
    /// G.711 mu-law: the codec this ships for audio.
    /// </summary>
    /// <remarks>
    /// Opus is the codec one would reach for, and it stays reserved. It would need a native
    /// encoder here and a native decoder inside the APK, and this repository has no native build
    /// at all - no NDK invocation, no CMake, no checked-in <c>jniLibs</c>. Either half of that
    /// would be code that could not be compiled, tested, or run on a device, which is worse than
    /// a slower codec that works.
    /// <para>
    /// mu-law is 48 KB/s for stereo at 24 kHz, against 100-150 KB for a single screen frame, and
    /// it is about 38 dB of signal-to-noise ratio: telephone quality. It is also memoryless, so
    /// there is no quantiser state for two independent implementations to disagree about.
    /// </para>
    /// </remarks>
    public const ushort MuLaw = 3;
}

/// <summary>Per-tile descriptor that precedes each tile's JPEG payload.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TileDescriptor
{
    public ushort Col;         // offset 0
    public ushort Row;         // offset 2
    public ushort TileWidth;   // offset 4
    public ushort TileHeight;  // offset 6
    public uint TileLength;    // offset 8

    public const int Size = 14;

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), Col);
        BitConverter.TryWriteBytes(b.AsSpan(2, 2), Row);
        BitConverter.TryWriteBytes(b.AsSpan(4, 2), TileWidth);
        BitConverter.TryWriteBytes(b.AsSpan(6, 2), TileHeight);
        BitConverter.TryWriteBytes(b.AsSpan(8, 4), TileLength);
        return b;
    }
}

/// <summary>Shared JSON settings for the wire protocol.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null, // every DTO carries explicit [JsonPropertyName]
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new JsonStringEnumConverter() },
        };
        return o;
    }
}
