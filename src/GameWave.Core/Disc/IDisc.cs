namespace GameWave.Disc;

/// <summary>A read-only view of a Game Wave disc's files.</summary>
public interface IDisc : IDisposable
{
    /// <summary>A short name for the disc, from its volume label or file name.</summary>
    string Label { get; }

    /// <summary>
    /// Finds a file by its disc path, such as <c>/data/game.zbc</c>. Game Wave discs were
    /// authored on Windows and the scripts disagree with the disc about case (<c>/Data/</c>
    /// against <c>data</c>), so lookup ignores case, as the console's file system did.
    /// </summary>
    DiscFile? Find(string path);

    /// <summary>Lists a directory's entries, or null when there is no such directory.</summary>
    IReadOnlyList<DiscEntry>? List(string path);
}

public readonly record struct DiscEntry(string Name, bool IsDirectory, long Length);

/// <summary>A file on a disc, openable any number of times for independent reading.</summary>
public abstract class DiscFile
{
    public abstract string Path { get; }
    public abstract long Length { get; }
    public abstract Stream Open();

    public byte[] ReadAll()
    {
        using var s = Open();
        var buf = new byte[Length];
        s.ReadExactly(buf);
        return buf;
    }
}

public static class DiscPath
{
    public static string[] Split(string path)
        => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static string Normalize(string path) => "/" + string.Join('/', Split(path));

    public static string Combine(string dir, string file)
    {
        if (file.StartsWith('/') || file.StartsWith('\\'))
            return Normalize(file);
        return Normalize(dir.TrimEnd('/', '\\') + "/" + file);
    }
}
