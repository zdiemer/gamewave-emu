using GameWave.Disc;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Engine;

/// <summary>Raised only inside the game thread to unwind a host call after quickload.</summary>
sealed class LuaStateRestoredException : Exception
{
}

public sealed partial class Machine
{
    enum QuickRequestKind { Save, Load }

    sealed class QuickRequest(QuickRequestKind kind)
    {
        public QuickRequestKind Kind { get; } = kind;
        public ManualResetEventSlim Done { get; } = new(false);
        public string? Error;
    }

    sealed record FileState(DiscFile File, long Position);

    sealed class ApiState
    {
        public required Dictionary<int, string> Resources { get; init; }
        public required Dictionary<int, Font> Fonts { get; init; }
        public required Dictionary<int, (int Texture, int Overlay)> Texts { get; init; }
        public required Dictionary<int, Picture> Iframes { get; init; }
        public required Dictionary<int, Sound> Sounds { get; init; }
        public required Dictionary<int, FileState> Files { get; init; }
        public required Dictionary<int, WordList> Dictionaries { get; init; }
        public required List<SaveStore.Slot> EnumeratedSaves { get; init; }
        public int NextId;
        public int BuiltinFontId;
        public int RandSeed;
        public int TrayState;
    }

    sealed class QuickState
    {
        public required LuaStateSnapshot Lua { get; init; }
        public required ApiState Api { get; init; }
        public required Osd.State Osd { get; init; }
        public required InputQueue.State Input { get; init; }
        public required AudioMixer.State Audio { get; init; }
        public required VideoPlane.State Video { get; init; }
        public required long Clock { get; init; }
    }

    readonly object _quickGate = new();
    volatile QuickRequest? _quickRequest;
    QuickState? _quickState;

    /// <summary>Whether this machine has a state in its single quicksave slot.</summary>
    public bool HasQuickSave
    {
        get { lock (_quickGate) return _quickState is not null; }
    }

    /// <summary>Captures the running game in this machine's single quicksave slot.</summary>
    /// <returns>Null on success, otherwise a message suitable for the user.</returns>
    public string? QuickSave() => SubmitQuickRequest(QuickRequestKind.Save);

    /// <summary>Restores this machine's single quicksave slot.</summary>
    /// <returns>Null on success, otherwise a message suitable for the user.</returns>
    public string? QuickLoad() => SubmitQuickRequest(QuickRequestKind.Load);

    string? SubmitQuickRequest(QuickRequestKind kind)
    {
        if (State != MachineState.Running || _thread is null || !_thread.IsAlive)
            return "The game is not running";
        var request = new QuickRequest(kind);
        lock (_quickGate)
        {
            if (kind == QuickRequestKind.Load && _quickState is null)
                return "No quicksave for this game";
            if (_quickRequest is not null)
                return "Another save-state operation is in progress";
            _quickRequest = request;
        }

        var wasPaused = Paused;

        // A paused game is usually inside an engine poll rather than at a bytecode
        // boundary. Let it advance to the safe point; the host reapplies pause afterwards.
        if (wasPaused)
            Paused = false;
        try
        {
            if (!request.Done.Wait(TimeSpan.FromSeconds(5)))
            {
                lock (_quickGate)
                    if (ReferenceEquals(_quickRequest, request))
                        _quickRequest = null;
                return "The game did not reach a safe save-state point";
            }
            return request.Error;
        }
        finally
        {
            if (wasPaused)
                Paused = true;
        }
    }

