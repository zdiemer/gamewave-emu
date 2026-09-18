using GameWave.Lua;
if (args.Length >= 2 && args[0] == "disasm")
{
    var p = ZbcLoader.Load(File.ReadAllBytes(args[1]));
    Disassembler.Dump(p, Console.Out);
}
