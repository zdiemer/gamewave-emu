using GameWave.Disc;
using GameWave.Engine;
using GameWave.Lua;

namespace GameWave.Cli;

/// <summary><c>gamewave info</c> and <c>gamewave disasm</c>.</summary>
internal static class InfoCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: gamewave info <disc>");
        using var disc = DiscLoader.Open(args[0]);
        var diz = disc.Find("/gamewave.diz") ?? throw new InvalidDataException("this is not a Game Wave disc (there is no gamewave.diz)");
        var info = GameInfo.Parse(System.Text.Encoding.Latin1.GetString(diz.ReadAll()));

        Console.WriteLine($"Disc           {disc.Label}");
        Console.WriteLine($"Title          {info.AppName}");
        Console.WriteLine($"Game program   {info.AppFile}");
        if (info.Version.Length > 0) Console.WriteLine($"Game version   {info.Version}");
        if (info.EngineVersion.Length > 0) Console.WriteLine($"Engine         {info.EngineVersion}");

        var counts = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        void Walk(string path)
        {
            foreach (var e in disc.List(path) ?? [])
            {
                var full = path.TrimEnd('/') + "/" + e.Name;
                if (e.IsDirectory)
                {
                    Walk(full);
                    continue;
                }
                var ext = Path.GetExtension(e.Name).ToLowerInvariant();
                var c = counts.GetValueOrDefault(ext);
                counts[ext] = (c.Files + 1, c.Bytes + e.Length);
            }
        }
        Walk("/");
        Console.WriteLine();
        Console.WriteLine("Contents");
        foreach (var (ext, (files, bytes)) in counts.OrderByDescending(kv => kv.Value.Bytes))
            Console.WriteLine($"  {Describe(ext),-26} {files,6} files  {bytes / (1024.0 * 1024):N1} MB");
        return 0;
    }

    static string Describe(string ext) => ext switch
    {
        ".mpg" => "movies (.mpg)",
        ".m2v" => "stills (.m2v)",
        ".zbm" => "images (.zbm)",
        ".zwf" => "sounds (.zwf)",
        ".dat" => "fonts and data (.dat)",
        ".zbc" => "game program (.zbc)",
        ".bin" => "engine (.bin)",
        "" => "(no extension)",
        _ => ext,
    };

    public static int Disassemble(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("usage: gamewave disasm <file.zbc>");
        var p = ZbcLoader.Load(File.ReadAllBytes(args[0]));
        Disassembler.Dump(p, Console.Out);
        return 0;
    }
}
