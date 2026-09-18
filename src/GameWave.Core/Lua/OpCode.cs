using System.Text;

namespace GameWave.Lua;

public enum OpCode : byte
{
    Move, LoadK, LoadBool, LoadNil, GetUpval, GetGlobal, GetTable, SetGlobal, SetUpval,
    SetTable, NewTable, Self, Add, Sub, Mul, Div, Pow, Unm, Not, Concat, Jmp, Eq, Lt, Le,
    Test, Call, TailCall, Return, ForLoop, TForLoop, TForPrep, SetList, SetListO, Close,
    Closure,
}

/// <summary>Lua 5.0 instruction fields: OP 6 bits, C 9, B 9, A 8, from the low bit up.</summary>
public static class Instr
{
    public const int MaxStack = 250;
    public const int MaxArgSBx = 131071;
    public const int FieldsPerFlush = 32;

    public static OpCode Op(uint i) => (OpCode)(i & 0x3F);
    public static int A(uint i) => (int)(i >> 24);
    public static int B(uint i) => (int)((i >> 15) & 0x1FF);
    public static int C(uint i) => (int)((i >> 6) & 0x1FF);
    public static int Bx(uint i) => (int)((i >> 6) & 0x3FFFF);
    public static int SBx(uint i) => Bx(i) - MaxArgSBx;
}

public static class Disassembler
{
    public static void Dump(LuaProto p, TextWriter w, string indent = "")
    {
        w.WriteLine($"{indent}function <{p.ShortSource}:{p.LineDefined}> params={p.NumParams}{(p.IsVararg ? "+" : "")} stack={p.MaxStackSize} upvals={p.NumUpvalues} consts={p.Constants.Length} code={p.Code.Length}");
        for (int pc = 0; pc < p.Code.Length; pc++)
            w.WriteLine($"{indent}  {pc + 1,5} [{p.LineAt(pc),4}] {Format(p, pc)}");
        foreach (var child in p.Protos)
        {
            w.WriteLine();
            Dump(child, w, indent + "  ");
        }
    }

    static string K(LuaProto p, int k)
    {
        if (k < 0 || k >= p.Constants.Length)
            return $"K{k}?";
        var v = p.Constants[k];
        return v.IsString ? Quote(v.AsString!) : v.ToString();
    }

    static string RK(LuaProto p, int x) => x >= Instr.MaxStack ? K(p, x - Instr.MaxStack) : $"R{x}";

    static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\n') sb.Append("\\n");
            else if (c < 32 || c > 126) sb.Append($"\\{(int)c}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    public static string Format(LuaProto p, int pc)
    {
        uint i = p.Code[pc];
        var op = Instr.Op(i);
        int a = Instr.A(i), b = Instr.B(i), c = Instr.C(i), bx = Instr.Bx(i), sbx = Instr.SBx(i);
        string name = op.ToString().ToUpperInvariant();
        string args = op switch
        {
            OpCode.Move => $"R{a} := R{b}",
            OpCode.LoadK => $"R{a} := {K(p, bx)}",
            OpCode.LoadBool => $"R{a} := {(b != 0 ? "true" : "false")}{(c != 0 ? "; pc++" : "")}",
            OpCode.LoadNil => $"R{a}..R{b} := nil",
            OpCode.GetUpval => $"R{a} := U{b}",
            OpCode.GetGlobal => $"R{a} := G[{K(p, bx)}]",
            OpCode.GetTable => $"R{a} := R{b}[{RK(p, c)}]",
            OpCode.SetGlobal => $"G[{K(p, bx)}] := R{a}",
            OpCode.SetUpval => $"U{b} := R{a}",
            OpCode.SetTable => $"R{a}[{RK(p, b)}] := {RK(p, c)}",
            OpCode.NewTable => $"R{a} := {{}} ({b},{c})",
            OpCode.Self => $"R{a + 1} := R{b}; R{a} := R{b}[{RK(p, c)}]",
            OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Pow =>
                $"R{a} := {RK(p, b)} {"+-*/^"[(int)op - (int)OpCode.Add]} {RK(p, c)}",
            OpCode.Unm => $"R{a} := -R{b}",
            OpCode.Not => $"R{a} := not R{b}",
            OpCode.Concat => $"R{a} := R{b}.. ... ..R{c}",
            OpCode.Jmp => $"-> {pc + 2 + sbx}",
            OpCode.Eq => $"if ({RK(p, b)} == {RK(p, c)}) ~= {a} then pc++",
            OpCode.Lt => $"if ({RK(p, b)} < {RK(p, c)}) ~= {a} then pc++",
            OpCode.Le => $"if ({RK(p, b)} <= {RK(p, c)}) ~= {a} then pc++",
            OpCode.Test => $"if R{b} <=> {c} then R{a} := R{b} else pc++",
            OpCode.Call => $"R{a}..R{a + c - 2} := R{a}(R{a + 1}..R{a + b - 1}) [{b},{c}]",
            OpCode.TailCall => $"return R{a}(R{a + 1}..R{a + b - 1}) [{b}]",
            OpCode.Return => $"return R{a}..R{a + b - 2} [{b}]",
            OpCode.ForLoop => $"R{a} += R{a + 2}; if R{a} <?= R{a + 1} -> {pc + 2 + sbx}",
            OpCode.TForLoop => $"R{a + 2}..R{a + 2 + c} := R{a}(R{a + 1}, R{a + 2})",
            OpCode.TForPrep => $"-> {pc + 2 + sbx}",
            OpCode.SetList or OpCode.SetListO => $"R{a}[{bx - bx % Instr.FieldsPerFlush}+i] := R{a}+i ({bx})",
            OpCode.Close => $"close >= R{a}",
            OpCode.Closure => $"R{a} := closure(F{bx})",
            _ => $"?? {i:x8}",
        };
        return $"{name,-10} {args}";
    }
}
