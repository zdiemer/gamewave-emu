using System.Runtime.CompilerServices;
using System.Text;

namespace GameWave.Lua;

/// <summary>The Lua 5.0 base library, coroutines, <c>table</c> and <c>debug</c>.</summary>
public static class BaseLib
{
    public static void Open(LuaState S)
    {
        var G = S.Globals;
        G["_G"] = G;
        G["_VERSION"] = "Lua 5.0";

        S.Register("print", a =>
        {
            var sb = new StringBuilder();
            for (int i = 1; i <= a.Count; i++)
            {
                if (i > 1)
                    sb.Append('\t');
                sb.Append(a.State.ToStringMeta(a[i]));
            }
            a.State.Output(sb.ToString());
            return 0;
        });
        S.Register("type", a => a.Return(a.Any(1).TypeName));
        S.Register("tostring", a => a.Return(a.State.ToStringMeta(a.Any(1))));
        S.Register("tonumber", ToNumber);
        S.Register("assert", a =>
        {
            if (a[1].IsFalsy)
                throw new LuaException(a.OptStr(2, "assertion failed!")!);
            return a.Return(a[1]);
        });
        S.Register("error", a =>
        {
            var msg = a[1];
            int level = a.OptInt(2, 1);
            if (msg.IsString && level > 0)
                msg = LuaValue.String(a.L.Where(level) + msg.AsString);
            throw new LuaException(msg);
        });
        S.Register("pcall", a =>
        {
            a.Any(1);
            var L = a.L;
            var err = L.PCall(a.Base, LuaThread.MultRet);
            return PushProtectedResult(L, a.Base, err);
        });
        S.Register("xpcall", a =>
        {
            var L = a.L;
            var handler = a[2];
            // Rearrange to f() with no arguments, as 5.0's xpcall takes none.
            L.Top = a.Base + 1;
            var err = L.PCall(a.Base, LuaThread.MultRet, handler);
            return PushProtectedResult(L, a.Base, err);
        });
        S.Register("next", a =>
        {
            var t = a.Table(1);
            if (t.Next(a[2], out var k, out var v))
                return a.Return(k, v);
            return a.Return(LuaValue.Nil);
        });
        var next = G["next"];
        S.Register("pairs", a => a.Return(next, a.Table(1), LuaValue.Nil));
        var ipairsIter = new LuaNative("ipairs_iterator", a =>
        {
            var t = a.Table(1);
            int i = a.Int(2) + 1;
            var v = t.Get(i);
            return v.IsNil ? a.Return(LuaValue.Nil) : a.Return(i, v);
        });
        S.Register("ipairs", a => a.Return(ipairsIter, a.Table(1), 0));
        S.Register("unpack", a =>
        {
            var t = a.Table(1);
            int n = GetN(t);
            a.L.EnsureStack(a.L.Top + n);
            for (int i = 1; i <= n; i++)
                a.L.Push(t.Get(i));
            return n;
        });
        S.Register("rawget", a => a.Return(a.Table(1).Get(a.Any(2))));
        S.Register("rawset", a =>
        {
            a.Table(1).Set(a.Any(2), a.Any(3));
            return a.Return(a[1]);
        });
        S.Register("rawequal", a => a.Return(a.Any(1).Equals(a.Any(2))));
        S.Register("getmetatable", a =>
        {
            var mt = a.State.GetMetatable(a.Any(1));
            if (mt is null)
                return a.Return(LuaValue.Nil);
            var protectedMt = mt["__metatable"];
            return a.Return(protectedMt.IsNil ? mt : protectedMt);
        });
        S.Register("setmetatable", a =>
        {
            var t = a.Table(1);
            if (!a[2].IsNil && !a[2].IsTable)
                throw a.ArgError(2, "nil or table expected");
            if (t.Metatable is not null && !t.Metatable["__metatable"].IsNil)
                throw new LuaException("cannot change a protected metatable");
            t.Metatable = a[2].AsTable;
            return a.Return(t);
        });
        S.Register("getfenv", a =>
        {
            var f = ResolveFunction(a, 1);
            return a.Return(f is LuaClosure c ? c.Env : a.State.Globals);
        });
        S.Register("setfenv", a =>
        {
            var env = a.Table(2);
            if (a[1].IsNumber && a[1].N == 0)
                return 0;
            var f = ResolveFunction(a, 1);
            if (f is LuaClosure c)
                c.Env = env;
            else
                throw new LuaException("`setfenv' cannot change environment of given function");
            return a.Return(f);
        });
        S.Register("collectgarbage", a => 0);
        S.Register("gcinfo", a => a.Return((int)(GC.GetTotalMemory(false) / 1024), 1 << 20));
        S.Register("loadstring", a => a.Return(LuaValue.Nil, "loadstring is not supported"));
        S.Register("newproxy", a =>
        {
            var u = new LuaUserdata(null);
            if (a[1].IsTruthy)
                u.Metatable = new LuaTable();
            return a.Return(LuaValue.Userdata(u));
        });

        OpenCoroutine(S);
        OpenTable(S);
        OpenDebug(S);
    }

