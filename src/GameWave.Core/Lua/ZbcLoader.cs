using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GameWave.Lua;

/// <summary>
/// Loads Game Wave bytecode files. A <c>.zbc</c> file is Lua 5.0 bytecode with the signature
/// <c>ESC "ZBC"</c> instead of <c>ESC "Lua"</c>, usually wrapped in a <c>ESC "ZCS"</c>
/// container that holds a zlib stream:
/// <code>
/// 0  4  ESC 'Z' 'C' 'S'
/// 4  4  0x0A 0x1A 0x00 0x00
/// 8  4  uncompressed size (little endian)
/// 12 4  compressed size
/// 16 .. zlib data
/// </code>
/// The bytecode header declares 4-byte ints, size_t and instructions, the stock 6/8/9/9
/// instruction field widths, and a 4-byte integer lua_Number.
/// </summary>
public static class ZbcLoader
{
    static readonly Encoding Latin1 = Encoding.Latin1;

    public static byte[] Unwrap(byte[] data)
    {
        if (data.Length >= 16 && data[0] == 0x1B && data[1] == 'Z' && data[2] == 'C' && data[3] == 'S')
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
            var output = new byte[size];
            using var z = new ZLibStream(new MemoryStream(data, 16, data.Length - 16), CompressionMode.Decompress);
            int read = 0;
            while (read < size)
            {
                int n = z.Read(output, read, size - read);
                if (n == 0)
                    break;
                read += n;
            }
            if (read != size)
                throw new InvalidDataException($"bytecode expanded to {read} bytes, expected {size}");
            return output;
        }
        return data;
    }

    public static LuaProto Load(byte[] data, string chunkName = "?")
    {
        var r = new Reader(Unwrap(data));
        r.Header();
        return r.Function(chunkName);
    }

    sealed class Reader(byte[] b)
    {
        int _p;
        bool _bigEndian;

        public void Header()
        {
            if (b.Length < 18 || b[0] != 0x1B)
                throw new InvalidDataException("not a bytecode file");
            string sig = Latin1.GetString(b, 1, 3);
            if (sig != "ZBC" && sig != "Lua")
                throw new InvalidDataException($"unknown bytecode signature '{sig}'");
            // The signature is followed by 0x0A 0x1A, like the ZCS container.
            _p = b[4] == 0x0A && b[5] == 0x1A ? 6 : 4;
            int version = Byte();
            if (version != 0x50)
                throw new InvalidDataException($"bytecode version {version:x2} is not Lua 5.0");
            // Three bytes the Zapit toolchain adds (always 01 00 01) before the stock header fields.
            _p += 3;
            _bigEndian = Byte() == 0;
            int sizeInt = Byte(), sizeT = Byte(), sizeInstr = Byte();
            int sizeOp = Byte(), sizeA = Byte(), sizeB = Byte(), sizeC = Byte();
            int sizeNum = Byte();
            if (sizeInt != 4 || sizeT != 4 || sizeInstr != 4 || sizeOp != 6 || sizeA != 8 || sizeB != 9 || sizeC != 9)
                throw new InvalidDataException("unsupported bytecode layout");
            if (sizeNum != 4)
                throw new InvalidDataException($"unsupported lua_Number size {sizeNum}");
            int test = Int();
            if (test != 31415926)
                throw new InvalidDataException("bytecode number format is not a 32-bit integer");
        }

        int Byte() => b[_p++];

        int Int()
        {
            int v = _bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(_p))
                : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(_p));
            _p += 4;
            return v;
        }

        string? Str()
        {
            int n = Int();
            if (n == 0)
                return null;
            // The stored length includes the terminating NUL.
            string s = Latin1.GetString(b, _p, n - 1);
            _p += n;
            return s;
        }

        public LuaProto Function(string parentSource)
        {
            var f = new LuaProto();
            f.Source = Str() ?? parentSource;
            f.LineDefined = Int();
            f.NumUpvalues = Byte();
            f.NumParams = Byte();
            f.IsVararg = Byte() != 0;
            f.MaxStackSize = Byte();

            int n = Int();
            f.LineInfo = new int[n];
            for (int i = 0; i < n; i++)
                f.LineInfo[i] = Int();

            n = Int();
            f.Locals = new LuaProto.LocalVar[n];
            for (int i = 0; i < n; i++)
            {
                string name = Str() ?? "?";
                int start = Int();
                int end = Int();
                f.Locals[i] = new(name, start, end);
            }

            n = Int();
            f.UpvalueNames = new string[n];
            for (int i = 0; i < n; i++)
                f.UpvalueNames[i] = Str() ?? "?";

            n = Int();
            f.Constants = new LuaValue[n];
            for (int i = 0; i < n; i++)
            {
                int t = Byte();
                f.Constants[i] = t switch
                {
                    0 => LuaValue.Nil,
                    3 => LuaValue.Number(Int()),
                    4 => LuaValue.String(Str() ?? ""),
                    _ => throw new InvalidDataException($"bad constant type {t}"),
                };
            }

            n = Int();
            f.Protos = new LuaProto[n];
            for (int i = 0; i < n; i++)
                f.Protos[i] = Function(f.Source);

            n = Int();
            f.Code = new uint[n];
            for (int i = 0; i < n; i++)
                f.Code[i] = unchecked((uint)Int());
            return f;
        }
    }
}
