namespace GameWave.Lua;

/// <summary>The Lua 5.0 bytecode interpreter loop.</summary>
public sealed partial class LuaThread
{
    /// <summary>Raised for runtime errors that still need a "source:line:" prefix.</summary>
    internal sealed class RuntimeError(string message) : LuaException(message);

    internal void Execute()
    {
        var S = State;
    newFrame:
        var f = Frames[FrameCount - 1];
        if (f.Protected != 0)
        {
            int count = f.Protected == 2 ? 1 : Top - f.Base;
            EnsureStack(f.Base + count + 1);
            for (int j = count; j > 0; j--) Stack[f.Base + j] = Stack[f.Base + j - 1];
            Stack[f.Base] = LuaValue.Bool(f.Protected == 1);
            bool boundary = f.Boundary;
            int want = f.Want;
            PostCall(f.Base, count + 1);
            if (boundary || FrameCount == 0) return;
            if (want != MultRet) Top = Frames[FrameCount - 1].Top;
            goto newFrame;
        }
        if (f.Native is { } native && f.Continuation is not null)
        {
            try
            {
                int count = native.Fn(new LuaArgs(this, f.Base, f.Top - f.Base));
                if (count == -1) goto newFrame;
                bool boundary = f.Boundary;
                int want = f.Want;
                PostCall(Top - count, count);
                if (boundary || FrameCount == 0) return;
                if (want != MultRet && Frames[FrameCount - 1].Closure is not null) Top = Frames[FrameCount - 1].Top;
                goto newFrame;
            }
            catch (LuaException error)
            {
                error.LuaTraceback ??= Traceback();
                if (HandleProtectedError(error)) goto newFrame;
                throw;
            }
            catch (GameWave.Engine.LuaStateRestoredException) { goto newFrame; }
        }
        var cl = f.Closure!;
        var p = cl.Proto;
        var k = p.Constants;
        var code = p.Code;
        int @base = f.Base;
        int pc = f.Pc;
        var s = Stack;

        try
        {
            while (true)
            {
                // Branches change the local PC after decoding an instruction. Publish
                // that next PC before a host boundary can capture the Lua stack.
                f.Pc = pc;
                bool skipBoundary = SkipInstructionBoundary;
                SkipInstructionBoundary = false;
                if (!skipBoundary && S.InstructionBoundary?.Invoke() == true)
                    goto newFrame;
                uint i = code[pc++];
                f.Pc = pc;
                int a = @base + (int)(i >> 24);
                switch ((OpCode)(i & 0x3F))
                {
                    case OpCode.Move:
                        s[a] = s[@base + Instr.B(i)];
                        break;

                    case OpCode.LoadK:
                        s[a] = k[Instr.Bx(i)];
                        break;

                    case OpCode.LoadBool:
                        s[a] = LuaValue.Bool(Instr.B(i) != 0);
                        if (Instr.C(i) != 0)
                            pc++;
                        break;

                    case OpCode.LoadNil:
                    {
                        int last = @base + Instr.B(i);
                        for (int r = a; r <= last; r++)
                            s[r] = LuaValue.Nil;
                        break;
                    }

                    case OpCode.GetUpval:
                        s[a] = cl.Upvalues[Instr.B(i)].Value;
                        break;

                    case OpCode.GetGlobal:
                    {
                        var key = k[Instr.Bx(i)];
                        var v = cl.Env.Get(key);
                        if (v.IsNil && cl.Env.Metatable is not null)
                        {
                            StartOperation("index", a, LuaValue.Table(cl.Env), key);
                            goto newFrame;
                        }
                        s[a] = v;
                        break;
                    }

                    case OpCode.GetTable:
                    {
                        var t = s[@base + Instr.B(i)];
                        int c = Instr.C(i);
                        var key = c >= Instr.MaxStack ? k[c - Instr.MaxStack] : s[@base + c];
                        LuaValue v;
                        if (t.O is LuaTable tt && (!(v = tt.Get(key)).IsNil || tt.Metatable is null))
                        {
                        }
                        else
                        {
                            StartOperation("index", a, t, key);
                            goto newFrame;
                        }
                        s[a] = v;
                        break;
                    }

                    case OpCode.SetGlobal:
                    {
                        var key = k[Instr.Bx(i)];
                        if (cl.Env.Metatable is null)
                            cl.Env.Set(key, s[a]);
                        else
                        {
                            StartOperation("set", a, LuaValue.Table(cl.Env), key, s[a]);
                            goto newFrame;
                        }
                        break;
                    }

                    case OpCode.SetUpval:
                        cl.Upvalues[Instr.B(i)].Value = s[a];
                        break;

                    case OpCode.SetTable:
                    {
                        int b = Instr.B(i), c = Instr.C(i);
                        var key = b >= Instr.MaxStack ? k[b - Instr.MaxStack] : s[@base + b];
                        var val = c >= Instr.MaxStack ? k[c - Instr.MaxStack] : s[@base + c];
                        var t = s[a];
                        if (t.O is LuaTable tt && tt.Metatable is null)
                            tt.Set(key, val);
                        else
                        {
                            StartOperation("set", a, t, key, val);
                            goto newFrame;
                        }
                        break;
                    }

                    case OpCode.NewTable:
                        s[a] = LuaValue.Table(new LuaTable(Math.Min(Instr.B(i), 256), 0));
                        break;

                    case OpCode.Self:
                    {
                        var obj = s[@base + Instr.B(i)];
                        int c = Instr.C(i);
                        var key = c >= Instr.MaxStack ? k[c - Instr.MaxStack] : s[@base + c];
                        s[a + 1] = obj;
                        LuaValue v;
                        if (obj.O is LuaTable tt && (!(v = tt.Get(key)).IsNil || tt.Metatable is null))
                        {
                        }
                        else
                        {
                            StartOperation("index", a, obj, key);
                            goto newFrame;
                        }
                        s[a] = v;
                        break;
                    }

                    case OpCode.Add:
                    case OpCode.Sub:
                    case OpCode.Mul:
                    case OpCode.Div:
                    case OpCode.Pow:
                    {
                        int b = Instr.B(i), c = Instr.C(i);
                        var x = b >= Instr.MaxStack ? k[b - Instr.MaxStack] : s[@base + b];
                        var y = c >= Instr.MaxStack ? k[c - Instr.MaxStack] : s[@base + c];
                        var op = (OpCode)(i & 0x3F);
                        if (x.Type == LuaType.Number && y.Type == LuaType.Number)
                            s[a] = LuaValue.Number(LuaState.ArithInt(op, x.N, y.N));
                        else
                        {
                            StartOperation("arith", a, x, y, (int)op);
                            goto newFrame;
                        }
                        break;
                    }

                    case OpCode.Unm:
                    {
                        var x = s[@base + Instr.B(i)];
                        if (x.Type == LuaType.Number)
                            s[a] = LuaValue.Number(unchecked(-x.N));
                        else
                        {
                            StartOperation("arith", a, x, x, (int)OpCode.Unm);
                            goto newFrame;
                        }
                        break;
                    }

                    case OpCode.Not:
                        s[a] = LuaValue.Bool(s[@base + Instr.B(i)].IsFalsy);
                        break;

                    case OpCode.Concat:
                    {
                        int b = @base + Instr.B(i), c = @base + Instr.C(i);
                        StartOperation("concat", a, s[b..(c + 1)]);
                        goto newFrame;
                    }

                    case OpCode.Jmp:
                        pc += Instr.SBx(i);
                        break;

                    case OpCode.Eq:
                    case OpCode.Lt:
                    case OpCode.Le:
                    {
                        int b = Instr.B(i), c = Instr.C(i);
                        var x = b >= Instr.MaxStack ? k[b - Instr.MaxStack] : s[@base + b];
                        var y = c >= Instr.MaxStack ? k[c - Instr.MaxStack] : s[@base + c];
                        StartOperation((OpCode)(i & 0x3F) switch
                        {
                            OpCode.Eq => "eq", OpCode.Lt => "lt", _ => "le",
                        }, a, x, y, LuaValue.Bool((i >> 24) != 0));
                        goto newFrame;
                    }

                    case OpCode.Test:
                    {
                        var rb = s[@base + Instr.B(i)];
                        if (rb.IsFalsy == (Instr.C(i) != 0))
                            pc++;
                        else
                        {
                            s[a] = rb;
                            pc += Instr.SBx(code[pc]) + 1;
                        }
                        break;
                    }

                    case OpCode.Call:
                    {
                        int b = Instr.B(i);
                        int want = Instr.C(i) - 1;
                        if (b != 0)
                            Top = a + b;
                        f.Pc = pc;
                        if (PreCall(a, want))
                            goto newFrame;
                        if (Yielding)
                            return;
                        s = Stack;
                        if (want != MultRet)
                            Top = f.Top;
                        break;
                    }

                    case OpCode.TailCall:
                    {
                        int b = Instr.B(i);
                        if (b != 0)
                            Top = a + b;
                        CloseUpvals(@base);
                        // Slide the callee and its arguments down over this frame.
                        int n = Top - a;
                        for (int j = 0; j < n; j++)
                            s[f.Func + j] = s[a + j];
                        Top = f.Func + n;
                        int want = f.Want;
                        bool boundary = f.Boundary;
                        FrameCount--;
                        int enteredFrame = FrameCount;
                        if (PreCall(f.Func, want))
                        {
                            Frames[enteredFrame].Boundary = boundary;
                            goto newFrame;
                        }
                        if (Yielding)
                        {
                            // Yielding from a tail call: re-create a frame to resume into.
                            return;
                        }
                        if (boundary)
                            return;
                        f = Frames[FrameCount - 1];
                        if (want != MultRet)
                            Top = f.Top;
                        goto newFrame;
                    }

                    case OpCode.Return:
                    {
                        int b = Instr.B(i);
                        int n = b != 0 ? b - 1 : Top - a;
                        CloseUpvals(@base);
                        bool boundary = f.Boundary;
                        int want = f.Want;
                        PostCall(a, n);
                        if (boundary)
                            return;
                        if (want != MultRet)
                            Top = Frames[FrameCount - 1].Top;
                        goto newFrame;
                    }

                    case OpCode.ForLoop:
                    {
                        if (!s[a].IsNumber)
                            throw new RuntimeError("`for' initial value must be a number");
                        if (!s[a + 1].TryToNumber(out int limit))
                            throw new RuntimeError("`for' limit must be a number");
                        if (!s[a + 2].TryToNumber(out int step))
                            throw new RuntimeError("`for' step must be a number");
                        int idx = unchecked(s[a].N + step);
                        if (step > 0 ? idx <= limit : idx >= limit)
                        {
                            pc += Instr.SBx(i);
                            s[a] = LuaValue.Number(idx);
                        }
                        break;
                    }

                    case OpCode.TForLoop:
                    {
                        StartOperation("iterator", a, s[a], s[a + 1], s[a + 2], Instr.C(i) + 1);
                        goto newFrame;
                    }

                    case OpCode.TForPrep:
                    {
                        if (s[a].IsTable)
                        {
                            s[a + 1] = s[a];
                            s[a] = S.Globals["next"];
                        }
                        pc += Instr.SBx(i);
                        break;
                    }

                    case OpCode.SetList:
                    case OpCode.SetListO:
                    {
                        var t = s[a].AsTable ?? throw new RuntimeError("SETLIST on a non-table");
                        int bc = Instr.Bx(i);
                        int n;
                        if ((OpCode)(i & 0x3F) == OpCode.SetList)
                            n = (bc & (Instr.FieldsPerFlush - 1)) + 1;
                        else
                        {
                            n = Top - a - 1;
                            Top = f.Top;
                        }
                        bc &= ~(Instr.FieldsPerFlush - 1);
                        for (int j = 1; j <= n; j++)
                            t.Set(LuaValue.Number(bc + j), s[a + j]);
                        break;
                    }

                    case OpCode.Close:
                        CloseUpvals(a);
                        break;

                    case OpCode.Closure:
                    {
                        var np = p.Protos[Instr.Bx(i)];
                        var ncl = new LuaClosure(np, cl.Env);
                        for (int j = 0; j < np.NumUpvalues; j++)
                        {
                            uint u = code[pc++];
                            if (Instr.Op(u) == OpCode.GetUpval)
                                ncl.Upvalues[j] = cl.Upvalues[Instr.B(u)];
                            else
                                ncl.Upvalues[j] = FindUpval(@base + Instr.B(u));
                        }
                        s[a] = LuaValue.Function(ncl);
                        break;
                    }

                    default:
                        throw new RuntimeError($"bad opcode {i & 0x3F}");
                }
            }
        }
        catch (LuaException error)
        {
            var e = error is RuntimeError
                ? new LuaException(LuaValue.String($"{p.ShortSource}:{p.LineAt(f.Pc - 1)}: {error.Message}"))
                : error;
            e.LuaTraceback ??= Traceback();
            if (HandleProtectedError(e)) goto newFrame;
            throw e;
        }
        catch (GameWave.Engine.LuaStateRestoredException)
        {
            // The exception unwinds a blocking native engine call. Continue from the
            // restored Lua frame; the saved graph has rewound that CALL instruction.
            goto newFrame;
        }
    }

    bool HandleProtectedError(LuaException error)
    {
        for (int i = FrameCount - 1; i >= 0; i--)
        {
            var frame = Frames[i];
            if (frame.Protected != 0)
            {
                CloseUpvals(frame.Base);
                FrameCount = i + 1;
                bool callHandler = frame.Protected == 1 && frame.ErrorHandler.IsFunction;
                frame.Protected = 2;
                Stack[frame.Base] = error.Value; Top = frame.Base + 1;
                if (callHandler)
                {
                    Stack[frame.Base] = frame.ErrorHandler; Push(error.Value);
                    try { PreCall(frame.Base, 1); }
                    catch (LuaException handlerError) { HandleProtectedError(handlerError); }
                }
                return true;
            }
            if (frame.Boundary) break;
        }
        return false;
    }
}
