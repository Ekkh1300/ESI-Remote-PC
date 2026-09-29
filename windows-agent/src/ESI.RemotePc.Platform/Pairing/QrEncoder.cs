using System.Text;

namespace Esi.Platform.Pairing;

/// <summary>
/// A minimal QR Code encoder: byte mode, error correction level L, versions 1 to 10.
/// <para>
/// It exists for one reason. Pairing has to move a 32-byte secret from the PC to the phone
/// through a channel the attacker does not control, and the only channel a person can
/// operate is one they can point a camera at. Returning the secret over the network would
/// hand it to whoever asked for the pairing, which is precisely the attack the
/// provisioning secret exists to stop.
/// </para>
/// <para>
/// The scope is deliberately narrow. Level L tolerates roughly 7% damage, which is ample for
/// a code rendered on a monitor and read from across a desk, and versions 1-10 hold 271 bytes
/// at L - far more than the pairing URL needs. Stopping there keeps the block-structure and
/// alignment tables small enough to check by eye, which matters for an algorithm that is
/// specified in prose rather than in a library.
/// </para>
/// <para>
/// Implements ISO/IEC 18004. The output is a boolean module matrix, and rendering is left to
/// the caller, so this has no dependency on a drawing library and can be used from a service,
/// a WinForms dialog, or a unit test.
/// </para>
/// </summary>
public static class QrEncoder
{
    /// <summary>Total codewords (data plus error correction) per version, 1 to 10.</summary>
    private static readonly int[] TotalCodewords = [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346];

    /// <summary>Error-correction codewords per block at level L, 1 to 10.</summary>
    private static readonly int[] ErrorCodewordsL = [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18];

    /// <summary>Number of error-correction blocks at level L, 1 to 10.</summary>
    private static readonly int[] BlockCountL = [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4];

