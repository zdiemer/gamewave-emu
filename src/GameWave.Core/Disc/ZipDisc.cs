using System.IO.Compression;

namespace GameWave.Disc;

/// <summary>
/// Disc images kept in zip archives. A DVD image does not suit reading from inside a
/// compressed archive (the games jump between thousands of files, and a deflate stream
/// can only be read from the start), so the image is unpacked once into a cache folder
/// and played from there. The cache keeps the most recently used images and removes the
/// rest.
/// </summary>
public static class ZipDisc
{
    /// <summary>The disc image inside a zip: its only .iso entry.</summary>
    public static ZipArchiveEntry? FindImage(ZipArchive zip)
        => zip.Entries
            .Where(e => e.FullName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();

    /// <summary>Where the cache keeps the image from <paramref name="zipPath"/>.</summary>
    public static string CachePath(string zipPath, string cacheDirectory, FileSystem? fileSystem = null)
    {
        fileSystem ??= LocalFileSystem.Instance;
        var info = fileSystem.Stat(zipPath) ?? throw new FileNotFoundException(zipPath);
        // The size and write time tell a replaced archive from the one that was unpacked.
        var stamp = $"{info.Length:x}-{info.WriteTime.Ticks:x}";
        if (info.WriteTime == default)
        {
            // VFS v3 has no timestamps. The zip directory identifies replaced images
            // without hashing an entire DVD on every load.
            using var input = fileSystem.OpenRead(zipPath);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = FindImage(archive) ?? throw new InvalidDataException("The archive has no ISO image.");
            stamp = $"{info.Length:x}-{entry.Length:x}-{entry.Crc32:x}";
        }
        var name = Path.GetFileNameWithoutExtension(zipPath);
        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');
        return Path.Combine(cacheDirectory, $"{name} [{stamp}].iso");
    }

    /// <summary>Whether the image is already unpacked and complete.</summary>
    public static bool IsUnpacked(string zipPath, string cacheDirectory)
        => File.Exists(CachePath(zipPath, cacheDirectory));

    /// <summary>
    /// Unpacks the image if it is not already, and returns its path. Progress is reported
    /// from 0 to 1. The image is written under a temporary name and renamed when complete,
    /// so an interrupted unpack is never mistaken for a finished one.
    /// </summary>
    public static string Unpack(string zipPath, string cacheDirectory, int keep, IProgress<double>? progress = null, CancellationToken cancel = default, FileSystem? fileSystem = null)
    {
        fileSystem ??= LocalFileSystem.Instance;
        var target = CachePath(zipPath, cacheDirectory, fileSystem);
        if (fileSystem.FileExists(target))
        {
            fileSystem.Touch(target);
            return target;
        }

        fileSystem.CreateDirectory(cacheDirectory);
        using var zipStream = fileSystem.OpenRead(zipPath);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = FindImage(zip) ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)} holds no .iso disc image");

        if (fileSystem is LocalFileSystem)
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!);
            if (drive.IsReady && drive.AvailableFreeSpace < entry.Length + (256L << 20))
                throw new IOException($"not enough free space in {cacheDirectory} to unpack {Path.GetFileName(zipPath)} ({entry.Length >> 20} MB)");
        }

        Prune(cacheDirectory, Math.Max(0, keep - 1), fileSystem);

        var partial = target + ".partial";
        try
        {
            using (var input = entry.Open())
            using (var output = fileSystem.Create(partial))
            {
                output.SetLength(entry.Length);
                var buffer = new byte[4 << 20];
                long done = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    done += n;
                    progress?.Report(entry.Length == 0 ? 1 : done / (double)entry.Length);
                }
                if (done != entry.Length)
                    throw new InvalidDataException($"{Path.GetFileName(zipPath)} ended early: {done} of {entry.Length} bytes");
            }
            fileSystem.Move(partial, target);
        }
        catch
        {
            TryDelete(partial, fileSystem);
            throw;
        }
        return target;
    }

    /// <summary>Removes cached images beyond the <paramref name="keep"/> most recently used.</summary>
    public static void Prune(string cacheDirectory, int keep, FileSystem? fileSystem = null)
    {
        fileSystem ??= LocalFileSystem.Instance;
        if (!fileSystem.DirectoryExists(cacheDirectory))
            return;
        var entries = fileSystem.List(cacheDirectory);
        var images = entries.Where(e => !e.IsDirectory && e.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.AccessTime > f.WriteTime ? f.AccessTime : f.WriteTime)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var old in images.Skip(keep))
            TryDelete(Path.Combine(cacheDirectory, old.Name), fileSystem);
        foreach (var partial in entries.Where(e => !e.IsDirectory && e.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)))
            TryDelete(Path.Combine(cacheDirectory, partial.Name), fileSystem);
    }

    static void TryDelete(string path, FileSystem fileSystem)
    {
        try
        {
            fileSystem.Delete(path);
        }
        catch (IOException)
        {
            // In use by another copy of the emulator; it can go next time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
