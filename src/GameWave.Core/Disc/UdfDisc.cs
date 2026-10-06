using System.Buffers.Binary;
using System.Text;

namespace GameWave.Disc;

/// <summary>
/// Reads the UDF file system of a DVD image. Game Wave discs are UDF 1.02 (authored with
/// Nero), with every file stored in one partition. Only what those discs use is supported:
/// short and long allocation descriptors, embedded data, and 8 or 16 bit file names.
/// </summary>
public sealed class UdfDisc : IDisc
{
    const int SectorSize = 2048;

    readonly Func<Stream> _openImage;
    readonly Stream _meta;
    long _partitionStart;
    readonly Node _root;

    public string Label { get; private set; } = "";

    public UdfDisc(Func<Stream> openImage)
    {
        _openImage = openImage;
        _meta = openImage();
        try { _root = ReadVolume(); }
        catch { _meta.Dispose(); throw; }
    }

    public static UdfDisc Open(string isoPath, FileSystem? fileSystem = null)
        => new(() => (fileSystem ?? LocalFileSystem.Instance).OpenRead(isoPath));

    /// <summary>True when the image carries a UDF anchor at sector 256.</summary>
    public static bool Probe(Stream s)
    {
        if (s.Length < 257 * SectorSize)
            return false;
        var b = new byte[16];
        s.Position = 256L * SectorSize;
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b) == 2;
    }

    byte[] ReadSectors(long sector, int count)
    {
        var b = new byte[count * SectorSize];
        lock (_meta)
        {
            _meta.Position = sector * SectorSize;
            _meta.ReadExactly(b);
        }
        return b;
    }

    static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));

    Node ReadVolume()
    {
        var anchor = ReadSectors(256, 1);
        if (U16(anchor, 0) != 2)
            throw new InvalidDataException("no UDF anchor volume descriptor");
        uint vdsLength = U32(anchor, 16);
        uint vdsLocation = U32(anchor, 20);

        long fsdBlock = -1;
        int count = (int)Math.Max(1, vdsLength / SectorSize);
        for (int i = 0; i < count; i++)
        {
            var d = ReadSectors(vdsLocation + i, 1);
            ushort tag = U16(d, 0);
            if (tag == 5)
            {
                // Partition Descriptor: starting location at 188.
                _partitionStart = U32(d, 188);
            }
            else if (tag == 6)
            {
                // Logical Volume Descriptor: identifier (dstring) at 84, contents use at 248
                // holds the long_ad of the File Set Descriptor.
                Label = DString(d, 84, 128);
                fsdBlock = U32(d, 248 + 4);
            }
            else if (tag == 8)
            {
                break;
            }
        }
        if (fsdBlock < 0)
            throw new InvalidDataException("no UDF logical volume descriptor");

        var fsd = ReadSectors(_partitionStart + fsdBlock, 1);
        if (U16(fsd, 0) != 256)
            throw new InvalidDataException("no UDF file set descriptor");
        uint rootBlock = U32(fsd, 400 + 4);
        var root = ReadFileEntry(rootBlock, "", true);
        return root;
    }

    static string DString(byte[] b, int offset, int length)
    {
        int used = b[offset + length - 1];
        if (used == 0)
            return "";
        return DecodeName(b, offset, Math.Min(used, length - 1));
    }

    static string DecodeName(byte[] b, int offset, int length)
    {
        if (length <= 0)
            return "";
        int compression = b[offset];
        if (compression == 16)
            return Encoding.BigEndianUnicode.GetString(b, offset + 1, (length - 1) & ~1);
        return Encoding.Latin1.GetString(b, offset + 1, length - 1);
    }

    Node ReadFileEntry(uint block, string name, bool isDirectory)
    {
        var fe = ReadSectors(_partitionStart + block, 1);
        ushort tag = U16(fe, 0);
        int adOffset, lea, lad;
        if (tag == 261)
        {
            lea = (int)U32(fe, 168);
            lad = (int)U32(fe, 172);
            adOffset = 176 + lea;
        }
        else if (tag == 266)
        {
            lea = (int)U32(fe, 208);
            lad = (int)U32(fe, 212);
            adOffset = 216 + lea;
        }
        else
        {
            throw new InvalidDataException($"expected a UDF file entry at block {block}, found tag {tag}");
        }
        long length = (long)U64(fe, 56);
        int adType = U16(fe, 16 + 18) & 7;
        var node = new Node(name, isDirectory, length);

        if (adType == 3)
        {
            node.Embedded = fe.AsSpan(adOffset, (int)Math.Min(lad, length)).ToArray();
        }
        else
        {
            int adSize = adType == 0 ? 8 : 16;
            for (int p = adOffset; p + adSize <= adOffset + lad; p += adSize)
            {
                uint len = U32(fe, p);
                uint type = len >> 30;
                len &= 0x3FFFFFFF;
                if (len == 0)
                    break;
                uint pos = U32(fe, p + 4);
                if (type == 3)
                    throw new InvalidDataException("chained UDF allocation extents are not supported");
                node.Extents.Add(new Extent(_partitionStart + pos, len, type != 0));
            }
        }
        return node;
    }

    void LoadChildren(Node dir)
    {
        if (dir.Children is not null)
            return;
        var children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var data = ReadNodeData(dir);
        int p = 0;
        while (p + 38 <= data.Length)
        {
            ushort tag = U16(data, p);
            if (tag != 257)
                break;
            byte characteristics = data[p + 18];
            int lfi = data[p + 19];
            uint icbBlock = U32(data, p + 20 + 4);
            int liu = U16(data, p + 36);
            int nameOffset = p + 38 + liu;
            bool isDir = (characteristics & 0x02) != 0;
            bool isParent = (characteristics & 0x08) != 0;
            bool deleted = (characteristics & 0x04) != 0;
            if (!isParent && !deleted && lfi > 0)
            {
                string name = DecodeName(data, nameOffset, lfi);
                var child = ReadFileEntry(icbBlock, name, isDir);
                children.TryAdd(name, child);
            }
            int size = 38 + liu + lfi;
            p += (size + 3) & ~3;
        }
        dir.Children = children;
    }

    byte[] ReadNodeData(Node n)
    {
        if (n.Embedded is not null)
            return n.Embedded;
        var ms = new MemoryStream();
        foreach (var e in n.Extents)
        {
            int sectors = (int)((e.Length + SectorSize - 1) / SectorSize);
            var b = e.Unrecorded ? new byte[sectors * SectorSize] : ReadSectors(e.Sector, sectors);
            ms.Write(b, 0, (int)e.Length);
        }
        return ms.ToArray();
    }

    Node? Walk(string path)
    {
        var node = _root;
        foreach (var part in DiscPath.Split(path))
        {
            if (!node.IsDirectory)
                return null;
            LoadChildren(node);
            if (!node.Children!.TryGetValue(part, out var next))
                return null;
            node = next;
        }
        return node;
    }

    public DiscFile? Find(string path)
    {
        Node? n;
        lock (_root)
            n = Walk(path);
        if (n is null || n.IsDirectory)
            return null;
        return new UdfFile(this, DiscPath.Normalize(path), n);
    }

    public IReadOnlyList<DiscEntry>? List(string path)
    {
        lock (_root)
        {
            var n = Walk(path);
            if (n is null || !n.IsDirectory)
                return null;
            LoadChildren(n);
            return n.Children!.Values.Select(c => new DiscEntry(c.Name, c.IsDirectory, c.Length)).ToList();
        }
    }

    public void Dispose() => _meta.Dispose();

    readonly record struct Extent(long Sector, uint Length, bool Unrecorded);

    sealed class Node(string name, bool isDirectory, long length)
    {
        public readonly string Name = name;
        public readonly bool IsDirectory = isDirectory;
        public readonly long Length = length;
        public readonly List<Extent> Extents = new();
        public byte[]? Embedded;
        public Dictionary<string, Node>? Children;
    }

    sealed class UdfFile(UdfDisc disc, string path, Node node) : DiscFile
    {
        public override string Path => path;
        public override long Length => node.Length;

        public override Stream Open()
        {
            if (node.Embedded is not null)
                return new MemoryStream(node.Embedded, false);
            return new ExtentStream(disc._openImage(), node);
        }
    }

    /// <summary>A file's bytes, read across its extents from a private image stream.</summary>
    sealed class ExtentStream(Stream image, Node node) : Stream
    {
        long _pos;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => node.Length;

        public override long Position
        {
            get => _pos;
            set => _pos = Math.Clamp(value, 0, node.Length);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _pos < node.Length)
            {
                // Find the extent holding _pos.
                long start = 0;
                Extent? hit = null;
                long within = 0;
                foreach (var e in node.Extents)
                {
                    if (_pos < start + e.Length)
                    {
                        hit = e;
                        within = _pos - start;
                        break;
                    }
                    start += e.Length;
                }
                if (hit is null)
                    break;
                var ex = hit.Value;
                int n = (int)Math.Min(buffer.Length, Math.Min(ex.Length - within, node.Length - _pos));
                if (ex.Unrecorded)
                {
                    buffer[..n].Clear();
                }
                else
                {
                    image.Position = ex.Sector * SectorSize + within;
                    image.ReadExactly(buffer[..n]);
                }
                buffer = buffer[n..];
                _pos += n;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => node.Length + offset,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                image.Dispose();
            base.Dispose(disposing);
        }
    }
}
