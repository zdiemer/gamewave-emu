using GameWave.Lua;

namespace GameWave.Tests;

public class LibraryTests
{
    readonly LuaState _s;

    public LibraryTests()
    {
        _s = new LuaState { Output = _ => { } };
        BaseLib.Open(_s);
        StringLib.Open(_s);
    }

    LuaValue[] Call(string module, string name, params LuaValue[] args)
        => _s.MainThread.Call(_s.Globals[module].AsTable![name], args);

    [Theory]
    [InlineData("%d", 42, "42")]
    [InlineData("%05d", 42, "00042")]
    [InlineData("%-4d|", 7, "7   |")]
    [InlineData("%x", 255, "ff")]
    [InlineData("%02d,", 5, "05,")]
    public void FormatsNumbers(string format, int value, string expected)
        => Assert.Equal(expected, Call("string", "format", format, value)[0].AsString);

    [Fact]
    public void FormatsStrings()
        => Assert.Equal("[ab]", Call("string", "format", "[%s]", "ab")[0].AsString);

    [Fact]
    public void FindsPatterns()
    {
        var r = Call("string", "find", "hello world", "(o%s*w)");
        Assert.Equal(5, r[0].N);
        Assert.Equal(7, r[1].N);
        Assert.Equal("o w", r[2].AsString);
    }

    [Fact]
    public void FindsPlainText()
    {
        var r = Call("string", "find", "a.b.c", ".", 3, true);
        Assert.Equal(4, r[0].N);
    }

    [Fact]
    public void SubstitutesWithCaptures()
    {
        var r = Call("string", "gsub", "hello world", "(%w+)", "<%1>");
        Assert.Equal("<hello> <world>", r[0].AsString);
        Assert.Equal(2, r[1].N);
    }

    [Fact]
    public void SubHandlesNegativeIndexes()
        => Assert.Equal("lo", Call("string", "sub", "hello", -2)[0].AsString);

    [Fact]
    public void TableInsertRemoveAndSort()
    {
        var t = new LuaTable();
        Call("table", "insert", t, 3);
        Call("table", "insert", t, 1);
        Call("table", "insert", t, 2);
        Call("table", "sort", t);
        Assert.Equal([1, 2, 3], new[] { t[1].N, t[2].N, t[3].N });
        Assert.Equal(1, Call("table", "remove", t, 1)[0].N);
        Assert.Equal(2, Call("table", "getn", t)[0].N);
    }

    [Fact]
    public void ToNumberRejectsFractions()
    {
        Assert.Equal(12, Call("_G", "tonumber", "12")[0].N);
        Assert.True(Call("_G", "tonumber", "1.5")[0].IsNil);
        Assert.Equal(255, Call("_G", "tonumber", "ff", 16)[0].N);
    }

    [Fact]
    public void NextSurvivesClearingDuringTraversal()
    {
        var t = new LuaTable();
        for (int i = 0; i < 20; i++)
            t[$"k{i}"] = i;
        int seen = 0;
        var key = LuaValue.Nil;
        while (t.Next(key, out var k, out _))
        {
            t[k] = LuaValue.Nil;
            key = k;
            seen++;
        }
        Assert.Equal(20, seen);
    }

    [Fact]
    public void ArrayPartTracksLength()
    {
        var t = new LuaTable();
        for (int i = 1; i <= 5; i++)
            t[i] = i * 10;
        Assert.Equal(5, t.Length);
        t[5] = LuaValue.Nil;
        Assert.Equal(4, t.Length);
    }
}
