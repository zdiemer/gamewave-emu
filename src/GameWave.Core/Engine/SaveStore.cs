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

    /// <summary>Opens a store backed by a file, or an in-memory one when the path is null.</summary>
    public SaveStore(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
            Load(File.ReadAllBytes(path));
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

    // File layout: "GWSAVE1\0", count, then per slot: id, game name, slot name, data.
    void Load(byte[] b)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(b), Encoding.UTF8);
            if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "GWSAVE1\0")
                return;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var s = new Slot
                {
                    Id = r.ReadInt32(),
                    GameName = r.ReadString(),
                    SlotName = r.ReadString(),
                };
                s.Data = r.ReadBytes(r.ReadInt32());
                _slots.Add(s);
            }
        }
        catch (EndOfStreamException)
        {
        }
    }

    void Save()
    {
        if (_path is null)
            return;
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Encoding.ASCII.GetBytes("GWSAVE1\0"));
            w.Write(_slots.Count);
            foreach (var s in _slots)
            {
                w.Write(s.Id);
                w.Write(s.GameName);
                w.Write(s.SlotName);
                w.Write(s.Data.Length);
                w.Write(s.Data);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, _path, true);
    }
}
