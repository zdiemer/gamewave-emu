using System.Buffers.Binary;
using System.Text;
using GameWave.Disc;
using GameWave.Engine;
using GameWave.Lua;

namespace GameWave.Tests;

/// <summary>
/// The disc tray: games that open it to ask for their other disc (the two-disc Rewind sets),
/// closing it on the same disc, and putting in another.
/// </summary>
public sealed class TrayTests : IDisposable
{
    static uint ABC(OpCode op, int a, int b, int c) => (uint)op | (uint)c << 6 | (uint)b << 15 | (uint)a << 24;
    static uint ABx(OpCode op, int a, int bx) => (uint)op | (uint)bx << 6 | (uint)a << 24;
    static uint AsBx(OpCode op, int a, int sbx) => ABx(op, a, sbx + Instr.MaxArgSBx);
    const int K = Instr.MaxStack;

    readonly string _root = Path.Combine(Path.GetTempPath(), "gamewave-tray-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>print("boot NAME"), then engine.OpenTray().</summary>
    static LuaProto BootThenOpenTray(string name) => new()
    {
        MaxStackSize = 2,
        Constants = ["print", "boot " + name, "engine", "OpenTray"],
        Code =
        [
            ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.LoadK, 1, 1), ABC(OpCode.Call, 0, 2, 1),
            ABx(OpCode.GetGlobal, 0, 2), ABC(OpCode.GetTable, 0, 0, K + 3), ABC(OpCode.Call, 0, 1, 1),
            ABC(OpCode.Return, 0, 1, 0),
        ],
    };