    /// <summary>
    /// Alignment-pattern row/column centres, 1 to 10. Empty for version 1, which has none.
    /// </summary>
    private static readonly int[][] AlignmentCentres =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30],
        [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    /// <summary>
    /// Encodes <paramref name="text"/> and returns the module matrix.
    /// <para>
    /// <c>true</c> is a dark module. The matrix is not mirrored and carries no quiet zone;
    /// both are the caller's responsibility, because a quiet zone is a rendering concern and
    /// a scanner is much more forgiving about it than about the data itself.
    /// </para>
    /// </summary>
    /// <param name="text">The payload. Interpreted as UTF-8.</param>
    /// <param name="size">The edge length of the resulting square matrix.</param>
    /// <exception cref="ArgumentException">The payload does not fit in version 10 at level L.</exception>
    public static bool[,] Encode(string text, out int version)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = Encoding.UTF8.GetBytes(text);

        version = SelectVersion(data.Length);
        if (version == 0)
            throw new ArgumentException(
                $"A payload of {data.Length} bytes does not fit in a version 10 QR code at error " +
                "correction level L (the maximum is 271).", nameof(text));

        var size = 17 + 4 * version;
        var modules = BuildFunctionPatterns(size, version);
        var reserved = BuildReservedMap(size, version);

        var dataCodewords = BuildDataCodewords(data, version);
        var allCodewords = Interleave(dataCodewords, version);

        PlaceData(modules, reserved, size, allCodewords);

        // Mask selection is part of encoding, not decoration: the eight candidates are
        // scored and the lowest-penalty one wins, because a mask that creates a pattern a
        // scanner mistakes for a finder is a code that will not scan.
        var best = ApplyBestMask(modules, reserved, size);

        return best;
    }

    /// <summary>Encodes and returns the module matrix, discarding the version number.</summary>
    public static bool[,] Encode(string text) => Encode(text, out _);

    // -----------------------------------------------------------------------------
    // Capacity
    // -----------------------------------------------------------------------------

    private static int SelectVersion(int payloadBytes)
    {
        for (var version = 1; version <= 10; version++)
        {
            var capacity = DataCapacityBytes(version);

            // Byte mode: 4 bits of mode indicator, 8 or 16 bits of length, then the payload.
            var headerBits = version <= 9 ? 4 + 8 : 4 + 16;
            if (headerBits + payloadBytes * 8 <= capacity * 8)
                return version;
        }

        return 0;
    }

    /// <summary>
    /// Data codewords available at level L for a version: the total minus the error
    /// correction, which is the number of parity symbols each block consumes.
    /// </summary>
    private static int DataCapacityBytes(int version)
        => TotalCodewords[version] - ErrorCodewordsL[version] * BlockCountL[version];

    /// <summary>
    /// Splits the data codewords into blocks.
    /// <para>
    /// The specification puts the short blocks first, which is not an aesthetic choice: the
    /// decoder reads block 0 first and the two halves have to line up. When the division is
    /// exact every block is the same length.
    /// </para>
    /// </summary>
    private static List<byte[]> SplitIntoBlocks(byte[] dataCodewords, int version)
    {
        var blockCount = BlockCountL[version];
        var total = DataCapacityBytes(version);
        var shortLength = total / blockCount;
        var longBlockCount = total % blockCount;

        var blocks = new List<byte[]>(blockCount);
        var offset = 0;

        for (var b = 0; b < blockCount; b++)
        {
            var length = b < blockCount - longBlockCount ? shortLength : shortLength + 1;

            var block = new byte[length];
            Array.Copy(dataCodewords, offset, block, 0, length);
            offset += length;

            blocks.Add(block);
        }

        return blocks;
    }

    // -----------------------------------------------------------------------------
    // Data encoding
    // -----------------------------------------------------------------------------

    private static byte[] BuildDataCodewords(byte[] payload, int version)
    {
        var capacity = DataCapacityBytes(version);
        var bits = new BitWriter();

        // Byte mode. Alphanumeric or numeric mode would be smaller, but a pairing URL is
        // lowercase hex, base64url and punctuation: byte mode is the only mode that encodes
        // the whole URL without a mode switch, and the capacity difference is irrelevant at
        // these sizes.
        bits.Append(0b0100, 4);
        bits.Append(payload.Length, version <= 9 ? 8 : 16);

        foreach (var b in payload)
            bits.Append(b, 8);

        var capacityBits = capacity * 8;
        var terminator = Math.Min(4, capacityBits - bits.Length);
        bits.Append(0, terminator);

        // Pad to a byte boundary, then alternate the two specified pad codewords. A decoder
        // that reads past the payload relies on these being present.
        while (bits.Length % 8 != 0)
            bits.Append(0, 1);

        var result = bits.ToBytes();
        var pad = new byte[] { 0xEC, 0x11 };
        var start = result.Count;

        for (var i = start; i < capacity; i++)
            result.Add(pad[(i - start) % 2]);

        return result.ToArray();
    }

    /// <summary>
    /// Computes the error correction parity and interleaves the result.
    /// <para>
    /// Interleaving is what makes the code damage-tolerant: a scratch across one corner
    /// destroys a run of consecutive symbols in every block, which is spread across the
    /// whole code by the interleave, so the damage is shared instead of destroying one
    /// block outright.
    /// </para>
    /// </summary>
    private static byte[] Interleave(byte[] dataCodewords, int version)
    {
        var blocks = SplitIntoBlocks(dataCodewords, version);
        var ecPerBlock = ErrorCodewordsL[version];

        var parity = new List<byte[]>(blocks.Count);
        foreach (var block in blocks)
            parity.Add(ReedSolomon.Encode(block, ecPerBlock));

        var result = new List<byte>(TotalCodewords[version]);

        // Data symbols, one column across all blocks at a time.
        for (var i = 0; i < blocks.Max(b => b.Length); i++)
            foreach (var block in blocks)
                if (i < block.Length)
                    result.Add(block[i]);

        // Then the parity symbols, the same way.
        for (var i = 0; i < ecPerBlock; i++)
            foreach (var block in parity)
                result.Add(block[i]);

        return result.ToArray();
    }

    // -----------------------------------------------------------------------------
    // Matrix construction
    // -----------------------------------------------------------------------------

    private static bool[,] BuildFunctionPatterns(int size, int version)
    {
        var modules = new bool[size, size];

        // Finder patterns and their separators, in three corners. The bottom-right is
        // deliberately left empty: that is where the data starts and the dark module sits.
        PlaceFinder(modules, 0, 0, size);
        PlaceFinder(modules, size - 7, 0, size);
        PlaceFinder(modules, 0, size - 7, size);

        // Timing patterns, spanning the gap between the finder blocks.
        for (var i = 8; i < size - 8; i++)
        {
            var dark = i % 2 == 0;
            modules[6, i] = dark;
            modules[i, 6] = dark;
        }

        // Alignment patterns, skipping the three that would collide with the finders.
        var centres = AlignmentCentres[version];
        foreach (var row in centres)
        {
            foreach (var col in centres)
            {
                var onFinder = (row == 6 && col == 6)
                               || (row == 6 && col == size - 7)
                               || (row == size - 7 && col == 6);

                if (!onFinder)
                    PlaceAlignment(modules, row, col);
            }
        }

        // The dark module, which is always set.
        modules[size - 8, 8] = true;

        // Version information, versions 7 and up.
        if (version >= 7)
        {
            var bits = VersionInformationBits(version);
            for (var i = 0; i < 18; i++)
            {
                var dark = (bits >> i & 1) == 1;
                var row = i / 3;
                var col = size - 11 + i % 3;

                modules[row, col] = dark;
                modules[col, row] = dark;
            }
        }

        return modules;
    }

    /// <summary>
    /// Marks every module that is not available to the data stream.
    /// <para>
    /// Derived from the same geometry as the function patterns, and deliberately precise.
    /// Over-reserving is not a safe default: it silently shortens the data area, so the last
    /// codewords have nowhere to go and the result looks perfectly well formed while decoding
    /// to garbage. Under-reserving is worse, because data would land on a finder and corrupt
    /// the code outright. The two have to agree exactly, which is why both are written here
    /// from one description rather than guessed at independently.
    /// </para>
    /// </summary>
    private static bool[,] BuildReservedMap(int size, int version)
    {
        var reserved = new bool[size, size];

        // The three finder blocks and their separators: an 8x8 corner each, being the 7x7
        // finder plus the light separator module to its right and below.
        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                reserved[i, j] = true;
                reserved[i, size - 1 - j] = true;
                reserved[size - 1 - i, j] = true;
            }
        }

        // Timing patterns.
        for (var i = 8; i < size - 8; i++)
        {
            reserved[6, i] = true;
            reserved[i, 6] = true;
        }

        // Format information: 15 modules around the top-left finder, and 15 split between
        // the other two. This deliberately overlaps the finder blocks above.
        for (var i = 0; i < 15; i++)
        {
            if (i < 6) reserved[8, i] = true;
            else if (i < 8) reserved[8, i + 1] = true;
            else if (i == 8) reserved[7, 8] = true;
            else reserved[14 - i, 8] = true;

            if (i < 8) reserved[size - 1 - i, 8] = true;
            else reserved[8, size - 15 + i] = true;
        }

        // The dark module.
        reserved[size - 8, 8] = true;

        // Version information, versions 7 and up.
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

        // Alignment patterns, skipping the three that would collide with the finders.
        var centres = AlignmentCentres[version];
        foreach (var row in centres)
        {
            foreach (var col in centres)
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

    private static void PlaceFinder(bool[,] modules, int top, int left, int size)
    {
        for (var dy = -1; dy <= 7; dy++)
        {
            for (var dx = -1; dx <= 7; dx++)
            {
                var row = top + dy;
                var col = left + dx;

                if (row < 0 || row >= size || col < 0 || col >= size)
                    continue;

                var inRing = (dy is >= 0 and <= 6 && dx is 0 or 6) || (dx is >= 0 and <= 6 && dy is 0 or 6);
                var inCore = dy is >= 2 and <= 4 && dx is >= 2 and <= 4;

                modules[row, col] = inRing || inCore;
            }
        }
    }

    private static void PlaceAlignment(bool[,] modules, int centreRow, int centreCol)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                modules[centreRow + dy, centreCol + dx] = ring != 1;
            }
        }
    }

    // -----------------------------------------------------------------------------
    // Data placement
    // -----------------------------------------------------------------------------

    private static void PlaceData(bool[,] modules, bool[,] reserved, int size, byte[] codewords)
    {
        var bitIndex = 0;
        var totalBits = codewords.Length * 8;

        // Zigzag, upward and downward in two-module columns, right to left, skipping the
        // vertical timing column. The direction alternates per column, which is what stops
        // the pattern from producing a recognisable texture of its own.
        var upward = true;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5; // the vertical timing pattern column is not a data column

            for (var step = 0; step < size; step++)
            {
                var row = upward ? size - 1 - step : step;

                for (var c = 0; c < 2; c++)
                {
                    var col = right - c;
                    if (reserved[row, col])
                        continue;

                    var dark = false;
                    if (bitIndex < totalBits)
                        dark = (codewords[bitIndex >> 3] >> (7 - (bitIndex & 7)) & 1) == 1;

                    modules[row, col] = dark;
                    bitIndex++;
                }
            }

            upward = !upward;
        }
    }

    // -----------------------------------------------------------------------------
    // Masking
    // -----------------------------------------------------------------------------

    private static bool[,] ApplyBestMask(bool[,] modules, bool[,] reserved, int size)
    {
        var best = (bool[,])modules.Clone();
        var bestMask = 0;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])modules.Clone();
            ApplyMask(candidate, reserved, size, mask);
            WriteFormatInformation(candidate, size, mask);

            var penalty = Penalty(candidate, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                best = candidate;
            }
        }

        // The winning candidate already carries its own format information. Rewriting it here
        // with a fixed mask is the kind of bug that produces a code which looks perfect, is
        // the right size, has valid finder patterns - and does not scan, because the decoder
        // unmasks with the wrong pattern and reads noise.
        return best;
    }

    private static void ApplyMask(bool[,] modules, bool[,] reserved, int size, int mask)
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
    /// The four penalty rules from the specification, summed. Low is good: a high penalty
    /// means the code has large same-colour regions or finder-like patterns, both of which
    /// make a scanner's geometric detection harder.
    /// </summary>
    private static int Penalty(bool[,] modules, int size)
    {
        const int penaltyN1 = 3, penaltyN2 = 3, penaltyN3 = 40, penaltyN4 = 10;
        var score = 0;

        // Rule 1: runs of five or more identical modules in a row or column.
        for (var i = 0; i < size; i++)
        {
            var runRow = 1;
            var runCol = 1;

            for (var j = 1; j < size; j++)
            {
                runRow = modules[i, j] == modules[i, j - 1] ? runRow + 1 : 1;
                if (runRow == 5) score += penaltyN1;
                else if (runRow > 5) score++;

                runCol = modules[j, i] == modules[j - 1, i] ? runCol + 1 : 1;
                if (runCol == 5) score += penaltyN1;
                else if (runCol > 5) score++;
            }
        }

        // Rule 2: 2x2 blocks of one colour.
        for (var row = 0; row < size - 1; row++)
        {
            for (var col = 0; col < size - 1; col++)
            {
                var v = modules[row, col];
                if (v == modules[row, col + 1] && v == modules[row + 1, col] && v == modules[row + 1, col + 1])
                    score += penaltyN2;
            }
        }

        // Rule 3: patterns that mimic a finder.
        for (var row = 0; row < size; row++)
            for (var col = 0; col + 6 < size; col++)
                if (IsFinderLike(modules, row, col, true))
                    score += penaltyN3;

        for (var col = 0; col < size; col++)
            for (var row = 0; row + 6 < size; row++)
                if (IsFinderLike(modules, row, col, false))
                    score += penaltyN3;

        // Rule 4: deviation from an even balance of dark and light.
        var dark = 0;
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
                if (modules[row, col])
                    dark++;

        var percent = dark * 100 / (size * size);
        var k = Math.Abs(percent - 50) / 5;
        score += k * penaltyN4;

        return score;
    }

    private static bool IsFinderLike(bool[,] modules, int row, int col, bool alongRow)
    {
        int size = modules.GetLength(0);

        // 1:1:3:1:1 with four light modules on one side, in either direction.
        bool[] pattern = [true, false, true, true, true, false, true];
        var matches = 0;
        for (var i = 0; i < 7; i++)
        {
            var actual = alongRow ? modules[row, col + i] : modules[row + i, col];
            if (actual == pattern[i])
                matches++;
        }

        if (matches != 7)
            return false;

        // Check the light run on each side.
        var before = 0;
        for (var i = 1; i <= 4; i++)
        {
            var index = alongRow ? col - i : row - i;
            if (index < 0)
                break;

            var value = alongRow ? modules[row, index] : modules[index, col];
            if (value)
                break;

            before++;
        }

        var after = 0;
        for (var i = 0; i < 4; i++)
        {
            var index = (alongRow ? col : row) + 7 + i;
            if (index >= size)
                break;

            var value = alongRow ? modules[row, index] : modules[index, col];
            if (value)
                break;

            after++;
        }

        return before >= 4 || after >= 4;
    }

    // -----------------------------------------------------------------------------
    // Format and version information
    // -----------------------------------------------------------------------------

    private static void WriteFormatInformation(bool[,] modules, int size, int mask)
    {
        // Error correction level L is 01 in the two-bit field. Format information is a
        // 5-bit payload (2 bits level, 3 bits mask) run through a BCH(15,5) code and XORed
        // with the standard 0x5412 mask so the code is never all zeros.
        const int levelL = 0b01;
        var data = (levelL << 3) | mask;

        var remainder = data;
        for (var i = 0; i < 10; i++)
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);

        var bits = ((data << 10) | remainder) ^ 0x5412;

        for (var i = 0; i < 15; i++)
        {
            var dark = (bits >> i & 1) == 1;

            // First copy, around the top-left finder.
            if (i < 6) modules[8, i] = dark;
            else if (i < 8) modules[8, i + 1] = dark;
            else if (i == 8) modules[7, 8] = dark;
            else modules[14 - i, 8] = dark;

            // Second copy, split between the other two finders.
            if (i < 8) modules[size - 1 - i, 8] = dark;
            else modules[8, size - 15 + i] = dark;
        }

        modules[size - 8, 8] = true; // the dark module
    }

    private static int VersionInformationBits(int version)
    {
        var remainder = version;
        for (var i = 0; i < 12; i++)
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);

        return (version << 12) | remainder;
    }

    // -----------------------------------------------------------------------------
    // Small helpers
    // -----------------------------------------------------------------------------

    /// <summary>A growable most-significant-bit-first bit writer.</summary>
    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public int Length => _bits.Count;

        public void Append(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                _bits.Add((value >> i & 1) == 1);
        }

        public List<byte> ToBytes()
        {
            var bytes = new List<byte>((_bits.Count + 7) / 8);

            for (var i = 0; i < _bits.Count; i += 8)
            {
                var b = 0;
                for (var j = 0; j < 8 && i + j < _bits.Count; j++)
                    if (_bits[i + j])
                        b |= 1 << (7 - j);

                bytes.Add((byte)b);
            }

            return bytes;
        }
    }
}

