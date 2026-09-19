using System.Buffers.Binary;
using System.Text;

namespace GameWave.Graphics;

/// <summary>
/// A bitmap font: a <c>.dat</c> metrics file and the <c>.zbm</c> glyph sheet it names.
/// <code>
/// 0x000 128  family name           0x180 128  style ("Regular")
/// 0x080 128  PostScript name       0x200 128  glyph sheet file name
/// 0x100 128  script ("Western")
/// 0x280  20  glyph count, first char, last char, cell height, kerning pair count
/// then per glyph 32 bytes: cell left, top, right, bottom in the sheet, advance,
///     left bearing, ink width, right bearing
/// then per kerning pair 12 bytes: first char, second char, adjustment
/// </code>
/// All fields are 32-bit little endian.
/// </summary>
public sealed class Font
{
    public string Family { get; private set; } = "";
    public string SheetName { get; private set; } = "";
    public int FirstChar { get; private set; }
    public int LastChar { get; private set; }
    public int LineHeight { get; private set; }
    public Glyph[] Glyphs { get; private set; } = [];
    public Texture? Sheet { get; set; }
    readonly Dictionary<(int, int), int> _kerning = new();

    public readonly record struct Glyph(int Left, int Top, int Right, int Bottom, int Advance, int Bearing, int Width, int RightBearing);

    public static Font FromDat(byte[] d)
    {
        var f = new Font();
        f.Family = Name(d, 0);
        f.SheetName = Name(d, 0x200);
        int p = 0x280;
        int count = I(d, p);
        f.FirstChar = I(d, p + 4);
        f.LastChar = I(d, p + 8);
        f.LineHeight = I(d, p + 12);
        int kerns = I(d, p + 16);
        p += 20;
        f.Glyphs = new Glyph[count];
        for (int i = 0; i < count && p + 32 <= d.Length; i++, p += 32)
            f.Glyphs[i] = new Glyph(I(d, p), I(d, p + 4), I(d, p + 8), I(d, p + 12), I(d, p + 16), I(d, p + 20), I(d, p + 24), I(d, p + 28));
        for (int i = 0; i < kerns && p + 12 <= d.Length; i++, p += 12)
            f._kerning[(I(d, p), I(d, p + 4))] = I(d, p + 8);
        return f;
    }

    static int I(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));

    static string Name(byte[] d, int o)
    {
        int end = o;
        while (end < o + 128 && end < d.Length && d[end] != 0)
            end++;
        return Encoding.Latin1.GetString(d, o, end - o);
    }

    public bool TryGetGlyph(char c, out Glyph g)
    {
        int i = c - FirstChar;
        if (i >= 0 && i < Glyphs.Length)
        {
            g = Glyphs[i];
            return true;
        }
        g = default;
        return false;
    }

    public int Kerning(char a, char b) => _kerning.TryGetValue((a, b), out int k) ? k : 0;

    /// <summary>The advance of a run of text, including kerning and extra tracking.</summary>
    public int Measure(ReadOnlySpan<char> text, int tracking = 0)
    {
        int w = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!TryGetGlyph(text[i], out var g) && !TryGetGlyph('?', out g))
                continue;
            w += g.Advance + tracking;
            if (i + 1 < text.Length)
                w += Kerning(text[i], text[i + 1]);
        }
        return w;
    }
}
