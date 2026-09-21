namespace GameWave.Lua;

public enum CoroutineStatus
{
    Suspended,
    Running,
    Normal,
    Dead,
}

internal sealed class CallFrame
{
    public LuaClosure? Closure;
    public LuaNative? Native;
    public int Func;
    public int Base;
    public int Top;
    public int Pc;
    public int Want;
    /// <summary>The Execute loop that entered this frame returns when it returns.</summary>
    public bool Boundary;
}

/// <summary>A Lua thread: the main thread or a coroutine, with its own stack and call frames.</summary>
public sealed partial class LuaThread
{
    public const int MultRet = -1;
    const int ExtraStack = 24;

    public LuaState State { get; }
    internal LuaValue[] Stack = new LuaValue[256];
    public int Top;

    internal CallFrame[] Frames = new CallFrame[16];
    internal int FrameCount;

    internal readonly List<UpVal> OpenUpvals = new();

    public CoroutineStatus Status { get; internal set; } = CoroutineStatus.Suspended;
    internal LuaFunction? StartFunction;
    internal bool Started;
    /// <summary>How many native-to-Lua call boundaries are live on this thread.</summary>
    internal int NativeDepth;
    internal bool Yielding;
    internal int YieldFunc;
    internal int YieldWant;
    internal LuaValue[]? TransferValues;

    internal LuaThread(LuaState state)
    {
        State = state;
    }

    // ---------------------------------------------------------------- stack helpers

    public void EnsureStack(int needed)
    {
        if (needed + ExtraStack > Stack.Length)
        {
            int n = Stack.Length;
            while (n < needed + ExtraStack)
                n *= 2;
            if (n > 1 << 22)
                throw new LuaException("stack overflow");
            Array.Resize(ref Stack, n);
        }
    }

    public void Push(LuaValue v)
    {
        if (Top >= Stack.Length - ExtraStack)
            EnsureStack(Top + 1);
        Stack[Top++] = v;
    }

    internal CallFrame PushFrame()
    {
        if (FrameCount == Frames.Length)
        {
            if (FrameCount >= 200 * 4)
                throw new LuaException("stack overflow");
            Array.Resize(ref Frames, Frames.Length * 2);
        }
        var f = Frames[FrameCount] ??= new CallFrame();
        FrameCount++;
        f.Closure = null;
        f.Native = null;
        f.Boundary = false;
        f.Pc = 0;
        return f;
    }

    internal CallFrame? CurrentFrame => FrameCount > 0 ? Frames[FrameCount - 1] : null;

    // ---------------------------------------------------------------- upvalues

    internal UpVal FindUpval(int index)
    {
        for (int i = OpenUpvals.Count - 1; i >= 0; i--)
        {
            var u = OpenUpvals[i];
            if (u.Index == index)
                return u;
            if (u.Index < index)
            {
                var n = new UpVal(this, index);
                OpenUpvals.Insert(i + 1, n);
                return n;
            }
        }
        var created = new UpVal(this, index);
        OpenUpvals.Insert(0, created);
        return created;
    }

    internal void CloseUpvals(int level)
    {
        while (OpenUpvals.Count > 0)
        {
            var u = OpenUpvals[^1];
            if (u.Index < level)
                break;
            u.Close();
            OpenUpvals.RemoveAt(OpenUpvals.Count - 1);
        }
    }

    // ---------------------------------------------------------------- calls