/// <summary>
/// Reed-Solomon error correction over GF(2^8) with the QR primitive polynomial
/// 0x11D, exactly as ISO/IEC 18004 specifies.
/// </summary>
public static class ReedSolomon
{
    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];

    static ReedSolomon()
    {
        var x = 1;

        for (var i = 0; i < 255; i++)
        {
            Exp[i] = x;
            Log[x] = i;

            // Multiply by the generator 2, which is the polynomial 0x11D.
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= 0x11D;
        }

        for (var i = 255; i < 512; i++)
            Exp[i] = Exp[i - 255];
    }

    /// <summary>Computes <paramref name="errorCodewords"/> parity symbols for a data block.</summary>
    public static byte[] Encode(byte[] data, int errorCodewords)
    {
        var generator = BuildGenerator(errorCodewords);
        var result = new byte[errorCodewords];

        // Polynomial long division: the running remainder of the data by the generator.
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;

            for (var i = 0; i < errorCodewords; i++)
                result[i] ^= (byte)Multiply(generator[i], factor);
        }

        return result;
    }

    private static int[] BuildGenerator(int degree)
    {
        var result = new int[degree];
        result[degree - 1] = 1;

        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static int Multiply(int a, int b)
        => b == 0 || a == 0 ? 0 : Exp[Log[a] + Log[b]];
}
