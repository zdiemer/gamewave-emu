using GameWave.Disc;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Engine;

/// <summary>Raised inside the game thread to unwind it when the machine stops or resets.</summary>
public sealed class MachineStoppedException : Exception
{
}

public enum MachineState
{
    Stopped,
    Running,
    Finished,
    Crashed,
    /// <summary>The game opened the disc tray and is waiting for a disc.</summary>
    TrayOpen,
}

/// <summary>
/// A Game Wave console with a disc in it. The game's Lua program runs on its own thread,
/// as it did on the console's game task; the host reads frames and sound from the machine
/// and feeds it key presses.
/// </summary>
public sealed partial class Machine : IDisposable
{
    public IDisc Disc { get; private set; }
    public GameInfo Info { get; private set; }
    public Osd Osd { get; } = new();
    public EmuClock Clock { get; } = new();
    public InputQueue Input { get; } = new();
    public AudioMixer Audio { get; }
    public VideoPlane Video { get; }
    public SaveStore Saves { get; }

    /// <summary>Called for every line the game logs, and for the machine's own messages.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>How much of the game's own logging to pass on (the engine's log levels 0..5).</summary>
    public int LogLevel { get; set; } = 3;

    public MachineState State { get; private set; } = MachineState.Stopped;
    public string? CrashMessage { get; private set; }

    /// <summary>Raised (on the game thread) when the game opens the disc tray.</summary>
    public event Action? TrayOpened;

    Thread? _thread;
    volatile bool _stopRequested;
    volatile bool _resetRequested;
    volatile bool _trayRequested;
    LuaState? _lua;
    EngineResources? _engineResources;
    int _pollCount;
    readonly FrameStepper? _frames;
    int _frameInstructions;
    bool _portableResume;
    double? _sleepUntil, _resumeSleepUntil;

    public Machine(IDisc disc, SaveStore saves, bool frameDriven = false)
    {
        Disc = disc;
        Info = ReadInfo(disc);
        Saves = saves;
        Audio = new AudioMixer(Clock, externalDevice: frameDriven);
        Video = new VideoPlane(Audio);
        if (frameDriven)
        {
            Clock.SetExternalTime(0);
            _frames = new FrameStepper(Clock);
        }
    }

    /// <summary>Runs a frame in a machine constructed with frameDriven enabled.</summary>
    public void RunFrame(double seconds)
    {
        if (_frames is null)
            throw new InvalidOperationException("This machine uses the wall clock.");
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _frames.Run(seconds);
    }

    static GameInfo ReadInfo(IDisc disc)
    {
        var diz = disc.Find("/gamewave.diz");
        if (diz is null)
            throw new InvalidDataException("this is not a Game Wave disc (there is no gamewave.diz)");
        return GameInfo.Parse(System.Text.Encoding.Latin1.GetString(diz.ReadAll()));
    }

    public string Title => string.IsNullOrEmpty(Info.AppName) ? Disc.Label : Info.AppName;

