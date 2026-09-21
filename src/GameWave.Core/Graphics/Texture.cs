using System.Buffers.Binary;
using System.IO.Compression;

namespace GameWave.Graphics;

/// <summary>An OSD bitmap: 0xAARRGGBB pixels, not premultiplied.</summary>
public sealed class Texture
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }
    public string Name { get; }

    /// <summary>Bumped whenever the pixels change, so a renderer can cache uploads.</summary>
    public int Version { get; private set; }

    /// <summary>The 0..15 level set by <c>gl.SetTextureAlphaLevel</c>; 15 leaves alpha alone.</summary>
    public int AlphaLevel { get; set; } = 15;

    public Texture(int width, int height, string name = "")
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new uint[Width * Height];
        Name = name;
    }

    public void Touch() => Version++;

    internal Texture Clone()
    {
        var copy = new Texture(Width, Height, Name) { AlphaLevel = AlphaLevel };
        Pixels.CopyTo(copy.Pixels, 0);
        copy.Version = Version;
        return copy;
    }

    /// <summary>
    /// Decodes a <c>.zbm</c> image. The 48-byte little-endian header holds 1, 1, the pixel
    /// format (4 for 16-bit A4Y6U3V3, 100 for 32-bit AYUV), bytes per pixel, width, height,
    /// two zeros, the frame count, the compressed size, the raw size and a zero; zlib data
    /// follows. Rows are packed with no padding.
    /// </summary>
    public static Texture FromZbm(byte[] data, string name = "")
    {
        if (data.Length < 48)
            throw new InvalidDataException("zbm file is too short");
        int format = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int bpp = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        int w = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        int h = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        int compressed = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(36));
        int raw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(40));
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
            throw new InvalidDataException($"zbm has a bad size {w}x{h}");

        var pixels = new byte[Math.Max(raw, (w * h + 1) * bpp)];
        int avail = Math.Min(compressed, data.Length - 48);
        using (var z = new ZLibStream(new MemoryStream(data, 48, avail), CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw)
            {
                int n = z.Read(pixels, read, raw - read);
                if (n == 0)
                    break;
                read += n;
            }
        }

        var t = new Texture(w, h, name);
        var dst = t.Pixels;
        if (bpp == 2)
        {
            // Pixels are big endian, but stored as 32-bit words that hold two of them with
            // the second pixel first, so pixel i lives at byte 2 * (i ^ 1).
            for (int i = 0; i < w * h; i++)
            {
                int o = 2 * (i ^ 1);
                dst[i] = Color.FromAyuv4633((ushort)((pixels[o] << 8) | pixels[o + 1]));
            }
        }
        else if (bpp == 4)
        {
            for (int i = 0; i < w * h; i++)
                dst[i] = Color.FromAyuv8888(pixels[4 * i], pixels[4 * i + 1], pixels[4 * i + 2], pixels[4 * i + 3]);
        }
        else
        {
            throw new InvalidDataException($"zbm pixel format {format} with {bpp} bytes per pixel is not supported");
        }
        return t;
    }

    /// <summary>Copies another texture in at (x, y), replacing what is under it.</summary>
    public void Blit(Texture src, int x, int y, bool blend)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + src.Width), y1 = Math.Min(Height, y + src.Height);
        for (int dy = y0; dy < y1; dy++)
        {
            int srow = (dy - y) * src.Width - x;
            int drow = dy * Width;
            for (int dx = x0; dx < x1; dx++)
            {
                uint s = src.Pixels[srow + dx];
                if (!blend)
                {
                    Pixels[drow + dx] = s;
                    continue;
                }
                uint sa = s >> 24;
                if (sa == 0)
                    continue;
                if (sa == 255)
                {
                    Pixels[drow + dx] = s;
                    continue;
                }
                Pixels[drow + dx] = Over(s, Pixels[drow + dx]);
            }
        }
        Touch();
    }

    /// <summary>Straight-alpha "source over destination".</summary>
    public static uint Over(uint s, uint d)
    {
        uint sa = s >> 24, da = d >> 24;
        uint outA = sa + da * (255 - sa) / 255;
        if (outA == 0)
            return 0;
        uint Channel(int shift)
        {
            uint sc = (s >> shift) & 0xFF, dc = (d >> shift) & 0xFF;
            return (sc * sa + dc * da * (255 - sa) / 255) / outA;
        }
        return (outA << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}
