namespace GameWave.Engine;

/// <summary>
/// The boot description in a disc's <c>gamewave.diz</c>, an INI file:
/// <code>
/// [global]
/// appname=Zap21
/// appfile=/data/game.zbc
/// [platform]
/// board=3
/// engine=/data/app_sdram_3.cat.bin
/// </code>
/// Some discs list a second [platform] for board revision "a".
/// </summary>
public sealed class GameInfo
{
    public string AppName { get; init; } = "";
    public string AppFile { get; init; } = "/data/game.zbc";
    public string Version { get; init; } = "";
    public string? EngineFile { get; init; }
    public string EngineVersion { get; init; } = "";

    public static GameInfo Parse(string text)
    {
        string section = "";
        string app = "", file = "/data/game.zbc", version = "";
        string? engine = null;
        string engineVersion = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            string key = line[..eq].Trim().ToLowerInvariant();
            string value = line[(eq + 1)..].Trim();
            if (section == "global")
            {
                if (key == "appname") app = value;
                else if (key == "appfile") file = value;
                else if (key == "version") version = value;
            }
            else if (section == "platform" && engine is null && key == "engine")
            {
                // The first platform listed is board 3, the retail console.
                engine = value;
            }
            else if (section == "platform" && key == "version" && engineVersion.Length == 0)
            {
                engineVersion = value;
            }
        }
        return new GameInfo { AppName = app, AppFile = file, Version = version, EngineFile = engine, EngineVersion = engineVersion };
    }
}
