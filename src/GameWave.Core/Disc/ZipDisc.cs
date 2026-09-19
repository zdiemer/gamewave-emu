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
    public static string CachePath(string zipPath, string cacheDirectory)
    {
        var info = new FileInfo(zipPath);
        // The size and write time tell a replaced archive from the one that was unpacked.
        var stamp = $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
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
    public static string Unpack(string zipPath, string cacheDirectory, int keep, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var target = CachePath(zipPath, cacheDirectory);
        if (File.Exists(target))
        {
            File.SetLastAccessTimeUtc(target, DateTime.UtcNow);
            return target;
        }

        Directory.CreateDirectory(cacheDirectory);
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = FindImage(zip) ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)} holds no .iso disc image");

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!);
        if (drive.IsReady && drive.AvailableFreeSpace < entry.Length + (256L << 20))
            throw new IOException($"not enough free space in {cacheDirectory} to unpack {Path.GetFileName(zipPath)} ({entry.Length >> 20} MB)");

        Prune(cacheDirectory, Math.Max(0, keep - 1));

        var partial = target + ".partial";
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
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
            File.Move(partial, target, true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        return target;
    }

    /// <summary>Removes cached images beyond the <paramref name="keep"/> most recently used.</summary>
    public static void Prune(string cacheDirectory, int keep)
    {
        if (!Directory.Exists(cacheDirectory))
            return;
        var images = new DirectoryInfo(cacheDirectory).GetFiles("*.iso")
            .OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)
            .ToList();
        foreach (var old in images.Skip(keep))
            TryDelete(old.FullName);
        foreach (var partial in Directory.EnumerateFiles(cacheDirectory, "*.partial"))
            TryDelete(partial);
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
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
