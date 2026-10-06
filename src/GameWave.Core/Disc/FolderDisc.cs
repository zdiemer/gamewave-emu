namespace GameWave.Disc;

/// <summary>A disc whose files have been copied out into a folder.</summary>
public sealed class FolderDisc : IDisc
{
    readonly string _root;
    readonly FileSystem _fileSystem;

    public FolderDisc(string root, FileSystem? fileSystem = null)
    {
        _root = Path.GetFullPath(root);
        _fileSystem = fileSystem ?? LocalFileSystem.Instance;
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
            var candidates = _fileSystem.List(current).Where(e => e.IsDirectory == !(last && !wantDirectory));
            foreach (var c in candidates)
            {
                if (string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = Path.Combine(current, c.Name);
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
        return p is null ? null : new FolderFile(DiscPath.Normalize(path), p, _fileSystem);
    }

    public IReadOnlyList<DiscEntry>? List(string path)
    {
        var p = DiscPath.Split(path).Length == 0 ? _root : Resolve(path, true);
        if (p is null)
            return null;
        return _fileSystem.List(p).Select(e => new DiscEntry(e.Name, e.IsDirectory, e.Length)).ToArray();
    }

    public void Dispose() { }

    sealed class FolderFile(string discPath, string fullPath, FileSystem fileSystem) : DiscFile
    {
        public override string Path => discPath;
        public override long Length => fileSystem.Stat(fullPath)?.Length ?? throw new FileNotFoundException(fullPath);
        public override Stream Open() => fileSystem.OpenRead(fullPath);
    }
}
