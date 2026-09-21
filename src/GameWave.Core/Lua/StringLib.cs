using System.Text;

namespace GameWave.Lua;

/// <summary>The Lua 5.0 string library, including its pattern matcher (ported from lstrlib.c).</summary>
public static class StringLib
{
    public static void Open(LuaState S)
    {
        S.RegisterModule("string",
            ("len", a => a.Return(a.Str(1).Length)),
            ("sub", a =>
            {
                string s = a.Str(1);
                int l = s.Length;
                int i = PosRelat(a.OptInt(2, 1), l);
                int j = PosRelat(a.OptInt(3, -1), l);
                if (i < 1) i = 1;
                if (j > l) j = l;
                return a.Return(i <= j ? s.Substring(i - 1, j - i + 1) : "");
            }),
            ("lower", a => a.Return(Lower(a.Str(1)))),
            ("upper", a => a.Return(Upper(a.Str(1)))),
            ("char", a =>
            {
                var sb = new StringBuilder(a.Count);
                for (int i = 1; i <= a.Count; i++)
                {
                    int c = a.Int(i);
                    if ((uint)c > 255)
                        throw a.ArgError(i, "invalid value");
                    sb.Append((char)c);
                }
                return a.Return(sb.ToString());
            }),
            ("rep", a =>
            {
                string s = a.Str(1);
                int n = a.Int(2);
                var sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                    sb.Append(s);
                return a.Return(sb.ToString());
            }),
            ("byte", a =>
            {
                string s = a.Str(1);
                int p = PosRelat(a.OptInt(2, 1), s.Length);
                if (p <= 0 || p > s.Length)
                    return 0;
                return a.Return((int)(s[p - 1] & 0xFF));
            }),
            ("format", a => a.Return(Format(a))),
            ("find", a => Find(a, true)),
            ("gfind", GFind),
            ("gsub", GSub),
            ("dump", a => throw new LuaException("unable to dump given function")));
    }

    static int PosRelat(int pos, int len) => pos >= 0 ? pos : len + pos + 1;

    static string Lower(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
            if (c[i] >= 'A' && c[i] <= 'Z') c[i] = (char)(c[i] + 32);
        return new string(c);
    }

    static string Upper(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
            if (c[i] >= 'a' && c[i] <= 'z') c[i] = (char)(c[i] - 32);
        return new string(c);
    }

    // ---------------------------------------------------------------- format

