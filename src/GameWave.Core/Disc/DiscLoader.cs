namespace GameWave.Disc;

public static class DiscLoader
{
    /// <summary>Opens a disc image (.iso) or a folder holding a disc's files.</summary>
    public static IDisc Open(string path)
    {
        if (Directory.Exists(path))
            return new FolderDisc(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{path} does not exist");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".zip")
            return ZipDisc.Open(path);
        using (var probe = File.OpenRead(path))
        {
            if (!UdfDisc.Probe(probe))
                throw new InvalidDataException($"{Path.GetFileName(path)} is not a DVD image");
        }
        var disc = UdfDisc.Open(path);
        return new LabelledDisc(disc, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Uses the file name as the label, which reads better than a volume id.</summary>
    sealed class LabelledDisc(IDisc inner, string label) : IDisc
    {
        public string Label => label;
        public DiscFile? Find(string path) => inner.Find(path);
        public IReadOnlyList<DiscEntry>? List(string path) => inner.List(path);
        public void Dispose() => inner.Dispose();
    }
}
