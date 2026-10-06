using System.Diagnostics;

namespace GameWave.Engine;

/// <summary>
/// The console's millisecond clock (<c>time.GetRealTime</c>). It stops while the emulator
/// is paused, so games never see time pass that the player did not.
/// </summary>
public sealed class EmuClock
{
    readonly Stopwatch _watch = Stopwatch.StartNew();
    readonly object _gate = new();
    long _pausedAt = -1;
    long _pausedTotal;
    double? _externalSeconds;

    internal void SetExternalTime(double seconds)
    {
        lock (_gate)
            _externalSeconds = seconds;
    }

    /// <summary>Milliseconds since the machine started, not counting paused time.</summary>
    public long Now
    {
        get
        {
            lock (_gate)
            {
                if (_externalSeconds is { } seconds)
                    return (long)(seconds * 1000);
                long raw = _pausedAt >= 0 ? _pausedAt : _watch.ElapsedMilliseconds;
                return raw - _pausedTotal;
            }
        }
    }

    /// <summary>Microsecond-precision time, for A/V sync.</summary>
    public double NowSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_externalSeconds is { } seconds)
                    return seconds;
                double raw = _pausedAt >= 0 ? _pausedAt / 1000.0 : _watch.Elapsed.TotalSeconds;
                return raw - _pausedTotal / 1000.0;
            }
        }
    }

    public bool Paused
    {
        get { lock (_gate) return _pausedAt >= 0; }
        set
        {
            lock (_gate)
            {
                if (value && _pausedAt < 0)
                    _pausedAt = _watch.ElapsedMilliseconds;
                else if (!value && _pausedAt >= 0)
                {
                    _pausedTotal += _watch.ElapsedMilliseconds - _pausedAt;
                    _pausedAt = -1;
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Moves emulated time to a saved instant without changing its pause state.</summary>
    internal void Restore(long now)
    {
        lock (_gate)
        {
            if (_externalSeconds.HasValue) _externalSeconds = Math.Max(0, now) / 1000.0;
            bool paused = _pausedAt >= 0;
            _watch.Restart();
            _pausedTotal = -Math.Max(0, now);
            _pausedAt = paused ? 0 : -1;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Waits until the clock reaches <paramref name="until"/>, or <paramref name="cancel"/> is
    /// set. Returns false when cancelled.
    /// </summary>
    public bool WaitUntil(long until, Func<bool> cancel)
    {
        while (true)
        {
            if (cancel())
                return false;
            long now = Now;
            if (now >= until)
                return true;
            lock (_gate)
            {
                int wait = _pausedAt >= 0 ? 50 : (int)Math.Clamp(until - now, 1, 20);
                Monitor.Wait(_gate, wait);
            }
        }
    }
}
