using GameWave.Cli;
using GameWave.Lua;

namespace GameWave;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "run")
            return RunCommand.Run(args[1..]);
        if (args.Length >= 2 && args[0] == "disasm")
        {
            var p = ZbcLoader.Load(File.ReadAllBytes(args[1]));
            Disassembler.Dump(p, Console.Out);
            return 0;
        }
        Console.Error.WriteLine("usage: gamewave run <disc> | gamewave disasm <file.zbc>");
        return 2;
    }
}
