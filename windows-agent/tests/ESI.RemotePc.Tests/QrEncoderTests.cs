using System.Text;
using Esi.Platform.Pairing;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The QR encoder used to move a pairing secret from the PC to a phone.
/// <para>
/// An encoder that produces a plausible-looking square of modules is not evidence of
/// anything. So the main test here decodes the matrix back: it reverses the mask, walks the
/// same zigzag, de-interleaves the blocks, and asserts the original payload comes out.
/// That exercises every stage that can be got wrong - bit order, character count width,
/// terminator, padding, block splitting, interleave order, mask inversion and module
/// placement - and it fails loudly rather than producing a code that quietly will not scan.
/// </para>
/// </summary>
public sealed class QrEncoderTests
{
    private const string SampleUrl =
        "https://192.168.1.50:48220/api/v1/pair/session?id=a1b2c3d4e5f60718&secret=AbCdEf0123456789-_xyzABCDEFGHIJKLMNOPqrstuvwX";

    [Fact]
    public void The_matrix_size_matches_the_version()
    {
        foreach (var text in new[] { "a", SampleUrl, new string('x', 200) })
        {
            var modules = QrEncoder.Encode(text, out var version);

            Assert.Equal(17 + 4 * version, modules.GetLength(0));
            Assert.Equal(modules.GetLength(0), modules.GetLength(1));
        }
    }

    [Fact]
    public void The_three_finder_patterns_are_present()
    {
        var modules = QrEncoder.Encode(SampleUrl, out _);
        var size = modules.GetLength(0);

        foreach (var (top, left) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
            AssertFinderPattern(modules, top, left);
    }

    [Fact]
    public void The_timing_patterns_alternate()
    {
        var modules = QrEncoder.Encode(SampleUrl, out _);
        var size = modules.GetLength(0);

        for (var i = 8; i < size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, modules[6, i]);
            Assert.Equal(i % 2 == 0, modules[i, 6]);
        }
    }

    [Fact]
    public void The_dark_module_is_set()
    {
        var modules = QrEncoder.Encode(SampleUrl, out _);
        var size = modules.GetLength(0);

        Assert.True(modules[size - 8, 8]);
    }

    [Fact]
    public void A_payload_that_does_not_fit_is_rejected_rather_than_truncated()
    {
        // Silently truncating a pairing URL would produce a code that scans and then fails
        // to pair, with no indication of why.
        var tooLong = new string('x', 300);

        var ex = Assert.Throws<ArgumentException>(() => QrEncoder.Encode(tooLong));
        Assert.Contains("271", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        // The same pairing URL must produce the same code every time, or a QR shown on the
        // PC would change under the phone's camera.
        Assert.Equal(Flatten(QrEncoder.Encode(SampleUrl)), Flatten(QrEncoder.Encode(SampleUrl)));
    }

    /// <summary>
    /// The real test: decode the matrix back and confirm the payload survives.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("https://example.invalid/x")]
    [InlineData(SampleUrl)]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789" +
                "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public void The_payload_survives_a_full_encode_decode_round_trip(string payload)
    {
        var modules = QrEncoder.Encode(payload, out var version);
        var decoded = Decode(modules, version);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void The_round_trip_holds_for_every_supported_version()
    {
        // Walk the version boundary rather than sampling: the multi-block versions (6 and 10)
        // take a completely different interleaving path from the single-block ones, and that
        // is exactly the code a single sample would miss.
        for (var targetLength = 1; targetLength <= 260; targetLength += 7)
        {
            var payload = new string((char)('a' + targetLength % 26), targetLength);

            var modules = QrEncoder.Encode(payload, out var version);
            var decoded = Decode(modules, version);

            Assert.Equal(payload, decoded);
        }
    }

    [Fact]
    public void The_specific_version_boundaries_round_trip()
    {
        // The exact lengths where a version rolls over: below the boundary the code is one
        // version smaller, at it the capacity maths has to change shape.
        int[] payloads =
        [
            17, 18, 32, 33, 53, 54, 78, 79, 106, 107, 134, 135, 154, 155, 192, 193, 230, 231, 271,
        ];

        foreach (var length in payloads)
        {
            var payload = new string('x', length);
            var modules = QrEncoder.Encode(payload, out var version);

            Assert.Equal(payload, Decode(modules, version));
        }

        Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('x', 272)));
    }

