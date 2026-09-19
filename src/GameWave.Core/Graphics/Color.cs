using System.Runtime.CompilerServices;

namespace GameWave.Graphics;

/// <summary>Colour conversions. Pixels are held as 0xAARRGGBB, not premultiplied.</summary>
public static class Color
{
    static readonly uint[] Ayuv4633Table = BuildAyuv4633();

    /// <summary>BT.601 studio range YCbCr to 0xFFRRGGBB.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint YuvToRgb(int y, int cb, int cr)
    {
        int c = (y - 16) * 298;
        int d = cb - 128;
        int e = cr - 128;
        int r = (c + 409 * e + 128) >> 8;
        int g = (c - 100 * d - 208 * e + 128) >> 8;
        int b = (c + 516 * d + 128) >> 8;
        return 0xFF000000u | (uint)(Clamp(r) << 16) | (uint)(Clamp(g) << 8) | (uint)Clamp(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>
    /// The OSD's 16-bit pixel format, stored big endian: 4 bits of alpha, 6 of luma and 3 each
    /// of Cb and Cr. Font glyphs are y=59, cb=cr=4, which lands on studio white (236, 128, 128),
    /// so luma scales by 4 and chroma by 32 around a centre of 4.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromAyuv4633(ushort v) => Ayuv4633Table[v];

    static uint[] BuildAyuv4633()
    {
        var t = new uint[65536];
        for (int v = 0; v < 65536; v++)
        {
            int a = v >> 12;
            int y = (v >> 6) & 63;
            int u = (v >> 3) & 7;
            int w = v & 7;
            uint rgb = YuvToRgb(y << 2, u << 5, w << 5) & 0xFFFFFF;
            t[v] = ((uint)(a * 17) << 24) | rgb;
        }
        return t;
    }

    /// <summary>The 32-bit OSD format some fonts use: bytes A, Y, Cb, Cr.</summary>
    public static uint FromAyuv8888(byte a, byte y, byte cb, byte cr)
        => ((uint)a << 24) | (YuvToRgb(y, cb, cr) & 0xFFFFFF);
}
