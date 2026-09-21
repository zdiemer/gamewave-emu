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
                if (S.InstructionBoundary?.Invoke() == true)
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
                            v = S.Index(LuaValue.Table(cl.Env), key);
                            s = Stack;
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
                            v = S.Index(t, key);
                            s = Stack;
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
                            S.SetIndex(LuaValue.Table(cl.Env), key, s[a]);
                            s = Stack;
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
                            S.SetIndex(t, key, val);
                            s = Stack;
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
                            v = S.Index(obj, key);
                            s = Stack;
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
                            var r = S.Arith(op, x, y);
                            s = Stack;
                            s[a] = r;
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
                            var r = S.Arith(OpCode.Unm, x, x);
                            s = Stack;
                            s[a] = r;
                        }
                        break;
                    }

                    case OpCode.Not:
                        s[a] = LuaValue.Bool(s[@base + Instr.B(i)].IsFalsy);
                        break;

                    case OpCode.Concat:
                    {
                        int b = @base + Instr.B(i), c = @base + Instr.C(i);
                        var r = S.Concat(s, b, c);
                        s = Stack;
                        s[a] = r;
                        break;
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
                        bool res = (OpCode)(i & 0x3F) switch
                        {
                            OpCode.Eq => S.ValuesEqual(x, y),
                            OpCode.Lt => S.LessThan(x, y),
                            _ => S.LessEqual(x, y),
                        };
                        s = Stack;
                        if (res != ((i >> 24) != 0))
                            pc++;
                        else
                            pc += Instr.SBx(code[pc]) + 1;
                        break;
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
                        if (PreCall(f.Func, want))
                        {
                            Frames[FrameCount - 1].Boundary = boundary;
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
                        int nvar = Instr.C(i) + 1;
                        int cb = a + nvar + 2;
                        EnsureStack(cb + 3);
                        s = Stack;
                        s[cb] = s[a];
                        s[cb + 1] = s[a + 1];
                        s[cb + 2] = s[a + 2];
                        Top = cb + 3;
                        Call(cb, nvar);
                        s = Stack;
                        Top = f.Top;
                        for (int j = 0; j < nvar; j++)
                            s[a + 2 + j] = s[cb + j];
                        if (s[a + 2].IsNil)
                            pc++;
                        else
                            pc += Instr.SBx(code[pc]) + 1;
                        break;
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
        catch (RuntimeError e)
        {
            throw new LuaException(LuaValue.String($"{p.ShortSource}:{p.LineAt(f.Pc - 1)}: {e.Message}"))
            {
                LuaTraceback = Traceback(),
            };
        }
        catch (LuaException e) when (e.LuaTraceback is null)
        {
            e.LuaTraceback = Traceback();
            throw;
        }
        catch (GameWave.Engine.LuaStateRestoredException)
        {
            // The exception unwinds a blocking native engine call. Continue from the
            // restored Lua frame; the saved graph has rewound that CALL instruction.
            goto newFrame;
        }
    }
}
