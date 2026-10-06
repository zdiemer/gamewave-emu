using System.Text;

namespace GameWave.Engine;

/// <summary>
/// The console's save memory. Games save named slots ("TOP 10 SCORES") under their game
/// name; the store keeps every slot from every game in one file, as the console kept them
/// in one flash chip.
/// </summary>
public sealed class SaveStore
{
    public sealed class Slot
    {
        public int Id;
        public string GameName = "";
        public string SlotName = "";
        public byte[] Data = [];
    }

    readonly string? _path;
    readonly List<Slot> _slots = new();
    readonly object _gate = new();
    bool _dirty;
    byte[]? _persisted;
    long _revision;
    public long Revision { get { lock (_gate) return _revision; } }
    public bool WriteThrough { get; set; } = true;
    public void Flush() { lock (_gate) if (_dirty) Save(force: true); }

    /// <summary>Opens a store backed by a file, or an in-memory one when the path is null.</summary>
    public SaveStore(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            _persisted = File.ReadAllBytes(path);
            try { Import(_persisted); _dirty = false; }
            catch (IOException) { }
        }
    }

    public IReadOnlyList<Slot> Slots
    {
        get { lock (_gate) return _slots.ToList(); }
    }

    public List<Slot> Enumerate(string gameName)
    {
        lock (_gate)
            return _slots.Where(s => s.GameName == gameName).ToList();
    }

    public void Add(int id, string gameName, string slotName, byte[] data)
    {
        lock (_gate)
        {
            _slots.RemoveAll(s => s.GameName == gameName && s.SlotName == slotName);
            _slots.Add(new Slot { Id = id, GameName = gameName, SlotName = slotName, Data = data });
            Save();
        }
    }

    public void Update(Slot slot, byte[] data)
    {
        lock (_gate)
        {
            // Keep the slot's size, as a fixed flash slot would.
            var copy = new byte[slot.Data.Length == 0 ? data.Length : slot.Data.Length];
            Array.Copy(data, copy, Math.Min(data.Length, copy.Length));
            slot.Data = copy;
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _slots.Clear();
            Save();
        }
    }

    internal void RestoreSlots(IEnumerable<Slot> slots, bool persist = true)
    {
        lock (_gate)
        {
            _slots.Clear();
            _slots.AddRange(slots);
            _dirty = true;
            _revision++;
            if (persist) Save(force: true);
        }
    }

    // File layout: "GWSAVE1\0", count, then per slot: id, game name, slot name, data.
    public void Import(ReadOnlySpan<byte> bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes.ToArray()), Encoding.UTF8);
        if (!r.ReadBytes(8).AsSpan().SequenceEqual("GWSAVE1\0"u8)) throw new InvalidDataException("Invalid flash save image.");
        int count = r.ReadInt32();
        if (count < 0 || count > 65536 || count > (r.BaseStream.Length - r.BaseStream.Position) / 10)
            throw new InvalidDataException("Invalid flash slot count.");
        string Text()
        {
            int length = r.Read7BitEncodedInt();
            if (length < 0 || length > 1 << 20 || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Invalid flash slot name.");
            return Encoding.UTF8.GetString(r.ReadBytes(length));
        }
        var slots = new List<Slot>();
        for (int i = 0; i < count; i++)
        {
            var slot = new Slot { Id = r.ReadInt32(), GameName = Text(), SlotName = Text() };
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Invalid flash slot length.");
            slot.Data = r.ReadBytes(length); slots.Add(slot);
        }
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Unexpected flash save data.");
        lock (_gate)
        {
            // Keep open game save handles bound to the existing slot objects.
            for (int i = 0; i < slots.Count; i++)
            {
                var imported = slots[i];
                if (_slots.FirstOrDefault(s => s.GameName == imported.GameName && s.SlotName == imported.SlotName) is { } existing)
                {
                    existing.Id = imported.Id; existing.Data = imported.Data; slots[i] = existing;
                }
            }
            _slots.Clear(); _slots.AddRange(slots); Save();
        }
    }

    public byte[] Export()
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write("GWSAVE1\0"u8); w.Write(_slots.Count);
            foreach (var s in _slots)
            {
                w.Write(s.Id); w.Write(s.GameName); w.Write(s.SlotName); w.Write(s.Data.Length); w.Write(s.Data);
            }
            return ms.ToArray();
        }
    }

    void Save(bool force = false)
    {
        if (!force) _revision++;
        _dirty = true;
        if (!force && !WriteThrough) return;
        if (_path is null)
        {
            _dirty = false;
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        byte[] bytes = Export();
        if (_persisted is not null && bytes.AsSpan().SequenceEqual(_persisted))
        {
            _dirty = false;
            return;
        }
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, _path, true);
        _persisted = bytes;
        _dirty = false;
    }
}
