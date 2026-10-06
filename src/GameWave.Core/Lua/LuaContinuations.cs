namespace GameWave.Lua;

// These callbacks unwind only the short dispatch function, before entering Lua.
// No managed continuation survives an emulation boundary.
internal sealed class PendingLuaCall(LuaValue function, LuaValue[] arguments) : Exception
{
    public LuaValue Function = function;
    public LuaValue[] Arguments = arguments;
}

public sealed partial class LuaThread
{
    internal int ScheduleCallback(PendingLuaCall call, int want = 1)
    {
        var frame = CurrentFrame!;
        Top = frame.Top;
        Push(call.Function);
        foreach (var argument in call.Arguments) Push(argument);
        PreCall(frame.Top, want);
        return -1;
    }

    internal LuaValue CallbackResult => Stack[CurrentFrame!.Top];

    internal T Dispatch<T>(Func<T> action)
    {
        CaptureCallback = true;
        try { return action(); }
        finally { CaptureCallback = false; }
    }

    internal void StartOperation(string operation, int destination, params LuaValue[] values)
    {
        var parent = CurrentFrame!;
        Top = Math.Max(Top, parent.Top);
        int func = Top;
        Push(State.NativeFunctions["vm.operation"]);
        var args = new LuaTable();
        for (int i = 0; i < values.Length; i++) args[i + 1] = values[i];
        var state = new LuaTable();
        state["operation"] = operation; state["destination"] = destination;
        state["arguments"] = args; state["count"] = values.Length;
        var frame = PushFrame();
        frame.Native = State.NativeFunctions["vm.operation"];
        frame.Func = func; frame.Base = frame.Top = Top; frame.Want = 0;
        frame.Continuation = state;
    }
}

internal static class LuaContinuations
{
    internal static void Open(LuaState state)
        => state.NativeFunctions["vm.operation"] = new LuaNative("vm.operation", Operation);

    static int Operation(LuaArgs a)
    {
        var thread = a.L;
        var frame = thread.CurrentFrame!;
        var state = frame.Continuation!;
        var arguments = state["arguments"].AsTable!;
        string op = state["operation"].AsString!;
        int destination = state["destination"].N;
        var parent = thread.Frames[thread.FrameCount - 2];
        LuaValue result;
        if (op == "iterator")
        {
            int count = arguments[4].N;
            if (state["waiting"].IsNil)
            {
                state["waiting"] = true;
                return thread.ScheduleCallback(new(arguments[1], [arguments[2], arguments[3]]), count);
            }
            for (int i = 0; i < count; i++) thread.Stack[destination + 2 + i] = thread.Stack[frame.Top + i];
            Branch(parent, !thread.Stack[destination + 2].IsNil, invert: true);
            return 0;
        }
        if (op == "concat")
        {
            int index = state["index"].IsNil ? state["count"].N - 1 : state["index"].N;
            var accumulator = state["accumulator"].IsNil ? arguments[state["count"].N] : state["accumulator"];
            if (!state["waiting"].IsNil)
            {
                accumulator = thread.CallbackResult; index--;
                state["waiting"] = LuaValue.Nil;
            }
            while (index >= 1)
            {
                state["index"] = index; state["accumulator"] = accumulator;
                try
                {
                    var pair = new[] { arguments[index], accumulator };
                    accumulator = thread.Dispatch(() => a.State.Concat(pair, 0, 1)); index--;
                }
                catch (PendingLuaCall call)
                {
                    state["waiting"] = true;
                    return thread.ScheduleCallback(call);
                }
            }
            result = accumulator;
        }
        else if (!state["waiting"].IsNil)
        {
            result = thread.CallbackResult;
            if (state["negate"].IsTruthy) result = LuaValue.Bool(!result.IsTruthy);
        }
        else
        {
            try
            {
                if (op == "le")
                {
                    var handler = a.State.GetMetamethod(arguments[1], "__le");
                    state["negate"] = handler.IsNil || !handler.Equals(a.State.GetMetamethod(arguments[2], "__le"));
                }
                result = thread.Dispatch(() => op switch
                {
                    "index" => a.State.Index(arguments[1], arguments[2]),
                    "set" => Set(a.State, arguments),
                    "eq" => LuaValue.Bool(a.State.ValuesEqual(arguments[1], arguments[2])),
                    "lt" => LuaValue.Bool(a.State.LessThan(arguments[1], arguments[2])),
                    "le" => LuaValue.Bool(a.State.LessEqual(arguments[1], arguments[2])),
                    _ => a.State.Arith((OpCode)arguments[3].N, arguments[1], arguments[2]),
                });
            }
            catch (PendingLuaCall call)
            {
                state["waiting"] = true;
                return thread.ScheduleCallback(call);
            }
        }
        if (op is "eq" or "lt" or "le") Branch(parent, result.IsTruthy, state["arguments"].AsTable![3].IsTruthy);
        else if (op != "set") thread.Stack[destination] = result;
        return 0;
    }

    static LuaValue Set(LuaState state, LuaTable arguments)
    {
        state.SetIndex(arguments[1], arguments[2], arguments[3]); return LuaValue.Nil;
    }

    static void Branch(CallFrame parent, bool result, bool invert)
    {
        int pc = parent.Pc;
        parent.Pc = result != invert ? pc + 1 : pc + Instr.SBx(parent.Closure!.Proto.Code[pc]) + 1;
    }

