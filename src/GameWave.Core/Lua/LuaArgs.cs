namespace GameWave.Lua;

/// <summary>The arguments of a native call, with Lua-style checking helpers. Indexes are 1-based.</summary>
public readonly struct LuaArgs
{
    public readonly LuaThread L;
    public readonly int Base;
    public readonly int Count;

    public LuaArgs(LuaThread l, int @base, int count)
    {
        L = l;
        Base = @base;
        Count = count;
    }

    public LuaState State => L.State;

    public LuaValue this[int n] => n >= 1 && n <= Count ? L.Stack[Base + n - 1] : LuaValue.Nil;

    public bool IsNone(int n) => n > Count;
    public bool IsNoneOrNil(int n) => this[n].IsNil;

    string FunctionName => L.CurrentFrame?.Native?.Name ?? "?";

    public LuaException ArgError(int n, string message)
        => new LuaThread.RuntimeError($"bad argument #{n} to `{FunctionName}' ({message})");

    LuaException TypeError(int n, string expected)
        => ArgError(n, $"{expected} expected, got {(IsNone(n) ? "no value" : this[n].TypeName)}");

    public LuaValue Any(int n)
    {
        if (IsNone(n))
            throw ArgError(n, "value expected");
        return this[n];
    }

    public int Int(int n)
    {
        if (!this[n].TryToNumber(out int v))
            throw TypeError(n, "number");
        return v;
    }

    public int OptInt(int n, int def) => this[n].IsNil ? def : Int(n);

    public bool Bool(int n) => this[n].IsTruthy;

    public string Str(int n)
    {
        if (!this[n].TryToStr(out var s))
            throw TypeError(n, "string");
        return s;
    }

    public string? OptStr(int n, string? def) => this[n].IsNil ? def : Str(n);

    public LuaTable Table(int n) => this[n].AsTable ?? throw TypeError(n, "table");

    public LuaTable? OptTable(int n) => this[n].IsNil ? null : Table(n);

    public LuaFunction Function(int n) => this[n].AsFunction ?? throw TypeError(n, "function");

    public LuaThread Thread(int n) => this[n].O as LuaThread ?? throw TypeError(n, "coroutine");

    public LuaUserdata Userdata(int n) => this[n].AsUserdata ?? throw TypeError(n, "userdata");

    public int Return() => 0;

    public int Return(LuaValue v)
    {
        L.Push(v);
        return 1;
    }

    public int Return(LuaValue a, LuaValue b)
    {
        L.Push(a);
        L.Push(b);
        return 2;
    }

    public int Return(LuaValue a, LuaValue b, LuaValue c)
    {
        L.Push(a);
        L.Push(b);
        L.Push(c);
        return 3;
    }

    public int Return(params ReadOnlySpan<LuaValue> values)
    {
        foreach (var v in values)
            L.Push(v);
        return values.Length;
    }

    public static LuaException Error(string message) => new LuaThread.RuntimeError(message);
}
