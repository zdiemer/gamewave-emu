using GameWave.Config;

namespace GameWave.Cli;

/// <summary><c>gamewave play</c>: the window.</summary>
internal static class PlayCommand
{
    public static int Run(string[] args)
    {
        string? disc = null;
        bool noConfig = args.Contains("--no-config");
        var session = noConfig ? SettingsSession.Detached() : SettingsSession.Stored();
        var settings = noConfig ? new GameWaveSettings() : SettingsStore.Load();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--no-config":
                    break;
                case "--fullscreen":
                    session.Override(settings, "video.fullscreen", true);
                    break;
                case "--windowed":
                    session.Override(settings, "video.fullscreen", false);
                    break;
                case "--mute":
                    session.Override(settings, "audio.muted", true);
                    break;
                case "--scale":
                    session.Override(settings, "video.windowScale", Math.Clamp(int.Parse(Next(args, ref i)), 1, 4));
                    break;
                case "--volume":
                    session.Override(settings, "audio.volume", Math.Clamp(int.Parse(Next(args, ref i)), 0, 100));
                    break;
                case "--log":
                    session.Override(settings, "emulation.gameLogLevel", Math.Clamp(int.Parse(Next(args, ref i)), 0, 5));
                    break;
                default:
                    if (a.StartsWith("--"))
                        Console.Error.WriteLine($"gamewave: warning: ignored unknown option {a}");
                    else
                        disc = a;
                    break;
            }
        }

        if (disc is not null && !File.Exists(disc) && !Directory.Exists(disc))
            throw new FileNotFoundException($"{disc} does not exist");

        using var window = new PlayerWindow(disc, settings, settings.BuildInputMap(), session);
        window.Run();
        return 0;
    }

    static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{args[i]} needs a value");
        return args[++i];
    }
}
