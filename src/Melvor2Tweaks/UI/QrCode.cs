using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Melvor2Tweaks.UI;

/// <summary>
/// Minimal QR code encoder (byte mode, error correction level M, versions 1-10, so up to 213 bytes), after
/// Project Nayuki's reference implementation (MIT). Used to show the phone login link as a scannable code.
/// </summary>
internal sealed class QrCode
{
    // Error correction level M, index = version (0 unused).
    private static readonly int[] EccPerBlock = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    private static readonly int[] NumBlocks = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
    private const int FormatBitsM = 0;

    public readonly int Size;
    private readonly bool[,] _modules;   // [y, x]
    private readonly bool[,] _function;

    public bool this[int x, int y] => _modules[y, x];

    private QrCode(int version)
    {
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
    }

    public static QrCode Encode(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        for (; version <= 10; version++)
            if (4 + (version < 10 ? 8 : 16) + data.Length * 8 <= DataCodewords(version) * 8) break;
        if (version > 10) throw new ArgumentException("text too long for a QR code here");

        // Bit stream: byte mode, length, data, terminator, padding.
        var bits = new List<bool>();
        void Append(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, version < 10 ? 8 : 16);
        foreach (byte b in data) Append(b, 8);
        int capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        var qr = new QrCode(version);
        qr.DrawFunctionPatterns(version);
        qr.DrawCodewords(qr.AddEccAndInterleave(codewords, version));

        // Pick the mask with the lowest penalty.
        int best = 0, bestScore = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            qr.ApplyMask(mask);
            qr.DrawFormatBits(mask);
            int score = qr.Penalty();
            if (score < bestScore) { best = mask; bestScore = score; }
            qr.ApplyMask(mask); // undo (XOR)
        }
        qr.ApplyMask(best);
        qr.DrawFormatBits(best);
        return qr;
    }

    /// <summary>The code as a black-on-white texture, <paramref name="scale"/> px per module, with the 4-module quiet zone.</summary>
    public ImageTexture ToTexture(int scale)
    {
        int border = 4, px = (Size + border * 2) * scale;
        var image = Image.CreateEmpty(px, px, false, Image.Format.Rgb8);
        image.Fill(Colors.White);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (_modules[y, x])
                    image.FillRect(new Rect2I((x + border) * scale, (y + border) * scale, scale, scale), Colors.Black);
        return ImageTexture.CreateFromImage(image);
    }

    // ---- capacity ----------------------------------------------------------------------------------
    private static int RawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int ver) => RawDataModules(ver) / 8 - EccPerBlock[ver] * NumBlocks[ver];

    // ---- function patterns ------------------------------------------------------------------------
    private void SetFunction(int x, int y, bool dark) { _modules[y, x] = dark; _function[y, x] = true; }

    private void DrawFunctionPatterns(int version)
    {
        for (int i = 0; i < Size; i++) { SetFunction(6, i, i % 2 == 0); SetFunction(i, 6, i % 2 == 0); }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions(version);
        for (int i = 0; i < align.Length; i++)
            for (int j = 0; j < align.Length; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == align.Length - 1 || i == align.Length - 1 && j == 0))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(0); // reserve the area; redrawn with the real mask later
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int vbits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((vbits >> i) & 1) != 0;
                int a = Size - 11 + i % 3, b = i / 3;
                SetFunction(a, b, bit);
                SetFunction(b, a, bit);
            }
        }
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size) SetFunction(x, y, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions(int version)
    {
        if (version == 1) return Array.Empty<int>();
        int numAlign = version / 7 + 2;
        int step = (version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        int data = FormatBitsM << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6));
        SetFunction(8, 8, Bit(7));
        SetFunction(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));
        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true); // always dark
    }

    // ---- data -------------------------------------------------------------------------------------
    private byte[] AddEccAndInterleave(byte[] data, int version)
    {
        int numBlocks = NumBlocks[version], eccLen = EccPerBlock[version];
        int rawCodewords = RawDataModules(version) / 8;
        int numShort = numBlocks - rawCodewords % numBlocks;
        int shortLen = rawCodewords / numBlocks;
        var divisor = RsDivisor(eccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;
            var ecc = RsRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            Array.Copy(dat, block, datLen);
            Array.Copy(ecc, 0, block, block.Length - eccLen, eccLen); // short blocks keep a 0 placeholder before the ECC
            blocks.Add(block);
        }
        var result = new List<byte>();
        for (int i = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= numShort) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)RsMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = RsMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)RsMultiply(divisor[i], factor);
        }
        return result;
    }

    private static int RsMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_function[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_function[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) _modules[y, x] = !_modules[y, x];
            }
    }

    /// <summary>Penalty rules 1 (runs), 2 (2x2 blocks), 3 (finder-like) and 4 (dark balance).</summary>
    private int Penalty()
    {
        int score = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < Size; a++)
            {
                int run = 0;
                bool color = false;
                for (int b = 0; b < Size; b++)
                {
                    bool m = pass == 0 ? _modules[a, b] : _modules[b, a];
                    if (b > 0 && m == color) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                    else { color = m; run = 1; }
                }
                // finder-like 1:1:3:1:1 with light space on one side
                for (int b = 0; b + 10 < Size; b++)
                {
                    bool At(int k) => pass == 0 ? _modules[a, b + k] : _modules[b + k, a];
                    bool core = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6);
                    if (core && !At(7) && !At(8) && !At(9) && !At(10)) score += 40;
                    bool core2 = At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    if (core2 && !At(0) && !At(1) && !At(2) && !At(3)) score += 40;
                }
            }
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) score += 3;
            }
        int dark = 0;
        foreach (bool m in _modules) if (m) dark++;
        int total = Size * Size;
        int k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return score + Math.Max(0, k2) * 10;
    }
}
