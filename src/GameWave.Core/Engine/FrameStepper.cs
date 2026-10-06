namespace GameWave.Engine;

/// <summary>Cooperatively runs the game thread within a host-supplied time budget.</summary>
sealed class FrameStepper(EmuClock clock)
{
    readonly object _gate = new();
    double _end;
    bool _waiting, _finished, _cancelled;

    public void Begin()
    {
        lock (_gate)
        {
            _end = clock.NowSeconds;
            _waiting = _finished = _cancelled = false;
        }
    }

    public void Run(double seconds)
    {
        lock (_gate)
        {
            _end += seconds;
            _waiting = false;
            Monitor.PulseAll(_gate);
            var deadline = Environment.TickCount64 + 5000;
            while (!_waiting && !_finished && !_cancelled)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    throw new TimeoutException("The game did not yield within a frame.");
                Monitor.Wait(_gate, (int)remaining);
            }
            if (_finished)
                clock.SetExternalTime(_end);
        }
    }

    public void AdvanceTo(double until)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_cancelled)
                    throw new MachineStoppedException();
                if (clock.NowSeconds >= _end)
                {
                    _waiting = true;
                    Monitor.PulseAll(_gate);
                    Monitor.Wait(_gate);
                    continue;
                }
                if (until <= clock.NowSeconds)
                    return;
                clock.SetExternalTime(Math.Min(until, _end));
            }
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            _finished = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancelled = true;
            Monitor.PulseAll(_gate);
        }
    }
}
