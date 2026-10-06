using GameWave.Engine;

namespace GameWave.Libretro;

sealed class RetroInput
{
    // Libretro B,Y,Select,Start,Up,Down,Left,Right,A,X,L,R,L2,R2,L3,R3.
    static readonly RemoteKey?[] Normal =
    [RemoteKey.B, RemoteKey.D, RemoteKey.GameMenu, RemoteKey.Select,
        RemoteKey.Up, RemoteKey.Down, RemoteKey.Left, RemoteKey.Right,
        RemoteKey.A, RemoteKey.C, RemoteKey.DvdMenu, RemoteKey.Select, null, null, null, null];
    static readonly RemoteKey?[] Numbers =
    [RemoteKey.Num2, RemoteKey.Num4, null, null,
        RemoteKey.Num5, RemoteKey.Num7, RemoteKey.Num8, RemoteKey.Num6,
        RemoteKey.Num1, RemoteKey.Num3, RemoteKey.Num9, RemoteKey.Num0, null, null, null, null];
    readonly int[] _previous = new int[6];
    readonly long[,] _repeatAt = new long[6, 21];
    public readonly uint[] Devices = [1, 1, 1, 1, 1, 1];
    public int KeyboardRemote = 1;

    internal sealed record State(int[] Previous, long[] RepeatAt);
    public void WriteState(BinaryWriter writer)
    {
        foreach (int value in _previous) writer.Write(value);
        foreach (long value in _repeatAt) writer.Write(value);
    }
    public static State ReadState(BinaryReader reader)
    {
        int[] previous = new int[6]; long[] repeatAt = new long[6 * 21];
        for (int i = 0; i < previous.Length; i++) previous[i] = reader.ReadInt32();
        for (int i = 0; i < repeatAt.Length; i++) repeatAt[i] = reader.ReadInt64();
        return new(previous, repeatAt);
    }
    public void RestoreState(State state)
    {
        state.Previous.CopyTo(_previous, 0);
        for (int port = 0; port < 6; port++)
            for (int key = 0; key < 21; key++) _repeatAt[port, key] = state.RepeatAt[port * 21 + key];
    }

    public void Clear()
    {
        Array.Clear(_previous);
        Array.Clear(_repeatAt);
    }

    public void Poll(uint port, int buttons, Machine machine)
    {
        int pressed = 0;
        var map = (buttons & (1 << 13)) != 0 ? Numbers : Normal;
        for (int id = 0; id < map.Length; id++)
            if ((buttons & (1 << id)) != 0 && map[id] is { } key)
                pressed |= 1 << (int)key;
        long now = machine.Clock.Now;
        for (int key = 0; key <= 20; key++)
        {
            int mask = 1 << key;
            if ((pressed & mask) == 0)
                continue;
            bool fresh = (_previous[port] & mask) == 0;
            bool direction = key >= (int)RemoteKey.Up && key <= (int)RemoteKey.Left;
            if (fresh || (direction && now >= _repeatAt[port, key]))
            {
                machine.Input.Push((RemoteKey)key, (Remote)(port + 1), now);
                _repeatAt[port, key] = now + (fresh ? 350 : 100);
            }
        }
        _previous[port] = pressed;
    }

    public void Keyboard(uint key, Machine machine)
    {
        RemoteKey? remoteKey = key switch
        {
            >= 48 and <= 57 => (RemoteKey)(key - 48),
            >= 256 and <= 264 => (RemoteKey)(key - 255), // keypad 1..9
            265 => RemoteKey.Num0,
            273 => RemoteKey.Up, 274 => RemoteKey.Down, 275 => RemoteKey.Right, 276 => RemoteKey.Left,
            13 or 32 or 271 => RemoteKey.Select,
            97 => RemoteKey.A, 98 => RemoteKey.B, 99 => RemoteKey.C, 100 => RemoteKey.D,
            103 => RemoteKey.GameMenu, 278 => RemoteKey.DvdMenu,
            _ => null,
        };
        if (remoteKey is { } k && Devices[KeyboardRemote - 1] != 0)
            machine.Input.Push(k, (Remote)KeyboardRemote, machine.Clock.Now);
    }
}
