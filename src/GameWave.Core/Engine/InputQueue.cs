namespace GameWave.Engine;

/// <summary>The keys on a Game Wave remote, numbered as the games number them.</summary>
public enum RemoteKey
{
    Num0 = 0, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9,
    Up = 10,
    Down = 11,
    Right = 12,
    Left = 13,
    Select = 14,
    DvdMenu = 15,
    A = 16,
    B = 17,
    C = 18,
    D = 19,
    GameMenu = 20,
}

/// <summary>The six player remotes, by colour, as the games number them.</summary>
public enum Remote
{
    Red = 1,
    Yellow = 2,
    Blue = 3,
    Green = 4,
    Purple = 5,
    Orange = 6,
}

public readonly record struct KeyEvent(int Key, int Remote, long Timestamp);

/// <summary>The engine's key queue, fed by the host and drained by <c>input.GetKey</c>.</summary>
public sealed class InputQueue
{
    internal sealed record State(KeyEvent[] Events, int Capacity, int Mode, bool RemotesEnabled, int[] RandomKeys);
    public const int NoKey = 255;
    public const int NoRemote = 255;

    readonly Queue<KeyEvent> _queue = new();
    readonly object _gate = new();
    int _capacity = 16;
    readonly Random _random = new();
    int[] _randomKeys = [];

    /// <summary>0 normal; 1 "auto" mode, where the engine invents key presses for soak testing.</summary>
    public int Mode { get; set; }

    public bool RemotesEnabled { get; set; } = true;

    public int Capacity
    {
        get { lock (_gate) return _capacity; }
        set { lock (_gate) _capacity = Math.Max(1, value); }
    }

    public void SetRandomKeys(int[] keys)
    {
        lock (_gate)
            _randomKeys = keys;
    }

    /// <summary>The keys the game says make sense on its current screen, for its auto mode; often empty.</summary>
    public int[] RandomKeys
    {
        get { lock (_gate) return _randomKeys; }
    }

    public void Push(RemoteKey key, Remote remote, long timestamp)
    {
        lock (_gate)
        {
            if (_queue.Count >= _capacity)
                return;
            _queue.Enqueue(new KeyEvent((int)key, (int)remote, timestamp));
            Monitor.PulseAll(_gate);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _queue.Clear();
    }

    internal State CaptureState()
    {
        lock (_gate)
            return new State(_queue.ToArray(), _capacity, Mode, RemotesEnabled, (int[])_randomKeys.Clone());
    }

    internal void RestoreState(State state)
    {
        lock (_gate)
        {
            _queue.Clear();
            foreach (var item in state.Events)
                _queue.Enqueue(item);
            _capacity = state.Capacity;
            Mode = state.Mode;
            RemotesEnabled = state.RemotesEnabled;
            _randomKeys = (int[])state.RandomKeys.Clone();
            Monitor.PulseAll(_gate);
        }
    }

    public bool TryTake(out KeyEvent e, long now)
    {
        lock (_gate)
        {
            if (Mode == 1 && _randomKeys.Length > 0 && _queue.Count == 0)
            {
                e = new KeyEvent(_randomKeys[_random.Next(_randomKeys.Length)], _random.Next(1, 7), now);
                return true;
            }
            return _queue.TryDequeue(out e);
        }
    }

    /// <summary>Waits up to <paramref name="ms"/> for a key to arrive.</summary>
    public void WaitForAny(int ms)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
                Monitor.Wait(_gate, ms);
        }
    }
}