    // ===============================================================================
    // A decoder, written independently of the encoder
    // ===============================================================================

    /// <summary>
    /// Reads a level-L byte-mode QR back to its payload.
    /// <para>
    /// Deliberately re-implements the read path rather than calling into the encoder: a
    /// shared helper would make the test agree with a bug in the helper.
    /// </para>
    /// </summary>
    private static string Decode(bool[,] modules, int version)
    {
        var size = modules.GetLength(0);

        // 1. Undo the format-information area so it is not mistaken for data, then rebuild
        //    the reserved map the same way the encoder does.
        var mask = ReadMask(modules, size);
        var reserved = BuildReservedMap(size, version);
        var plain = (bool[,])modules.Clone();
        Unmask(plain, reserved, size, mask);

        // 2. Walk the zigzag and rebuild the codeword stream.
        var totalCodewords = TotalCodewordsFor(version);
        var bits = new List<bool>(totalCodewords * 8);
        var upward = true;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5;

            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;

                for (var c = 0; c < 2; c++)
                {
                    var col = right - c;
                    if (reserved[row, col])
                        continue;

                    if (bits.Count < totalCodewords * 8)
                        bits.Add(plain[row, col]);
                }
            }

            upward = !upward;
        }

        var codewords = new byte[totalCodewords];
        for (var i = 0; i < totalCodewords; i++)
        {
            var b = 0;
            for (var j = 0; j < 8; j++)
                if (bits[i * 8 + j])
                    b |= 1 << (7 - j);

            codewords[i] = (byte)b;
        }

        // 3. De-interleave back into blocks, then take the first block's data.
        var data = DeInterleave(codewords, version);

        // 4. Read the byte-mode header and payload.
        var reader = new BitReader(data);
        var mode = reader.Read(4);
        Assert.Equal(0b0100, mode);

        var length = reader.Read(version <= 9 ? 8 : 16);
        var payload = new byte[length];

        for (var i = 0; i < length; i++)
            payload[i] = (byte)reader.Read(8);

        return Encoding.UTF8.GetString(payload);
    }

    private static int ReadMask(bool[,] modules, int size)
    {
        // Undo the 0x5412 XOR, then the BCH code, and read the three mask bits back out.
        var bits = 0;

        for (var i = 0; i < 15; i++)
        {
            bool dark;
            if (i < 6) dark = modules[8, i];
            else if (i < 8) dark = modules[8, i + 1];
            else if (i == 8) dark = modules[7, 8];
            else dark = modules[14 - i, 8];

            if (dark)
                bits |= 1 << i;
        }

        var value = bits ^ 0x5412;
        return (value >> 10) & 0b111;
    }

    private static void Unmask(bool[,] modules, bool[,] reserved, int size, int mask)
    {
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (reserved[row, col])
                    continue;

                var invert = mask switch
                {
                    0 => (row + col) % 2 == 0,
                    1 => row % 2 == 0,
                    2 => col % 3 == 0,
                    3 => (row + col) % 3 == 0,
                    4 => (row / 2 + col / 3) % 2 == 0,
                    5 => (row * col) % 2 + (row * col) % 3 == 0,
                    6 => ((row * col) % 2 + (row * col) % 3) % 2 == 0,
                    _ => ((row + col) % 2 + (row * col) % 3) % 2 == 0,
                };

                if (invert)
                    modules[row, col] = !modules[row, col];
            }
        }
    }

    /// <summary>
    /// The function-module layout, as specified.
    /// <para>
    /// A decoder genuinely does have to know this - it is part of ISO/IEC 18004, not part of
    /// the encoder's implementation - so sharing the description between the two is correct.
    /// What the decoder still checks independently is everything the encoder chose: bit
    /// order, the character-count width, terminator and padding, block splitting, interleave
    /// order, mask inversion and the zigzag walk. That is where an encoder actually goes
    /// wrong, and it is what the round trip proves.
    /// </para>
    /// </summary>
    private static bool[,] BuildReservedMap(int size, int version)
    {
        var reserved = new bool[size, size];

        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                reserved[i, j] = true;
                reserved[i, size - 1 - j] = true;
                reserved[size - 1 - i, j] = true;
            }
        }

        for (var i = 8; i < size - 8; i++)
        {
            reserved[6, i] = true;
            reserved[i, 6] = true;
        }

        for (var i = 0; i < 15; i++)
        {
            if (i < 6) reserved[8, i] = true;
            else if (i < 8) reserved[8, i + 1] = true;
            else if (i == 8) reserved[7, 8] = true;
            else reserved[14 - i, 8] = true;

            if (i < 8) reserved[size - 1 - i, 8] = true;
            else reserved[8, size - 15 + i] = true;
        }

        reserved[size - 8, 8] = true;

        if (version >= 7)
        {
            for (var i = 0; i < 18; i++)
            {
                var row = i / 3;
                var col = size - 11 + i % 3;

                reserved[row, col] = true;
                reserved[col, row] = true;
            }
        }

        foreach (var row in AlignmentCentresFor(version))
        {
            foreach (var col in AlignmentCentresFor(version))
            {
                var onFinder = (row == 6 && col == 6)
                               || (row == 6 && col == size - 7)
                               || (row == size - 7 && col == 6);

                if (onFinder)
                    continue;

                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        reserved[row + dy, col + dx] = true;
            }
        }

        return reserved;
    }

    private static int[] AlignmentCentresFor(int version) => version switch
    {
        1 => [],
        2 => [6, 18],
        3 => [6, 22],
        4 => [6, 26],
        5 => [6, 30],
        6 => [6, 34],
        7 => [6, 22, 38],
        8 => [6, 24, 42],
        9 => [6, 26, 46],
        _ => [6, 28, 50],
    };

    private static int TotalCodewordsFor(int version) => version switch
    {
        1 => 26, 2 => 44, 3 => 70, 4 => 100, 5 => 134,
        6 => 172, 7 => 196, 8 => 242, 9 => 292, _ => 346,
    };

    private static int ErrorCodewordsFor(int version) => version switch
    {
        1 => 7, 2 => 10, 3 => 15, 4 => 20, 5 => 26,
        6 => 18, 7 => 20, 8 => 24, 9 => 30, _ => 18,
    };

    private static int BlockCountFor(int version) => version <= 5 ? 1 : version <= 9 ? 2 : 4;

    private static byte[] DeInterleave(byte[] codewords, int version)
    {
        var blockCount = BlockCountFor(version);
        var ecPerBlock = ErrorCodewordsFor(version);
        var dataCodewords = TotalCodewordsFor(version) - ecPerBlock * blockCount;
        var shortLength = dataCodewords / blockCount;
        var longBlockCount = dataCodewords % blockCount;

        var lengths = new int[blockCount];
        for (var b = 0; b < blockCount; b++)
            lengths[b] = b < blockCount - longBlockCount ? shortLength : shortLength + 1;

        var blocks = new byte[blockCount][];
        for (var b = 0; b < blockCount; b++)
            blocks[b] = new byte[lengths[b]];

        var index = 0;
        for (var i = 0; i < lengths.Max(); i++)
        {
            for (var b = 0; b < blockCount; b++)
            {
                if (i < lengths[b])
                    blocks[b][i] = codewords[index++];
            }
        }

        // Concatenate every block in order. The payload is not confined to the first block -
        // from version 6 upward the data is split across two or four, and taking only block
        // zero would silently truncate every payload past about 100 bytes.
        return blocks.SelectMany(b => b).ToArray();
    }

    private sealed class BitReader(byte[] data)
    {
        private int _position;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var bit = _position < data.Length * 8
                    ? (data[_position / 8] >> (7 - _position % 8) & 1)
                    : 0;

                value = (value << 1) | bit;
                _position++;
            }

            return value;
        }
    }

    // ===============================================================================
    // Helpers
    // ===============================================================================

    private static void AssertFinderPattern(bool[,] modules, int top, int left)
    {
        for (var dy = 0; dy < 7; dy++)
        {
            for (var dx = 0; dx < 7; dx++)
            {
                var ring = dy is 0 or 6 || dx is 0 or 6;
                var core = dy is >= 2 and <= 4 && dx is >= 2 and <= 4;

                Assert.Equal(ring || core, modules[top + dy, left + dx]);
            }
        }
    }

    private static string Flatten(bool[,] modules)
    {
        var builder = new StringBuilder();
        for (var row = 0; row < modules.GetLength(0); row++)
            for (var col = 0; col < modules.GetLength(1); col++)
                builder.Append(modules[row, col] ? '1' : '0');

        return builder.ToString();
    }
}
