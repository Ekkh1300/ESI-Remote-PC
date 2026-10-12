using System.Buffers.Binary;

using Esi.Protocol;

namespace Esi.Platform.Audio;

/// <summary>
/// Turns a run of 16-bit PCM samples into one wire frame: the shared 32-byte header, then one
/// µ-law byte per sample.
/// </summary>
/// <remarks>
/// Deliberately free of NAudio and of any socket, for the same reason
/// <c>ScreenStreamEncoder</c> is: the part that decides what goes on the wire has to be
/// testable without a sound card, an audio session or a phone. Everything that needs hardware
/// lives in <c>AudioPump</c> and delegates here.
/// </remarks>
public static class AudioPacketiser
{
    /// <summary>
    /// The sample rate the PC encodes at.
    /// </summary>
    /// <remarks>
    /// 24 kHz is the largest rate that is an exact integer division of the 48 kHz almost every
    /// Windows mix format runs at, so the resampler does exact decimation instead of a
    /// fractional-ratio filter, and the µ-law curve still covers 12 kHz of audio bandwidth.
    /// </remarks>
    public const int SampleRate = 24000;

    /// <summary>Channels the PC encodes.</summary>
    public const int Channels = 2;

    /// <summary>
    /// Samples per channel in one packet.
    /// </summary>
    /// <remarks>
    /// 480 is 20 ms at 24 kHz. Long enough that fifty packets a second do not each carry a
    /// header worth mentioning, short enough that a lost packet is a gap no longer than a blink.
    /// Smaller packets show up as an audible stutter every time the Wi-Fi decides to queue one;
    /// larger ones turn a single loss into a half-second hole.
    /// </remarks>
    public const int SamplesPerChannel = 480;

    /// <summary>Total samples - both channels - in one packet.</summary>
    public const int SamplesPerPacket = SamplesPerChannel * Channels;

    /// <summary>Bytes of µ-law payload in one packet.</summary>
    public const int PayloadBytes = SamplesPerPacket;

    /// <summary>Bytes one packet occupies on the wire, header included.</summary>
    public const int PacketBytes = ScreenFrameHeader.Size + PayloadBytes;

    /// <summary>
    /// Writes one packet into <paramref name="destination"/>.
    /// </summary>
    /// <param name="samples">
    /// Interleaved 16-bit PCM. Must be exactly <see cref="SamplesPerPacket"/> long, because a
    /// packet with a sample count the phone cannot predict is a packet it has to buffer and
    /// guess at.
    /// </param>
    /// <param name="sequence">Monotonic frame index, for the phone to drop late packets by.</param>
    /// <returns>Bytes written.</returns>
    public static int WritePacket(ReadOnlySpan<short> samples, uint sequence, Span<byte> destination)
    {
        if (samples.Length != SamplesPerPacket)
        {
            throw new ArgumentException(
                $"A packet carries {SamplesPerPacket} samples; got {samples.Length}.", nameof(samples));
        }
        if (destination.Length < PacketBytes)
        {
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; a packet needs {PacketBytes}.", nameof(destination));
        }

        // The audio fields reuse the header's picture-shaped members rather than adding any,
        // because the struct is fixed at 32 bytes and both ends read it by offset. Width is the
        // sample rate, height is the channel count, GridCols is the sample count per channel.
        // GridRows is unused, which is the clearest sign that these are not picture dimensions:
        // audio has no grid. PROTOCOL.md 7.5 says so, and widening the struct would move every
        // later field and break the screen stream that is already deployed.
        BinaryPrimitives.WriteUInt32LittleEndian(destination, ScreenFrameHeader.ExpectedMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], FrameTypes.AudioChunk);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], Codecs.MuLaw);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], Channels);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[24..], SamplesPerChannel);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], PayloadBytes);

        var payload = destination[ScreenFrameHeader.Size..];
        MuLaw.Encode(samples, payload);
        return PacketBytes;
    }

    /// <summary>Encodes one packet into a fresh array. The path the pump uses.</summary>
    public static byte[] Packet(ReadOnlySpan<short> samples, uint sequence)
    {
        var packet = new byte[PacketBytes];
        WritePacket(samples, sequence, packet);
        return packet;
    }
}
