namespace GameWave.Disc;

/// <summary>A disc image inside a zip archive.</summary>
public static class ZipDisc
{
    public static IDisc Open(string path)
        => throw new NotSupportedException("zip archives are not supported yet; extract the .iso first");
}
