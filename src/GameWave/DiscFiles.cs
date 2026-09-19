namespace GameWave;

/// <summary>What counts as a disc on disk, and how to name one.</summary>
public static class DiscFiles
{
    /// <summary>The filter string for the Open dialog.</summary>
    public const string DialogFilter = "Game Wave discs (*.iso, *.zip)\0*.iso;*.zip\0All files (*.*)\0*.*\0\0";

    /// <summary>Whether a path looks like something the emulator can open.</summary>
    public static bool IsDiscFile(string path)
    {
        if (Directory.Exists(path))
            return File.Exists(Path.Combine(path, "gamewave.diz"));
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".iso" or ".zip";
    }

    /// <summary>Disc images and disc folders directly inside a folder, sorted by name.</summary>
    public static IReadOnlyList<string> Find(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder).Where(IsDiscFile);
            var dirs = Directory.EnumerateDirectories(folder).Where(IsDiscFile);
            return files.Concat(dirs).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A disc's name for menus: the file name without its extension.</summary>
    public static string DisplayName(string path)
        => Directory.Exists(path) ? Path.GetFileName(path.TrimEnd('\\', '/')) : Path.GetFileNameWithoutExtension(path);
}
