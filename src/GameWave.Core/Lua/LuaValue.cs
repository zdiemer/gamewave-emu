using System.Runtime.CompilerServices;

namespace GameWave.Lua;

public enum LuaType : byte
{
    Nil,
    Boolean,
    LightUserdata,
    Number,
    String,
    Table,
    Function,
    Userdata,
    Thread,
}

/// <summary>
/// One Lua value. Game Wave scripts were compiled with a 32-bit integer <c>lua_Number</c>,
/// so numbers are plain <see cref="int"/>s with wrapping arithmetic.
/// </summary>
public readonly struct LuaValue : IEquatable<LuaValue>
{
    public readonly object? O;
    public readonly int N;
    public readonly LuaType Type;

    LuaValue(LuaType type, int n, object? o)
    {
        Type = type;
        N = n;
        O = o;
    }

    public static readonly LuaValue Nil = default;
    public static readonly LuaValue True = new(LuaType.Boolean, 1, null);
    public static readonly LuaValue False = new(LuaType.Boolean, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LuaValue Number(int n) => new(LuaType.Number, n, null);
    public static LuaValue Bool(bool b) => b ? True : False;
    public static LuaValue String(string s) => new(LuaType.String, 0, s);
    public static LuaValue Table(LuaTable t) => new(LuaType.Table, 0, t);
    public static LuaValue Function(LuaFunction f) => new(LuaType.Function, 0, f);
    public static LuaValue Userdata(LuaUserdata u) => new(LuaType.Userdata, 0, u);
    public static LuaValue LightUserdata(object o) => new(LuaType.LightUserdata, 0, o);
    public static LuaValue Thread(LuaThread t) => new(LuaType.Thread, 0, t);

    public static implicit operator LuaValue(int n) => Number(n);
    public static implicit operator LuaValue(bool b) => Bool(b);
    public static implicit operator LuaValue(string? s) => s is null ? Nil : String(s);
    public static implicit operator LuaValue(LuaTable? t) => t is null ? Nil : Table(t);
    public static implicit operator LuaValue(LuaFunction? f) => f is null ? Nil : Function(f);

    public bool IsNil => Type == LuaType.Nil;
    public bool IsFalsy => Type == LuaType.Nil || (Type == LuaType.Boolean && N == 0);
    public bool IsTruthy => !IsFalsy;
    public bool IsNumber => Type == LuaType.Number;
    public bool IsString => Type == LuaType.String;
    public bool IsTable => Type == LuaType.Table;
    public bool IsFunction => Type == LuaType.Function;

    public string? AsString => O as string;
    public LuaTable? AsTable => O as LuaTable;
    public LuaFunction? AsFunction => O as LuaFunction;
    public LuaUserdata? AsUserdata => O as LuaUserdata;

    public string TypeName => TypeNameOf(Type);

    public static string TypeNameOf(LuaType t) => t switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => "boolean",
        LuaType.LightUserdata => "userdata",
        LuaType.Number => "number",
        LuaType.String => "string",
        LuaType.Table => "table",
        LuaType.Function => "function",
        LuaType.Userdata => "userdata",
        LuaType.Thread => "thread",
        _ => "?",
    };

    /// <summary>Number coercion as the VM does it: numbers, and strings that parse as numbers.</summary>
    public bool TryToNumber(out int n)
    {
        if (Type == LuaType.Number)
        {
            n = N;
            return true;
        }
        if (Type == LuaType.String)
            return LuaNumber.TryParse((string)O!, out n);
        n = 0;
        return false;
    }

    /// <summary>String coercion as the VM does it: strings, and numbers formatted as integers.</summary>
    public bool TryToStr(out string s)
    {
        if (Type == LuaType.String)
        {
            s = (string)O!;
            return true;
        }
        if (Type == LuaType.Number)
        {
            s = LuaNumber.Format(N);
            return true;
        }
        s = "";
        return false;
    }

    /// <summary>Raw equality (no metamethods).</summary>
    public bool Equals(LuaValue other)
    {
        if (Type != other.Type)
            return false;
        return Type switch
        {
            LuaType.Nil => true,
            LuaType.Boolean or LuaType.Number => N == other.N,
            LuaType.String => string.Equals((string)O!, (string)other.O!, StringComparison.Ordinal),
            _ => ReferenceEquals(O, other.O),
        };
    }

    public override bool Equals(object? obj) => obj is LuaValue v && Equals(v);

    public override int GetHashCode() => Type switch
    {
        LuaType.Nil => 0,
        LuaType.Boolean or LuaType.Number => N * 31 + (int)Type,
        LuaType.String => StringComparer.Ordinal.GetHashCode((string)O!),
        _ => RuntimeHelpers.GetHashCode(O!),
    };

    public static bool operator ==(LuaValue a, LuaValue b) => a.Equals(b);
    public static bool operator !=(LuaValue a, LuaValue b) => !a.Equals(b);

    public override string ToString() => Type switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => N != 0 ? "true" : "false",
        LuaType.Number => LuaNumber.Format(N),
        LuaType.String => (string)O!,
        LuaType.Table => $"table: {Address(O!)}",
        LuaType.Function => $"function: {Address(O!)}",
        LuaType.Userdata => $"userdata: {Address(O!)}",
        LuaType.LightUserdata => $"userdata: {Address(O!)}",
        LuaType.Thread => $"thread: {Address(O!)}",
        _ => "?",
    };

    static string Address(object o) => "0x" + ((uint)RuntimeHelpers.GetHashCode(o)).ToString("x8");
}

public static class LuaNumber
{
    public static string Format(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses the way an integer Lua build's <c>lua_str2number</c> does: optional surrounding
    /// whitespace, an optional sign, then decimal digits or a <c>0x</c> hex literal. Anything
    /// else (including a decimal point) makes the whole string not a number.
    /// </summary>
    public static bool TryParse(string s, out int n)
    {
        n = 0;
        int i = 0, len = s.Length;
        while (i < len && char.IsWhiteSpace(s[i]))
            i++;
        bool neg = false;
        if (i < len && (s[i] == '-' || s[i] == '+'))
        {
            neg = s[i] == '-';
            i++;
        }
        long v = 0;
        int digits = 0;
        if (i + 1 < len && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
        {
            i += 2;
            while (i < len && Uri.IsHexDigit(s[i]))
            {
                v = (v << 4) | (uint)Convert.ToInt32(s[i].ToString(), 16);
                v &= 0xFFFFFFFF;
                i++;
                digits++;
            }
        }
        else
        {
            while (i < len && s[i] >= '0' && s[i] <= '9')
            {
                v = v * 10 + (s[i] - '0');
                if (v > 0xFFFFFFFFL)
                    v &= 0xFFFFFFFF;
                i++;
                digits++;
            }
        }
        if (digits == 0)
            return false;
        while (i < len && char.IsWhiteSpace(s[i]))
            i++;
        if (i != len)
            return false;
        n = unchecked((int)(neg ? -v : v));
        return true;
    }
}