    internal EngineResources EngineResources
        => _engineResources ??= EngineResources.Load(Disc, Info);

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_thread is not null && _thread.IsAlive)
            return;
        RequestFineTimer();
        _stopRequested = false;
        _frames?.Begin();
        _frameInstructions = _pollCount = 0;
        _sleepUntil = _resumeSleepUntil = null;
        _portableResume = false;
        State = MachineState.Running;
        CrashMessage = null;
        _thread = new Thread(GameThread) { IsBackground = true, Name = "Game Wave game" };
        _thread.Start();
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);

    static bool _fineTimer;

    /// <summary>
    /// Asks Windows for 1 ms timer resolution: the games sleep and poll in steps of a few
    /// milliseconds, which the default 15.6 ms tick would stretch. SDL asks for the same when
    /// a window opens; a headless run has no SDL.
    /// </summary>
    static void RequestFineTimer()
    {
        if (_fineTimer || !OperatingSystem.IsWindows())
            return;
        _fineTimer = true;
        try
        {
            timeBeginPeriod(1);
        }
        catch (DllNotFoundException)
        {
        }
    }

    public void Stop()
    {
        var t = _thread;
        if (t is null)
            return;
        _stopRequested = true;
        _frames?.Cancel();
        CancelQuickRequest("The game stopped");
        Clock.Paused = false;
        Video.StopMovie(true);
        if (!t.Join(3000))
            Emit("The game thread did not stop in time.");
        _thread = null;
        Audio.StopAll();
        Video.Clear();
        Osd.Reset();
        if (State == MachineState.Running)
            State = MachineState.Stopped;
    }

    /// <summary>Restarts the game from its boot, as the console's reset does.</summary>
    public void Reset()
    {
        Stop();
        Start();
    }

    /// <summary>Takes out the disc and puts another in, keeping the machine powered on.</summary>
    public void ChangeDisc(IDisc disc)
    {
        // Read the new disc first, so one that is not a Game Wave disc leaves the old one in.
        GameInfo info;
        try
        {
            info = ReadInfo(disc);
        }
        catch
        {
            disc.Dispose();
            throw;
        }
        Stop();
        lock (_quickGate)
            _quickState = null;
        Disc.Dispose();
        Disc = disc;
        Info = info;
        _engineResources = null;
        _stateIdentity = null;
        Start();
    }

    public void Dispose()
    {
        Stop();
        Audio.Dispose();
        Disc.Dispose();
    }

    void GameThread()
    {
        try
        {
            GameThreadLoop();
        }
        catch (MachineStoppedException)
        {
        }
        finally
        {
            CancelQuickRequest("The game stopped");
            _frames?.Finish();
        }
    }

    void GameThreadLoop()
    {
        while (true)
        {
            _resetRequested = false;
            _trayRequested = false;
            try
            {
                RunGame();
                if (!_stopRequested)
                {
                    State = MachineState.Finished;
                    Emit("The game program ended.");
                }
            }
            catch (PortableStateRestoredException)
            {
                continue;
            }
            catch (MachineStoppedException)
            {
            }
            catch (LuaException e)
            {
                State = MachineState.Crashed;
                CrashMessage = e.Message;
                Emit($"Game script error: {e.Message}");
                if (e.LuaTraceback is { } tb)
                    Emit(tb);
            }
            catch (Exception e)
            {
                State = MachineState.Crashed;
                CrashMessage = e.Message;
                Emit($"Emulator error: {e}");
            }
            if (_trayRequested && !_stopRequested)
            {
                State = MachineState.TrayOpen;
                Audio.StopAll();
                Osd.Reset();
                TrayOpened?.Invoke();
                break;
            }
            if (_resetRequested && !_stopRequested)
            {
                // engine.SoftwareReset(): clear everything and boot again.
                Video.StopMovie(true);
                Video.Clear();
                Audio.StopAll();
                Osd.Reset();
                State = MachineState.Running;
                continue;
            }
            break;
        }
    }

    void RunGame()
    {
        LuaState L;
        if (_portableResume)
        {
            _portableResume = false;
            L = _lua!;
        }
        else
        {
            var file = Disc.Find(Info.AppFile) ?? throw new FileNotFoundException($"the game program {Info.AppFile} is not on the disc");
            var proto = ZbcLoader.Load(file.ReadAll(), Info.AppFile);
            L = CreateLuaState(reset: true);
            L.MainThread.Push(LuaValue.Function(L.Load(proto)));
            L.MainThread.PreCall(0, LuaThread.MultRet);
            L.MainThread.Frames[0].Boundary = true;
            L.MainThread.NativeDepth = 1;
        }
        _lua = L;
        _frames?.AdvanceTo(Clock.NowSeconds);
        L.MainThread.Execute();
    }

    LuaState CreateLuaState(bool reset)
    {
        var L = new LuaState { Output = s => Emit(s.TrimEnd('\n')) };
        L.InstructionBoundary = () =>
        {
            if (_frames is not null && ++_frameInstructions >= 10000)
            {
                _frameInstructions = 0;
                if (StopPending)
                    throw new MachineStoppedException();
                _frames.AdvanceTo(Clock.NowSeconds + 0.001);
            }
            return ServiceQuickState(insideNativeCall: false);
        };
        BaseLib.Open(L);
        StringLib.Open(L);
        RegisterApi(L, reset);
        return L;
    }

    internal void Emit(string message) => Log?.Invoke(message);

    /// <summary>Throws out of the game thread when it has been asked to stop.</summary>
    internal void CheckStop()
    {
        ServiceQuickState(insideNativeCall: true);
        if (_stopRequested || _resetRequested || _trayRequested)
            throw new MachineStoppedException();
    }

    internal bool StopPending => _stopRequested || _resetRequested || _trayRequested;

    /// <summary>Shows the "insert disc" screen and stops the game until a disc goes in.</summary>
    internal void OpenTray()
    {
        Video.StopMovie(false);
        var still = EngineResources.Get("insert_disc.m2v") is { } d ? DecodeStill(d) : null;
        if (still is null)
            Video.Clear();
        else
            Video.ShowStill(still);
        _trayRequested = true;
        throw new MachineStoppedException();
    }

    /// <summary>Opens the tray from outside the game, as the console's eject button does.</summary>
    public void OpenTrayFromHost()
    {
        if (State != MachineState.Running || _thread is null)
            return;
        Video.StopMovie(false);
        var still = EngineResources.Get("insert_disc.m2v") is { } d ? DecodeStill(d) : null;
        if (still is null)
            Video.Clear();
        else
            Video.ShowStill(still);
        _trayRequested = true;
        Clock.Paused = false;
        _thread.Join(3000);
    }

    /// <summary>Closes the tray on the disc already in it, which boots it again.</summary>
    public void CloseTray()
    {
        if (State != MachineState.TrayOpen)
            return;
        _thread?.Join(1000);
        _thread = null;
        Start();
    }

    internal void RequestSoftwareReset()
    {
        _resetRequested = true;
        throw new MachineStoppedException();
    }

    /// <summary>
    /// Called by polling functions. Games spin on <c>input.GetKey</c> and friends with no
    /// sleep, which the console's slow CPU made harmless; here it would burn a core, so every
    /// few polls the game thread naps for a millisecond.
    /// </summary>
    internal void Poll()
    {
        CheckStop();
        if (++_pollCount >= 8)
        {
            _pollCount = 0;
            if (_frames is null)
                Thread.Sleep(1);
            else
                _frames.AdvanceTo(Clock.NowSeconds + 0.001);
        }
        WaitWhilePaused();
    }

    internal void WaitWhilePaused()
    {
        while (Clock.Paused)
        {
            CheckStop();
            Thread.Sleep(10);
        }
    }

    internal void Sleep(int ms)
    {
        _pollCount = 0;
        if (_frames is not null)
        {
            CheckStop();
            _sleepUntil = _resumeSleepUntil ?? Clock.NowSeconds + Math.Max(0, ms) / 1000.0;
            _resumeSleepUntil = null;
            _frames.AdvanceTo(_sleepUntil.Value);
            _sleepUntil = null;
            return;
        }
        long until = Clock.Now + Math.Max(0, ms);
        while (!Clock.WaitUntil(until, () => StopPending || _quickRequest is not null))
            CheckStop();
    }

    // ------------------------------------------------------------------ display

    /// <summary>Composes the current picture: the video plane with the OSD over it.</summary>
    public void RenderFrame(uint[] frame)
    {
        Osd.Animate(Clock.Now);
        Video.Render(frame);
        Osd.Composite(frame);
    }

    public bool Paused
    {
        get => Clock.Paused;
        set
        {
            Clock.Paused = value;
            Audio.Paused = value;
        }
    }
}