    /// <summary>
    /// Starts a call of the value at <paramref name="func"/> with the arguments above it up
    /// to <see cref="Top"/>. A native function runs to completion here; a Lua function gets a
    /// new frame and returns true so the caller can run it.
    /// </summary>
    internal bool PreCall(int func, int want)
    {
        var fv = Stack[func];
        if (fv.O is not LuaFunction fn)
        {
            fn = State.CallMetamethod(fv) ?? throw State.TypeError(fv, "call");
            // Shift the arguments up and put the __call handler first.
            EnsureStack(Top + 1);
            for (int i = Top; i > func; i--)
                Stack[i] = Stack[i - 1];
            Top++;
            Stack[func] = LuaValue.Function(fn);
        }

        if (fn is LuaClosure cl)
        {
            var p = cl.Proto;
            int nargs = Top - func - 1;
            int @base = func + 1;
            EnsureStack(@base + p.MaxStackSize + 1);
            if (nargs < p.NumParams)
            {
                for (int i = nargs; i < p.NumParams; i++)
                    Stack[@base + i] = LuaValue.Nil;
                nargs = p.NumParams;
            }
            if (p.IsVararg)
            {
                int extra = nargs - p.NumParams;
                var arg = new LuaTable(Math.Max(extra, 0), 1);
                for (int i = 0; i < extra; i++)
                    arg[i + 1] = Stack[@base + p.NumParams + i];
                arg["n"] = extra;
                Stack[@base + p.NumParams] = LuaValue.Table(arg);
                Top = @base + p.NumParams + 1;
            }
            else
            {
                Top = @base + p.NumParams;
            }
            int frameTop = @base + p.MaxStackSize;
            for (int i = Top; i < frameTop; i++)
                Stack[i] = LuaValue.Nil;

            var f = PushFrame();
            f.Closure = cl;
            f.Func = func;
            f.Base = @base;
            f.Top = frameTop;
            f.Want = want;
            Top = frameTop;
            return true;
        }

        var native = (LuaNative)fn;
        {
            EnsureStack(Top + 20);
            var f = PushFrame();
            f.Native = native;
            f.Func = func;
            f.Base = func + 1;
            f.Top = Top;
            f.Want = want;
            int n = native.Fn(new LuaArgs(this, func + 1, Top - func - 1));
            if (Yielding)
            {
                // coroutine.yield: keep the values for Resume and leave the frame.
                TransferValues = new LuaValue[n];
                Array.Copy(Stack, Top - n, TransferValues, 0, n);
                FrameCount--;
                YieldFunc = func;
                YieldWant = want;
                Top = func;
                return false;
            }
            PostCall(Top - n, n);
            return false;
        }
    }

    /// <summary>Moves results into place for the frame being left and pops it.</summary>
    internal void PostCall(int firstResult, int count)
    {
        var f = Frames[--FrameCount];
        int res = f.Func;
        int want = f.Want;
        if (want == MultRet)
        {
            for (int i = 0; i < count; i++)
                Stack[res + i] = Stack[firstResult + i];
            Top = res + count;
        }
        else
        {
            EnsureStack(res + want);
            int i = 0;
            for (; i < want && i < count; i++)
                Stack[res + i] = Stack[firstResult + i];
            for (; i < want; i++)
                Stack[res + i] = LuaValue.Nil;
            Top = res + want;
        }
    }

    /// <summary>
    /// Calls the function at <paramref name="func"/> with the arguments up to Top, leaving
    /// <paramref name="want"/> results (or all of them for <see cref="MultRet"/>) at func.
    /// This is a native boundary: Lua code it runs cannot yield out through it.
    /// </summary>
    public void Call(int func, int want)
    {
        if (PreCall(func, want))
        {
            Frames[FrameCount - 1].Boundary = true;
            NativeDepth++;
            try
            {
                Execute();
            }
            finally
            {
                NativeDepth--;
            }
        }
        else if (Yielding)
        {
            Yielding = false;
            throw new LuaException("attempt to yield across C-call boundary");
        }
    }

    /// <summary>Calls a value with the given arguments and returns all results.</summary>
    public LuaValue[] Call(LuaValue fn, params ReadOnlySpan<LuaValue> args)
    {
        int func = CallBase();
        EnsureStack(func + args.Length + 1);
        Stack[func] = fn;
        for (int i = 0; i < args.Length; i++)
            Stack[func + 1 + i] = args[i];
        Top = func + 1 + args.Length;
        int savedTop = func;
        Call(func, MultRet);
        var results = new LuaValue[Top - func];
        Array.Copy(Stack, func, results, 0, results.Length);
        Top = savedTop;
        RestoreFrameTop();
        return results;
    }

    /// <summary>Calls a value and returns just its first result.</summary>
    public LuaValue Call1(LuaValue fn, params ReadOnlySpan<LuaValue> args)
    {
        int func = CallBase();
        EnsureStack(func + args.Length + 1);
        Stack[func] = fn;
        for (int i = 0; i < args.Length; i++)
            Stack[func + 1 + i] = args[i];
        Top = func + 1 + args.Length;
        Call(func, 1);
        var r = Stack[func];
        Top = func;
        RestoreFrameTop();
        return r;
    }

    /// <summary>Where a nested call may put its function without clobbering live registers.</summary>
    int CallBase()
    {
        var f = CurrentFrame;
        if (f?.Closure is not null)
            return Math.Max(Top, f.Top);
        return Top;
    }

    void RestoreFrameTop()
    {
        var f = CurrentFrame;
        if (f?.Closure is not null && Top < f.Top)
            Top = f.Top;
    }

