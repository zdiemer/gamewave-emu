using System.Text;

namespace GameWave.Lua;

/// <summary>A Lua universe: globals, the main thread, and the metamethod-aware operations.</summary>
public sealed class LuaState
{
    public LuaTable Globals { get; internal set; } = new();
    public LuaThread MainThread { get; }
    public LuaThread CurrentThread { get; internal set; }

    /// <summary>
    /// Called between bytecode instructions. Returning true means the live Lua graph was
    /// replaced and the interpreter must reload its current frame.
    /// </summary>
    internal Func<bool>? InstructionBoundary { get; set; }
    internal Dictionary<string, LuaNative> NativeFunctions { get; } = new(StringComparer.Ordinal);
    internal int ManagedCallDepth;
    internal readonly Dictionary<LuaValue, int> ObjectIds = new();
    internal int NextObjectId;

    /// <summary>Where <c>print</c> and the engine's log output go.</summary>
    public Action<string> Output { get; set; } = Console.WriteLine;

    public LuaState()
    {
        MainThread = new LuaThread(this) { Status = CoroutineStatus.Running, Started = true };
        CurrentThread = MainThread;
    }

    public LuaThread NewThread(LuaFunction fn) => new(this) { StartFunction = fn };

    public LuaClosure Load(LuaProto proto) => new(proto, Globals);

    public void Register(string name, LuaNativeFn fn)
    {
        var native = new LuaNative(name, fn);
        NativeFunctions[name] = native;
        Globals[name] = native;
    }

    public LuaTable RegisterModule(string module, params (string Name, LuaNativeFn Fn)[] fns)
    {
        var t = Globals[module].AsTable;
        if (t is null)
        {
            t = new LuaTable();
            Globals[module] = t;
        }
        foreach (var (name, fn) in fns)
        {
            var native = new LuaNative($"{module}.{name}", fn);
            NativeFunctions[native.Name] = native;
            t[name] = native;
        }
        return t;
    }

    // ---------------------------------------------------------------- metatables

    public LuaTable? GetMetatable(LuaValue v) => v.Type switch
    {
        LuaType.Table => ((LuaTable)v.O!).Metatable,
        LuaType.Userdata => ((LuaUserdata)v.O!).Metatable,
        _ => null,
    };

    public LuaValue GetMetamethod(LuaValue v, string ev)
    {
        var mt = GetMetatable(v);
        return mt is null ? LuaValue.Nil : mt.Get(LuaValue.String(ev));
    }

    internal LuaFunction? CallMetamethod(LuaValue v) => GetMetamethod(v, "__call").AsFunction;

    internal LuaException TypeError(LuaValue v, string op)
        => new LuaThread.RuntimeError($"attempt to {op} a {v.TypeName} value");

    // ---------------------------------------------------------------- indexing

    public LuaValue Index(LuaValue t, LuaValue key)
    {
        for (int loop = 0; loop < 100; loop++)
        {
            LuaValue h;
            if (t.O is LuaTable tt)
            {
                var v = tt.Get(key);
                if (!v.IsNil)
                    return v;
                if (tt.Metatable is null)
                    return LuaValue.Nil;
                h = tt.Metatable.Get(LuaValue.String("__index"));
                if (h.IsNil)
                    return LuaValue.Nil;
            }
            else
            {
                h = GetMetamethod(t, "__index");
                if (h.IsNil)
                    throw TypeError(t, "index");
            }
            if (h.IsFunction)
                return CurrentThread.Call1(h, t, key);
            t = h;
        }
        throw new LuaThread.RuntimeError("loop in gettable");
    }

    public void SetIndex(LuaValue t, LuaValue key, LuaValue value)
    {
        for (int loop = 0; loop < 100; loop++)
        {
            LuaValue h;
            if (t.O is LuaTable tt)
            {
                if (tt.Metatable is null)
                {
                    tt.Set(key, value);
                    return;
                }
                var old = tt.Get(key);
                if (!old.IsNil)
                {
                    tt.Set(key, value);
                    return;
                }
                h = tt.Metatable.Get(LuaValue.String("__newindex"));
                if (h.IsNil)
                {
                    tt.Set(key, value);
                    return;
                }
            }
            else
            {
                h = GetMetamethod(t, "__newindex");
                if (h.IsNil)
                    throw TypeError(t, "index");
            }
            if (h.IsFunction)
            {
                CurrentThread.Call(h, t, key, value);
                return;
            }
            t = h;
        }
        throw new LuaThread.RuntimeError("loop in settable");
    }

    // ---------------------------------------------------------------- arithmetic

    public static int ArithInt(OpCode op, int x, int y) => op switch
    {
        OpCode.Add => unchecked(x + y),
        OpCode.Sub => unchecked(x - y),
        OpCode.Mul => unchecked(x * y),
        OpCode.Div => y == 0 ? 0 : (x == int.MinValue && y == -1 ? x : x / y),
        OpCode.Pow => IntPow(x, y),
        OpCode.Unm => unchecked(-x),
        _ => 0,
    };

    static int IntPow(int x, int y)
    {
        if (y < 0)
            return x == 1 ? 1 : 0;
        int r = 1;
        while (y > 0)
        {
            if ((y & 1) != 0)
                r = unchecked(r * x);
            x = unchecked(x * x);
            y >>= 1;
        }
        return r;
    }

