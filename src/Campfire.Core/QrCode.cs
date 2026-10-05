using System.Text;

namespace Campfire.Core;

public static class QrCode
{
    public static string Token(string text) => Base64Url.Encode(Encoding.UTF8.GetBytes(text));

    public static string? Text(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(Base64Url.Decode(token));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Svg(string text)
    {
        var modules = Encode(Encoding.UTF8.GetBytes(text));
        var size = modules.GetLength(0);
        var builder = new StringBuilder();
        builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(size).Append(' ').Append(size).Append("\" shape-rendering=\"crispEdges\">");
        builder.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>");
        builder.Append("<path fill=\"#000\" d=\"");
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!modules[y, x])
                    continue;
                builder.Append("M").Append(x).Append(' ').Append(y).Append("h1v1h-1z");
            }
        }
        builder.Append("\"/></svg>");
        return builder.ToString();
    }

    public static bool[,] Encode(byte[] data)
    {
        var version = ChooseVersion(data.Length);
        var spec = Specs[version - 1];
        var codewords = Pack(data, spec.DataCodewords, version);
        var blocks = Split(codewords, spec);
        var interleaved = Interleave(blocks, spec);
        var size = 21 + (version - 1) * 4;
        var modules = new bool[size, size];
        var function = new bool[size, size];
        DrawFunction(modules, function, version, size);
        PlaceData(modules, function, interleaved, spec.Remainder);
        var best = (bool[,])modules.Clone();
        var bestMask = 0;
        var bestScore = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])modules.Clone();
            ApplyMask(candidate, function, mask);
            DrawFormat(candidate, version, size, mask);
            var score = Penalty(candidate);
            if (score < bestScore)
            {
                bestScore = score;
                bestMask = mask;
                best = candidate;
            }
        }

        DrawFormat(best, version, size, bestMask);
        return best;
    }

    private static int ChooseVersion(int length)
    {
        for (var version = 1; version <= Specs.Length; version++)
        {
            var capacity = Specs[version - 1].DataCodewords - (version < 10 ? 2 : 3);
            if (length <= capacity)
                return version;
        }
        throw new AppException(422, "That link is too long for a QR code.");
    }

    private static byte[] Pack(byte[] data, int capacity, int version)
    {
        var countBits = version >= 10 ? 16 : 8;
        var bits = new List<bool> { false, true, false, false };
        for (var i = countBits - 1; i >= 0; i--)
            bits.Add(((data.Length >> i) & 1) == 1);
        foreach (var value in data)
        {
            for (var i = 7; i >= 0; i--)
                bits.Add(((value >> i) & 1) == 1);
        }
        for (var i = 0; i < 4 && bits.Count < capacity * 8; i++)
            bits.Add(false);
        while (bits.Count % 8 != 0)
            bits.Add(false);
        var bytes = new List<byte>();
        for (var i = 0; i < bits.Count; i += 8)
        {
            byte value = 0;
            for (var bit = 0; bit < 8; bit++)
                if (bits[i + bit])
                    value |= (byte)(1 << (7 - bit));
            bytes.Add(value);
        }
        var pad = 0xEC;
        while (bytes.Count < capacity)
        {
            bytes.Add((byte)pad);
            pad = pad == 0xEC ? 0x11 : 0xEC;
        }
        return bytes.ToArray();
    }

    private static byte[][] Split(byte[] codewords, VersionSpec spec)
    {
        var blocks = new byte[spec.Blocks][];
        var offset = 0;
        for (var i = 0; i < spec.Group1; i++)
        {
            blocks[i] = codewords[offset..(offset + spec.Data1)];
            offset += spec.Data1;
        }
        for (var i = 0; i < spec.Group2; i++)
        {
            blocks[spec.Group1 + i] = codewords[offset..(offset + spec.Data2)];
            offset += spec.Data2;
        }
        return blocks;
    }

    private static byte[] Interleave(byte[][] blocks, VersionSpec spec)
    {
        var ecc = new byte[blocks.Length][];
        for (var i = 0; i < blocks.Length; i++)
            ecc[i] = ReedSolomon(blocks[i], spec.Ecc);
        var result = new List<byte>();
        var longest = Math.Max(spec.Data1, spec.Data2);
        for (var i = 0; i < longest; i++)
        {
            foreach (var block in blocks)
            {
                if (i < block.Length)
                    result.Add(block[i]);
            }
        }
        for (var i = 0; i < spec.Ecc; i++)
        {
            foreach (var block in ecc)
                result.Add(block[i]);
        }
        return result.ToArray();
    }

    private static byte[] ReedSolomon(byte[] data, int eccLength)
    {
        var generator = new byte[eccLength + 1];
        generator[0] = 1;
        byte root = 1;
        for (var i = 0; i < eccLength; i++)
        {
            for (var j = i; j >= 0; j--)
                generator[j + 1] ^= Multiply(generator[j], root);
            root = Multiply(root, 2);
        }
        var remainder = new byte[eccLength];
        foreach (var value in data)
        {
            var factor = (byte)(value ^ remainder[0]);
            Buffer.BlockCopy(remainder, 1, remainder, 0, eccLength - 1);
            remainder[^1] = 0;
            for (var i = 0; i < eccLength; i++)
                remainder[i] ^= Multiply(generator[i + 1], factor);
        }
        return remainder;
    }

    private static byte Multiply(byte left, byte right)
    {
        var result = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((right & 1) != 0)
                result ^= left;
            var high = (left & 0x80) != 0;
            left <<= 1;
            if (high)
                left ^= 0x1D;
            right >>= 1;
        }
        return (byte)result;
    }

    private static void DrawFunction(bool[,] modules, bool[,] function, int version, int size)
    {
        DrawFinder(modules, function, 0, 0);
        DrawFinder(modules, function, size - 7, 0);
        DrawFinder(modules, function, 0, size - 7);
        for (var i = 0; i < size; i++)
        {
            if (!function[i, 6])
                Set(modules, function, 6, i, i % 2 == 0);
            if (!function[6, i])
                Set(modules, function, i, 6, i % 2 == 0);
        }
        var positions = Alignments[version - 1];
        foreach (var y in positions)
        {
            foreach (var x in positions)
            {
                if (function[y, x])
                    continue;
                DrawAlignment(modules, function, x, y);
            }
        }
        Set(modules, function, 8, 4 * version + 9, true);
        ReserveFormat(function, size);
        if (version >= 7)
            ReserveVersion(function, size);
    }

    private static void DrawFinder(bool[,] modules, bool[,] function, int x, int y)
    {
        for (var row = -1; row <= 7; row++)
        {
            for (var column = -1; column <= 7; column++)
            {
                var yy = y + row;
                var xx = x + column;
                if (yy < 0 || xx < 0 || yy >= modules.GetLength(0) || xx >= modules.GetLength(0))
                    continue;
                var edge = row is >= 0 and <= 6 && column is >= 0 and <= 6;
                var dark = edge && (row is 0 or 6 || column is 0 or 6 || (row is >= 2 and <= 4 && column is >= 2 and <= 4));
                Set(modules, function, xx, yy, dark);
            }
        }
    }

    private static void DrawAlignment(bool[,] modules, bool[,] function, int x, int y)
    {
        for (var row = -2; row <= 2; row++)
        {
            for (var column = -2; column <= 2; column++)
            {
                var dark = Math.Max(Math.Abs(row), Math.Abs(column)) != 1;
                Set(modules, function, x + column, y + row, dark);
            }
        }
    }

    private static void ReserveFormat(bool[,] function, int size)
    {
        for (var i = 0; i < 9; i++)
        {
            function[8, i] = true;
            function[i, 8] = true;
        }
        for (var i = 0; i < 8; i++)
        {
            function[8, size - 1 - i] = true;
            function[size - 1 - i, 8] = true;
        }
    }

    private static void ReserveVersion(bool[,] function, int size)
    {
        for (var i = 0; i < 6; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                function[i, size - 11 + j] = true;
                function[size - 11 + j, i] = true;
            }
        }
    }

    private static void PlaceData(bool[,] modules, bool[,] function, byte[] data, int remainder)
    {
        var size = modules.GetLength(0);
        var bitIndex = 0;
        var total = data.Length * 8 + remainder;
        for (var right = size - 1; right > 0; right -= 2)
        {
            if (right == 6)
                right--;
            for (var vertical = 0; vertical < size; vertical++)
            {
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    var upward = ((right + 1) / 2) % 2 == 0;
                    var y = upward ? size - 1 - vertical : vertical;
                    if (function[y, x])
                        continue;
                    var dark = bitIndex < data.Length * 8 && ((data[bitIndex / 8] >> (7 - bitIndex % 8)) & 1) == 1;
                    modules[y, x] = dark;
                    bitIndex++;
                    if (bitIndex >= total)
                        return;
                }
            }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] function, int mask)
    {
        var size = modules.GetLength(0);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (function[y, x] || !Masked(mask, y, x))
                    continue;
                modules[y, x] = !modules[y, x];
            }
        }
    }

    private static bool Masked(int mask, int y, int x) => mask switch
    {
        0 => (y + x) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (y + x) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => (y * x) % 2 + (y * x) % 3 == 0,
        6 => ((y * x) % 2 + (y * x) % 3) % 2 == 0,
        _ => ((y + x) % 2 + (y * x) % 3) % 2 == 0,
    };

    private static void DrawFormat(bool[,] modules, int version, int size, int mask)
    {
        var bits = FormatBits(mask);
        for (var i = 0; i < 6; i++)
            modules[i, 8] = Bit(bits, i);
        modules[7, 8] = Bit(bits, 6);
        modules[8, 8] = Bit(bits, 7);
        modules[8, 7] = Bit(bits, 8);
        for (var i = 9; i < 15; i++)
            modules[8, 14 - i] = Bit(bits, i);
        for (var i = 0; i < 8; i++)
            modules[8, size - 1 - i] = Bit(bits, i);
        for (var i = 8; i < 15; i++)
            modules[size - 15 + i, 8] = Bit(bits, i);
        modules[8, size - 8] = true;
        if (version < 7)
            return;
        var versionBits = VersionBits(version);
        for (var i = 0; i < 18; i++)
        {
            var dark = Bit(versionBits, i);
            modules[i / 3, size - 11 + (i % 3)] = dark;
            modules[size - 11 + (i % 3), i / 3] = dark;
        }
    }

    private static bool Bit(int bits, int index) => ((bits >> index) & 1) == 1;

    private static int FormatBits(int mask)
    {
        var data = mask;
        var bits = data << 10;
        const int generator = 0b10100110111;
        for (var i = 4; i >= 0; i--)
        {
            if (((bits >> (i + 10)) & 1) != 0)
                bits ^= generator << i;
        }
        return ((data << 10) | (bits & 0x3FF)) ^ 0b101010000010010;
    }

    private static int VersionBits(int version)
    {
        var bits = version << 12;
        const int generator = 0b1111100100101;
        for (var i = 5; i >= 0; i--)
        {
            if (((bits >> (i + 12)) & 1) != 0)
                bits ^= generator << i;
        }
        return (version << 12) | (bits & 0xFFF);
    }

    private static int Penalty(bool[,] modules)
    {
        var size = modules.GetLength(0);
        var score = 0;
        for (var y = 0; y < size; y++)
            score += RunPenalty(modules, y, true);
        for (var x = 0; x < size; x++)
            score += RunPenalty(modules, x, false);
        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var dark = modules[y, x];
                if (modules[y, x + 1] == dark && modules[y + 1, x] == dark && modules[y + 1, x + 1] == dark)
                    score += 3;
            }
        }
        var darkCount = 0;
        foreach (var dark in modules)
        {
            if (dark)
                darkCount++;
        }
        score += Math.Abs(darkCount * 100 / (size * size) - 50) / 5 * 10;
        return score;
    }

    private static int RunPenalty(bool[,] modules, int fixedIndex, bool row)
    {
        var size = modules.GetLength(0);
        var score = 0;
        var run = 1;
        var previous = row ? modules[fixedIndex, 0] : modules[0, fixedIndex];
        for (var i = 1; i < size; i++)
        {
            var dark = row ? modules[fixedIndex, i] : modules[i, fixedIndex];
            if (dark == previous)
                run++;
            else
            {
                if (run >= 5)
                    score += 3 + run - 5;
                run = 1;
                previous = dark;
            }
        }
        if (run >= 5)
            score += 3 + run - 5;
        return score;
    }

    private static void Set(bool[,] modules, bool[,] function, int x, int y, bool dark)
    {
        modules[y, x] = dark;
        function[y, x] = true;
    }

    private readonly record struct VersionSpec(int DataCodewords, int Ecc, int Group1, int Data1, int Group2, int Data2, int Remainder)
    {
        public int Blocks => Group1 + Group2;
    }

    private static readonly VersionSpec[] Specs =
    [
        new(16, 10, 1, 16, 0, 0, 0),
        new(28, 16, 1, 28, 0, 0, 7),
        new(44, 26, 1, 44, 0, 0, 7),
        new(64, 18, 2, 32, 0, 0, 7),
        new(86, 24, 2, 43, 0, 0, 7),
        new(108, 16, 4, 27, 0, 0, 7),
        new(124, 18, 4, 31, 0, 0, 0),
        new(154, 22, 2, 38, 2, 39, 0),
        new(182, 22, 3, 36, 2, 37, 0),
        new(216, 26, 4, 43, 1, 44, 0),
    ];

    private static readonly int[][] Alignments =
    [
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50],
    ];
}
