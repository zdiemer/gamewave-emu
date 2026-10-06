using GameWave.Lua;

namespace GameWave.Engine;

public sealed partial class Machine
{
    /// <summary>Writes an integer global or table field at a parked frame boundary.</summary>
    public bool SetLuaInteger(string path, int value)
    {
        if (_frames is null) throw new InvalidOperationException("Memory access requires frame-driven emulation.");
        return _frames.AtBoundary(() =>
        {
            if (_lua is null) return false;
            string[] parts = path.Split('.');
            LuaTable table = _lua.Globals;
            for (int i = 0; i < parts.Length; i++)
            {
                LuaValue key = int.TryParse(parts[i], out int index) ? LuaValue.Number(index) : LuaValue.String(parts[i]);
                var existing = table.Get(key);
                if (i == parts.Length - 1)
                {
                    if (!existing.IsNumber) return false;
                    table.Set(key, value); return true;
                }
                if (existing.AsTable is not { } child) return false;
                table = child;
            }
            return false;
        });
    }
}
