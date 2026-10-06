using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GameWave.Engine;
using GameWave.Graphics;

namespace GameWave.Tests;

/// <summary>The disc's own file formats, built synthetically.</summary>
public class FormatTests
{
    static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
            z.Write(data);
        return ms.ToArray();
    }

    static byte[] Zbm(int w, int h, ushort[] pixels)
    {
        // Pixels are big endian and stored in 32-bit words with the pair swapped.
        var raw = new byte[(pixels.Length + 1) / 2 * 4];
        for (int i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2 * (i ^ 1)), pixels[i]);
        var packed = Zlib(raw);
        var header = new byte[48];
        int[] fields = [1, 1, 4, 2, w, h, 0, 0, 1, packed.Length, raw.Length, 0];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(i * 4), fields[i]);
        return [.. header, .. packed];
    }

    [Fact]
    public void DecodesZbmPixelsInOrder()
    {
        // Opaque white (y 59, neutral chroma), then fully transparent.
        ushort white = (ushort)(15 << 12 | 59 << 6 | 4 << 3 | 4);
        var t = Texture.FromZbm(Zbm(3, 1, [white, 0, white]));
        Assert.Equal(3, t.Width);
        Assert.Equal(0xFFu, t.Pixels[0] >> 24);
        Assert.Equal(0u, t.Pixels[1] >> 24);
        Assert.True((t.Pixels[0] & 0xFF) > 230, "white stays white");
    }

    [Fact]
    public void NeutralChromaIsGrey()
    {
        uint c = Color.FromAyuv4633((ushort)(15 << 12 | 32 << 6 | 4 << 3 | 4));
        Assert.Equal((c >> 16) & 0xFF, c & 0xFF);
        Assert.Equal((c >> 8) & 0xFF, c & 0xFF);
    }

    [Fact]
    public void ParsesFontMetrics()
    {
        var dat = new byte[0x280 + 20 + 2 * 32 + 12];
        Encoding.ASCII.GetBytes("Test").CopyTo(dat, 0);
        Encoding.ASCII.GetBytes("sheet.zbm").CopyTo(dat, 0x200);
        int[] head = [2, 65, 66, 10, 1];
        for (int i = 0; i < head.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(0x280 + 4 * i), head[i]);
        int[] a = [0, 0, 10, 10, 6, 0, 5, 1];
        int[] b = [10, 0, 20, 10, 7, 1, 5, 1];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(0x294 + 4 * i), a[i]);
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(0x294 + 32 + 4 * i), b[i]);
        }
        int[] kern = [65, 66, -2];
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteInt32LittleEndian(dat.AsSpan(0x294 + 64 + 4 * i), kern[i]);

        var f = Font.FromDat(dat);
        Assert.Equal("Test", f.Family);
        Assert.Equal("sheet.zbm", f.SheetName);
        Assert.Equal(10, f.LineHeight);
        Assert.Equal(-2, f.Kerning('A', 'B'));
        Assert.Equal(6 + 7 - 2, f.Measure("AB"));
        Assert.Equal(6 + 7 - 2 + 2, f.Measure("AB", tracking: 1));
    }

    [Fact]
    public void LooksWordsUpInTheTrie()
    {
        // Root: two children, 'a' (a word) and 'b' leading to a node with 'e' (a word): "a", "be".
        byte[] trie =
        [
            1, 1, 0, 0, 0, 0, 0, 0,
            2, 0x21, 0x42, 4,
            1, 0x25,
        ];
        var w = WordList.Load(trie);
        Assert.True(w.Contains("a"));
        Assert.True(w.Contains("be"));
        Assert.False(w.Contains("b"));
        Assert.False(w.Contains("bee"));
        Assert.False(w.Contains("c"));
    }

    [Fact]
    public void ParsesTheBootFile()
    {
        var info = GameInfo.Parse("[global]\r\nappname=Zap21\r\nappfile=/data/game.zbc\r\nversion=1.05\r\n\r\n[platform]\r\nboard=3\r\nengine=/data/app_sdram_3.cat.bin\r\nversion=1.01.3\r\n");
        Assert.Equal("Zap21", info.AppName);
        Assert.Equal("/data/game.zbc", info.AppFile);
        Assert.Equal("/data/app_sdram_3.cat.bin", info.EngineFile);
        Assert.Equal("1.01.3", info.EngineVersion);
    }

    [Fact]
    public void SavesSurviveAReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gw-save-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new SaveStore(path);
            store.Add(7, "Gemz", "TOP 10 SCORES", [1, 2, 3, 4]);
            var again = new SaveStore(path);
            var slot = Assert.Single(again.Enumerate("Gemz"));
            Assert.Equal("TOP 10 SCORES", slot.SlotName);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, slot.Data);
            Assert.Empty(again.Enumerate("Zap21"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SpeculativeSavesStayInMemoryUntilCommitted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gw-save-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new SaveStore(path);
            store.Add(7, "Gemz", "TOP 10 SCORES", [1, 2, 3, 4]);
            byte[] committed = File.ReadAllBytes(path);
            store.WriteThrough = false;
            store.Add(7, "Gemz", "TOP 10 SCORES", [8, 8, 8, 8]);
            Assert.Equal(committed, File.ReadAllBytes(path));
            store.RestoreSlots([new() { Id = 7, GameName = "Gemz", SlotName = "TOP 10 SCORES", Data = [1, 2, 3, 4] }], persist: false);
            Assert.Equal(committed, File.ReadAllBytes(path));
            store.Flush(); Assert.Equal(committed, File.ReadAllBytes(path));
            store.Add(7, "Gemz", "TOP 10 SCORES", [5, 6, 7, 8]); store.Flush();
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, Assert.Single(new SaveStore(path).Slots).Data);
        }
        finally { File.Delete(path); }
    }
}
