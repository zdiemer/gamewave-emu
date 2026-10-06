using System.Buffers.Binary;
using System.Globalization;
using GameWave.Engine;

namespace GameWave.Libretro;

sealed record CoreCheat(string? LuaPath, int Value, int Offset = 0, int Width = 0)
{
    public static CoreCheat Parse(string code)
    {
        code = code.Trim();
        if (code.StartsWith("lua:", StringComparison.OrdinalIgnoreCase))
        {
            int equals = code.LastIndexOf('=');
            if (equals < 5 || !int.TryParse(code[(equals + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new FormatException("Lua cheats use lua:global.field=decimal_integer.");
            string path = code[4..equals].Trim();
            if (path.Split('.').Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
                throw new FormatException("Invalid Lua cheat field path.");
            return new(path, value);
        }
        string[] parts = code.Split(':');
        if (parts.Length != 4 || !parts[0].Equals("sram", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset) ||
            !int.TryParse(parts[2], out int width) || width is not (8 or 16 or 32) ||
            !uint.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint data) ||
            offset < 16 || offset > FrontendMemory.Size - width / 8 || (width < 32 && data >= 1u << width))
            throw new FormatException("Flash cheats use sram:hex_offset:8|16|32:hex_value.");
        return new(null, unchecked((int)data), offset, width);
    }

    public void Apply(Machine machine, SaveStore store)
    {
        if (LuaPath is { } path) { machine.SetLuaInteger(path, Value); return; }
        byte[] image = store.Export();
        int offset = Offset - 16;
        if (offset > image.Length - Width / 8) return;
        var destination = image.AsSpan(offset);
        bool changed = Width switch
        {
            8 => destination[0] != (byte)Value,
            16 => BinaryPrimitives.ReadUInt16LittleEndian(destination) != (ushort)Value,
            _ => BinaryPrimitives.ReadInt32LittleEndian(destination) != Value,
        };
        if (!changed) return;
        switch (Width)
        {
            case 8: destination[0] = (byte)Value; break;
            case 16: BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)Value); break;
            case 32: BinaryPrimitives.WriteInt32LittleEndian(destination, Value); break;
        }
        store.Import(image);
    }
}
