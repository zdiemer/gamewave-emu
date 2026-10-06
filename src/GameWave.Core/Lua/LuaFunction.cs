namespace GameWave.Lua;

public abstract class LuaFunction
{
    public abstract string Name { get; }
}

/// <summary>A compiled Lua 5.0 function prototype.</summary>
public sealed class LuaProto
{
    public string Source = "?";
    public int LineDefined;
    public int NumUpvalues;
    public int NumParams;
    public bool IsVararg;
    public int MaxStackSize;
    public int[] LineInfo = [];
    public LocalVar[] Locals = [];
    public string[] UpvalueNames = [];
    public LuaValue[] Constants = [];
    public LuaProto[] Protos = [];
    public uint[] Code = [];

    public int LineAt(int pc) => pc >= 0 && pc < LineInfo.Length ? LineInfo[pc] : 0;

    public string ShortSource
    {
        get
        {
            if (Source.StartsWith('@') || Source.StartsWith('='))
                return Source[1..];
            return "[string]";
        }
    }

    public readonly record struct LocalVar(string Name, int StartPc, int EndPc);
}

public sealed class LuaClosure : LuaFunction
{
    public readonly LuaProto Proto;
    public readonly UpVal[] Upvalues;
    public LuaTable Env;

    public LuaClosure(LuaProto proto, LuaTable env)
    {
        Proto = proto;
        Upvalues = new UpVal[proto.NumUpvalues];
        Env = env;
    }

    public override string Name => $"{Proto.ShortSource}:{Proto.LineDefined}";
}

/// <summary>
/// A function implemented in C#. It reads its arguments from <see cref="LuaArgs"/>, pushes
/// its results with <see cref="LuaThread.Push(LuaValue)"/>, and returns how many it pushed.
/// </summary>
public delegate int LuaNativeFn(LuaArgs args);

public sealed class LuaNative : LuaFunction
{
    public readonly LuaNativeFn Fn;
    readonly string _name;
    readonly Func<Func<LuaThread, LuaThread>, LuaNative>? _snapshotClone;
    public LuaTable? Env;
    internal LuaThread? WrappedThread;
    internal Func<(string Source, string Pattern, int Position)>? IteratorState;

    public LuaNative(string name, LuaNativeFn fn, Func<Func<LuaThread, LuaThread>, LuaNative>? snapshotClone = null)
    {
        _name = name;
        Fn = fn;
        _snapshotClone = snapshotClone;
    }

    public override string Name => _name;

    internal LuaNative CloneForSnapshot(Func<LuaThread, LuaThread> cloneThread)
        => _snapshotClone?.Invoke(cloneThread) ?? this;
}

public sealed class UpVal
{
    LuaThread? _thread;
    public int Index;
    LuaValue _closed;

    public UpVal(LuaThread thread, int index)
    {
        _thread = thread;
        Index = index;
    }

    public bool IsOpen => _thread is not null;
    internal LuaThread? Thread => _thread;

    internal UpVal(LuaValue value)
    {
        _closed = value;
    }

    internal void Bind(LuaThread? thread, int index)
    {
        _thread = thread;
        Index = index;
    }

    public LuaValue Value
    {
        get => _thread is not null ? _thread.Stack[Index] : _closed;
        set
        {
            if (_thread is not null) _thread.Stack[Index] = value;
            else _closed = value;
        }
    }

    public void Close()
    {
        _closed = _thread!.Stack[Index];
        _thread = null;
    }
}

public sealed class LuaUserdata
{
    public object? Payload;
    public LuaTable? Metatable;
    public LuaTable? Env;

    public LuaUserdata(object? payload) => Payload = payload;
}

public class LuaException : Exception
{
    public LuaValue Value { get; }
    public string? LuaTraceback { get; set; }

    public LuaException(string message) : base(message) => Value = LuaValue.String(message);

    public LuaException(LuaValue value)
        : base(value.TryToStr(out var s) ? s : $"(error object is a {value.TypeName} value)")
        => Value = value;
}