    static string Format(LuaArgs a)
    {
        string fmt = a.Str(1);
        var sb = new StringBuilder();
        int arg = 1;
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }
            if (++i >= fmt.Length)
                break;
            if (fmt[i] == '%')
            {
                sb.Append('%');
                continue;
            }
            // Flags, width, precision.
            int start = i;
            while (i < fmt.Length && "-+ #0".IndexOf(fmt[i]) >= 0) i++;
            while (i < fmt.Length && char.IsDigit(fmt[i])) i++;
            int precision = -1;
            if (i < fmt.Length && fmt[i] == '.')
            {
                i++;
                int ps = i;
                while (i < fmt.Length && char.IsDigit(fmt[i])) i++;
                precision = i > ps ? int.Parse(fmt[ps..i]) : 0;
            }
            if (i >= fmt.Length)
                throw new LuaException("invalid format string to `format'");
            string spec = fmt[start..i];
            string flags = new(spec.TakeWhile(ch => "-+ #0".IndexOf(ch) >= 0).ToArray());
            int dot = spec.IndexOf('.');
            string widthStr = spec[flags.Length..(dot >= 0 ? dot : spec.Length)];
            int width = widthStr.Length > 0 ? int.Parse(widthStr) : 0;
            char conv = fmt[i];
            arg++;
            string body;
            switch (conv)
            {
                case 'd':
                case 'i':
                {
                    long n = a.Int(arg);
                    body = Math.Abs(n).ToString();
                    if (precision >= 0)
                        body = body.PadLeft(precision, '0');
                    string sign = n < 0 ? "-" : flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : "";
                    body = PadNumber(sign, body, width, flags, precision < 0);
                    sb.Append(body);
                    continue;
                }
                case 'u':
                    body = ((uint)a.Int(arg)).ToString();
                    sb.Append(PadNumber("", body, width, flags, precision < 0));
                    continue;
                case 'x':
                case 'X':
                case 'o':
                {
                    uint n = (uint)a.Int(arg);
                    body = conv == 'o' ? Convert.ToString(n, 8) : n.ToString(conv == 'x' ? "x" : "X");
                    if (precision >= 0)
                        body = body.PadLeft(precision, '0');
                    string prefix = flags.Contains('#') && n != 0 ? (conv == 'o' ? "0" : conv == 'x' ? "0x" : "0X") : "";
                    sb.Append(PadNumber(prefix, body, width, flags, precision < 0));
                    continue;
                }
                case 'c':
                    body = ((char)(a.Int(arg) & 0xFF)).ToString();
                    break;
                case 'e':
                case 'E':
                case 'f':
                case 'g':
                case 'G':
                {
                    double d = a.Int(arg);
                    int prec = precision < 0 ? 6 : precision;
                    body = conv switch
                    {
                        'f' => d.ToString("F" + prec, System.Globalization.CultureInfo.InvariantCulture),
                        'e' or 'E' => d.ToString((conv == 'e' ? "e" : "E") + prec, System.Globalization.CultureInfo.InvariantCulture),
                        _ => d.ToString("G" + Math.Max(prec, 1), System.Globalization.CultureInfo.InvariantCulture),
                    };
                    string sign = d >= 0 && flags.Contains('+') ? "+" : "";
                    sb.Append(PadNumber(sign, body.TrimStart('-'), width, flags, true, d < 0));
                    continue;
                }
                case 'q':
                {
                    string s = a.Str(arg);
                    var q = new StringBuilder("\"");
                    foreach (char ch in s)
                    {
                        switch (ch)
                        {
                            case '"': q.Append("\\\""); break;
                            case '\\': q.Append("\\\\"); break;
                            case '\n': q.Append("\\\n"); break;
                            case '\r': q.Append("\\r"); break;
                            case '\0': q.Append("\\000"); break;
                            default: q.Append(ch); break;
                        }
                    }
                    sb.Append(q.Append('"'));
                    continue;
                }
                case 's':
                {
                    string s = a.L.State.ToStringMeta(a.Any(arg));
                    if (precision >= 0 && s.Length > precision)
                        s = s[..precision];
                    body = s;
                    break;
                }
                default:
                    throw new LuaException($"invalid option to `format'");
            }
            if (body.Length < width)
                body = flags.Contains('-') ? body.PadRight(width) : body.PadLeft(width);
            sb.Append(body);
        }
        return sb.ToString();
    }

    static string PadNumber(string sign, string digits, int width, string flags, bool zeroAllowed, bool negative = false)
    {
        if (negative)
            sign = "-" + sign;
        int len = sign.Length + digits.Length;
        if (len >= width)
            return sign + digits;
        if (flags.Contains('-'))
            return (sign + digits).PadRight(width);
        if (flags.Contains('0') && zeroAllowed)
            return sign + digits.PadLeft(width - sign.Length, '0');
        return (sign + digits).PadLeft(width);
    }

    // ---------------------------------------------------------------- find / gfind / gsub

    static int Find(LuaArgs a, bool find)
    {
        string s = a.Str(1);
        string p = a.Str(2);
        int init = PosRelat(a.OptInt(3, 1), s.Length) - 1;
        if (init < 0)
            init = 0;
        else if (init > s.Length)
            init = s.Length;
        bool plain = a[4].IsTruthy;
        if (find && (plain || p.IndexOfAny(Specials) < 0))
        {
            int idx = s.IndexOf(p, init, StringComparison.Ordinal);
            if (idx < 0)
                return a.Return(LuaValue.Nil);
            return a.Return(idx + 1, idx + p.Length);
        }
        var ms = new MatchState(s, p, a.L);
        bool anchor = p.Length > 0 && p[0] == '^';
        int pi = anchor ? 1 : 0;
        int si = init;
        do
        {
            ms.Level = 0;
            int e = ms.Match(si, pi);
            if (e >= 0)
            {
                int n = 2;
                a.L.Push(si + 1);
                a.L.Push(e);
                n += ms.PushCaptures(-1, -1, false);
                return n;
            }
            si++;
        } while (si <= s.Length && !anchor);
        return a.Return(LuaValue.Nil);
    }

    static readonly char[] Specials = "^$*+?.([%-".ToCharArray();

    static int GFind(LuaArgs a)
    {
        string s = a.Str(1);
        string p = a.Str(2);
        return a.Return(GFindIterator(s, p, 0));

        static LuaNative GFindIterator(string s, string p, int start)
        {
            int pos = start;
            return new LuaNative("gfind_iterator", b =>
            {
                var ms = new MatchState(s, p, b.L);
                for (int src = pos; src <= s.Length; src++)
                {
                    ms.Level = 0;
                    int e = ms.Match(src, 0);
                    if (e >= 0)
                    {
                        int newStart = e;
                        if (e == src)
                            newStart++;
                        pos = newStart;
                        return ms.PushCaptures(src, e, true);
                    }
                }
                pos = s.Length + 1;
                return 0;
            }, _ => GFindIterator(s, p, pos));
        }
    }

    static int GSub(LuaArgs a)
    {
        string src = a.Str(1);
        string p = a.Str(2);
        var repl = a[3];
        if (!(repl.IsString || repl.IsNumber || repl.IsTable || repl.IsFunction))
            throw a.ArgError(3, "string or function expected");
        int maxN = a.OptInt(4, src.Length + 1);
        bool anchor = p.Length > 0 && p[0] == '^';
        int pi = anchor ? 1 : 0;
        int n = 0;
        int si = 0;
        var b = new StringBuilder();
        var ms = new MatchState(src, p, a.L);
        while (n < maxN)
        {
            ms.Level = 0;
            int e = ms.Match(si, pi);
            if (e >= 0)
            {
                n++;
                AddValue(ms, b, si, e, repl);
            }
            if (e >= 0 && e > si)
                si = e;
            else if (si < src.Length)
                b.Append(src[si++]);
            else
                break;
            if (anchor)
                break;
        }
        if (si < src.Length)
            b.Append(src, si, src.Length - si);
        return a.Return(b.ToString(), n);
    }

    static void AddValue(MatchState ms, StringBuilder b, int s, int e, LuaValue repl)
    {
        var L = ms.L;
        if (repl.IsString || repl.IsNumber)
        {
            repl.TryToStr(out var r);
            for (int i = 0; i < r.Length; i++)
            {
                if (r[i] != '%')
                    b.Append(r[i]);
                else
                {
                    i++;
                    if (i < r.Length && char.IsDigit(r[i]))
                    {
                        var cap = ms.GetCapture(r[i] - '1', s, e);
                        cap.TryToStr(out var cs);
                        b.Append(cs);
                    }
                    else if (i < r.Length)
                        b.Append(r[i]);
                }
            }
            return;
        }
        LuaValue result;
        if (repl.IsFunction)
        {
            int top = L.Top;
            int n = ms.PushCaptures(s, e, true);
            var args = new LuaValue[n];
            Array.Copy(L.Stack, L.Top - n, args, 0, n);
            L.Top = top;
            result = L.Call1(repl, args);
        }
        else
        {
            var key = ms.GetCapture(0, s, e);
            result = L.State.Index(repl, key);
        }
        if (result.IsFalsy)
            b.Append(ms.Src, s, e - s);
        else if (result.TryToStr(out var rs))
            b.Append(rs);
        else
            throw new LuaException($"invalid replacement value (a {result.TypeName})");
    }

    sealed class MatchState(string src, string pat, LuaThread l)
    {
        const int CapUnfinished = -1;
        const int CapPosition = -2;
        const int MaxCaptures = 32;

        public readonly string Src = src;
        readonly string _p = pat;
        public readonly LuaThread L = l;
        public int Level;
        readonly int[] _capInit = new int[MaxCaptures];
        readonly int[] _capLen = new int[MaxCaptures];
        int _depth;

        public LuaValue GetCapture(int i, int s, int e)
        {
            if (i >= Level)
            {
                if (i == 0)
                    return LuaValue.String(Src.Substring(s, e - s));
                throw new LuaException("invalid capture index");
            }
            int l = _capLen[i];
            if (l == CapUnfinished)
                throw new LuaException("unfinished capture");
            if (l == CapPosition)
                return LuaValue.Number(_capInit[i] + 1);
            return LuaValue.String(Src.Substring(_capInit[i], l));
        }

        public int PushCaptures(int s, int e, bool wholeIfNone)
        {
            int n = Level == 0 && wholeIfNone ? 1 : Level;
            for (int i = 0; i < n; i++)
                L.Push(GetCapture(i, s, e));
            return n;
        }

        int ClassEnd(int p)
        {
            if (p >= _p.Length)
                throw new LuaException("malformed pattern (ends with `%')");
            char c = _p[p++];
            if (c == '%')
            {
                if (p >= _p.Length)
                    throw new LuaException("malformed pattern (ends with `%')");
                return p + 1;
            }
            if (c == '[')
            {
                if (p < _p.Length && _p[p] == '^')
                    p++;
                do
                {
                    if (p >= _p.Length)
                        throw new LuaException("malformed pattern (missing `]')");
                    char cc = _p[p++];
                    if (cc == '%' && p < _p.Length)
                        p++;
                } while (p >= _p.Length || _p[p] != ']');
                return p + 1;
            }
            return p;
        }

        static bool MatchClass(char c, char cl)
        {
            bool res;
            switch (char.ToLowerInvariant(cl))
            {
                case 'a': res = char.IsAsciiLetter(c); break;
                case 'c': res = c < 32 || c == 127; break;
                case 'd': res = c >= '0' && c <= '9'; break;
                case 'l': res = c >= 'a' && c <= 'z'; break;
                case 'p': res = c > 32 && c < 127 && !char.IsAsciiLetterOrDigit(c); break;
                case 's': res = c == ' ' || (c >= '\t' && c <= '\r'); break;
                case 'u': res = c >= 'A' && c <= 'Z'; break;
                case 'w': res = char.IsAsciiLetterOrDigit(c); break;
                case 'x': res = char.IsAsciiHexDigit(c); break;
                case 'z': res = c == 0; break;
                default: return cl == c;
            }
            return char.IsUpper(cl) ? !res : res;
        }

        bool MatchBracketClass(char c, int p, int ec)
        {
            bool sig = true;
            if (_p[p + 1] == '^')
            {
                sig = false;
                p++;
            }
            while (++p < ec)
            {
                if (_p[p] == '%')
                {
                    p++;
                    if (MatchClass(c, _p[p]))
                        return sig;
                }
                else if (p + 2 < ec && _p[p + 1] == '-')
                {
                    if (_p[p] <= c && c <= _p[p + 2])
                        return sig;
                    p += 2;
                }
                else if (_p[p] == c)
                    return sig;
            }
            return !sig;
        }

        bool SingleMatch(int s, int p, int ep)
        {
            if (s >= Src.Length)
                return false;
            char c = Src[s];
            return _p[p] switch
            {
                '.' => true,
                '%' => MatchClass(c, _p[p + 1]),
                '[' => MatchBracketClass(c, p, ep - 1),
                _ => _p[p] == c,
            };
        }

        public int Match(int s, int p)
        {
            if (++_depth > 200)
                throw new LuaException("pattern too complex");
            try
            {
                while (true)
                {
                    if (p >= _p.Length)
                        return s;
                    switch (_p[p])
                    {
                        case '(':
                            if (p + 1 < _p.Length && _p[p + 1] == ')')
                                return StartCapture(s, p + 2, CapPosition);
                            return StartCapture(s, p + 1, CapUnfinished);
                        case ')':
                            return EndCapture(s, p + 1);
                        case '%':
                            if (p + 1 < _p.Length)
                            {
                                char nx = _p[p + 1];
                                if (nx == 'b')
                                {
                                    s = MatchBalance(s, p + 2);
                                    if (s < 0)
                                        return -1;
                                    p += 4;
                                    continue;
                                }
                                if (nx == 'f')
                                {
                                    p += 2;
                                    if (p >= _p.Length || _p[p] != '[')
                                        throw new LuaException("missing `[' after `%f' in pattern");
                                    int ep2 = ClassEnd(p);
                                    char prev = s == 0 ? '\0' : Src[s - 1];
                                    char cur = s < Src.Length ? Src[s] : '\0';
                                    if (!MatchBracketClass(prev, p, ep2 - 1) && MatchBracketClass(cur, p, ep2 - 1))
                                    {
                                        p = ep2;
                                        continue;
                                    }
                                    return -1;
                                }
                                if (char.IsDigit(nx))
                                {
                                    s = MatchCapture(s, nx);
                                    if (s < 0)
                                        return -1;
                                    p += 2;
                                    continue;
                                }
                            }
                            goto default;
                        case '$':
                            if (p + 1 == _p.Length)
                                return s == Src.Length ? s : -1;
                            goto default;
                        default:
                        {
                            int ep = ClassEnd(p);
                            bool m = SingleMatch(s, p, ep);
                            char next = ep < _p.Length ? _p[ep] : '\0';
                            switch (next)
                            {
                                case '?':
                                {
                                    if (m)
                                    {
                                        int r = Match(s + 1, ep + 1);
                                        if (r >= 0)
                                            return r;
                                    }
                                    p = ep + 1;
                                    continue;
                                }
                                case '*':
                                    return MaxExpand(s, p, ep);
                                case '+':
                                    return m ? MaxExpand(s + 1, p, ep) : -1;
                                case '-':
                                    return MinExpand(s, p, ep);
                                default:
                                    if (!m)
                                        return -1;
                                    s++;
                                    p = ep;
                                    continue;
                            }
                        }
                    }
                }
            }
            finally
            {
                _depth--;
            }
        }

        int MaxExpand(int s, int p, int ep)
        {
            int i = 0;
            while (SingleMatch(s + i, p, ep))
                i++;
            while (i >= 0)
            {
                int r = Match(s + i, ep + 1);
                if (r >= 0)
                    return r;
                i--;
            }
            return -1;
        }

        int MinExpand(int s, int p, int ep)
        {
            while (true)
            {
                int r = Match(s, ep + 1);
                if (r >= 0)
                    return r;
                if (SingleMatch(s, p, ep))
                    s++;
                else
                    return -1;
            }
        }

        int StartCapture(int s, int p, int what)
        {
            if (Level >= MaxCaptures)
                throw new LuaException("too many captures");
            _capInit[Level] = s;
            _capLen[Level] = what;
            Level++;
            int r = Match(s, p);
            if (r < 0)
                Level--;
            return r;
        }

        int EndCapture(int s, int p)
        {
            int l = -1;
            for (int i = Level - 1; i >= 0; i--)
            {
                if (_capLen[i] == CapUnfinished)
                {
                    l = i;
                    break;
                }
            }
            if (l < 0)
                throw new LuaException("invalid pattern capture");
            _capLen[l] = s - _capInit[l];
            int r = Match(s, p);
            if (r < 0)
                _capLen[l] = CapUnfinished;
            return r;
        }

        int MatchBalance(int s, int p)
        {
            if (p + 1 >= _p.Length)
                throw new LuaException("unbalanced pattern");
            if (s >= Src.Length || Src[s] != _p[p])
                return -1;
            char b = _p[p], e = _p[p + 1];
            int cont = 1;
            while (++s < Src.Length)
            {
                if (Src[s] == e)
                {
                    if (--cont == 0)
                        return s + 1;
                }
                else if (Src[s] == b)
                    cont++;
            }
            return -1;
        }

        int MatchCapture(int s, char l)
        {
            int idx = l - '1';
            if (idx < 0 || idx >= Level || _capLen[idx] == CapUnfinished)
                throw new LuaException("invalid capture index");
            int len = _capLen[idx];
            if (Src.Length - s >= len && string.CompareOrdinal(Src, _capInit[idx], Src, s, len) == 0)
                return s + len;
            return -1;
        }
    }
}