    static string EventName(OpCode op) => op switch
    {
        OpCode.Add => "__add",
        OpCode.Sub => "__sub",
        OpCode.Mul => "__mul",
        OpCode.Div => "__div",
        OpCode.Pow => "__pow",
        _ => "__unm",
    };

    public LuaValue Arith(OpCode op, LuaValue x, LuaValue y)
    {
        if (x.TryToNumber(out int a) && y.TryToNumber(out int b))
            return LuaValue.Number(ArithInt(op, a, b));
        string ev = EventName(op);
        var h = GetMetamethod(x, ev);
        if (h.IsNil)
            h = GetMetamethod(y, ev);
        if (h.IsNil)
        {
            var bad = x.TryToNumber(out _) ? y : x;
            throw TypeError(bad, "perform arithmetic on");
        }
        return CurrentThread.Call1(h, x, y);
    }

    public LuaValue Concat(LuaValue[] stack, int first, int last)
    {
        // Fast path: everything is a string or number.
        bool simple = true;
        for (int r = first; r <= last; r++)
        {
            var t = stack[r].Type;
            if (t != LuaType.String && t != LuaType.Number)
            {
                simple = false;
                break;
            }
        }
        if (simple)
        {
            var sb = new StringBuilder();
            for (int r = first; r <= last; r++)
            {
                stack[r].TryToStr(out var str);
                sb.Append(str);
            }
            return LuaValue.String(sb.ToString());
        }

        // Right to left, pairwise, consulting __concat.
        var acc = stack[last];
        for (int r = last - 1; r >= first; r--)
            acc = Concat2(stack[r], acc);
        return acc;
    }

    LuaValue Concat2(LuaValue x, LuaValue y)
    {
        if (x.TryToStr(out var a) && y.TryToStr(out var b))
            return LuaValue.String(a + b);
        var h = GetMetamethod(x, "__concat");
        if (h.IsNil)
            h = GetMetamethod(y, "__concat");
        if (h.IsNil)
        {
            var bad = x.TryToStr(out _) ? y : x;
            throw TypeError(bad, "concatenate");
        }
        return CurrentThread.Call1(h, x, y);
    }

    // ---------------------------------------------------------------- comparison

    public bool ValuesEqual(LuaValue x, LuaValue y)
    {
        if (x.Type != y.Type)
            return false;
        if (x.Equals(y))
            return true;
        if (x.Type != LuaType.Table && x.Type != LuaType.Userdata)
            return false;
        var h1 = GetMetamethod(x, "__eq");
        if (h1.IsNil)
            return false;
        var h2 = GetMetamethod(y, "__eq");
        if (!h1.Equals(h2))
            return false;
        return CurrentThread.Call1(h1, x, y).IsTruthy;
    }

    public bool LessThan(LuaValue x, LuaValue y)
    {
        if (x.Type == LuaType.Number && y.Type == LuaType.Number)
            return x.N < y.N;
        if (x.Type == LuaType.String && y.Type == LuaType.String)
            return string.CompareOrdinal((string)x.O!, (string)y.O!) < 0;
        var h = OrderMetamethod(x, y, "__lt");
        return CurrentThread.Call1(h, x, y).IsTruthy;
    }

    public bool LessEqual(LuaValue x, LuaValue y)
    {
        if (x.Type == LuaType.Number && y.Type == LuaType.Number)
            return x.N <= y.N;
        if (x.Type == LuaType.String && y.Type == LuaType.String)
            return string.CompareOrdinal((string)x.O!, (string)y.O!) <= 0;
        var h = GetMetamethod(x, "__le");
        if (!h.IsNil && h.Equals(GetMetamethod(y, "__le")))
            return CurrentThread.Call1(h, x, y).IsTruthy;
        var lt = OrderMetamethod(y, x, "__lt");
        return !CurrentThread.Call1(lt, y, x).IsTruthy;
    }

    LuaValue OrderMetamethod(LuaValue x, LuaValue y, string ev)
    {
        if (x.Type == y.Type)
        {
            var h = GetMetamethod(x, ev);
            if (!h.IsNil && h.Equals(GetMetamethod(y, ev)))
                return h;
        }
        if (x.Type == y.Type)
            throw new LuaThread.RuntimeError($"attempt to compare two {x.TypeName} values");
        throw new LuaThread.RuntimeError($"attempt to compare {x.TypeName} with {y.TypeName}");
    }

    // ---------------------------------------------------------------- conversions

    public string ToStringMeta(LuaValue v)
    {
        var h = GetMetamethod(v, "__tostring");
        if (!h.IsNil)
        {
            var r = CurrentThread.Call1(h, v);
            if (!r.TryToStr(out var s))
                throw new LuaException("`tostring' must return a string");
            return s;
        }
        if (v.Type is LuaType.Table or LuaType.Function or LuaType.Userdata or LuaType.Thread)
        {
            if (!ObjectIds.TryGetValue(v, out int id)) ObjectIds[v] = id = ++NextObjectId;
            return $"{v.TypeName}: 0x{id:x8}";
        }
        return v.ToString();
    }
}
