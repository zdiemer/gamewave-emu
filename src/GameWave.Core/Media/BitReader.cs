using System.Runtime.CompilerServices;

namespace GameWave.Media;

/// <summary>A big-endian bit reader over a byte array, reading past the end as zeros.</summary>
internal sealed class BitReader
{
    byte[] _data = [];
    int _end;
    int _bit;

    public void Reset(byte[] data, int start, int end)
    {
        _data = data;
        _end = end;
        _bit = start * 8;
    }

    public int BytePosition => (_bit + 7) >> 3;
    public int BitPosition => _bit;
    public bool AtEnd => _bit >= _end * 8;
    public int BitsLeft => _end * 8 - _bit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek(int n)
    {
        int byteIndex = _bit >> 3;
        ulong v = 0;
        // Load 8 bytes starting at byteIndex (zero padded).
        if (byteIndex + 8 <= _end)
        {
            v = ((ulong)_data[byteIndex] << 56) | ((ulong)_data[byteIndex + 1] << 48) | ((ulong)_data[byteIndex + 2] << 40)
              | ((ulong)_data[byteIndex + 3] << 32) | ((ulong)_data[byteIndex + 4] << 24) | ((ulong)_data[byteIndex + 5] << 16)
              | ((ulong)_data[byteIndex + 6] << 8) | _data[byteIndex + 7];
        }
        else
        {
            for (int i = 0; i < 8; i++)
            {
                v <<= 8;
                if (byteIndex + i < _end)
                    v |= _data[byteIndex + i];
            }
        }
        v <<= _bit & 7;
        return (uint)(v >> (64 - n));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int n) => _bit += n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(int n)
    {
        if (n == 0)
            return 0;
        uint v = Peek(n);
        _bit += n;
        return v;
    }

    public bool ReadBit() => Read(1) != 0;

    public void AlignToByte() => _bit = (_bit + 7) & ~7;

    /// <summary>Advances to the next start code (00 00 01 xx); returns its code or -1.</summary>
    public int NextStartCode()
    {
        AlignToByte();
        int i = _bit >> 3;
        while (i + 3 < _end)
        {
            if (_data[i] == 0 && _data[i + 1] == 0 && _data[i + 2] == 1)
            {
                _bit = (i + 3) * 8;
                return _data[i + 3];
            }
            i++;
        }
        _bit = _end * 8;
        return -1;
    }

    /// <summary>True when the next 23 bits are zero, which is how a slice ends.</summary>
    public bool NextBitsAreZero23 => Peek(23) == 0;
}
