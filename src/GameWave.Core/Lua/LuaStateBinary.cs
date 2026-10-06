using GameWave.Engine;

namespace GameWave.Lua;

/// <summary>An explicit graph format; reference IDs preserve cycles and shared upvalues.</summary>
internal static class LuaStateBinary
{
    enum Kind : byte { Table, Closure, Native, Userdata, Thread, Upvalue, Proto }

    public static void Write(BinaryWriter writer, LuaStateSnapshot snapshot)
    {
        var graph = new Writer(writer, snapshot.Copy);
        graph.Reference(snapshot.Copy.MainThread);
        graph.Reference(snapshot.Copy.Globals);
    }

    public static LuaStateSnapshot Read(BinaryReader reader, LuaState state)
    {
        var graph = new Reader(reader, state);
        if (!ReferenceEquals(graph.Reference(), state.MainThread))
            throw new InvalidDataException("Missing main Lua thread.");
        state.Globals = (LuaTable)graph.Reference()!;
        state.CurrentThread = state.MainThread;
        return new LuaStateSnapshot(state);
    }

    sealed class Writer(BinaryWriter w, LuaState state)
    {
        readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        public void Value(LuaValue value)
        {
            w.Write((byte)value.Type);
            switch (value.Type)
            {
                case LuaType.Nil: break;
                case LuaType.Boolean: case LuaType.Number: w.Write(value.N); break;
                case LuaType.String: StateIO.Text(w, value.AsString!); break;
                case LuaType.LightUserdata: throw new InvalidDataException("Light userdata cannot be saved.");
                default: Reference(value.O); break;
            }
        }
        public void Reference(object? value)
        {
            if (value is null) { w.Write(0); return; }
            if (_ids.TryGetValue(value, out int known)) { w.Write(known); return; }
            int id = _ids.Count + 1; _ids.Add(value, id); w.Write(-id);
            switch (value)
            {
                case LuaTable table:
                    w.Write((byte)Kind.Table); Reference(table.Metatable);
                    var size = BaseLib.SavedTableSize(table); w.Write(size.HasValue); if (size.HasValue) w.Write(size.Value);
                    StateIO.Array(w, table.Pairs(), pair => { Value(pair.Key); Value(pair.Value); }); break;
                case LuaClosure closure:
                    w.Write((byte)Kind.Closure); Reference(closure.Proto); Reference(closure.Env);
                    StateIO.Array(w, closure.Upvalues, Reference); break;
                case LuaNative native:
                    w.Write((byte)Kind.Native); StateIO.Text(w, native.Name);
                    if (native.WrappedThread is { } wrapped) { w.Write((byte)1); Reference(wrapped); }
                    else if (native.IteratorState is { } getIterator)
                    {
                        w.Write((byte)2); var iterator = getIterator();
                        StateIO.Text(w, iterator.Source); StateIO.Text(w, iterator.Pattern); w.Write(iterator.Position);
                    }
                    else w.Write((byte)0);
                    Reference(native.Env); break;
                case LuaUserdata data:
                    w.Write((byte)Kind.Userdata); w.Write(data.Payload is not null);
                    if (data.Payload is not null)
                        StateIO.Bytes(w, data.Payload as byte[] ?? throw new InvalidDataException("Unknown userdata payload."));
                    Reference(data.Metatable); Reference(data.Env); break;
                case UpVal upvalue:
                    w.Write((byte)Kind.Upvalue); Reference(upvalue.Thread); w.Write(upvalue.Index);
                    if (!upvalue.IsOpen) Value(upvalue.Value); break;
                case LuaThread thread:
                    w.Write((byte)Kind.Thread); w.Write(ReferenceEquals(thread, state.MainThread));
                    int used = Math.Max(thread.Top, thread.FrameCount == 0 ? 0 : thread.Frames.Take(thread.FrameCount).Max(f => f.Top));
                    StateIO.Array(w, thread.Stack.Take(used), Value); w.Write(thread.Top);
                    StateIO.Array(w, thread.Frames.Take(thread.FrameCount), f =>
                    {
                        Reference(f.Closure); Reference(f.Native); w.Write(f.Func); w.Write(f.Base); w.Write(f.Top);
                        w.Write(f.Pc); w.Write(f.Want); w.Write(f.Boundary);
                    });
                    StateIO.Array(w, thread.OpenUpvals, Reference); w.Write((int)thread.Status); Reference(thread.StartFunction);
                    w.Write(thread.Started); w.Write(thread.ResumeContinuation); w.Write(thread.NativeDepth); w.Write(thread.Yielding);
                    w.Write(thread.YieldFunc); w.Write(thread.YieldWant); w.Write(thread.TransferValues is not null);
                    if (thread.TransferValues is { } transfers) StateIO.Array(w, transfers, Value); break;
                case LuaProto proto:
                    w.Write((byte)Kind.Proto); StateIO.Text(w, proto.Source); w.Write(proto.LineDefined);
                    w.Write(proto.NumUpvalues); w.Write(proto.NumParams); w.Write(proto.IsVararg); w.Write(proto.MaxStackSize);
                    StateIO.Array(w, proto.LineInfo, w.Write);
                    StateIO.Array(w, proto.Locals, l => { StateIO.Text(w, l.Name); w.Write(l.StartPc); w.Write(l.EndPc); });
                    StateIO.Array(w, proto.UpvalueNames, s => StateIO.Text(w, s)); StateIO.Array(w, proto.Constants, Value);
                    StateIO.Array(w, proto.Protos, Reference); StateIO.Array(w, proto.Code, w.Write); break;
                default: throw new InvalidDataException("Unknown Lua graph object.");
            }
        }
    }

