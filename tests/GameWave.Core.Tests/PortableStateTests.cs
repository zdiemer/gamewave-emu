using GameWave.Disc;
using GameWave.Engine;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GameWave.Tests;

public sealed class PortableStateTests : IDisposable
{
    [Fact]
    public void RepeatedVmOperationsKeepStateBytesIdenticalAcrossRestoration()
    {
        // Concat leaves its internal callback on an unused stack register. A later
        // operation must share that identity after loading, as it did originally.
        var program = new LuaProto { MaxStackSize = 4, Constants = ["one", "two", "time", "Sleep", 100],
            Code = [ABx(OpCode.LoadK, 0, 0), ABx(OpCode.LoadK, 1, 1), ABC(OpCode.Concat, 0, 0, 1),
                ABx(OpCode.GetGlobal, 2, 2), ABC(OpCode.GetTable, 2, 2, Instr.MaxStack + 3),
                ABx(OpCode.LoadK, 3, 4), ABC(OpCode.Call, 2, 2, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 8)] };
        using var machine = Create(customProgram: program); machine.RunFrame(.05);
        byte[] saved = machine.SaveState(); machine.RunFrame(.101); byte[] expected = machine.SaveState();
        machine.LoadState(saved, persistSaves: false); machine.RunFrame(.101);
        Assert.Equal(expected, machine.SaveState());
        using var fresh = Create(customProgram: program); fresh.LoadState(saved, persistSaves: false); fresh.RunFrame(.101);
        Assert.Equal(expected, fresh.SaveState());
    }
    [Fact]
    public void LuaBinaryRetainsClearedKeysForLiveNextIteratorsAndObjectStrings()
    {
        Directory.CreateDirectory(_root);
        var lua = new LuaState(); BaseLib.Open(lua); StringLib.Open(lua);
        var table = new LuaTable(); table["a"] = 1; table["b"] = 2; table["c"] = 3;
        lua.Globals["table"] = table;
        string identity = lua.ToStringMeta(table);
        Assert.True(table.Next(LuaValue.Nil, out var first, out _)); Assert.Equal("a", first.AsString);
        table["a"] = LuaValue.Nil;
        var restored = RoundTrip(lua); var copy = restored.Globals["table"].AsTable!;
        Assert.Equal(identity, restored.ToStringMeta(copy));
        Assert.True(copy.Next(first, out var second, out var value)); Assert.Equal("b", second.AsString); Assert.Equal(2, value.N);
        copy["b"] = LuaValue.Nil; Assert.True(copy.Next(second, out var third, out _)); Assert.Equal("c", third.AsString);
    }
    [Fact]
    public void IntegerCheatsApplyAtBoundaryAndSurviveStateRestoration()
    {
        var program = new LuaProto { MaxStackSize = 2, Constants = ["score", 1, "time", "Sleep", 100, "print"],
            Code = [ABx(OpCode.LoadK, 0, 1), ABx(OpCode.SetGlobal, 0, 0),
                ABx(OpCode.GetGlobal, 0, 2), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 3),
                ABx(OpCode.LoadK, 1, 4), ABC(OpCode.Call, 0, 2, 1),
                ABx(OpCode.GetGlobal, 0, 5), ABx(OpCode.GetGlobal, 1, 0), ABC(OpCode.Call, 0, 2, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 8)] };
        using var machine = Create(customProgram: program); machine.RunFrame(.05);
        Assert.False(machine.SetLuaInteger("missing.field", 9)); Assert.False(machine.SetLuaInteger("print", 9));
        Assert.True(machine.SetLuaInteger("score", 99)); byte[] saved = machine.SaveState();
        var lines = new List<string>(); machine.Log = lines.Add; machine.RunFrame(.051); Assert.Single(lines, "99");
        Assert.True(machine.SetLuaInteger("score", 7)); machine.LoadState(saved); lines.Clear();
        machine.RunFrame(.051); Assert.Single(lines, "99");
    }
    [Theory]
    [InlineData("sort")]
    [InlineData("foreach")]
    [InlineData("foreachi")]
    [InlineData("gsub")]
    [InlineData("index")]
    [InlineData("newindex")]
    [InlineData("add")]
    [InlineData("concat")]
    [InlineData("tostring")]
    [InlineData("lt")]
    [InlineData("le")]
    [InlineData("iterator")]
    public void BlockingNativeCallbacksRestoreWithoutRepeatingEffects(string operation)
    {
        var constants = new List<LuaValue>();
        int K(LuaValue value) { constants.Add(value); return constants.Count - 1; }
        var code = new List<uint>();
        void Global(int register, string name) => code.Add(ABx(OpCode.GetGlobal, register, K(name)));
        void Load(int register, LuaValue value) => code.Add(ABx(OpCode.LoadK, register, K(value)));
        void Field(int register, int table, string name) => code.Add(ABC(OpCode.GetTable, register, table, Instr.MaxStack + K(name)));
        var callback = new LuaProto
        {
            NumParams = 2, MaxStackSize = 6,
            Constants = ["print", "entered", "time", "Sleep", 100, "replacement", 42],
            Code = [ABx(OpCode.GetGlobal, 2, 0), ABx(OpCode.LoadK, 3, 1), ABC(OpCode.Call, 2, 2, 1),
                ABx(OpCode.GetGlobal, 2, 2), ABC(OpCode.GetTable, 2, 2, Instr.MaxStack + 3),
                ABx(OpCode.LoadK, 3, 4), ABC(OpCode.Call, 2, 2, 1)],
        };
        uint[] returns = operation switch
        {
            "foreach" or "foreachi" or "newindex" => [ABC(OpCode.Return, 0, 1, 0)],
            "sort" or "lt" or "le" => [ABC(OpCode.LoadBool, 2, 1, 0), ABC(OpCode.Lt, 1, 0, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx + 1), ABC(OpCode.LoadBool, 2, 0, 0), ABC(OpCode.Return, 2, 2, 0)],
            "iterator" => [ABC(OpCode.Return, 2, 1, 0)],
            "gsub" or "concat" or "tostring" => [ABx(OpCode.LoadK, 2, 5), ABC(OpCode.Return, 2, 2, 0)],
            _ => [ABx(OpCode.LoadK, 2, 6), ABC(OpCode.Return, 2, 2, 0)],
        };
        if (operation is "lt" or "le") returns = [ABC(OpCode.LoadBool, 2, 1, 0), ABC(OpCode.Return, 2, 2, 0)];
        callback.Code = [..callback.Code, ..returns];
        if (operation is "sort" or "foreach" or "foreachi")
        {
            Global(0, "table"); Field(0, 0, operation);
            code.Add(ABC(OpCode.NewTable, 1, 0, 0));
            for (int i = 0; i < 6; i++) Load(i + 2, 6 - i);
            code.Add(ABx(OpCode.SetList, 1, 5));
            code.Add(ABx(OpCode.Closure, 2, 0)); code.Add(ABC(OpCode.Call, 0, 3, 2));
        }
        else if (operation == "gsub")
        {
            Global(0, "string"); Field(0, 0, "gsub"); Load(1, "abc"); Load(2, "(.)");
            code.Add(ABx(OpCode.Closure, 3, 0)); code.Add(ABC(OpCode.Call, 0, 4, 2));
        }
        else if (operation == "iterator")
        {
            code.Add(ABx(OpCode.Closure, 0, 0)); code.Add(ABC(OpCode.LoadNil, 1, 2, 0));
            code.Add(ABC(OpCode.TForLoop, 0, 0, 0)); code.Add(ABx(OpCode.Jmp, 0, Instr.MaxArgSBx));
        }
        else
        {
            code.Add(ABC(OpCode.NewTable, 0, 0, 0)); code.Add(ABC(OpCode.NewTable, 1, 0, 0));
            code.Add(ABx(OpCode.Closure, 2, 0));
            string metamethod = operation is "tostring" ? "__tostring" : "__" + operation;
            code.Add(ABC(OpCode.SetTable, 1, Instr.MaxStack + K(metamethod), 2));
            Global(3, "setmetatable"); code.Add(ABC(OpCode.Move, 4, 0, 0)); code.Add(ABC(OpCode.Move, 5, 1, 0));
            code.Add(ABC(OpCode.Call, 3, 3, 1));
            switch (operation)
            {
                case "index": Field(0, 0, "missing"); break;
                case "newindex": code.Add(ABC(OpCode.SetTable, 0, Instr.MaxStack + K("missing"), Instr.MaxStack + K(17))); break;
                case "add": code.Add(ABC(OpCode.Add, 0, 0, 0)); break;
                case "concat": code.Add(ABC(OpCode.Move, 1, 0, 0)); code.Add(ABC(OpCode.Concat, 0, 0, 1)); break;
                case "tostring": Global(1, "tostring"); code.Add(ABC(OpCode.Move, 2, 0, 0)); code.Add(ABC(OpCode.Call, 1, 2, 2)); code.Add(ABC(OpCode.Move, 0, 1, 0)); break;
                default: code.Add(ABC(operation == "lt" ? OpCode.Lt : OpCode.Le, 1, 0, 0)); code.Add(ABx(OpCode.Jmp, 0, Instr.MaxArgSBx)); break;
            }
        }
        Global(2, "print"); code.Add(ABC(OpCode.Move, 3, 0, 0)); code.Add(ABC(OpCode.Call, 2, 2, 1));
        int loop = code.Count;
        if (operation == "concat")
            code.Add(ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - code.Count - 1));
        else
        {
            Global(0, "time"); Field(0, 0, "Sleep"); Load(1, 10000); code.Add(ABC(OpCode.Call, 0, 2, 1));
            code.Add(ABx(OpCode.Jmp, 0, Instr.MaxArgSBx + loop - code.Count - 1));
        }
        var main = new LuaProto { MaxStackSize = 10, Constants = constants.ToArray(), Protos = [callback], Code = code.ToArray() };
        using var machine = Create(customProgram: main);
        var lines = new List<string>(); machine.Log = lines.Add;
        machine.RunFrame(.05);
        byte[] saved = machine.SaveState(); lines.Clear();
        machine.RunFrame(4);
        Assert.Equal(MachineState.Running, machine.State);
        byte[] expected = machine.SaveState(); var expectedLines = lines.ToArray();
        lines.Clear(); machine.LoadState(saved, persistSaves: false); machine.RunFrame(4);
        Assert.Equal(expected, machine.SaveState()); Assert.Equal(expectedLines, lines);
        using var fresh = Create(customProgram: main); var freshLines = new List<string>(); fresh.Log = freshLines.Add;
        fresh.LoadState(saved, persistSaves: false); fresh.RunFrame(4);
        Assert.Equal(expected, fresh.SaveState()); Assert.Equal(expectedLines, freshLines);
    }

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
            MaxStackSize = 2,
            Constants = ["time", "Sleep", 1000],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1),
                ABx(OpCode.LoadK, 1, 2), ABC(OpCode.Call, 0, 2, 1), ABC(OpCode.Return, 0, 1, 0)],
        };
        var main = new LuaProto
        {
            MaxStackSize = 2,
            Constants = ["pcall"],
            Protos = [body],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.Closure, 1, 0), ABC(OpCode.Call, 0, 2, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 4)],
        };
        using var machine = Create(customProgram: main);
        byte[] initial = machine.SaveState();
        machine.RunFrame(.25);
        byte[] sleeping = machine.SaveState();
        machine.RunFrame(.25); Assert.Equal(.5, machine.Clock.NowSeconds);
        byte[] expected = machine.SaveState();
        machine.LoadState(sleeping, persistSaves: false);
        machine.RunFrame(.25); Assert.Equal(expected, machine.SaveState());
        machine.RunFrame(.8); expected = machine.SaveState();
        machine.LoadState(sleeping, persistSaves: false);
        machine.RunFrame(1.05); Assert.Equal(expected, machine.SaveState());
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
            MaxStackSize = 2,
            Constants = ["input", "WaitForKey", "time", "Sleep", 1000, "print", "tick"],
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

    [Theory]
    [InlineData("sleep")]
    [InlineData("poll")]
    [InlineData("instructions")]
    [InlineData("key")]
    public void ReplayingFramesRestoresExactExecutionAndInput(string boundary)
    {
        LuaProto program = boundary switch
        {
            "instructions" => new() { MaxStackSize = 1, Code = [ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 1)] },
            "poll" or "key" => new()
            {
                MaxStackSize = 3,
                Constants = ["input", boundary == "key" ? "WaitForKey" : "GetKey"],
                Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1),
                    ABC(OpCode.Call, 0, 1, 4), ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 4)],
            },
            _ => new()
            {
                MaxStackSize = 2,
                Constants = ["time", "Sleep", 23],
                Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1),
                    ABx(OpCode.LoadK, 1, 2), ABC(OpCode.Call, 0, 2, 1), ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 5)],
            },
        };
        using var machine = Create(customProgram: program);
        for (int i = 0; i < 11; i++) machine.RunFrame(1.0 / 60);
        byte[] saved = machine.SaveState();
        byte[] Advance()
        {
            for (int i = 0; i < 17; i++)
            {
                if (i % 3 == 0) machine.Input.Push(RemoteKey.A, Remote.Blue, machine.Clock.Now);
                machine.RunFrame(1.0 / 60);
            }
            Assert.Equal(MachineState.Running, machine.State);
            return machine.SaveState();
        }
        byte[] expected = Advance();
        machine.LoadState(saved, persistSaves: false);
        Assert.Equal(expected, Advance());
        machine.LoadState(saved, persistSaves: false);
        Assert.Equal(expected, Advance());
    }

    [Fact]
    public void RandomInputRepeatsAfterRestoration()
    {
        Directory.CreateDirectory(_root);
        var input = new InputQueue { Mode = 1 };
        input.SetRandomKeys([10, 11, 12, 13, 14]);
        var initial = input.CaptureState();
        KeyEvent[] Read() => Enumerable.Range(0, 100).Select(i =>
        {
            Assert.True(input.TryTake(out var key, i));
            return key;
        }).ToArray();
        var expected = Read(); input.RestoreState(initial);
        Assert.Equal(expected, Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovieSnapshotsReplayPicturesPcmResamplingPauseAndLoopExactly(bool loop)
    {
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "movie.mpg"), Path.Combine(_root, "movie.mpg"));
        using var machine = Create();
        machine.Video.Movie.Load(machine.Disc.Find("/movie.mpg"));
        machine.Video.Movie.Loop = loop; machine.Video.StartMovie();
        var frame = new uint[Osd.Width * Osd.Height]; var pcm = new float[1470];
        bool sawPicture = false, heardAudio = false;
        byte[] Advance(int count)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < count; i++)
            {
                if (i == 7) machine.Video.Movie.Pause();
                if (i == 13) machine.Video.Movie.Resume();
                machine.RunFrame(1.0 / 60); machine.Video.Movie.Pump();
                machine.Audio.Mix(pcm); machine.RenderFrame(frame);
                sawPicture |= frame.Any(pixel => pixel != 0xFF000000);
                heardAudio |= pcm.Any(sample => sample != 0);
                hash.AppendData(MemoryMarshal.AsBytes(frame.AsSpan()));
                hash.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan()));
            }
            return hash.GetHashAndReset();
        }
        foreach (int count in new[] { 7, 23, 61, 60 })
        {
            byte[] saved = machine.SaveState(); byte[] expected = Advance(count);
            byte[] finalState = machine.SaveState();
            machine.LoadState(saved, persistSaves: false);
            Assert.Equal(saved, machine.SaveState());
            Assert.Equal(expected, Advance(count));
            Assert.Equal(finalState, machine.SaveState());
        }
        Assert.Equal(loop ? 1 : 0, machine.Video.Movie.State);
        Assert.True(sawPicture); Assert.True(heardAudio);
    }

    [Fact]
    public void StateResumesSleepingProtectedErrorHandlerAndReturnsItsResult()
    {
        var failed = new LuaProto
        {
            MaxStackSize = 3,
            Constants = ["error", "boom", 0],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.LoadK, 1, 1), ABx(OpCode.LoadK, 2, 2), ABC(OpCode.Call, 0, 3, 1)],
        };
        var handler = new LuaProto
        {
            NumParams = 1,
            MaxStackSize = 2,
            Constants = ["time", "Sleep", 1000, "handled"],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1), ABx(OpCode.LoadK, 1, 2),
                ABC(OpCode.Call, 0, 2, 1), ABx(OpCode.LoadK, 0, 3), ABC(OpCode.Return, 0, 2, 0)],
        };
        var main = new LuaProto
        {
            MaxStackSize = 3,
            Constants = ["xpcall", "print"],
            Protos = [failed, handler],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.Closure, 1, 0), ABx(OpCode.Closure, 2, 1), ABC(OpCode.Call, 0, 3, 3),
                ABC(OpCode.Move, 2, 1, 0), ABC(OpCode.Move, 1, 0, 0), ABx(OpCode.GetGlobal, 0, 1), ABC(OpCode.Call, 0, 3, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 9)],
        };
        using var machine = Create(customProgram: main); machine.RunFrame(.25);
        byte[] saved = machine.SaveState(); var lines = new List<string>(); machine.Log = lines.Add;
        machine.RunFrame(.8); Assert.Single(lines, "false\thandled"); byte[] expected = machine.SaveState();
        lines.Clear(); machine.LoadState(saved, persistSaves: false); machine.RunFrame(.8);
        Assert.Single(lines, "false\thandled"); Assert.Equal(expected, machine.SaveState());
    }

    [Fact]
    public void FrameBudgetWaitsForOrdinaryNativeCallbacksToReturn()
    {
        var callback = new LuaProto
        {
            NumParams = 2,
            MaxStackSize = 3,
            Constants = [0, 20000, 1],
            Code = [ABx(OpCode.LoadK, 0, 0), ABx(OpCode.LoadK, 1, 1), ABx(OpCode.LoadK, 2, 2),
                ABx(OpCode.ForLoop, 0, Instr.MaxArgSBx - 1), ABC(OpCode.Return, 0, 1, 0)],
        };
        var main = new LuaProto
        {
            MaxStackSize = 3,
            Constants = ["table", "foreach", 1],
            Protos = [callback],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.GetTable, 0, 0, Instr.MaxStack + 1), ABC(OpCode.NewTable, 1, 0, 0),
                ABx(OpCode.LoadK, 2, 2), ABx(OpCode.SetList, 1, 0), ABx(OpCode.Closure, 2, 0), ABC(OpCode.Call, 0, 3, 1),
                ABx(OpCode.Jmp, 0, Instr.MaxArgSBx - 8)],
        };
        using var machine = Create(customProgram: main); machine.RunFrame(1.0 / 60);
        byte[] saved = machine.SaveState(); machine.RunFrame(1.0 / 60); byte[] expected = machine.SaveState();
        machine.LoadState(saved, persistSaves: false); machine.RunFrame(1.0 / 60);
        Assert.Equal(expected, machine.SaveState());
    }
}
