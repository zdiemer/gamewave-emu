namespace GameWave.Engine;

/// <summary>
/// A word dictionary for <c>dict.Lookup</c> (Letter Zap's <c>.zdt</c> files): a compact trie
/// after an 8-byte header. A node is a child count followed by that many entries; an entry
/// is one byte, the letter in the low five bits (1 is 'a'), 0x20 set when a word ends there,
/// and the top two bits giving how many little-endian bytes of child offset follow. The
/// offset is measured from the start of the node the entry belongs to.
/// </summary>
public sealed class WordList
{
    readonly byte[] _data;

    WordList(byte[] data) => _data = data;

    public static WordList Load(byte[] data) => new(data);
    internal byte[] StateData => _data;

    /// <summary>Whether a word is in the dictionary. Words are looked up in lower case.</summary>
    public bool Contains(string word)
    {
        if (word.Length == 0)
            return true;
        int node = 8;
        for (int i = 0; i < word.Length; i++)
        {
            if (node < 0 || node >= _data.Length)
                return false;
            int count = _data[node];
            int p = node + 1;
            int found = -1;
            for (int c = 0; c < count && p < _data.Length; c++)
            {
                int entry = _data[p];
                if (((entry & 0x1F) | 0x60) == word[i])
                {
                    found = p;
                    break;
                }
                p += (entry >> 6) + 1;
            }
            if (found < 0)
                return false;

            int flags = _data[found];
            if (i == word.Length - 1)
                return (flags & 0x20) != 0;

            int size = flags >> 6;
            if (size == 0 || found + size >= _data.Length)
                return false;
            int offset = _data[found + 1];
            if (size >= 2)
                offset |= _data[found + 2] << 8;
            if (size >= 3)
                offset |= _data[found + 3] << 16;
            node += offset;
        }
        return false;
    }
}
