using GameWave.Config;
using GameWave.Disc;

namespace GameWave.Cli;

/// <summary>Opens a disc for a command, showing progress when a zip has to be unpacked.</summary>
internal static class CliDisc
{
    public static IDisc Open(string path)
    {
        var settings = SettingsStore.Load();
        var cache = string.IsNullOrWhiteSpace(settings.Emulation.UnpackDirectory) ? DiscLoader.DefaultCacheDirectory : settings.Emulation.UnpackDirectory;
        int last = -1;
        var progress = new ProgressReport(p =>
        {
            int percent = (int)(p * 100);
            if (percent / 10 != last / 10)
                Console.Error.WriteLine($"Unpacking {DiscFiles.DisplayName(path)}: {percent}%");
            last = percent;
        });
        return DiscLoader.Open(path, cache, Math.Max(1, settings.Emulation.UnpackedDiscsKept), progress);
    }

    sealed class ProgressReport(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
