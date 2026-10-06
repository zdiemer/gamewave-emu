using GameWave.Disc;
using GameWave.Engine;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Tests;

public sealed class PortableStateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "gamewave-state-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(_root, true);
    static uint ABC(OpCode op, int a, int b, int c) => (uint)op | (uint)c << 6 | (uint)b << 15 | (uint)a << 24;
    static uint ABx(OpCode op, int a, int bx) => (uint)op | (uint)bx << 6 | (uint)a << 24;

    Machine Create(string title = "State test", LuaProto? customProgram = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "gamewave.diz"), $"[global]\nappname={title}\nappfile=/game.zbc\n");
        var program = new LuaProto
        {
            MaxStackSize = 2,
            Constants = ["time", "Sleep", 1000, "print", "tick"],
            Code = [ABx(OpCode.GetGlobal,0,0), ABC(OpCode.GetTable,0,0,Instr.MaxStack+1),
                ABx(OpCode.LoadK,1,2), ABC(OpCode.Call,0,2,1),
                ABx(OpCode.GetGlobal,0,3), ABx(OpCode.LoadK,1,4), ABC(OpCode.Call,0,2,1),
                ABx(OpCode.Jmp,0,Instr.MaxArgSBx-8)],
        };
        File.WriteAllBytes(Path.Combine(_root, "game.zbc"), TrayTests.Bytecode(customProgram ?? program));
        var machine = new Machine(new FolderDisc(_root), new SaveStore(null), frameDriven: true);
        machine.Start();
        return machine;
    }

    [Fact]
    public void StateSurvivesFreshMachineWithGraphicsAudioInputAndSaveMemory()
    {
        byte[] bytes;
        using (var machine = Create())
        {
            machine.RunFrame(.25);
            var texture = new Texture(1, 1, "pixel"); texture.Pixels[0] = 0xFFFF0000;
            int id = machine.Osd.AddTexture(texture); int overlay = machine.Osd.CreateOverlay(texture);
            machine.Osd.Modify(overlay, o => { o.X = 30; o.Y = 40; o.Visible = true; });
            machine.Audio.Play(new Sound([.25f, .5f, .75f], "sound"), true);
            machine.Input.Push(RemoteKey.A, Remote.Blue, 123);
            machine.Saves.Add(9, "State test", "Slot", [1, 2, 3]);
            bytes = machine.SaveState();
            machine.Osd.GetTexture(id)!.Pixels[0] = 0xFF00FF00;
            machine.Saves.Clear();
        }
        using var restored = Create();
        restored.LoadState(bytes);
        Assert.Equal(.25, restored.Clock.NowSeconds);
        var frame = new uint[Osd.Width * Osd.Height]; restored.RenderFrame(frame);
        Assert.Equal(0xFFFF0000u, frame[40 * Osd.Width + 30]);
        Assert.True(restored.Input.TryTake(out var key, 250)); Assert.Equal(new KeyEvent(16, 3, 123), key);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(restored.Saves.Slots).Data);
        var sound = new float[6]; restored.Audio.Mix(sound);
        Assert.Equal(new float[] { .25f, .25f, .5f, .5f, .75f, .75f }, sound);
        int ticks = 0; restored.Log = line => { if (line == "tick") ticks++; };
        restored.RunFrame(.5); Assert.Equal(0, ticks);
        restored.RunFrame(.251); Assert.Equal(1, ticks); // Remaining sleep, rather than a new full second.
    }

    [Fact]
    public void InvalidStatesLeaveTheLiveMachineIntact()
    {
        using var machine = Create(); machine.RunFrame(.25);
        byte[] good = machine.SaveState(), bad = (byte[])good.Clone(); bad[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => machine.LoadState(bad));
        Assert.Throws<InvalidDataException>(() => machine.LoadState(good.AsSpan(0, good.Length - 1)));
        Assert.Equal(.25, machine.Clock.NowSeconds);
        machine.RunFrame(.25); Assert.Equal(.5, machine.Clock.NowSeconds);
        using var wrong = Create("Different disc");
        Assert.Throws<InvalidDataException>(() => wrong.LoadState(good));
    }

    static LuaState RoundTrip(LuaState source)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) LuaStateBinary.Write(writer, LuaStateSnapshot.Capture(source));
        stream.Position = 0; var target = new LuaState(); BaseLib.Open(target); StringLib.Open(target);
        using var reader = new BinaryReader(stream); LuaStateBinary.Read(reader, target); return target;
    }

    [Fact]
    public void LuaBinaryRetainsCyclesClosedUpvaluesIteratorsAndReboundNatives()
    {
        Directory.CreateDirectory(_root);
        var lua = new LuaState(); BaseLib.Open(lua); StringLib.Open(lua);
        var table = new LuaTable(); table["self"] = table; lua.Globals["a"] = table; lua.Globals["b"] = table;
        table["bytes"] = LuaValue.Userdata(new LuaUserdata(new byte[] { 0, 255 }));
        BaseLib.SetN(table, 5);
        var proto = new LuaProto { NumUpvalues = 1 };
        var function = new LuaClosure(proto, lua.Globals);
        var upvalue = new UpVal(LuaValue.Function(function)); function.Upvalues[0] = upvalue; lua.Globals["recursive"] = function;
        lua.Globals["savedType"] = lua.Globals["type"]; lua.Globals["type"] = 17;
        var iterator = lua.MainThread.Call1(lua.Globals["string"].AsTable!["gfind"], "ab", "(.)");
        lua.Globals["iterator"] = iterator; Assert.Equal("a", lua.MainThread.Call1(iterator).AsString);
        var restored = RoundTrip(lua); var copy = restored.Globals["a"].AsTable!;
        Assert.Same(copy, restored.Globals["b"].AsTable); Assert.Same(copy, copy["self"].AsTable);
        Assert.Equal(5, BaseLib.GetN(copy));
        Assert.Equal(new byte[] { 0, 255 }, (byte[])copy["bytes"].AsUserdata!.Payload!);
        var closure = (LuaClosure)restored.Globals["recursive"].AsFunction!;
        Assert.Same(closure, closure.Upvalues[0].Value.AsFunction);
        Assert.Equal("number", restored.MainThread.Call1(restored.Globals["savedType"], 3).AsString);
        Assert.Equal("b", restored.MainThread.Call1(restored.Globals["iterator"]).AsString);
    }

    [Fact]
    public void LuaBinaryResumesWrappedCoroutineAtItsYield()
    {
        Directory.CreateDirectory(_root);
        var lua = new LuaState(); BaseLib.Open(lua); StringLib.Open(lua);
        var body = new LuaProto
        {
            MaxStackSize = 2,
            Constants = ["coroutine", "yield", 42],
            Code = [ABx(OpCode.GetGlobal,0,0),ABC(OpCode.GetTable,0,0,Instr.MaxStack+1),ABx(OpCode.LoadK,1,2),
                ABC(OpCode.Call,0,2,2),ABC(OpCode.Return,0,2,0)]
        };
        var wrapped = lua.MainThread.Call1(lua.Globals["coroutine"].AsTable!["wrap"], lua.Load(body));
        lua.Globals["wrapped"] = wrapped; Assert.Equal(42, lua.MainThread.Call1(wrapped).N);
        var restored = RoundTrip(lua);
        Assert.Equal(19, restored.MainThread.Call1(restored.Globals["wrapped"], 19).N);
    }

    [Fact]
    public void StateResumesAnActiveCoroutineWithoutRestartingItsBody()
    {
        var body = new LuaProto
        {
            MaxStackSize = 2,
            Constants = ["time", "Sleep", 1000, "print", "started", "done"],
            Code = [ABx(OpCode.GetGlobal,0,3),ABx(OpCode.LoadK,1,4),ABC(OpCode.Call,0,2,1),
                ABx(OpCode.GetGlobal,0,0),ABC(OpCode.GetTable,0,0,Instr.MaxStack+1),ABx(OpCode.LoadK,1,2),ABC(OpCode.Call,0,2,1),
                ABx(OpCode.GetGlobal,0,3),ABx(OpCode.LoadK,1,5),ABC(OpCode.Call,0,2,1),ABx(OpCode.Jmp,0,Instr.MaxArgSBx-8)]
        };
        var main = new LuaProto
        {
            MaxStackSize = 2,
            Constants = ["coroutine", "wrap"],
            Protos = [body],
            Code = [ABx(OpCode.GetGlobal,0,0),ABC(OpCode.GetTable,0,0,Instr.MaxStack+1),ABx(OpCode.Closure,1,0),
                ABC(OpCode.Call,0,2,2),ABC(OpCode.Call,0,1,1),ABC(OpCode.Return,0,1,0)]
        };
        using var machine = Create(customProgram: main); machine.RunFrame(.25); var saved = machine.SaveState();
        var lines = new List<string>(); machine.Log = lines.Add;
        machine.RunFrame(1); Assert.Contains("done", lines); lines.Clear();
        machine.LoadState(saved); machine.RunFrame(.751);
        Assert.DoesNotContain("started", lines); Assert.Single(lines, "done");
        Assert.Equal(MachineState.Running, machine.State);
    }

    [Fact]
    public void SavingInsideProtectedCallbackPreservesFrameTimingAndCanLoadAfterStop()
    {
        var body = new LuaProto
        {
            MaxStackSize = 2, Constants = ["time", "Sleep", 1000],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1),
                ABx(OpCode.LoadK, 1, 2), ABC(OpCode.Call, 0, 2, 1), ABC(OpCode.Return, 0, 1, 0)],
        };
        var main = new LuaProto
        {
            MaxStackSize = 2, Constants = ["pcall"], Protos = [body],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.Closure, 1, 0), ABC(OpCode.Call, 0, 2, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 4)],
        };
        using var machine = Create(customProgram: main);
        byte[] initial = machine.SaveState();
        machine.RunFrame(.25);
        Assert.Throws<InvalidOperationException>(() => machine.SaveState());
        machine.RunFrame(.25); Assert.Equal(.5, machine.Clock.NowSeconds);
        machine.Stop(); machine.LoadState(initial);
        Assert.Equal(MachineState.Running, machine.State);
        Assert.Equal(0, machine.Clock.NowSeconds);
        machine.RunFrame(.25); Assert.Equal(.25, machine.Clock.NowSeconds);
    }

    [Fact]
    public void LuaBinaryKeepsSharedOpenUpvaluesBoundToTheRestoredStack()
    {
        Directory.CreateDirectory(_root);
        var lua = new LuaState(); BaseLib.Open(lua); StringLib.Open(lua);
        lua.MainThread.Push(42);
        var upvalue = lua.MainThread.FindUpval(0);
        var proto = new LuaProto { NumUpvalues = 1 };
        var first = new LuaClosure(proto, lua.Globals); var second = new LuaClosure(proto, lua.Globals);
        first.Upvalues[0] = upvalue; second.Upvalues[0] = upvalue;
        lua.Globals["first"] = first; lua.Globals["second"] = second;
        var restored = RoundTrip(lua);
        var a = ((LuaClosure)restored.Globals["first"].AsFunction!).Upvalues[0];
        var b = ((LuaClosure)restored.Globals["second"].AsFunction!).Upvalues[0];
        Assert.Same(a, b); Assert.Same(restored.MainThread, a.Thread); Assert.Equal(42, a.Value.N);
        restored.MainThread.Stack[0] = 99; Assert.Equal(99, b.Value.N);
    }

    [Fact]
    public void KeyArrivingOnLoadDoesNotReuseThePollDeadlineForTheNextSleep()
    {
        var program = new LuaProto
        {
            MaxStackSize = 2, Constants = ["input", "WaitForKey", "time", "Sleep", 1000, "print", "tick"],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1), ABC(OpCode.Call, 0, 1, 1),
                ABx(OpCode.GetGlobal, 0, 2), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 3), ABx(OpCode.LoadK, 1, 4), ABC(OpCode.Call, 0, 2, 1),
                ABx(OpCode.GetGlobal, 0, 5), ABx(OpCode.LoadK, 1, 6), ABC(OpCode.Call, 0, 2, 1), ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 11)],
        };
        using var machine = Create(customProgram: program); machine.RunFrame(.25);
        byte[] saved = machine.SaveState(); machine.LoadState(saved);
        var lines = new List<string>(); machine.Log = lines.Add;
        machine.Input.Push(RemoteKey.Select, Remote.Red, machine.Clock.Now);
        machine.RunFrame(.25); Assert.Empty(lines);
        machine.RunFrame(.751); Assert.Single(lines, "tick");
    }

    [Fact]
    public void StateAtInstructionBudgetResumesTheBranchTarget()
    {
        var program = new LuaProto { MaxStackSize = 1, Code = [ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 1)] };
        using var machine = Create(customProgram: program);
        machine.RunFrame(1.0 / 60);
        byte[] saved = machine.SaveState();
        machine.RunFrame(1.0 / 60); machine.LoadState(saved); machine.RunFrame(1.0 / 60);
        Assert.Equal(MachineState.Running, machine.State);
        Assert.Null(machine.CrashMessage);
    }
}