    static LuaFunction? ResolveFunction(LuaArgs a, int n)
    {
        if (a[n].IsFunction)
            return a[n].AsFunction;
        int level = a.OptInt(n, 1);
        int idx = a.L.FrameCount - 1 - level;
        if (idx < 0)
            throw a.ArgError(n, "invalid level");
        var f = a.L.Frames[idx];
        return (LuaFunction?)f.Closure ?? f.Native;
    }

    static int PushProtectedResult(LuaThread L, int @base, LuaException? err)
    {
        if (err is not null)
        {
            L.Top = @base;
            L.Push(LuaValue.False);
            L.Push(err.Value);
            return 2;
        }
        int n = L.Top - @base;
        L.EnsureStack(L.Top + 1);
        for (int j = L.Top; j > @base; j--)
            L.Stack[j] = L.Stack[j - 1];
        L.Stack[@base] = LuaValue.True;
        L.Top++;
        return n + 1;
    }

    static int ToNumber(LuaArgs a)
    {
        int radix = a.OptInt(2, 10);
        if (radix == 10)
        {
            a.Any(1);
            return a.Return(a[1].TryToNumber(out int n) ? LuaValue.Number(n) : LuaValue.Nil);
        }
        string s = a.Str(1).Trim().ToLowerInvariant();
        if (radix < 2 || radix > 36)
            throw a.ArgError(2, "base out of range");
        long v = 0;
        bool neg = s.StartsWith('-');
        if (neg)
            s = s[1..];
        if (s.Length == 0)
            return a.Return(LuaValue.Nil);
        foreach (char c in s)
        {
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : 99;
            if (d >= radix)
                return a.Return(LuaValue.Nil);
            v = (v * radix + d) & 0xFFFFFFFF;
        }
        return a.Return(unchecked((int)(neg ? -v : v)));
    }

    // ---------------------------------------------------------------- coroutines

    static void OpenCoroutine(LuaState S)
    {
        S.RegisterModule("coroutine",
            ("create", a => a.Return(LuaValue.Thread(a.State.NewThread(a.Function(1))))),
            ("resume", a =>
            {
                var co = a.Thread(1);
                var args = new LuaValue[Math.Max(0, a.Count - 1)];
                for (int i = 0; i < args.Length; i++)
                    args[i] = a[i + 2];
                var r = co.Resume(a.L, args, out bool ok);
                a.L.Push(LuaValue.Bool(ok));
                foreach (var v in r)
                    a.L.Push(v);
                return r.Length + 1;
            }),
            ("yield", a =>
            {
                var L = a.L;
                if (L == a.State.MainThread || L.NativeDepth > 0)
                    throw new LuaException("attempt to yield across metamethod/C-call boundary");
                L.Yielding = true;
                // The arguments are already the top values of the stack.
                return a.Count;
            }),
            ("status", a => a.Return(a.Thread(1).Status switch
            {
                CoroutineStatus.Suspended => "suspended",
                CoroutineStatus.Running => "running",
                CoroutineStatus.Normal => "normal",
                _ => "dead",
            })),
            ("wrap", a =>
            {
                var co = a.State.NewThread(a.Function(1));
                return a.Return(WrapCoroutine(co));
            }));

        static LuaNative WrapCoroutine(LuaThread coroutine)
        {
            return new LuaNative("wrap", b =>
            {
                var args = new LuaValue[b.Count];
                for (int i = 0; i < args.Length; i++)
                    args[i] = b[i + 1];
                var r = coroutine.Resume(b.L, args, out bool ok);
                if (!ok)
                {
                    var e = r.Length > 0 ? r[0] : LuaValue.Nil;
                    if (e.IsString)
                        e = LuaValue.String(b.L.Where(1) + e.AsString);
                    throw new LuaException(e);
                }
                foreach (var v in r)
                    b.L.Push(v);
                return r.Length;
            }, clone => WrapCoroutine(clone(coroutine)));
        }
    }

