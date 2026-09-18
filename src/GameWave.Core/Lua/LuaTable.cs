namespace GameWave.Lua;

/// <summary>
/// A Lua table: an array part for keys 1..n and an insertion-ordered hash part. Clearing a
/// hash entry leaves a tombstone so that <c>next</c> can keep walking while a loop clears
/// fields, which Lua allows. Tombstones are swept only when a new key is added.
/// </summary>
public sealed class LuaTable
{
    LuaValue[] _array = [];
    int _arrayCount;

    readonly Dictionary<LuaValue, int> _index = new();
    LuaValue[] _keys = [];
    LuaValue[] _values = [];
    int _entryCount;
    int _deadCount;

    public LuaTable? Metatable { get; set; }

    public LuaTable() { }

    public LuaTable(int arrayHint, int hashHint)
    {
        if (arrayHint > 0)
            _array = new LuaValue[arrayHint];
        if (hashHint > 0)
        {
            _keys = new LuaValue[hashHint];
            _values = new LuaValue[hashHint];
        }
    }

    public LuaValue this[LuaValue key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public LuaValue this[string key]
    {
        get => Get(LuaValue.String(key));
        set => Set(LuaValue.String(key), value);
    }

    public LuaValue this[int key]
    {
        get => Get(key);
        set => Set(LuaValue.Number(key), value);
    }

    public LuaValue Get(int key)
    {
        if ((uint)(key - 1) < (uint)_arrayCount)
            return _array[key - 1];
        return GetHash(LuaValue.Number(key));
    }

    public LuaValue Get(LuaValue key)
    {
        if (key.Type == LuaType.Number && (uint)(key.N - 1) < (uint)_arrayCount)
            return _array[key.N - 1];
        if (key.Type == LuaType.Nil)
            return LuaValue.Nil;
        return GetHash(key);
    }

    LuaValue GetHash(LuaValue key)
    {
        if (_entryCount == 0)
            return LuaValue.Nil;
        return _index.TryGetValue(key, out int slot) ? _values[slot] : LuaValue.Nil;
    }

    public void Set(LuaValue key, LuaValue value)
    {
        if (key.Type == LuaType.Number)
        {
            int k = key.N;
            if ((uint)(k - 1) < (uint)_arrayCount)
            {
                _array[k - 1] = value;
                if (value.IsNil && k == _arrayCount)
                    TrimArray();
                return;
            }
            if (k == _arrayCount + 1 && !value.IsNil)
            {
                AppendArray(value);
                return;
            }
        }
        else if (key.Type == LuaType.Nil)
        {
            throw new LuaException("table index is nil");
        }
        SetHash(key, value);
    }

    void AppendArray(LuaValue value)
    {
        // The key may already sit in the hash part (it was added before the array reached it).
        var numKey = LuaValue.Number(_arrayCount + 1);
        if (_entryCount > 0 && _index.TryGetValue(numKey, out int slot))
            RemoveSlot(numKey, slot);
        if (_arrayCount == _array.Length)
            Array.Resize(ref _array, Math.Max(4, _array.Length * 2));
        _array[_arrayCount++] = value;

        // Pull any following integer keys across from the hash part.
        while (_entryCount - _deadCount > 0)
        {
            var next = LuaValue.Number(_arrayCount + 1);
            if (!_index.TryGetValue(next, out int s) || _values[s].IsNil)
                break;
            var v = _values[s];
            RemoveSlot(next, s);
            if (_arrayCount == _array.Length)
                Array.Resize(ref _array, _array.Length * 2);
            _array[_arrayCount++] = v;
        }
    }

    void RemoveSlot(LuaValue key, int slot)
    {
        // Leave a tombstone rather than moving entries, so traversal order stays stable.
        _values[slot] = LuaValue.Nil;
        _keys[slot] = LuaValue.Nil;
        _index.Remove(key);
        _deadCount++;
    }

    void TrimArray()
    {
        while (_arrayCount > 0 && _array[_arrayCount - 1].IsNil)
            _arrayCount--;
    }

    void SetHash(LuaValue key, LuaValue value)
    {
        if (_index.TryGetValue(key, out int slot))
        {
            // A cleared entry keeps its key so next() can continue from it; it counts as dead.
            bool wasDead = _values[slot].IsNil;
            _values[slot] = value;
            if (value.IsNil && !wasDead)
                _deadCount++;
            else if (!value.IsNil && wasDead)
                _deadCount--;
            return;
        }
        if (value.IsNil)
            return;
        if (_entryCount == _keys.Length)
        {
            if (_deadCount > _entryCount / 2)
                Compact();
            if (_entryCount == _keys.Length)
            {
                int n = Math.Max(4, _keys.Length * 2);
                Array.Resize(ref _keys, n);
                Array.Resize(ref _values, n);
            }
        }
        _keys[_entryCount] = key;
        _values[_entryCount] = value;
        _index[key] = _entryCount;
        _entryCount++;
    }

    void Compact()
    {
        int w = 0;
        _index.Clear();
        for (int r = 0; r < _entryCount; r++)
        {
            if (_values[r].IsNil)
                continue;
            _keys[w] = _keys[r];
            _values[w] = _values[r];
            _index[_keys[w]] = w;
            w++;
        }
        for (int i = w; i < _entryCount; i++)
        {
            _keys[i] = LuaValue.Nil;
            _values[i] = LuaValue.Nil;
        }
        _entryCount = w;
        _deadCount = 0;
    }

    /// <summary>Lua's <c>next</c>. Returns false at the end of the traversal.</summary>
    public bool Next(LuaValue key, out LuaValue nextKey, out LuaValue nextValue)
    {
        int i;
        if (key.IsNil)
        {
            i = 0;
        }
        else if (key.Type == LuaType.Number && (uint)(key.N - 1) < (uint)_arrayCount)
        {
            i = key.N;
        }
        else
        {
            if (!_index.TryGetValue(key, out int slot))
                throw new LuaException("invalid key to `next'");
            i = _arrayCount + slot + 1;
        }

        for (; i < _arrayCount; i++)
        {
            if (!_array[i].IsNil)
            {
                nextKey = LuaValue.Number(i + 1);
                nextValue = _array[i];
                return true;
            }
        }
        for (int s = i - _arrayCount; s < _entryCount; s++)
        {
            if (!_values[s].IsNil)
            {
                nextKey = _keys[s];
                nextValue = _values[s];
                return true;
            }
        }
        nextKey = LuaValue.Nil;
        nextValue = LuaValue.Nil;
        return false;
    }

    /// <summary>A border of the table, as <c>luaH_getn</c> computes one.</summary>
    public int Length
    {
        get
        {
            if (_arrayCount > 0)
            {
                if (!_array[_arrayCount - 1].IsNil)
                {
                    if (_entryCount == 0)
                        return _arrayCount;
                    int j = _arrayCount + 1;
                    while (!Get(j).IsNil)
                        j++;
                    return j - 1;
                }
                int lo = 0, hi = _arrayCount;
                while (hi - lo > 1)
                {
                    int m = (lo + hi) / 2;
                    if (_array[m - 1].IsNil) hi = m;
                    else lo = m;
                }
                return lo;
            }
            int n = 0;
            while (!GetHash(LuaValue.Number(n + 1)).IsNil)
                n++;
            return n;
        }
    }

    public IEnumerable<KeyValuePair<LuaValue, LuaValue>> Pairs()
    {
        var k = LuaValue.Nil;
        while (Next(k, out var nk, out var nv))
        {
            yield return new(nk, nv);
            k = nk;
        }
    }
}