    /// <summary>
    /// Runs a protected call; on error the stack and frames are restored and the error is
    /// returned instead of thrown.
    /// </summary>
    public LuaException? PCall(int func, int want, LuaValue handler = default)
    {
        int savedFrames = FrameCount;
        int savedDepth = NativeDepth;
        try
        {
            Call(func, want);
            return null;
        }
        catch (LuaException e)
        {
            if (!handler.IsNil && handler.IsFunction)
            {
                try
                {
                    var hv = Call1ForHandler(handler, e.Value);
                    e = new LuaException(hv);
                }
                catch (LuaException e2)
                {
                    e = e2;
                }
            }
            CloseUpvals(func);
            FrameCount = savedFrames;
            NativeDepth = savedDepth;
            Yielding = false;
            Top = func;
            return e;
        }
    }

    LuaValue Call1ForHandler(LuaValue handler, LuaValue err)
    {
        // The handler runs on top of the failed frames so it can inspect them (traceback).
        int func = Top;
        EnsureStack(func + 2);
        Stack[func] = handler;
        Stack[func + 1] = err;
        Top = func + 2;
        Call(func, 1);
        return Stack[func];
    }

    // ---------------------------------------------------------------- coroutines

    public LuaValue[] Resume(LuaThread from, ReadOnlySpan<LuaValue> args, out bool ok)
    {
        if (Status != CoroutineStatus.Suspended)
        {
            ok = false;
            return [LuaValue.String(Status == CoroutineStatus.Dead
                ? "cannot resume dead coroutine"
                : "cannot resume non-suspended coroutine")];
        }
        var prev = State.CurrentThread;
        from.Status = CoroutineStatus.Normal;
        Status = CoroutineStatus.Running;
        State.CurrentThread = this;
        try
        {
            if (!Started)
            {
                Started = true;
                Top = 0;
                Push(LuaValue.Function(StartFunction!));
                foreach (var a in args)
                    Push(a);
                if (PreCall(0, MultRet))
                {
                    Frames[FrameCount - 1].Boundary = true;
                    Execute();
                }
            }
            else
            {
                // Hand the resume arguments back as the results of the yield call.
                int func = YieldFunc;
                int want = YieldWant;
                int n = args.Length;
                EnsureStack(func + Math.Max(n, want) + 1);
                if (want == MultRet)
                {
                    for (int i = 0; i < n; i++)
                        Stack[func + i] = args[i];
                    Top = func + n;
                }
                else
                {
                    for (int i = 0; i < want; i++)
                        Stack[func + i] = i < n ? args[i] : LuaValue.Nil;
                    var f = CurrentFrame!;
                    Top = f.Top;
                }
                if (FrameCount > 0)
                    Execute();
            }

            ok = true;
            if (Yielding)
            {
                Yielding = false;
                Status = CoroutineStatus.Suspended;
                var yv = TransferValues ?? [];
                TransferValues = null;
                return yv;
            }
            Status = CoroutineStatus.Dead;
            var results = new LuaValue[Top];
            Array.Copy(Stack, 0, results, 0, Top);
            Top = 0;
            return results;
        }
        catch (LuaException e)
        {
            Status = CoroutineStatus.Dead;
            ok = false;
            return [e.Value];
        }
        finally
        {
            State.CurrentThread = prev;
            from.Status = CoroutineStatus.Running;
        }
    }

    /// <summary>A traceback of the live frames, innermost first.</summary>
    public string Traceback(int skip = 0)
    {
        var sb = new System.Text.StringBuilder("stack traceback:");
        for (int i = FrameCount - 1 - skip; i >= 0; i--)
        {
            var f = Frames[i];
            if (f.Closure is { } cl)
                sb.Append($"\n\t{cl.Proto.ShortSource}:{cl.Proto.LineAt(f.Pc - 1)}: in function <{cl.Proto.ShortSource}:{cl.Proto.LineDefined}>");
            else if (f.Native is { } nf)
                sb.Append($"\n\t[C]: in function `{nf.Name}'");
        }
        return sb.ToString();
    }

    /// <summary>
    /// "source:line: " for the function <paramref name="level"/> frames up, where 0 is the
    /// running function (a native, when called from one) and 1 its caller.
    /// </summary>
    public string Where(int level)
    {
        int idx = FrameCount - 1 - level;
        if (idx >= 0 && idx < FrameCount && Frames[idx].Closure is { } cl)
            return $"{cl.Proto.ShortSource}:{cl.Proto.LineAt(Frames[idx].Pc - 1)}: ";
        return "";
    }
}