    // ---------------------------------------------------------------- table

    static readonly ConditionalWeakTable<LuaTable, StrongBox<int>> Sizes = new();

    public static int GetN(LuaTable t)
    {
        if (Sizes.TryGetValue(t, out var box))
            return box.Value;
        var n = t["n"];
        if (n.IsNumber)
            return n.N;
        return t.Length;
    }

    public static void SetN(LuaTable t, int n)
    {
        if (t["n"].IsNumber)
            t["n"] = n;
        else
            Sizes.AddOrUpdate(t, new StrongBox<int>(n));
    }

    internal static void CopyTableSize(LuaTable source, LuaTable destination)
    {
        if (Sizes.TryGetValue(source, out var size))
            Sizes.AddOrUpdate(destination, new StrongBox<int>(size.Value));
    }

    static void OpenTable(LuaState S)
    {
        S.RegisterModule("table",
            ("getn", a => a.Return(GetN(a.Table(1)))),
            ("setn", a =>
            {
                SetN(a.Table(1), a.Int(2));
                return 0;
            }),
            ("insert", a =>
            {
                var t = a.Table(1);
                int n = GetN(t) + 1;
                int pos;
                LuaValue v;
                if (a.Count <= 2)
                {
                    pos = n;
                    v = a[2];
                }
                else
                {
                    pos = a.Int(2);
                    if (pos > n)
                        n = pos;
                    v = a[3];
                }
                SetN(t, n);
                for (int i = n; i > pos; i--)
                    t.Set(LuaValue.Number(i), t.Get(i - 1));
                t.Set(LuaValue.Number(pos), v);
                return 0;
            }),
            ("remove", a =>
            {
                var t = a.Table(1);
                int n = GetN(t);
                int pos = a.OptInt(2, n);
                if (n <= 0)
                    return 0;
                SetN(t, n - 1);
                var v = t.Get(pos);
                for (; pos < n; pos++)
                    t.Set(LuaValue.Number(pos), t.Get(pos + 1));
                t.Set(LuaValue.Number(n), LuaValue.Nil);
                return a.Return(v);
            }),
            ("concat", a =>
            {
                var t = a.Table(1);
                string sep = a.OptStr(2, "")!;
                int i = a.OptInt(3, 1);
                int j = a.IsNoneOrNil(4) ? GetN(t) : a.Int(4);
                var sb = new StringBuilder();
                for (int k = i; k <= j; k++)
                {
                    if (!t.Get(k).TryToStr(out var s))
                        throw new LuaException("table contains non-strings");
                    sb.Append(s);
                    if (k != j)
                        sb.Append(sep);
                }
                return a.Return(sb.ToString());
            }),
            ("foreach", a =>
            {
                var t = a.Table(1);
                var f = LuaValue.Function(a.Function(2));
                foreach (var kv in t.Pairs().ToList())
                {
                    var r = a.L.Call1(f, kv.Key, kv.Value);
                    if (!r.IsNil)
                        return a.Return(r);
                }
                return 0;
            }),
            ("foreachi", a =>
            {
                var t = a.Table(1);
                var f = LuaValue.Function(a.Function(2));
                int n = GetN(t);
                for (int i = 1; i <= n; i++)
                {
                    var r = a.L.Call1(f, i, t.Get(i));
                    if (!r.IsNil)
                        return a.Return(r);
                }
                return 0;
            }),
            ("sort", a =>
            {
                var t = a.Table(1);
                int n = GetN(t);
                var comp = a[2];
                new Sorter(a.L, t, comp).Sort(1, n);
                return 0;
            }));
    }

