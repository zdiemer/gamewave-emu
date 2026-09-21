using GameWave.Engine;
using GameWave.Graphics;
using GameWave.Lua;

namespace GameWave.Tests;

public sealed class QuickStateTests
{
    static uint ABC(OpCode op, int a, int b, int c) => (uint)op | (uint)c << 6 | (uint)b << 15 | (uint)a << 24;
    static uint ABx(OpCode op, int a, int bx) => (uint)op | (uint)bx << 6 | (uint)a << 24;

    [Fact]
    public void LuaSnapshotRestoresCyclesAliasingAndUserdata()
    {
        var lua = new LuaState();
        var shared = new LuaTable();
        var bytes = new byte[] { 1, 2, 3 };
        shared["self"] = shared;
        shared["bytes"] = LuaValue.Userdata(new LuaUserdata(bytes));
        lua.Globals["a"] = shared;
        lua.Globals["b"] = shared;

        var snapshot = LuaStateSnapshot.Capture(lua);
        shared["changed"] = true;
        bytes[0] = 9;

        snapshot.Restore(lua);
        var restored = lua.Globals["a"].AsTable!;
        Assert.Same(restored, lua.Globals["b"].AsTable);
        Assert.Same(restored, restored["self"].AsTable);
        Assert.True(restored["changed"].IsNil);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])restored["bytes"].AsUserdata!.Payload!);

        restored["changed"] = true;
        snapshot.Restore(lua);
        Assert.True(lua.Globals["a"].AsTable!["changed"].IsNil);
    }

    [Fact]
    public void LuaSnapshotPreservesSharedUpvalues()
    {
        var lua = new LuaState();
        var proto = new LuaProto { NumUpvalues = 1 };
        var upvalue = new UpVal(LuaValue.Number(12));
        var first = new LuaClosure(proto, lua.Globals);
        var second = new LuaClosure(proto, lua.Globals);
        first.Upvalues[0] = upvalue;
        second.Upvalues[0] = upvalue;
        lua.Globals["first"] = first;
        lua.Globals["second"] = second;

        var snapshot = LuaStateSnapshot.Capture(lua);
        upvalue.Value = 99;
        snapshot.Restore(lua);

        var restoredFirst = (LuaClosure)lua.Globals["first"].AsFunction!;
        var restoredSecond = (LuaClosure)lua.Globals["second"].AsFunction!;
        Assert.Same(restoredFirst.Upvalues[0], restoredSecond.Upvalues[0]);
        Assert.Equal(12, restoredFirst.Upvalues[0].Value.N);
    }

    [Fact]
    public void LuaSnapshotCanReplaceAProgramWhileItIsExecuting()
    {
        var lua = new LuaState();
        var proto = new LuaProto
        {
            MaxStackSize = 1,
            Constants = [1, 2, 3, "x"],
            Code =
            [
                ABx(OpCode.LoadK, 0, 0), ABx(OpCode.SetGlobal, 0, 3),
                ABx(OpCode.LoadK, 0, 1), ABx(OpCode.SetGlobal, 0, 3),
                ABx(OpCode.LoadK, 0, 2), ABx(OpCode.SetGlobal, 0, 3),
                ABC(OpCode.Return, 0, 2, 0),
            ],
        };
        LuaStateSnapshot? snapshot = null;
        bool restored = false;
        lua.InstructionBoundary = () =>
        {
            int x = lua.Globals["x"].IsNumber ? lua.Globals["x"].N : 0;
            if (x == 1 && snapshot is null)
                snapshot = LuaStateSnapshot.Capture(lua);
            if (x == 3 && !restored)
            {
                restored = true;
                snapshot!.Restore(lua);
                return true;
            }
            return false;
        };

        var result = lua.MainThread.Call(lua.Load(proto));

        Assert.True(restored);
        Assert.Equal(3, result[0].N);
        Assert.Equal(3, lua.Globals["x"].N);
    }

    [Fact]
    public void LuaSnapshotRewindsAnActiveNativeCall()
    {
        var lua = new LuaState();
        LuaStateSnapshot? snapshot = null;
        lua.Register("checkpoint", args =>
        {
            snapshot ??= LuaStateSnapshot.Capture(lua);
            return 0;
        });
        var proto = new LuaProto
        {
            MaxStackSize = 1,
            Constants = ["checkpoint", 42, "x"],
            Code =
            [
                ABx(OpCode.GetGlobal, 0, 0),
                ABC(OpCode.Call, 0, 1, 1),
                ABx(OpCode.LoadK, 0, 1),
                ABx(OpCode.SetGlobal, 0, 2),
                ABC(OpCode.Return, 0, 1, 0),
            ],
        };

        lua.MainThread.Call(lua.Load(proto));
        Assert.Equal(42, lua.Globals["x"].N);
        lua.Globals["x"] = 99;

        snapshot!.Restore(lua);
        lua.MainThread.Execute();

        Assert.Equal(42, lua.Globals["x"].N);
        Assert.Equal(0, lua.MainThread.FrameCount);
    }

    [Fact]
    public void LuaSnapshotCopiesWrappedCoroutineState()
    {
        var lua = new LuaState();
        BaseLib.Open(lua);
        var body = new LuaProto
        {
            NumParams = 1,
            MaxStackSize = 4,
            Constants = ["coroutine", "yield", 1, 2],
            Code =
            [
                ABx(OpCode.GetGlobal, 1, 0),
                ABC(OpCode.GetTable, 1, 1, Instr.MaxStack + 1),
                ABC(OpCode.Add, 2, 0, Instr.MaxStack + 2),
                ABC(OpCode.Call, 1, 2, 2),
                ABC(OpCode.Mul, 1, 1, Instr.MaxStack + 3),
                ABC(OpCode.Return, 1, 2, 0),
            ],
        };
        var wrap = lua.Globals["coroutine"].AsTable!["wrap"];
        var wrapped = lua.MainThread.Call1(wrap, lua.Load(body));
        lua.Globals["wrapped"] = wrapped;
        Assert.Equal(11, lua.MainThread.Call1(wrapped, 10).N);
        var snapshot = LuaStateSnapshot.Capture(lua);

        Assert.Equal(10, lua.MainThread.Call1(wrapped, 5).N);
        snapshot.Restore(lua);

        Assert.Equal(14, lua.MainThread.Call1(lua.Globals["wrapped"], 7).N);
    }

    [Fact]
    public void LuaSnapshotCopiesPatternIteratorPosition()
    {
        var lua = new LuaState();
        BaseLib.Open(lua);
        StringLib.Open(lua);
        var gfind = lua.Globals["string"].AsTable!["gfind"];
        var iterator = lua.MainThread.Call1(gfind, "ab", "(.)");
        lua.Globals["iterator"] = iterator;
        Assert.Equal("a", lua.MainThread.Call1(iterator).AsString);
        var snapshot = LuaStateSnapshot.Capture(lua);

        Assert.Equal("b", lua.MainThread.Call1(iterator).AsString);
        snapshot.Restore(lua);

        Assert.Equal("b", lua.MainThread.Call1(lua.Globals["iterator"]).AsString);
    }

    [Fact]
    public void OsdSnapshotRestoresTexturesOverlaysAndAnimations()
    {
        var osd = new Osd();
        var texture = new Texture(2, 1, "test");
        texture.Pixels[0] = 0xFFFF0000;
        int textureId = osd.AddTexture(texture);
        int overlayId = osd.CreateOverlay(texture);
        osd.Modify(overlayId, overlay =>
        {
            overlay.X = 20;
            overlay.Visible = true;
            overlay.Animations.Add(new PositionAnimation { X0 = 20, X1 = 100, Duration = 1000 });
        });
        var snapshot = osd.CaptureState();

        texture.Pixels[0] = 0xFF00FF00;
        osd.Modify(overlayId, overlay =>
        {
            overlay.X = 300;
            overlay.Animations.Clear();
        });
        osd.RestoreState(snapshot);

        Assert.Equal(0xFFFF0000u, osd.GetTexture(textureId)!.Pixels[0]);
        Assert.Equal(20, osd.GetOverlay(overlayId)!.X);
        Assert.Single(osd.GetOverlay(overlayId)!.Animations);
    }

    [Fact]
    public void InputSnapshotRestoresQueuedEventsAndSettings()
    {
        var input = new InputQueue { Capacity = 4, Mode = 0, RemotesEnabled = false };
        input.SetRandomKeys([2, 4]);
        input.Push(RemoteKey.A, Remote.Blue, 123);
        var snapshot = input.CaptureState();

        Assert.True(input.TryTake(out _, 200));
        input.Capacity = 1;
        input.RemotesEnabled = true;
        input.RestoreState(snapshot);

        Assert.Equal(4, input.Capacity);
        Assert.False(input.RemotesEnabled);
        Assert.True(input.TryTake(out var restored, 200));
        Assert.Equal(new KeyEvent((int)RemoteKey.A, (int)Remote.Blue, 123), restored);
    }
}
