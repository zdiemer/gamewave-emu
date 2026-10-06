namespace GameWave.Lua;

/// <summary>
/// An in-process copy of a Lua universe. Host callbacks are shared unless they carry Lua
/// iterator or coroutine state; all Lua-owned mutable objects retain their reference graph.
/// </summary>
internal sealed class LuaStateSnapshot
{
    readonly LuaState _copy;

    internal LuaState Copy => _copy;

    internal LuaStateSnapshot(LuaState copy) => _copy = copy;

    public static LuaStateSnapshot Capture(LuaState source)
    {
        var copy = new LuaState();
        new GraphCopier(source, copy).CopyState(normalizeNativeCall: true);
        return new LuaStateSnapshot(copy);
    }

    public void Restore(LuaState destination)
        => new GraphCopier(_copy, destination).CopyState(normalizeNativeCall: false);

    sealed class GraphCopier
    {
        readonly LuaState _source;
        readonly LuaState _destination;
        readonly Dictionary<object, object> _objects = new(ReferenceEqualityComparer.Instance);

        public GraphCopier(LuaState source, LuaState destination)
        {
            _source = source;
            _destination = destination;
            _objects[source.MainThread] = destination.MainThread;
        }

        public void CopyState(bool normalizeNativeCall)
        {
            _destination.Globals = Table(_source.Globals);
            CopyThread(_source.MainThread, _destination.MainThread);
            _destination.CurrentThread = _destination.MainThread;

            if (normalizeNativeCall)
            {
                RewindNativeCalls(_destination.MainThread);
                foreach (var pair in _objects.ToArray())
                    if (pair.Key is LuaThread from && pair.Value is LuaThread to
                        && from != _source.MainThread && from.Status is CoroutineStatus.Running or CoroutineStatus.Normal)
                    {
                        if (from.NativeDepth != 0)
                            throw new InvalidOperationException("The coroutine is inside a nested Lua callback.");
                        RewindNativeCalls(to);
                        to.Status = CoroutineStatus.Suspended;
                        to.ResumeContinuation = true;
                    }
            }
        }

        static void RewindNativeCalls(LuaThread thread)
        {
            // A blocking host API (WaitForKey, Sleep, pause) may be where the game thread
            // notices a quicksave. It cannot be resumed in the middle of C# code, so retain
            // its arguments and make Lua execute the CALL again after a quickload.
            while (thread.FrameCount > 0 && thread.Frames[thread.FrameCount - 1].Native is not null)
            {
                var native = thread.Frames[--thread.FrameCount];
                thread.Top = native.Top;
                if (thread.FrameCount > 0)
                    thread.Frames[thread.FrameCount - 1].Pc--;
            }
        }

        LuaValue Value(LuaValue value) => value.Type switch
        {
            LuaType.Table => LuaValue.Table(Table((LuaTable)value.O!)),
            LuaType.Function => LuaValue.Function(Function((LuaFunction)value.O!)),
            LuaType.Userdata => LuaValue.Userdata(Userdata((LuaUserdata)value.O!)),
            LuaType.Thread => LuaValue.Thread(Thread((LuaThread)value.O!)),
            // The engine does not create light userdata. Keep identity for compatibility
            // with embedders that do.
            _ => value,
        };

        LuaTable Table(LuaTable source)
        {
            if (_objects.TryGetValue(source, out var known))
                return (LuaTable)known;
            var copy = new LuaTable();
            _objects[source] = copy;
            copy.Metatable = source.Metatable is null ? null : Table(source.Metatable);
            foreach (var pair in source.Pairs())
                copy.Set(Value(pair.Key), Value(pair.Value));
            BaseLib.CopyTableSize(source, copy);
            return copy;
        }

        LuaFunction Function(LuaFunction source)
        {
            if (_objects.TryGetValue(source, out var known))
                return (LuaFunction)known;
            if (source is LuaNative native)
            {
                LuaNative? target = null;
                var nativeCopy = new LuaNative(native.Name, args => target!.Fn(args), clone => target!.CloneForSnapshot(clone));
                _objects[source] = nativeCopy;
                target = native.CloneForSnapshot(Thread);
                nativeCopy.Env = native.Env is null ? null : Table(native.Env);
                nativeCopy.WrappedThread = target.WrappedThread;
                nativeCopy.IteratorState = target.IteratorState;
                return nativeCopy;
            }

            var closure = (LuaClosure)source;
            var copy = new LuaClosure(closure.Proto, new LuaTable());
            _objects[source] = copy;
            copy.Env = Table(closure.Env);
            for (int i = 0; i < closure.Upvalues.Length; i++)
                copy.Upvalues[i] = Upvalue(closure.Upvalues[i]);
            return copy;
        }

        UpVal Upvalue(UpVal source)
        {
            if (_objects.TryGetValue(source, out var known))
                return (UpVal)known;
            var copy = new UpVal(LuaValue.Nil);
            _objects[source] = copy;
            if (source.Thread is { } owner)
                copy.Bind(Thread(owner), source.Index);
            else
                copy.Value = Value(source.Value);
            return copy;
        }

        LuaUserdata Userdata(LuaUserdata source)
        {
            if (_objects.TryGetValue(source, out var known))
                return (LuaUserdata)known;
            var payload = source.Payload is byte[] bytes ? (byte[])bytes.Clone() : source.Payload;
            var copy = new LuaUserdata(payload);
            _objects[source] = copy;
            copy.Metatable = source.Metatable is null ? null : Table(source.Metatable);
            copy.Env = source.Env is null ? null : Table(source.Env);
            return copy;
        }

        LuaThread Thread(LuaThread source)
        {
            if (_objects.TryGetValue(source, out var known))
                return (LuaThread)known;
            var copy = new LuaThread(_destination);
            _objects[source] = copy;
            CopyThread(source, copy);
            return copy;
        }

        void CopyThread(LuaThread source, LuaThread copy)
        {
            copy.EnsureStack(source.Stack.Length);
            Array.Clear(copy.Stack);
            int used = Math.Max(source.Top,
                source.FrameCount == 0 ? 0 : source.Frames.Take(source.FrameCount).Max(f => f.Top));
            for (int i = 0; i < used; i++)
                copy.Stack[i] = Value(source.Stack[i]);
            copy.Top = source.Top;

            if (copy.Frames.Length < source.FrameCount)
                Array.Resize(ref copy.Frames, source.FrameCount);
            copy.FrameCount = source.FrameCount;
            for (int i = 0; i < source.FrameCount; i++)
            {
                var from = source.Frames[i];
                var to = copy.Frames[i] ??= new CallFrame();
                to.Closure = from.Closure is null ? null : (LuaClosure)Function(from.Closure);
                to.Native = from.Native;
                to.Func = from.Func;
                to.Base = from.Base;
                to.Top = from.Top;
                to.Pc = from.Pc;
                to.Want = from.Want;
                to.Boundary = from.Boundary;
            }

            copy.OpenUpvals.Clear();
            foreach (var upvalue in source.OpenUpvals)
                copy.OpenUpvals.Add(Upvalue(upvalue));
            copy.Status = source.Status;
            copy.StartFunction = source.StartFunction is null ? null : Function(source.StartFunction);
            copy.Started = source.Started;
            copy.ResumeContinuation = source.ResumeContinuation;
            copy.NativeDepth = source.NativeDepth;
            copy.Yielding = source.Yielding;
            copy.YieldFunc = source.YieldFunc;
            copy.YieldWant = source.YieldWant;
            copy.TransferValues = source.TransferValues?.Select(Value).ToArray();
        }
    }
}
