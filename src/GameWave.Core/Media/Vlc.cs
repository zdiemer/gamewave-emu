namespace GameWave.Media;

/// <summary>
/// A prefix-code lookup table built from the code strings as printed in the standard. Each
/// entry maps the next <see cref="Bits"/> bits of the stream to a value and a code length.
/// </summary>
internal sealed class Vlc
{
    public readonly int Bits;
    readonly int[] _value;
    readonly byte[] _length;

    public Vlc(int bits, params (string Code, int Value)[] codes)
    {
        Bits = bits;
        _value = new int[1 << bits];
        _length = new byte[1 << bits];
        foreach (var (codeText, value) in codes)
        {
            string code = codeText.Replace(" ", "");
            int len = code.Length;
            if (len > bits)
                throw new ArgumentException($"code {code} is longer than {bits} bits");
            int prefix = Convert.ToInt32(code, 2);
            int first = prefix << (bits - len);
            int count = 1 << (bits - len);
            for (int i = 0; i < count; i++)
            {
                if (_length[first + i] != 0)
                    throw new ArgumentException($"code {code} overlaps another");
                _value[first + i] = value;
                _length[first + i] = (byte)len;
            }
        }
    }

    /// <summary>Decodes one symbol; returns int.MinValue for an invalid code.</summary>
    public int Read(BitReader r)
    {
        uint idx = r.Peek(Bits);
        int len = _length[idx];
        if (len == 0)
            return int.MinValue;
        r.Skip(len);
        return _value[idx];
    }
}
