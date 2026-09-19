namespace GameWave.Graphics;

/// <summary>What <c>text.Render</c> asks for, beyond the string and font.</summary>
public sealed record TextStyle
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>0 left, 1 centre, 2 right.</summary>
    public int HAlign { get; init; }
    /// <summary>0 top, 1 middle, 2 bottom.</summary>
    public int VAlign { get; init; }
    /// <summary>Extra pixels between characters (the scripts use small negative values to tighten).</summary>
    public int Tracking { get; init; }
    /// <summary>Extra pixels between lines.</summary>
    public int Leading { get; init; }
    public bool UseColor { get; init; }
    public byte Y { get; init; } = 235;
    public byte Cb { get; init; } = 128;
    public byte Cr { get; init; } = 128;
}

/// <summary>Lays text out into a texture the way the engine's text module does.</summary>
public static class TextRenderer
{
    /// <summary>Renders into a new texture sized to the text (for <c>text.RenderSimple</c>).</summary>
    public static Texture RenderSimple(Font font, string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int w = Math.Max(1, lines.Max(l => font.Measure(l)));
        int h = Math.Max(1, lines.Length * font.LineHeight);
        return Render(font, text, new TextStyle { Width = w, Height = h });
    }

    public static Texture Render(Font font, string text, TextStyle style)
    {
        var tex = new Texture(style.Width, style.Height, "text");
        var lines = Wrap(font, text.Replace("\r", ""), style.Width, style.Tracking);
        int lineStep = font.LineHeight + style.Leading;
        int blockHeight = lines.Count == 0 ? 0 : (lines.Count - 1) * lineStep + font.LineHeight;
        int y = style.VAlign switch
        {
            1 => (style.Height - blockHeight) / 2,
            2 => style.Height - blockHeight,
            _ => 0,
        };
        uint tint = style.UseColor ? Color.YuvToRgb(style.Y, style.Cb, style.Cr) & 0xFFFFFF : 0;
        foreach (var line in lines)
        {
            int lw = font.Measure(line, style.Tracking);
            int x = style.HAlign switch
            {
                1 => (style.Width - lw) / 2,
                2 => style.Width - lw,
                _ => 0,
            };
            DrawLine(tex, font, line, x, y, style.Tracking, style.UseColor, tint);
            y += lineStep;
        }
        tex.Touch();
        return tex;
    }

    static List<string> Wrap(Font font, string text, int width, int tracking)
    {
        var result = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            if (font.Measure(para, tracking) <= width)
            {
                result.Add(para);
                continue;
            }
            var words = para.Split(' ');
            string current = "";
            foreach (var word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.Measure(candidate, tracking) > width)
                {
                    result.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            result.Add(current);
        }
        return result;
    }

    static void DrawLine(Texture dst, Font font, string line, int x, int y, int tracking, bool useColor, uint tint)
    {
        var sheet = font.Sheet;
        if (sheet is null)
            return;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!font.TryGetGlyph(c, out var g) && !font.TryGetGlyph('?', out g))
                continue;
            DrawGlyph(dst, sheet, g, x, y, useColor, tint);
            x += g.Advance + tracking;
            if (i + 1 < line.Length)
                x += font.Kerning(c, line[i + 1]);
        }
    }

    static void DrawGlyph(Texture dst, Texture sheet, Font.Glyph g, int x, int y, bool useColor, uint tint)
    {
        int cw = g.Right - g.Left, ch = g.Bottom - g.Top;
        for (int gy = 0; gy < ch; gy++)
        {
            int dy = y + gy;
            if (dy < 0 || dy >= dst.Height)
                continue;
            int sy = g.Top + gy;
            if (sy < 0 || sy >= sheet.Height)
                continue;
            for (int gx = 0; gx < cw; gx++)
            {
                int dx = x + gx;
                if (dx < 0 || dx >= dst.Width)
                    continue;
                int sx = g.Left + gx;
                if (sx < 0 || sx >= sheet.Width)
                    continue;
                uint s = sheet.Pixels[sy * sheet.Width + sx];
                if ((s >> 24) == 0)
                    continue;
                if (useColor)
                    s = (s & 0xFF000000) | tint;
                ref uint d = ref dst.Pixels[dy * dst.Width + dx];
                d = (d >> 24) == 0 ? s : Texture.Over(s, d);
            }
        }
    }
}