    sealed class Reader(BinaryReader r, LuaState state)
    {
        readonly Dictionary<int, object> _ids = new();
        int _depth;
        public LuaValue Value() => (LuaType)r.ReadByte() switch
        {
            LuaType.Nil => LuaValue.Nil,
            LuaType.Boolean => LuaValue.Bool(r.ReadInt32() != 0),
            LuaType.Number => LuaValue.Number(r.ReadInt32()),
            LuaType.String => LuaValue.String(StateIO.Text(r)),
            LuaType.Table => LuaValue.Table((LuaTable)Reference()!),
            LuaType.Function => LuaValue.Function((LuaFunction)Reference()!),
            LuaType.Userdata => LuaValue.Userdata((LuaUserdata)Reference()!),
            LuaType.Thread => LuaValue.Thread((LuaThread)Reference()!),
            _ => throw new InvalidDataException("Invalid Lua value type."),
        };
        public object? Reference()
        {
            if (++_depth > 512) throw new InvalidDataException("Save-state graph is too deep.");
            try { return ReadReference(); } finally { _depth--; }
        }
        object? ReadReference()
        {
            int id = r.ReadInt32(); if (id == 0) return null;
            if (id > 0) return _ids.TryGetValue(id, out var known) ? known : throw new InvalidDataException("Invalid graph reference.");
            id = checked(-id); if (_ids.ContainsKey(id) || _ids.Count >= 1 << 22) throw new InvalidDataException("Invalid object ID.");
            T Register<T>(T value) where T : class { _ids.Add(id, value); return value; }
            switch ((Kind)r.ReadByte())
            {
                case Kind.Table:
                    var table = Register(new LuaTable()); table.Metatable = (LuaTable?)Reference();
                    if (r.ReadBoolean()) BaseLib.SetN(table, r.ReadInt32());
                    int count = StateIO.Count(r); for (int i = 0; i < count; i++) { var key = Value(); table.Set(key, Value()); }
                    return table;
                case Kind.Closure:
                    var proto = (LuaProto)Reference()!; var closure = Register(new LuaClosure(proto, new LuaTable()));
                    closure.Env = (LuaTable)Reference()!;
                    var upvalues = StateIO.Array(r, () => (UpVal)Reference()!, 255);
                    if (upvalues.Length != closure.Upvalues.Length) throw new InvalidDataException("Invalid upvalue count.");
                    upvalues.CopyTo(closure.Upvalues, 0); return closure;
                case Kind.Native:
                    string name = StateIO.Text(r); LuaNative? target = null;
                    var native = Register(new LuaNative(name, a => target!.Fn(a), clone => target!.CloneForSnapshot(clone)));
                    byte mode = r.ReadByte();
                    if (mode == 1) { var co = (LuaThread)Reference()!; target = BaseLib.WrapCoroutine(co); native.WrappedThread = co; }
                    else if (mode == 2)
                    {
                        target = StringLib.GFindIterator(StateIO.Text(r), StateIO.Text(r), r.ReadInt32()); native.IteratorState = target.IteratorState;
                    }
                    else if (mode == 0) target = state.NativeFunctions.GetValueOrDefault(name) ?? throw new InvalidDataException("Unknown native function: " + name);
                    else throw new InvalidDataException("Invalid native function type.");
                    native.Env = (LuaTable?)Reference(); return native;
                case Kind.Userdata:
                    var data = Register(new LuaUserdata(null)); if (r.ReadBoolean()) data.Payload = StateIO.Bytes(r);
                    data.Metatable = (LuaTable?)Reference(); data.Env = (LuaTable?)Reference(); return data;
                case Kind.Upvalue:
                    var upvalue = Register(new UpVal(LuaValue.Nil)); var owner = (LuaThread?)Reference();
                    upvalue.Bind(owner, r.ReadInt32()); if (owner is null) upvalue.Value = Value(); return upvalue;
                case Kind.Thread:
                    var thread = Register(r.ReadBoolean() ? state.MainThread : new LuaThread(state));
                    var stack = StateIO.Array(r, Value); thread.EnsureStack(stack.Length); stack.CopyTo(thread.Stack, 0);
                    thread.Top = StateIO.Count(r, stack.Length);
                    thread.Frames = StateIO.Array(r, () => new CallFrame
                    {
                        Closure = (LuaClosure?)Reference(),
                        Native = (LuaNative?)Reference(),
                        Func = r.ReadInt32(),
                        Base = r.ReadInt32(),
                        Top = r.ReadInt32(),
                        Pc = r.ReadInt32(),
                        Want = r.ReadInt32(),
                        Boundary = r.ReadBoolean(),
                    }, 800); thread.FrameCount = thread.Frames.Length;
                    foreach (var frame in thread.Frames)
                        if (frame.Func < 0 || frame.Base < 0 || frame.Top < 0 || frame.Top > stack.Length || (frame.Closure is { } cl && (frame.Pc < 0 || frame.Pc > cl.Proto.Code.Length)))
                            throw new InvalidDataException("Invalid Lua call frame.");
                    if (thread.Frames.Length == 0) thread.Frames = new CallFrame[16];
                    thread.OpenUpvals.AddRange(StateIO.Array(r, () => (UpVal)Reference()!)); thread.Status = (CoroutineStatus)r.ReadInt32();
                    thread.StartFunction = (LuaFunction?)Reference(); thread.Started = r.ReadBoolean(); thread.ResumeContinuation = r.ReadBoolean(); thread.NativeDepth = r.ReadInt32();
                    thread.Yielding = r.ReadBoolean(); thread.YieldFunc = r.ReadInt32(); thread.YieldWant = r.ReadInt32();
                    thread.TransferValues = r.ReadBoolean() ? StateIO.Array(r, Value) : null; return thread;
                case Kind.Proto:
                    var p = Register(new LuaProto()); p.Source = StateIO.Text(r); p.LineDefined = r.ReadInt32();
                    p.NumUpvalues = StateIO.Count(r, 255); p.NumParams = StateIO.Count(r, 255); p.IsVararg = r.ReadBoolean(); p.MaxStackSize = StateIO.Count(r, 255);
                    p.LineInfo = StateIO.Array(r, r.ReadInt32); p.Locals = StateIO.Array(r, () => new LuaProto.LocalVar(StateIO.Text(r), r.ReadInt32(), r.ReadInt32()));
                    p.UpvalueNames = StateIO.Array(r, () => StateIO.Text(r)); p.Constants = StateIO.Array(r, Value);
                    p.Protos = StateIO.Array(r, () => (LuaProto)Reference()!); p.Code = StateIO.Array(r, r.ReadUInt32); return p;
                default: throw new InvalidDataException("Invalid graph object type.");
            }
        }
    }
}
