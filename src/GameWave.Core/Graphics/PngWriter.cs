using System.Buffers.Binary;
using System.IO.Compression;

namespace GameWave.Graphics;

/// <summary>Writes 0xAARRGGBB pixels as an RGB PNG.</summary>
public static class PngWriter
{
    public static void Write(string path, uint[] pixels, int width, int height)
    {
        using var f = File.Create(path);
        Write(f, pixels, width, height);
    }

    public static void Write(Stream s, uint[] pixels, int width, int height)
    {
        s.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolour
        Chunk(s, "IHDR", ihdr);

        var raw = new byte[(width * 3 + 1) * height];
        int p = 0;
        for (int y = 0; y < height; y++)
        {
            raw[p++] = 0;
            for (int x = 0; x < width; x++)
            {
                uint c = pixels[y * width + x];
                raw[p++] = (byte)(c >> 16);
                raw[p++] = (byte)(c >> 8);
                raw[p++] = (byte)c;
            }
        }
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
            z.Write(raw);
        Chunk(s, "IDAT", ms.ToArray());
        Chunk(s, "IEND", []);
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
