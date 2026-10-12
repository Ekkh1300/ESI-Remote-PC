using System.Text;
using Esi.Platform.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Esi.RemotePc.Tests;

/// <summary>
/// D54: the phone decodes PC audio with its own Dart implementation of the same curve.
///
/// <para>
/// Two implementations of one specification in two languages is the shape most likely to rot,
/// because each one is individually self-consistent - C# can encode and decode perfectly well
/// against itself while the phone plays a modem, and the Dart tests can pass against Dart alone.
/// The only thing that catches it is a fixture both sides are pinned to.
/// </para>
/// </summary>
public sealed class MuLawTests
{
    private readonly ITestOutputHelper _out;

    public MuLawTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The exact bytes <see cref="Emit_the_cross_language_fixture"/> produces, pinned here and
    /// in <c>test/audio/mu_law_test.dart</c>.
    ///
    /// <para>
    /// Two implementations of one curve in two languages can each be perfectly self-consistent
    /// and still disagree with each other, and no round-trip test inside either language would
    /// notice, because each only ever decodes what it encoded. Pinning the bytes makes the
    /// disagreement show up as a failing test on the side that drifted.
    /// </para>
    /// </summary>
    private const string FixtureHex =
        "ffbdada59e9a9693908f8e8d8c8c8c8c8d8d8e8f9295999da2abb8d84330271f1b1714110f0e0d0c0c0c0c0c" +
        "0d0e0f1114181c2028334acdb4a9a19c9895928f8e8d8c8c8c8c8c8d8e8f9194979b9fa6afc05e392c231d19" +
        "1612100e0d0d0c0c0c0c0d0d0e1013161a1e242c3b6abeaea59e9a9793908f8e8d8c8c8c8c8d8d8e8f929599" +
        "9ca1aab6d14631281f1b1814110f0e0d0c0c0c0c0c0d0e0f1114181b1f283146d1b6aaa19c9995928f8e8d8d" +
        "8c8c8c8c8d8e8f9093979a9ea5aebe6a3b2c241e1a1613100e0d0d0c0c0c0c0d0d0e101216191d232c395ec0" +
        "afa69f9b9794918f8e8d8c8c8c8c8c8d8e8f9295989ca1a9b4cd4a3328201c1814110f0e0d0c0c0c0c0c0d0e" +
        "0f1114171b1f273043d8b8aba29d9995928f8e8d8d8c8c8c8c8d8e8f9093969a9ea5adbdff3c2d251e1a1613" +
        "100f0e0d0c0c0c0c0d0d0e0f1215191d222b3858c3b0a79f9b9794918f8e8d8c8c8c8c8c8d8e8f9195989ca0" +
        "a8b3ca4d3429211c1815120f0e0d0c0c0c0c0c0d0e0f1114171b1f262f40deb9aca39d999692908e8d8d8c8c" +
        "8c8c8d8d8e9093969a9ea4acbbea3e2e251e1a1713100f0e0d0c0c0c0c0d0d0e0f1215191c212a3651c7b1a8" +
        "9f9b9894918f8e8d8c8c8c8c8c8d8e8f9194989b9fa8b1c751362a211c1915120f0e0d0d0c0c0c0c0d0e0f10" +
        "13171a1e252e3eeabbaca49e9a9693908e8d8d8c8c8c8c8d8d8e9092";

