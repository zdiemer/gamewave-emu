using System.IO.Compression;
using GameWave.Disc;
using GameWave.Engine;

namespace GameWave.Tests;

public class FileSystemTests
{
    sealed class MemoryFiles : GameWave.Disc.FileSystem
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        public int Writes, Renames;
        public override Stream OpenRead(string path) => new MemoryStream(Files[path], false);
        sealed class Output(MemoryFiles owner, string path) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing) { owner.Files[path] = ToArray(); owner.Writes++; }
                base.Dispose(disposing);
            }
        }
        public override Stream Create(string path) => new Output(this, path);
        public override FileSystemEntry? Stat(string path) => Files.TryGetValue(path, out var file)
            ? new(Path.GetFileName(path), false, file.Length) : _directories.Contains(path) ? new(Path.GetFileName(path), true, 0) : null;
        public override IReadOnlyList<FileSystemEntry> List(string path)
            => Files.Keys.Concat(_directories).Where(p => Path.GetDirectoryName(p) == path).Select(p => Stat(p)!.Value).ToArray();
        public override void CreateDirectory(string path) => _directories.Add(path);
        public override void Delete(string path) => Files.Remove(path);
        public override void Move(string source, string destination) { Files[destination] = Files[source]; Files.Remove(source); Renames++; }
    }

    static byte[] Archive(params byte[] image)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("disc.iso", CompressionLevel.NoCompression).Open(); entry.Write(image);
        }
        return bytes.ToArray();
    }

    [Fact]
    public void ZipCacheUsesFrontendIOAndDetectsSameSizeArchiveReplacementsWithoutTimestamps()
    {
        var files = new MemoryFiles();
        string directory = Path.GetFullPath("virtual-cache"), source = Path.Combine(directory, "disc.zip");
        files.Files[source] = Archive(1, 2, 3, 4);
        string first = ZipDisc.Unpack(source, directory, 3, fileSystem: files);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, files.Files[first]);
        int writes = files.Writes;
        Assert.Equal(first, ZipDisc.Unpack(source, directory, 3, fileSystem: files)); Assert.Equal(writes, files.Writes);
        int length = files.Files[source].Length; files.Files[source] = Archive(4, 3, 2, 1); Assert.Equal(length, files.Files[source].Length);
        string second = ZipDisc.Unpack(source, directory, 3, fileSystem: files);
        Assert.NotEqual(first, second); Assert.Equal(new byte[] { 4, 3, 2, 1 }, files.Files[second]);
        Assert.Equal(2, files.Renames); Assert.DoesNotContain(files.Files.Keys, p => p.EndsWith(".partial"));
    }

    [Fact]
    public void FolderContentAndFlashFilesUseTheInjectedFileSystem()
    {
        var files = new MemoryFiles(); string directory = Path.GetFullPath("virtual-content"); files.CreateDirectory(directory);
        files.Files[Path.Combine(directory, "GAMEWAVE.DIZ")] = [1, 2, 3];
        using var disc = DiscLoader.Open(directory, fileSystem: files);
        Assert.Equal(new byte[] { 1, 2, 3 }, disc.Find("/gamewave.diz")!.ReadAll()); Assert.Single(disc.List("/")!);
        string path = Path.Combine(directory, "console.saves");
        var store = new SaveStore(path, files); store.Add(1, "game", "slot", [9, 8]);
        Assert.Equal(new byte[] { 9, 8 }, Assert.Single(new SaveStore(path, files).Slots).Data);
        Assert.False(files.Files.ContainsKey(path + ".tmp")); Assert.Equal(1, files.Renames);
    }
}