    /// <summary>print("boot NAME"), then time.Sleep(10) forever.</summary>
    static LuaProto BootThenIdle(string name) => new()
    {
        MaxStackSize = 2,
        Constants = ["print", "boot " + name, "time", "Sleep", 10],
        Code =
        [
            ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.LoadK, 1, 1), ABC(OpCode.Call, 0, 2, 1),
            ABx(OpCode.GetGlobal, 0, 2), ABC(OpCode.GetTable, 0, 0, K + 3), ABx(OpCode.LoadK, 1, 4), ABC(OpCode.Call, 0, 2, 1),
            AsBx(OpCode.Jmp, 0, -5),
        ],
    };

    IDisc MakeDisc(string name, LuaProto program)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "gamewave.diz"), $"[global]\nappname={name}\nappfile=/data/game.zbc\n");
        File.WriteAllBytes(Path.Combine(dir, "data", "game.zbc"), Bytecode(program));
        return new FolderDisc(dir);
    }

    /// <summary>Lua 5.0 bytecode in the Game Wave's layout, as <see cref="ZbcLoader"/> reads it.</summary>
    internal static byte[] Bytecode(LuaProto p)
    {
        var s = new MemoryStream();
        s.Write([0x1B, (byte)'Z', (byte)'B', (byte)'C', 0x0A, 0x1A, 0x50, 1, 0, 1, 1, 4, 4, 4, 6, 8, 9, 9, 4]);
        Int(s, 31415926);
        Function(s, p);
        return s.ToArray();
    }

    static void Int(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    static void Str(Stream s, string v)
    {
        var bytes = Encoding.Latin1.GetBytes(v);
        Int(s, bytes.Length + 1);
        s.Write(bytes);
        s.WriteByte(0);
    }

    static void Function(Stream s, LuaProto p)
    {
        Str(s, "test");
        Int(s, 0);
        s.Write([(byte)p.NumUpvalues, (byte)p.NumParams, 0, (byte)p.MaxStackSize]);
        Int(s, 0); // line info
        Int(s, 0); // locals
        Int(s, 0); // upvalue names
        Int(s, p.Constants.Length);
        foreach (var k in p.Constants)
        {
            if (k.IsNumber)
            {
                s.WriteByte(3);
                Int(s, k.N);
            }
            else
            {
                s.WriteByte(4);
                Str(s, k.AsString!);
            }
        }
        Int(s, p.Protos.Length);
        foreach (var nested in p.Protos) Function(s, nested);
        Int(s, p.Code.Length);
        foreach (var i in p.Code)
            Int(s, unchecked((int)i));
    }

    sealed class Watched
    {
        public readonly List<string> Lines = new();
        public int TrayOpens;
    }

    static Watched Watch(Machine m)
    {
        var w = new Watched();
        m.Log = line =>
        {
            lock (w.Lines)
                w.Lines.Add(line);
        };
        m.TrayOpened += () => Interlocked.Increment(ref w.TrayOpens);
        return w;
    }

    static void WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("timed out waiting for " + what);
            Thread.Sleep(5);
        }
    }

    static int Boots(Watched w, string name)
    {
        lock (w.Lines)
            return w.Lines.Count(l => l == "boot " + name);
    }

    [Fact]
    public void FrameDrivenMachineOnlyAdvancesWhenTheHostRunsFrames()
    {
        using var m = new Machine(MakeDisc("frames", BootThenIdle("frames")), new SaveStore(null), frameDriven: true);
        var w = Watch(m);
        m.Start();
        Assert.Equal(0, m.Clock.Now);
        Assert.Equal(0, Boots(w, "frames"));
        for (int i = 0; i < 60; i++)
            m.RunFrame(1.0 / 60);
        Assert.Equal(1, Boots(w, "frames"));
        Assert.InRange(m.Clock.NowSeconds, 0.999999, 1.000001);
        long before = m.Clock.Now;
        Thread.Sleep(30);
        Assert.Equal(before, m.Clock.Now);
        m.Reset();
        m.RunFrame(1.0 / 60);
        Assert.Equal(2, Boots(w, "frames"));
    }

    [Fact]
    public void FrameDrivenBusyBytecodeYieldsAndStops()
    {
        var busy = new LuaProto { MaxStackSize = 1, Code = [AsBx(OpCode.Jmp, 0, -1)] };
        using var m = new Machine(MakeDisc("busy", busy), new SaveStore(null), frameDriven: true);
        m.Start();
        m.RunFrame(1.0 / 60);
        Assert.Equal(MachineState.Running, m.State);
        m.Stop();
        Assert.Equal(MachineState.Stopped, m.State);
    }

    [Fact]
    public void FrameDrivenProgramCanOpenTrayAndChangeDisc()
    {
        using var m = new Machine(MakeDisc("frames-A", BootThenOpenTray("A")), new SaveStore(null), frameDriven: true);
        m.Start();
        m.RunFrame(1.0 / 60);
        Assert.Equal(MachineState.TrayOpen, m.State);
        m.ChangeDisc(MakeDisc("frames-B", BootThenIdle("B")));
        m.RunFrame(1.0 / 60);
        Assert.Equal(MachineState.Running, m.State);
        Assert.Equal("frames-B", m.Title);
    }

    [Fact]
    public void GameOpeningTheTrayLeavesTheMachineWaitingForADisc()
    {
        using var m = new Machine(MakeDisc("A", BootThenOpenTray("A")), new SaveStore(null));
        var w = Watch(m);
        m.Start();
        WaitFor(() => m.State == MachineState.TrayOpen, "the tray to open");
        Assert.Equal(1, w.TrayOpens);
        Assert.Equal(1, Boots(w, "A"));
    }

    [Fact]
    public void ClosingTheTrayBootsTheSameDiscAgain()
    {
        using var m = new Machine(MakeDisc("A", BootThenOpenTray("A")), new SaveStore(null));
        var w = Watch(m);
        m.Start();
        WaitFor(() => m.State == MachineState.TrayOpen, "the tray to open");
        m.CloseTray();
        WaitFor(() => w.TrayOpens == 2 && m.State == MachineState.TrayOpen, "the tray to open again");
        Assert.Equal(2, Boots(w, "A"));
    }

    [Fact]
    public void ChangingDiscWhileTheTrayIsOpenBootsTheNewDisc()
    {
        using var m = new Machine(MakeDisc("A", BootThenOpenTray("A")), new SaveStore(null));
        var w = Watch(m);
        m.Start();
        WaitFor(() => m.State == MachineState.TrayOpen, "the tray to open");

        m.ChangeDisc(MakeDisc("B", BootThenIdle("B")));
        WaitFor(() => Boots(w, "B") == 1, "disc B to boot");
        Assert.Equal(MachineState.Running, m.State);
        Assert.Equal("B", m.Title);
        Assert.Equal(1, Boots(w, "A"));
    }

    [Fact]
    public void ChangingDiscDropsTheQuickSave()
    {
        using var m = new Machine(MakeDisc("A", BootThenIdle("A")), new SaveStore(null));
        var w = Watch(m);
        m.Start();
        WaitFor(() => Boots(w, "A") == 1, "disc A to boot");
        Assert.Null(m.QuickSave());
        Assert.True(m.HasQuickSave);

        m.ChangeDisc(MakeDisc("B", BootThenIdle("B")));
        WaitFor(() => Boots(w, "B") == 1, "disc B to boot");
        Assert.False(m.HasQuickSave);
    }

    [Fact]
    public void ChangingToSomethingThatIsNotAGameWaveDiscKeepsTheOldOne()
    {
        using var m = new Machine(MakeDisc("A", BootThenOpenTray("A")), new SaveStore(null));
        var w = Watch(m);
        m.Start();
        WaitFor(() => m.State == MachineState.TrayOpen, "the tray to open");

        var dir = Path.Combine(_root, "not-a-disc");
        Directory.CreateDirectory(dir);
        Assert.Throws<InvalidDataException>(() => m.ChangeDisc(new FolderDisc(dir)));
        Assert.Equal("A", m.Title);
        Assert.Equal(MachineState.TrayOpen, m.State);

        m.CloseTray();
        WaitFor(() => Boots(w, "A") == 2, "disc A to boot again");
    }
}
