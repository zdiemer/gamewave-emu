namespace GameWave.Disc;

public static class DiscLoader
{
    /// <summary>Where zipped discs are unpacked unless told otherwise.</summary>
    public static string DefaultCacheDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "gamewave", "unpacked");

    /// <summary>
    /// Opens a disc image (.iso), a zip holding one (unpacked into <paramref name="cacheDirectory"/>
    /// first, keeping the <paramref name="keep"/> most recent), or a folder holding a disc's files.
    /// </summary>
    public static IDisc Open(string path, string? cacheDirectory = null, int keep = 3, IProgress<double>? progress = null, CancellationToken cancel = default, FileSystem? fileSystem = null)
    {
        fileSystem ??= LocalFileSystem.Instance;
        if (fileSystem.DirectoryExists(path))
            return new FolderDisc(path, fileSystem);
        if (!fileSystem.FileExists(path))
            throw new FileNotFoundException($"{path} does not exist");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".zip")
        {
            var image = ZipDisc.Unpack(path, cacheDirectory ?? DefaultCacheDirectory, keep, progress, cancel, fileSystem);
            return new LabelledDisc(UdfDisc.Open(image, fileSystem), Path.GetFileNameWithoutExtension(path));
        }
        using (var probe = fileSystem.OpenRead(path))
        {
            if (!UdfDisc.Probe(probe))
                throw new InvalidDataException($"{Path.GetFileName(path)} is not a DVD image");
        }
        var disc = UdfDisc.Open(path, fileSystem);
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