    bool ServiceQuickState(bool insideNativeCall)
    {
        var request = _quickRequest;
        var lua = _lua;
        if (request is null || lua is null || lua.CurrentThread != lua.MainThread
            || lua.MainThread.NativeDepth != 1)
            return false;

        lock (_quickGate)
        {
            if (!ReferenceEquals(_quickRequest, request))
                return false;
            _quickRequest = null;
        }

        bool restored = false;
        try
        {
            if (request.Kind == QuickRequestKind.Save)
            {
                var state = CaptureQuickState(lua);
                lock (_quickGate)
                    _quickState = state;
            }
            else
            {
                QuickState state;
                lock (_quickGate)
                    state = _quickState!;
                RestoreQuickState(lua, state);
                restored = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            request.Error = $"{(request.Kind == QuickRequestKind.Save ? "Quicksave" : "Quickload")} failed: {ex.Message}";
        }
        finally
        {
            request.Done.Set();
        }

        if (restored && insideNativeCall)
            throw new LuaStateRestoredException();
        return restored;
    }

    QuickState CaptureQuickState(LuaState lua, bool resumeBoundary = false) => new()
    {
        Lua = LuaStateSnapshot.Capture(lua, resumeBoundary),
        Api = CaptureApiState(),
        Osd = Osd.CaptureState(),
        Input = Input.CaptureState(),
        Audio = Audio.CaptureState(),
        Video = Video.CaptureState(),
        Clock = Clock.Now,
    };

    void RestoreQuickState(LuaState lua, QuickState state)
    {
        RestoreApiState(state.Api);
        Video.RestoreState(state.Video);
        Audio.RestoreState(state.Audio);
        Osd.RestoreState(state.Osd);
        Input.RestoreState(state.Input);
        Clock.Restore(state.Clock);
        state.Lua.Restore(lua);
    }

    ApiState CaptureApiState() => new()
    {
        Resources = new(_resources),
        Fonts = new(_fonts),
        Texts = new(_texts),
        Iframes = _iframes.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
        Sounds = new(_sounds),
        Files = _files.ToDictionary(pair => pair.Key, pair => new FileState(pair.Value.File, pair.Value.Stream.Position)),
        Dictionaries = new(_dictionaries),
        EnumeratedSaves = new(_enumeratedSaves),
        NextId = _nextId,
        BuiltinFontId = _builtinFontId,
        RandSeed = _randSeed,
        TrayState = _trayState,
    };

    void RestoreApiState(ApiState state)
    {
        var reopened = new Dictionary<int, (DiscFile File, Stream Stream)>();
        try
        {
            foreach (var (id, saved) in state.Files)
            {
                var stream = saved.File.Open();
                stream.Position = Math.Clamp(saved.Position, 0, stream.Length);
                reopened.Add(id, (saved.File, stream));
            }
        }
        catch
        {
            foreach (var file in reopened.Values)
                file.Stream.Dispose();
            throw;
        }

        foreach (var file in _files.Values)
            file.Stream.Dispose();
        _resources.Clear();
        foreach (var pair in state.Resources) _resources.Add(pair.Key, pair.Value);
        _fonts.Clear();
        foreach (var pair in state.Fonts) _fonts.Add(pair.Key, pair.Value);
        _texts.Clear();
        foreach (var pair in state.Texts) _texts.Add(pair.Key, pair.Value);
        _iframes.Clear();
        foreach (var pair in state.Iframes) _iframes.Add(pair.Key, pair.Value.Clone());
        _sounds.Clear();
        foreach (var pair in state.Sounds) _sounds.Add(pair.Key, pair.Value);
        _files.Clear();
        foreach (var pair in reopened)
            _files.Add(pair.Key, pair.Value);
        _dictionaries.Clear();
        foreach (var pair in state.Dictionaries) _dictionaries.Add(pair.Key, pair.Value);
        _enumeratedSaves = new(state.EnumeratedSaves);
        _nextId = state.NextId;
        _builtinFontId = state.BuiltinFontId;
        _randSeed = state.RandSeed;
        _trayState = state.TrayState;
    }

    void CancelQuickRequest(string error)
    {
        QuickRequest? request;
        lock (_quickGate)
        {
            request = _quickRequest;
            _quickRequest = null;
        }
        if (request is null) return;
        request.Error = error;
        request.Done.Set();
    }
}
