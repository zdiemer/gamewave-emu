namespace GameWave.Engine;

/// <summary>A word dictionary for <c>dict.Lookup</c> (Letter Zap's .zdt files).</summary>
public sealed class WordList
{
    readonly HashSet<string> _words = new(StringComparer.Ordinal);

    public int Count => _words.Count;

    public bool Contains(string word) => _words.Contains(word.ToLowerInvariant());

    public static WordList Load(byte[] data)
    {
        var list = new WordList();
        return list;
    }
}
