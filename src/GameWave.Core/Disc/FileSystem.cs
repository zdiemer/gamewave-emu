namespace GameWave.Disc;

public readonly record struct FileSystemEntry(string Name, bool IsDirectory, long Length, DateTime WriteTime = default, DateTime AccessTime = default);

/// <summary>Per-session filesystem access, allowing frontends to own all content I/O.</summary>
public abstract class FileSystem
{
    public abstract Stream OpenRead(string path);
    public abstract Stream Create(string path);
    public abstract FileSystemEntry? Stat(string path);
    public abstract IReadOnlyList<FileSystemEntry> List(string path);
    public abstract void CreateDirectory(string path);
    public abstract void Delete(string path);
    public abstract void Move(string source, string destination);
    public virtual void Touch(string path) { }
    public bool FileExists(string path) => Stat(path) is { IsDirectory: false };
    public bool DirectoryExists(string path) => Stat(path) is { IsDirectory: true };
    public byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        if (stream.Length > int.MaxValue) throw new IOException("The file is too large to read into memory.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = Create(path); stream.Write(bytes);
    }
    public IEnumerable<string> ReadLines(string path)
    {
        using var stream = OpenRead(path); using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
}

public sealed class LocalFileSystem : FileSystem
{
    public static readonly LocalFileSystem Instance = new();
    public override Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    public override Stream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    public override FileSystemEntry? Stat(string path)
    {
        if (Directory.Exists(path)) return new(Path.GetFileName(path), true, 0);
        if (!File.Exists(path)) return null;
        var file = new FileInfo(path);
        return new(file.Name, false, file.Length, file.LastWriteTimeUtc, file.LastAccessTimeUtc);
    }
    public override IReadOnlyList<FileSystemEntry> List(string path)
        => Directory.EnumerateFileSystemEntries(path).Select(p => Stat(p)!.Value)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
    public override void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public override void Delete(string path) => File.Delete(path);
    public override void Move(string source, string destination) => File.Move(source, destination, true);
    public override void Touch(string path) => File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
}
