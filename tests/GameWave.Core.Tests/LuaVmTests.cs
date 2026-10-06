using GameWave.Lua;

namespace GameWave.Tests;

/// <summary>Runs hand-assembled Lua 5.0 functions through the interpreter.</summary>
public class LuaVmTests
{
    static uint ABC(OpCode op, int a, int b, int c) => (uint)op | (uint)c << 6 | (uint)b << 15 | (uint)a << 24;
    static uint ABx(OpCode op, int a, int bx) => (uint)op | (uint)bx << 6 | (uint)a << 24;
    static uint AsBx(OpCode op, int a, int sbx) => ABx(op, a, sbx + Instr.MaxArgSBx);
    static int K(int index) => Instr.MaxStack + index;

    static LuaState NewState()
    {
        var s = new LuaState { Output = _ => { } };
        BaseLib.Open(s);
        StringLib.Open(s);
        return s;
    }

    static LuaValue[] Run(LuaState s, LuaProto p, params LuaValue[] args)
        => s.MainThread.Call(LuaValue.Function(s.Load(p)), args);

    [Fact]
    public void AddsConstants()
    {
        var p = new LuaProto
        {
            MaxStackSize = 2,
            Constants = [LuaValue.Number(40), LuaValue.Number(2)],
            Code = [ABC(OpCode.Add, 0, K(0), K(1)), ABC(OpCode.Return, 0, 2, 0)],
        };
        Assert.Equal(42, Run(NewState(), p)[0].N);
    }

    [Fact]
    public void IntegerDivisionTruncatesTowardZero()
    {
        var p = new LuaProto
        {
            MaxStackSize = 2,
            Constants = [LuaValue.Number(-7), LuaValue.Number(2)],
            Code = [ABC(OpCode.Div, 0, K(0), K(1)), ABC(OpCode.Return, 0, 2, 0)],
        };
        Assert.Equal(-3, Run(NewState(), p)[0].N);
    }

    [Fact]
    public void NumericForLoopSums()
    {
        // local s = 0; for i = 1, 10 do s = s + i end; return s
        // 5.0 prepares the loop by subtracting the step, then jumps to FORLOOP.
        var p = new LuaProto
        {
            MaxStackSize = 5,
            Constants = [LuaValue.Number(0), LuaValue.Number(1), LuaValue.Number(10)],
            Code =
            [
                ABx(OpCode.LoadK, 0, 0),              // s = 0
                ABx(OpCode.LoadK, 1, 1),              // i = 1
                ABx(OpCode.LoadK, 2, 2),              // limit
                ABx(OpCode.LoadK, 3, 1),              // step
                ABC(OpCode.Sub, 1, 1, 3),             // i = i - step
                AsBx(OpCode.Jmp, 0, 1),               // -> FORLOOP
                ABC(OpCode.Add, 0, 0, 1),             // s = s + i
                AsBx(OpCode.ForLoop, 1, -2),          // i += step; if i <= limit -> body
                ABC(OpCode.Return, 0, 2, 0),
            ],
        };
        Assert.Equal(55, Run(NewState(), p)[0].N);
    }

    [Fact]
    public void ClosuresShareAnUpvalue()
    {
        // local n = 0; local f = function() n = n + 1; return n end; f(); f(); return f()
        var inner = new LuaProto
        {
            NumUpvalues = 1,
            MaxStackSize = 2,
            Constants = [LuaValue.Number(1)],
            Code =
            [
                ABC(OpCode.GetUpval, 0, 0, 0),
                ABC(OpCode.Add, 0, 0, K(0)),
                ABC(OpCode.SetUpval, 0, 0, 0),
                ABC(OpCode.Return, 0, 2, 0),
            ],
        };
        var outer = new LuaProto
        {
            MaxStackSize = 4,
            Constants = [LuaValue.Number(0)],
            Protos = [inner],
            Code =
            [
                ABx(OpCode.LoadK, 0, 0),
                ABx(OpCode.Closure, 1, 0),
                ABC(OpCode.Move, 0, 0, 0),           // upvalue: local 0
                ABC(OpCode.Move, 2, 1, 0),
                ABC(OpCode.Call, 2, 1, 1),
                ABC(OpCode.Move, 2, 1, 0),
                ABC(OpCode.Call, 2, 1, 1),
                ABC(OpCode.Move, 2, 1, 0),
                ABC(OpCode.TailCall, 2, 1, 0),
                ABC(OpCode.Return, 2, 0, 0),
            ],
        };
        Assert.Equal(3, Run(NewState(), outer)[0].N);
    }

