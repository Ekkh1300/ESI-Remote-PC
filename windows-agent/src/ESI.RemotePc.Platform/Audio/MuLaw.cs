namespace Esi.Platform.Audio;

/// <summary>
/// G.711 µ-law, the companding curve telephony has used since the 1960s, applied to PC audio.
///
/// <para>
/// This is a deliberate choice against the obvious one. Opus is the obvious codec for audio and
/// it is not available here: NAudio ships no Opus encoder, and the Flutter side has no decoder
/// for it either, and this repository has no native build at all - no NDK invocation, no CMake,
/// no checked-in <c>jniLibs</c>. Either half of that would be code that cannot be built, cannot
/// be tested and cannot be run on a device, which is worse than a slower codec that works.
/// Opus stays reserved in <see cref="Esi.Protocol.Codecs"/> for when a native toolchain exists.
/// </para>
///
/// <para>
/// ADPCM was the second choice and was abandoned for a more specific reason. Its step index is
/// not carried in the stream: the decoder derives it from the adjustment table applied to the
/// index the nibble was encoded with. That makes the quantiser's state part of the wire format,
/// so the encoder and decoder have to agree on an adaptation rule that neither transmits. Getting
/// it wrong does not crash and does not fail a round-trip test - an encoder and decoder written
/// the same wrong way agree perfectly and the result is static. Two variants were built and
/// measured before the point was made: one saturated the predictor to full scale within half a
/// second, the other decoded a steady tone as a 57-count sawtooth. Neither was distinguishable
/// from working by anything except measuring the signal-to-noise ratio of the output.
/// </para>
///
/// <para>
/// µ-law has none of that. It is a memoryless companding curve: each sample maps to one byte
/// through a fixed table, and nothing carries over. The whole codec is twenty lines, both ends
/// implement it from the same written definition, and a mistake shows up as a wrong number
/// rather than as a drift that only appears after half a second of audio.
/// </para>
///
/// <para>
/// The cost is quality. µ-law is about 38 dB of signal-to-noise ratio and 12 kHz of bandwidth at
/// 24 kHz sampling - it is a telephone curve, not a music codec. For "what is this PC playing",
/// heard on a phone speaker at arm's length, that is the right trade for a stream that cannot
/// drift. It is 48 KB/s for stereo, against 100-150 KB for a single screen frame.
/// </para>
/// </summary>
/// <remarks>
/// The Dart decoder in <c>lib/src/audio/mu_law.dart</c> implements the same curve. The two are
/// pinned to one fixture by <c>MuLawTests.CrossLanguageFixtureIsStable</c> and
/// <c>test/audio/mu_law_test.dart</c>, because a codec that exists twice in two languages can
/// be self-consistent on each side and wrong between them.
/// </remarks>
public static class MuLaw
{
    /// <summary>Maximum magnitude the curve encodes; anything louder clamps here.</summary>
    public const int Clip = 8159;

    /// <summary>The curve's pivot. Values are biased by this before the segment is chosen.</summary>
    public const int Bias = 0x84;

    /// <summary>
    /// Segment boundaries in the biased 14-bit domain. Choosing a segment is a search of this
    /// table, which is why the format needs no per-sample state: the segment follows from the
    /// sample and nothing else.
    /// </summary>
    private static ReadOnlySpan<short> SegmentEnds => [0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF];

    /// <summary>
    /// Quantises 16-bit samples to one µ-law byte each.
    /// </summary>
    /// <param name="samples">Any number of samples; mono and interleaved stereo alike.</param>
    /// <param name="destination">Receives one byte per sample.</param>
    /// <returns>How many bytes were written.</returns>
    public static int Encode(ReadOnlySpan<short> samples, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, samples.Length);

        var ends = SegmentEnds;
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = EncodeOne(samples[i], ends);
        }

        return samples.Length;
    }

    private static byte EncodeOne(short sample, ReadOnlySpan<short> ends)
    {
        // 16 bits down to the 14 the curve is defined over. Truncating rather than rounding is
        // correct here: the curve's output lands on a 16-bit grid anyway and rounding would only
        // move the quantisation error by half a count.
        var value = (int)sample >> 2;

        var mask = 0xFF;
        if (value < 0)
        {
            value = -value;
            mask = 0x7F;
        }

        if (value > Clip)
        {
            value = Clip;
        }
        value += Bias >> 2;

        // The first segment whose end is below the value. Starting the search at 7 instead of 0
        // looks equivalent - every path still ends up at a valid segment - and is not: the low
        // segments are the only ones that produce small codes, so a sample of zero comes out as
        // 0x8F, which decodes to roughly a third of full scale. Silence that is not silence, at
        // the bottom of the stream where it is hardest to notice by ear.
        var segment = 0;
        while (segment < ends.Length && value > ends[segment])
        {
            segment++;
        }

        if (segment >= ends.Length)
        {
            return (byte)(0x7F ^ mask);
        }

        var packed = (byte)((segment << 4) | ((value >> (segment + 1)) & 0x0F));
        return (byte)(packed ^ mask);
    }

    /// <summary>Expands µ-law bytes back to 16-bit samples.</summary>
    /// <returns>How many samples were written.</returns>
    public static int Decode(ReadOnlySpan<byte> source, Span<short> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, source.Length);

        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = DecodeOne(source[i]);
        }

        return source.Length;
    }

    /// <summary>Expands one µ-law byte.</summary>
    /// <remarks>
    /// This returns a 16-bit sample, not a 14-bit one, which is the part that is easy to get
    /// wrong: the curve is *defined* over 14 bits, and the bias inside it is 132 rather than the
    /// 33 the encoder adds, precisely because this side works in the 16-bit domain throughout.
    /// Shifting the result left by two more - as a first version did - scales the whole signal by
    /// four. It decodes to perfectly shaped audio at the wrong pitch.
    /// </remarks>
    public static short DecodeOne(byte value)
    {
        // The stored code is the one's complement of the curve, and the high bit is the inverted
        // sign. Inverting first is what puts both the sign bit and the segment back where the
        // shifts below expect them.
        var inverted = (byte)~value;

        // The mantissa gives four bits of the segment, the shift count gives the segment. The
        // bias is added before the shift so that it is scaled with everything else, which is why
        // it is subtracted after rather than before.
        var magnitude = (((inverted & 0x0F) << 3) + Bias) << ((inverted & 0x70) >> 4);
        magnitude -= Bias;

        return (short)Math.Clamp((inverted & 0x80) != 0 ? -magnitude : magnitude, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Signal-to-noise ratio of a round trip, in dB.
    /// </summary>
    /// <remarks>
    /// The measure that separates a working curve from a plausible one. A codec whose encoder
    /// and decoder were written from the same misreading of the segment table still round-trips
    /// exactly; it just sounds like a modem.
    /// </remarks>
    public static double RoundTripSnrDb(ReadOnlySpan<short> original)
    {
        var encoded = new byte[original.Length];
        Encode(original, encoded);

        var decoded = new short[original.Length];
        Decode(encoded, decoded);

        double signal = 0, noise = 0;
        for (var i = 0; i < original.Length; i++)
        {
            var d = (double)(original[i] - decoded[i]);
            signal += (double)original[i] * original[i];
            noise += d * d;
        }

        return signal <= 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise);
    }
}
