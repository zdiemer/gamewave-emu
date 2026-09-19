namespace GameWave.Disc;

/// <summary>A disc whose files have been copied out into a folder.</summary>
public sealed class FolderDisc : IDisc
{
    readonly string _root;

    public FolderDisc(string root)
    {
        _root = Path.GetFullPath(root);
        Label = Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public string Label { get; }

    string? Resolve(string path, bool wantDirectory)
    {
        string current = _root;
        var parts = DiscPath.Split(path);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            string? match = null;
            var candidates = last && !wantDirectory
                ? Directory.EnumerateFiles(current)
                : Directory.EnumerateDirectories(current);
            foreach (var c in candidates)
            {
                if (string.Equals(Path.GetFileName(c), parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = c;
                    break;
                }
            }
            if (match is null)
                return null;
            current = match;
        }
        return current;
    }

    public DiscFile? Find(string path)
    {
        var p = Resolve(path, false);
        return p is null ? null : new FolderFile(DiscPath.Normalize(path), p);
    }

    public IReadOnlyList<DiscEntry>? List(string path)
    {
        var p = DiscPath.Split(path).Length == 0 ? _root : Resolve(path, true);
        if (p is null)
            return null;
        var list = new List<DiscEntry>();
        foreach (var d in Directory.EnumerateDirectories(p))
            list.Add(new DiscEntry(Path.GetFileName(d), true, 0));
        foreach (var f in Directory.EnumerateFiles(p))
            list.Add(new DiscEntry(Path.GetFileName(f), false, new FileInfo(f).Length));
        return list;
    }

    public void Dispose() { }

    sealed class FolderFile(string discPath, string fullPath) : DiscFile
    {
        public override string Path => discPath;
        public override long Length => new FileInfo(fullPath).Length;
        public override Stream Open() => new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    }
}