    [Fact]
    public void VarargFunctionsGetAnArgTable()
    {
        // function(...) return arg.n end
        var p = new LuaProto
        {
            IsVararg = true,
            MaxStackSize = 3,
            Constants = [LuaValue.String("n")],
            Code = [ABC(OpCode.GetTable, 1, 0, K(0)), ABC(OpCode.Return, 1, 2, 0)],
        };
        Assert.Equal(3, Run(NewState(), p, 7, 8, 9)[0].N);
    }

    [Fact]
    public void CoroutinesYieldAndResume()
    {
        // co = coroutine.create(function(a) local b = coroutine.yield(a + 1); return b * 2 end)
        var body = new LuaProto
        {
            NumParams = 1,
            MaxStackSize = 4,
            Constants = [LuaValue.String("coroutine"), LuaValue.String("yield"), LuaValue.Number(1), LuaValue.Number(2)],
            Code =
            [
                ABx(OpCode.GetGlobal, 1, 0),
                ABC(OpCode.GetTable, 1, 1, K(1)),
                ABC(OpCode.Add, 2, 0, K(2)),
                ABC(OpCode.Call, 1, 2, 2),
                ABC(OpCode.Mul, 1, 1, K(3)),
                ABC(OpCode.Return, 1, 2, 0),
            ],
        };
        var s = NewState();
        var co = s.NewThread(s.Load(body));
        var first = co.Resume(s.MainThread, [LuaValue.Number(10)], out bool ok1);
        Assert.True(ok1);
        Assert.Equal(11, first[0].N);
        Assert.Equal(CoroutineStatus.Suspended, co.Status);
        var second = co.Resume(s.MainThread, [LuaValue.Number(5)], out bool ok2);
        Assert.True(ok2);
        Assert.Equal(10, second[0].N);
        Assert.Equal(CoroutineStatus.Dead, co.Status);
    }

    [Fact]
    public void PcallCatchesErrors()
    {
        var s = NewState();
        var pcall = s.Globals["pcall"];
        var error = s.Globals["error"];
        var r = s.MainThread.Call(pcall, error, "boom", 0);
        Assert.False(r[0].IsTruthy);
        Assert.Equal("boom", r[1].AsString);
    }

    [Fact]
    public void ProtectedLuaCallsReturnAllValuesAndTransformErrors()
    {
        var state = NewState();
        var body = new LuaProto
        {
            MaxStackSize = 2,
            Constants = [17, "second"],
            Code = [ABx(OpCode.LoadK, 0, 0), ABx(OpCode.LoadK, 1, 1), ABC(OpCode.Return, 0, 3, 0)],
        };
        var success = state.MainThread.Call(state.Globals["pcall"], state.Load(body));
        Assert.Equal(new LuaValue[] { true, 17, "second" }, success);
        var tail = new LuaProto
        {
            MaxStackSize = 2,
            Constants = ["pcall"],
            Protos = [body],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.Closure, 1, 0), ABC(OpCode.TailCall, 0, 2, 0)],
        };
        Assert.Equal(success, Run(state, tail));
        var failed = new LuaProto
        {
            MaxStackSize = 3,
            Constants = ["error", "boom", 0],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABx(OpCode.LoadK, 1, 1), ABx(OpCode.LoadK, 2, 2), ABC(OpCode.Call, 0, 3, 1)],
        };
        var handler = new LuaProto
        {
            NumParams = 1,
            MaxStackSize = 1,
            Constants = ["handled"],
            Code = [ABx(OpCode.LoadK, 0, 0), ABC(OpCode.Return, 0, 2, 0)],
        };
        Assert.Equal(new LuaValue[] { false, "handled" },
            state.MainThread.Call(state.Globals["xpcall"], state.Load(failed), state.Load(handler)));
        Assert.Equal(new LuaValue[] { false, "boom" },
            state.MainThread.Call(state.Globals["xpcall"], state.Load(failed), state.Load(failed)));
    }

    [Fact]
    public void RuntimeErrorsCarryTheSourceLine()
    {
        var p = new LuaProto
        {
            Source = "@test.zsc",
            MaxStackSize = 2,
            LineInfo = [12, 12],
            Constants = [LuaValue.String("x")],
            Code = [ABx(OpCode.GetGlobal, 0, 0), ABC(OpCode.Call, 0, 1, 1)],
        };
        var e = Assert.Throws<LuaException>(() => Run(NewState(), p));
        Assert.Equal("test.zsc:12: attempt to call a nil value", e.Message);
    }

    [Fact]
    public void MetatableIndexFallsBack()
    {
        var s = NewState();
        var t = new LuaTable();
        var fallback = new LuaTable { ["x"] = 5 };
        t.Metatable = new LuaTable { ["__index"] = fallback };
        Assert.Equal(5, s.Index(LuaValue.Table(t), "x").N);
    }
}
