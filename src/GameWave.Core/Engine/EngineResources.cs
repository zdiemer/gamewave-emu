using System.Buffers.Binary;
using System.Text;
using GameWave.Disc;

namespace GameWave.Engine;

/// <summary>
/// Files built into the engine image that every disc carries (<c>app_sdram_3.cat.bin</c>):
/// the built-in font and the predefined background stills. They sit in an archive inside
/// the image:
/// <code>
/// 0   8  12 34 56 78 87 65 43 21
/// 8   4  entry count (big endian)
/// 12  48 per entry: name[40], offset, size (offsets from the archive start)
/// </code>
/// </summary>
public sealed class EngineResources
{
    readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _files.Keys;

    public byte[]? Get(string name) => _files.GetValueOrDefault(name);

    public static EngineResources Load(IDisc disc, GameInfo info)
    {
        var r = new EngineResources();
        var candidates = new List<string>();
        if (info.EngineFile is { } e)
            candidates.Add(e);
        candidates.Add("/data/app_sdram_3.cat.bin");
        foreach (var path in candidates)
        {
            var f = disc.Find(path);
            if (f is null)
                continue;
            r.Parse(f.ReadAll());
            if (r._files.Count > 0)
                break;
        }
        return r;
    }

    void Parse(byte[] d)
    {
        int at = -1;
        for (int i = 0; i + 12 <= d.Length; i += 4)
        {
            if (d[i] == 0x12 && d[i + 1] == 0x34 && d[i + 2] == 0x56 && d[i + 3] == 0x78
                && d[i + 4] == 0x87 && d[i + 5] == 0x65 && d[i + 6] == 0x43 && d[i + 7] == 0x21)
            {
                at = i;
                break;
            }
        }
        if (at < 0)
            return;
        int count = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at + 8));
        for (int e = 0; e < count; e++)
        {
            int p = at + 12 + e * 48;
            if (p + 48 > d.Length)
                break;
            string name = Encoding.ASCII.GetString(d, p, 40);
            int nul = name.IndexOf('\0');
            if (nul >= 0)
                name = name[..nul];
            int offset = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 40));
            int size = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p + 44));
            if (offset < 0 || size < 0 || at + (long)offset + size > d.Length)
                continue;
            _files[name] = d.AsSpan(at + offset, size).ToArray();
        }
    }
}