    internal static int ToString(LuaArgs a, bool print)
    {
        var thread = a.L;
        var state = thread.CurrentFrame!.Continuation ??= new LuaTable();
        int index = state["index"].IsNil ? 1 : state["index"].N;
        string text = state["text"].AsString ?? "";
        while (index <= (print ? a.Count : 1))
        {
            string value;
            if (state["waiting"].IsTruthy)
            {
                if (!thread.CallbackResult.TryToStr(out value)) throw new LuaException("`tostring' must return a string");
                state["waiting"] = false;
            }
            else
            {
                try { value = thread.Dispatch(() => a.State.ToStringMeta(a.Any(index))); }
                catch (PendingLuaCall call)
                {
                    state["index"] = index; state["text"] = text; state["waiting"] = true;
                    return thread.ScheduleCallback(call);
                }
            }
            text += (index > 1 ? "\t" : "") + value; index++;
        }
        if (print) { a.State.Output(text); return 0; }
        return a.Return(text);
    }

    internal static int ForEach(LuaArgs a, bool array)
    {
        var thread = a.L;
        var table = a.Table(1);
        var function = LuaValue.Function(a.Function(2));
        var state = thread.CurrentFrame!.Continuation;
        if (state is null)
        {
            state = thread.CurrentFrame.Continuation = new LuaTable();
            var entries = new LuaTable();
            int count = 0;
            if (array)
            {
                count = BaseLib.GetN(table);
                for (int i = 1; i <= count; i++) entries[i] = i;
            }
            else foreach (var entry in table.Pairs().ToList())
            {
                var pair = new LuaTable(); pair[1] = entry.Key; pair[2] = entry.Value; entries[++count] = pair;
            }
            state["entries"] = entries; state["count"] = count; state["index"] = 1;
        }
        else
        {
            var result = thread.CallbackResult;
            if (!result.IsNil) return a.Return(result);
            state["index"] = state["index"].N + 1;
        }
        int index = state["index"].N;
        if (index > state["count"].N) return 0;
        var entryValue = state["entries"].AsTable![index];
        var pairValue = entryValue.AsTable;
        return thread.ScheduleCallback(new(function, array ? [index, table.Get(index)] : [pairValue![1], pairValue[2]]));
    }

    // Lua 5.0 auxsort, with the recursive ranges and comparison phase on the Lua graph.
    internal static int Sort(LuaArgs a)
    {
        var thread = a.L;
        var table = a.Table(1);
        var state = thread.CurrentFrame!.Continuation ??= new LuaTable();
        if (state["ranges"].IsNil)
        {
            state["ranges"] = new LuaTable(); state["depth"] = 0;
            state["l"] = 1; state["u"] = BaseLib.GetN(table); state["phase"] = 0;
        }
        bool result = thread.CallbackResult.IsTruthy;
        while (true)
        {
            int l = state["l"].N, u = state["u"].N, i = state["i"].N, j = state["j"].N;
            int phase = state["phase"].N;
            LuaValue x = LuaValue.Nil, y = LuaValue.Nil;
            bool compare = false;
            void Swap(int first, int second)
            {
                var saved = table.Get(first); table[first] = table.Get(second); table[second] = saved;
            }
            switch (phase)
            {
                case 0:
                    if (l >= u)
                    {
                        int depth = state["depth"].N;
                        if (depth == 0) return 0;
                        var ranges = state["ranges"].AsTable!;
                        var range = ranges[depth].AsTable!;
                        state["l"] = range[1]; state["u"] = range[2];
                        ranges[depth] = LuaValue.Nil; state["depth"] = depth - 1;
                        continue;
                    }
                    x = table.Get(u); y = table.Get(l); state["phase"] = 1; compare = true; break;
                case 1:
                    if (result) Swap(l, u);
                    if (u - l == 1) { state["l"] = u; state["phase"] = 0; continue; }
                    i = (l + u) / 2; state["i"] = i;
                    x = table.Get(i); y = table.Get(l); state["phase"] = 2; compare = true; break;
                case 2:
                    if (result) { Swap(i, l); state["phase"] = 4; continue; }
                    x = table.Get(u); y = table.Get(i); state["phase"] = 3; compare = true; break;
                case 3:
                    if (result) Swap(i, u);
                    state["phase"] = 4; continue;
                case 4:
                    if (u - l == 2) { state["l"] = u; state["phase"] = 0; continue; }
                    state["pivot"] = table.Get(i); Swap(i, u - 1);
                    state["i"] = l; state["j"] = u - 1; state["phase"] = 5; continue;
                case 5:
                    state["i"] = ++i;
                    x = table.Get(i); y = state["pivot"]; state["phase"] = 6; compare = true; break;
                case 6:
                    if (result)
                    {
                        if (i > u) throw new LuaException("invalid order function for sorting");
                        state["phase"] = 5;
                    }
                    else state["phase"] = 7;
                    continue;
                case 7:
                    state["j"] = --j;
                    x = state["pivot"]; y = table.Get(j); state["phase"] = 8; compare = true; break;
                case 8:
                    if (result)
                    {
                        if (j < l) throw new LuaException("invalid order function for sorting");
                        state["phase"] = 7; continue;
                    }
                    if (j < i) { state["phase"] = 9; continue; }
                    Swap(i, j); state["phase"] = 5; continue;
                case 9:
                {
                    Swap(u - 1, i);
                    var range = new LuaTable();
                    if (i - l < u - i)
                    {
                        range[1] = i + 1; range[2] = u;
                        state["u"] = i - 1;
                    }
                    else
                    {
                        range[1] = l; range[2] = i - 1;
                        state["l"] = i + 1;
                    }
                    int depth = state["depth"].N + 1;
                    state["ranges"].AsTable![depth] = range; state["depth"] = depth;
                    state["phase"] = 0; continue;
                }
            }
            if (!compare) throw new LuaException("invalid sort continuation");
            try
            {
                result = thread.Dispatch(() => a[2].IsNil
                    ? a.State.LessThan(x, y) : thread.Call1(a[2], x, y).IsTruthy);
            }
            catch (PendingLuaCall call) { return thread.ScheduleCallback(call); }
        }
    }
}