    /// <summary>Lua 5.0's <c>auxsort</c>, so ties land where the original put them.</summary>
    sealed class Sorter(LuaThread L, LuaTable t, LuaValue comp)
    {
        bool Lt(LuaValue x, LuaValue y)
        {
            if (!comp.IsNil)
                return L.Call1(comp, x, y).IsTruthy;
            return L.State.LessThan(x, y);
        }

        LuaValue Get(int i) => t.Get(i);
        void Set(int i, LuaValue v) => t.Set(LuaValue.Number(i), v);

        void Swap(int i, int j)
        {
            var a = Get(i);
            var b = Get(j);
            Set(i, b);
            Set(j, a);
        }

        public void Sort(int l, int u)
        {
            while (l < u)
            {
                if (Lt(Get(u), Get(l)))
                    Swap(l, u);
                if (u - l == 1)
                    break;
                int i = (l + u) / 2;
                if (Lt(Get(i), Get(l)))
                    Swap(i, l);
                else if (Lt(Get(u), Get(i)))
                    Swap(i, u);
                if (u - l == 2)
                    break;
                var p = Get(i);
                Swap(i, u - 1);
                i = l;
                int j = u - 1;
                for (;;)
                {
                    while (Lt(Get(++i), p))
                    {
                        if (i > u)
                            throw new LuaException("invalid order function for sorting");
                    }
                    while (Lt(p, Get(--j)))
                    {
                        if (j < l)
                            throw new LuaException("invalid order function for sorting");
                    }
                    if (j < i)
                        break;
                    Swap(i, j);
                }
                Swap(u - 1, i);
                if (i - l < u - i)
                {
                    j = l;
                    i = i - 1;
                    l = i + 2;
                }
                else
                {
                    j = i + 1;
                    i = u;
                    u = j - 2;
                }
                Sort(j, i);
            }
        }
    }

    // ---------------------------------------------------------------- debug

    static void OpenDebug(LuaState S)
    {
        S.RegisterModule("debug",
            ("traceback", a =>
            {
                string msg = a.OptStr(1, null) is { } m ? m + "\n" : "";
                return a.Return(msg + a.L.Traceback(1));
            }),
            ("getinfo", a =>
            {
                var info = new LuaTable();
                if (a[1].IsNumber)
                {
                    int idx = a.L.FrameCount - 1 - a.Int(1);
                    if (idx < 0)
                        return a.Return(LuaValue.Nil);
                    var f = a.L.Frames[idx];
                    if (f.Closure is { } cl)
                    {
                        info["source"] = cl.Proto.Source;
                        info["short_src"] = cl.Proto.ShortSource;
                        info["currentline"] = cl.Proto.LineAt(f.Pc - 1);
                        info["linedefined"] = cl.Proto.LineDefined;
                        info["what"] = "Lua";
                        info["func"] = cl;
                    }
                    else
                    {
                        info["source"] = "=[C]";
                        info["short_src"] = "[C]";
                        info["currentline"] = -1;
                        info["what"] = "C";
                        info["func"] = f.Native;
                    }
                }
                else if (a[1].AsFunction is LuaClosure c)
                {
                    info["source"] = c.Proto.Source;
                    info["short_src"] = c.Proto.ShortSource;
                    info["linedefined"] = c.Proto.LineDefined;
                    info["what"] = "Lua";
                    info["func"] = c;
                }
                return a.Return(info);
            }),
            ("sethook", a => 0),
            ("gethook", a => 0),
            ("getlocal", a => 0),
            ("setlocal", a => 0),
            ("getupvalue", a => 0),
            ("setupvalue", a => 0));
        S.Globals["_TRACEBACK"] = S.Globals["debug"].AsTable!["traceback"];
    }
}