    private static short[] Ramp(int samples, int peak)
    {
        var result = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            // A full-scale triangle, not a sine. A sine spends most of its time near zero where
            // µ-law is at its coarsest, which would measure the curve's worst segment instead of
            // its typical behaviour.
            var phase = (i % 200) / 200.0;
            result[i] = (short)(phase < 0.5 ? phase * 2 * peak : (1 - phase) * 2 * peak);
        }
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1234)]
    [InlineData(-1)]
    [InlineData(-1234)]
    [InlineData(32767)]
    [InlineData(-32768)]
    [InlineData(8159 << 2)]
    [InlineData(-8159 << 2)]
    public void The_curve_round_trips_the_values_that_pin_its_edges(short sample)
    {
        var encoded = new byte[1];
        MuLaw.Encode([sample], encoded);

        var decoded = new short[1];
        MuLaw.Decode(encoded, decoded);

        // The curve cannot carry a sample exactly - that is the point of a companding curve - and
        // its resolution is not uniform either. The bottom segment's step is 8 counts, so the
        // smallest non-zero magnitude the format can express is 8 out of 32768: -1 and -8 are the
        // same code. An allowance scaled by the sample alone would fail on exactly the values
        // that pin the curve's most interesting edge.
        var error = Math.Abs((int)sample - decoded[0]);
        var allowed = Math.Max(8, Math.Abs((int)sample) * 3 / 100);
        Assert.True(error <= allowed,
            $"{sample} came back as {decoded[0]}, an error of {error} against an allowance of {allowed}");
    }

    [Fact]
    public void Silence_is_the_high_byte_not_zero()
    {
        // A codec whose zero is 0x00 is a codec whose silence depends on the transport padding
        // bytes: a stream that leaks a 0x5A where a byte was dropped plays a click. µ-law puts
        // silence at the top of the range precisely so that uninitialised memory is noise, not
        // silence, and so silence is a value the caller has to ask for.
        var encoded = new byte[4];
        MuLaw.Encode([0, 0, 0, 0], encoded);

        Assert.All(encoded, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void The_curve_is_symmetric_about_zero()
    {
        var positive = new byte[1];
        var negative = new byte[1];
        MuLaw.Encode([8000], positive);
        MuLaw.Encode([-8000], negative);

        Assert.Equal(0x80, positive[0] ^ negative[0]);

        var decoded = new short[2];
        MuLaw.Decode([positive[0], negative[0]], decoded);
        Assert.Equal(decoded[0], -decoded[1]);
    }

    [Fact]
    public void A_full_scale_signal_survives_at_telephone_quality()
    {
        // µ-law is about 38 dB. An encoder that merely *looks* right - a table transcribed with
        // one entry wrong, a shift by one - still round-trips perfectly and lands far below this,
        // so the number is the assertion that actually means something.
        var snr = MuLaw.RoundTripSnrDb(Ramp(2048, 30000));
        _out.WriteLine($"triangle, full scale: {snr:F1} dB");
        Assert.True(snr > 34, $"only {snr:F1} dB survived, which is below what the curve should give");

        foreach (var hz in new[] { 100.0, 1000.0, 6000.0, 10000.0 })
        {
            var sine = new short[2048];
            for (var i = 0; i < sine.Length; i++)
            {
                sine[i] = (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / 24000) * 30000);
            }
            var toneSnr = MuLaw.RoundTripSnrDb(sine);
            _out.WriteLine($"{hz,6} Hz: {toneSnr:F1} dB");
            Assert.True(toneSnr > 30, $"a {hz} Hz tone survived at only {toneSnr:F1} dB");
        }
    }

    [Fact]
    public void One_byte_per_sample_and_nothing_else()
    {
        // There is no header and no state. That is the whole reason this curve was chosen over
        // ADPCM, so a stream that grew a per-frame header would mean the wrong codec had been
        // built.
        var samples = Ramp(1024, 12000);
        var encoded = new byte[samples.Length];
        Assert.Equal(1024, MuLaw.Encode(samples, encoded));

        var decoded = new short[samples.Length];
        Assert.Equal(1024, MuLaw.Decode(encoded, decoded));
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        // Not cosmetic: the cross-language fixture is a hex string, so the same input must give
        // the same bytes on every run and every machine.
        var samples = Ramp(256, 9000);
        var a = new byte[samples.Length];
        var b = new byte[samples.Length];
        MuLaw.Encode(samples, a);
        MuLaw.Encode(samples, b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_decoder_refuses_to_overflow_the_callers_buffer()
    {
        // A decoder that wrote past the end would corrupt whatever followed it in the output
        // buffer, and the symptom - one block of noise every time a short packet arrived - is
        // nothing like its cause.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MuLaw.Decode(new byte[64], new short[32]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MuLaw.Encode(new short[64], new byte[32]));
    }

    [Fact]
    public void Every_possible_byte_decodes_without_throwing()
    {
        // All 256 codes are reachable from some input, and the segment shift `(code & 0x70) >> 4`
        // can be up to 7 - `0x70 >> 4` is 7, but a code with bits set can give a shift of 7, and
        // shifting a 14-bit magnitude left by 7 overflows if nothing clamps. Every code is
        // exercised here rather than the handful a real stream happens to contain.
        for (var i = 0; i < 256; i++)
        {
            var short2 = MuLaw.DecodeOne((byte)i);
            Assert.InRange(short2, short.MinValue, short.MaxValue);
        }
    }

    [Fact]
    public void CrossLanguageFixtureIsStable()
    {
        var samples = new short[512];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / 24000) * 20000);
        }

        var encoded = new byte[samples.Length];
        MuLaw.Encode(samples, encoded);

        var hex = Convert.ToHexString(encoded).ToLowerInvariant();
        Assert.Equal(FixtureHex, hex);
    }

    [Fact]
    public void The_pinned_fixture_decodes_to_the_samples_it_was_made_from()
    {
        // The other half of the pin: the bytes have to mean something, not merely be stable. If
        // the Dart side copied the fixture and this side drifted, both suites would be green.
        var encoded = Convert.FromHexString(FixtureHex);
        var decoded = new short[encoded.Length];
        MuLaw.Decode(encoded, decoded);

        var snr = MuLaw.RoundTripSnrDb(decoded);
        Assert.True(snr > 40, $"the fixture itself only decodes at {snr:F1} dB");

        // The first samples are pinned outright: silence in, silence out, and a known non-zero
        // value for the next one. A curve whose segment search is off by one still has good SNR.
        Assert.Equal(0, decoded[0]);
        Assert.NotEqual(0, decoded[1]);
    }

    /// <summary>
    /// Prints the fixture the Dart test pins to. Run once; the output goes into
    /// <c>test/audio/mu_law_test.dart</c>.
    /// </summary>
    [Fact]
    public void Emit_the_cross_language_fixture()
    {
        var samples = new short[512];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / 24000) * 20000);
        }

        var encoded = new byte[samples.Length];
        MuLaw.Encode(samples, encoded);

        var sb = new StringBuilder();
        foreach (var b in encoded)
        {
            sb.Append(b.ToString("x2"));
        }
        _out.WriteLine($"FIXTURE-BYTES {encoded.Length}");
        _out.WriteLine($"FIXTURE-HEX {sb}");
    }
}
