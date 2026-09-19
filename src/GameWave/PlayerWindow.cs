using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.SDL;
using GameWave.Config;
using GameWave.Disc;
using GameWave.Engine;
using Osd = GameWave.Graphics.Osd;
using PngWriter = GameWave.Graphics.PngWriter;
using GameWave.Input;
using GameWave.Ui;
using GameWave.Ui.Native;

namespace GameWave;

/// <summary>
/// SDL front end: the window, sound, controllers, the menus, and the console in between.
/// </summary>
/// <remarks>
/// The console runs its game on a thread of its own and keeps its own time, so this loop
/// only has to draw what the console shows, pull its sound (on SDL's audio thread) and
/// hand it the players' key presses.
/// </remarks>
public sealed unsafe class PlayerWindow : IDisposable
{
    private const int SampleRate = 44100;

    /// <summary>Timer that keeps the picture alive while a native menu holds the thread.</summary>
    private const nuint MenuLoopTimerId = 1;

    private Machine? _machine;
    private string? _discPath;

    private readonly GameWaveSettings _settings;
    private readonly InputMap _input;
    private readonly SettingsSession _session;

    private readonly Sdl _sdl;
    private readonly Silk.NET.SDL.Window* _window;
    private readonly Renderer* _renderer;
    private readonly uint _audioDevice;
    private readonly PfnAudioCallback _audioCallback;

    private readonly Canvas _canvas = new();
    private readonly MenuController _menu = new();
    private readonly StatusOverlay _overlay = new();

    private readonly uint[] _frame = new uint[Osd.Width * Osd.Height];
    private Texture* _videoTexture;
    private Texture* _overlayTexture;

    /// <summary>Open controllers in connection order, by SDL instance id.</summary>
    private readonly List<(int Instance, nint Handle, string Name)> _controllers = new();
    private readonly HashSet<(int Instance, int Slot)> _heldAxes = new();
    private readonly Dictionary<(int Device, InputAction Action), long> _repeatDue = new();

    private double _measuredFps;
    private bool _focused = true;
    private bool _pausedByUser;
    private bool _pausedByMenu;
    private bool _pausedByFocus;

    private nint _nativeWindow;
    private Win32MenuBar? _menuBar;
    private Win32WindowHook? _messageHook;
    private bool _menuBarStale;
    private bool _inMenuLoop;
    private bool _pumping;
    private bool _altUsed;

    private bool _running = true;
    private bool _disposed;
    private string? _loadError;

    /// <summary>Work that must not run inside the event pump, such as a modal dialog.</summary>
    private readonly Queue<Action> _deferred = new();

    /// <summary>Creates the window and opens the sound device.</summary>
    /// <param name="discPath">A disc to put in straight away, or null for an empty console.</param>
    public PlayerWindow(string? discPath, GameWaveSettings settings, InputMap input, SettingsSession session)
    {
        _settings = settings;
        _input = input;
        _session = session;

        _sdl = LoadSdl();
        if (_sdl.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitGamecontroller) != 0)
            throw new SdlException($"SDL_Init failed: {_sdl.GetErrorS()}");

        // SDL swallows Alt and F10 so games can use them, which would leave a native menu
        // bar reachable only with the mouse. The menu matters more here than Alt does.
        if (settings.Interface.NativeMenuBar) SetHint("SDL_WINDOWS_ENABLE_MENU_MNEMONICS", "1");

        var scale = Math.Clamp(settings.Video.WindowScale, 1, 4);
        var (width, height) = WindowSize(settings.Video, scale);

        _window = _sdl.CreateWindow(
            "Game Wave",
            Sdl.WindowposCentered, Sdl.WindowposCentered,
            width, height,
            (uint)(WindowFlags.Shown | WindowFlags.Resizable | WindowFlags.AllowHighdpi));
        if (_window is null)
            throw new SdlException($"SDL_CreateWindow failed: {_sdl.GetErrorS()}");

        var rendererFlags = (uint)RendererFlags.Accelerated;
        if (settings.Video.VSync) rendererFlags |= (uint)RendererFlags.Presentvsync;

        _renderer = _sdl.CreateRenderer(_window, -1, rendererFlags);
        if (_renderer is null) _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null) throw new SdlException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");

        CreateVideoTexture();

        // The console mixes on SDL's audio thread, pulled a buffer at a time: a queue topped
        // up from this loop would run dry whenever Windows holds the loop, such as while the
        // window is being dragged.
        _audioCallback = new PfnAudioCallback(AudioCallback);
        var samples = (ushort)Math.Clamp(RoundToPowerOfTwo(settings.Audio.BufferMilliseconds * SampleRate / 1000), 256, 8192);
        var want = new AudioSpec
        {
            Freq = SampleRate,
            Format = Sdl.AudioF32Sys,
            Channels = 2,
            Samples = samples,
            Callback = _audioCallback,
        };
        AudioSpec have;
        _audioDevice = _sdl.OpenAudioDevice((byte*)null, 0, &want, &have, 0);
        if (_audioDevice == 0)
        {
            Console.Error.WriteLine($"gamewave: no sound device ({_sdl.GetErrorS()}); playing silently.");
            _menu.Toast("No sound device: playing silently", 5);
        }
        else
        {
            _deviceLatency = have.Samples / (double)have.Freq;
            _sdl.PauseAudioDevice(_audioDevice, 0);
        }

        OpenControllers();
        _menu.Changed += ApplySettings;

        CreateNativeMenu(height);
        _menuBarStale = false;

        if (settings.Video.Fullscreen) SetFullscreen(true);
        if (discPath is not null) LoadDisc(discPath);
        UpdateTitle();
        ApplySettings();
    }

    private double _deviceLatency;

    private static int RoundToPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>Window size for a scale, from the picture's shape.</summary>
    public static (int Width, int Height) WindowSize(VideoSettings video, int scale)
    {
        int h = 480 * scale;
        int w = video.Aspect switch
        {
            AspectRatio.Widescreen => 854 * scale,
            AspectRatio.SquarePixels => 720 * scale,
            _ => 640 * scale,
        };
        return (w, h);
    }

    // ------------------------------------------------------------ sound

    private void AudioCallback(void* userdata, byte* stream, int length)
    {
        var buffer = new Span<float>(stream, length / sizeof(float));
        var machine = _machine;
        if (machine is null || (!_focused && !_settings.Audio.PlayInBackground))
        {
            buffer.Clear();
            return;
        }
        machine.Audio.Mix(buffer);
    }

    // ------------------------------------------------------------ discs

    /// <summary>
    /// Puts the disc at <paramref name="path"/> in the console in place of the one there.
    /// If it cannot be opened the message is shown and the console carries on as it was.
    /// </summary>
    private void LoadDisc(string path)
    {
        if (_unpack is not null)
        {
            _menu.Toast("Still unpacking the last disc", 3);
            return;
        }

        // A zipped disc is unpacked once, in the background, with progress on screen.
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && !ZipDisc.IsUnpacked(path, UnpackDirectory()))
        {
            StartUnpack(path);
            return;
        }

        IDisc disc;
        try
        {
            disc = DiscLoader.Open(path, UnpackDirectory(), _settings.Emulation.UnpackedDiscsKept);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailLoad(path, ex);
            return;
        }
        Mount(disc, path);
    }

    private string UnpackDirectory()
        => string.IsNullOrWhiteSpace(_settings.Emulation.UnpackDirectory) ? DiscLoader.DefaultCacheDirectory : _settings.Emulation.UnpackDirectory;

    private sealed class UnpackJob
    {
        public required string Path { get; init; }
        public readonly CancellationTokenSource Cancel = new();
        public double Progress;
        public Task<IDisc>? Task;
    }

    private UnpackJob? _unpack;

    private void StartUnpack(string path)
    {
        var job = new UnpackJob { Path = path };
        var cache = UnpackDirectory();
        int keep = Math.Max(1, _settings.Emulation.UnpackedDiscsKept);
        job.Task = Task.Run(() => DiscLoader.Open(path, cache, keep, new SyncProgress(p => job.Progress = p), job.Cancel.Token));
        _unpack = job;
        _loadError = null;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Mounts an unpacked disc once its job is done.</summary>
    private void PollUnpack()
    {
        if (_unpack is not { Task.IsCompleted: true } job) return;
        _unpack = null;
        if (job.Task.IsCompletedSuccessfully)
        {
            Mount(job.Task.Result, job.Path);
            return;
        }
        if (job.Cancel.IsCancellationRequested)
        {
            _menu.Toast("Unpacking cancelled", 3);
            return;
        }
        FailLoad(job.Path, job.Task.Exception?.GetBaseException() ?? new IOException("unpacking failed"));
    }

    private void FailLoad(string path, Exception ex)
    {
        _loadError = $"Could not open {Path.GetFileName(path.TrimEnd('\\', '/'))}: {ex.Message}";
        if (_machine is not null || _menu.IsOpen) _menu.Toast(_loadError, 6);
        if (!File.Exists(path) && !Directory.Exists(path) && _settings.RecentDiscs.Remove(path))
        {
            _menuBarStale = true;
            SaveSettings();
        }
    }

    private void Mount(IDisc disc, string path)
    {
        Machine machine;
        try
        {
            machine = new Machine(disc, new SaveStore(SaveFilePath()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            disc.Dispose();
            FailLoad(path, ex);
            return;
        }

        var previous = _machine;
        _machine = null;
        previous?.Dispose();

        _loadError = null;
        _discPath = path;
        _machine = machine;
        machine.Log = OnGameLog;
        machine.TrayOpened += () => _menu.Toast("Tray open: close it to play again, or open another disc", 6);
        _pausedByUser = false;
        _menu.Close();
        ApplySettings();
        machine.Start();

        _settings.RecordRecentDisc(path);
        if (string.IsNullOrWhiteSpace(_settings.Emulation.GameDirectory))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path.TrimEnd('\\', '/')));
            if (folder is not null) _settings.Emulation.GameDirectory = folder;
        }
        SaveSettings();
        UpdateTitle();
        _menuBarStale = true;
        _overlay.Flash(_settings.Interface);
    }

    private void OnGameLog(string line)
    {
        // Engine and emulator messages are always worth seeing on a console; the game's
        // chatter is filtered by the machine's own log level.
        Console.Error.WriteLine(line);
    }

    private string SaveFilePath()
    {
        var folder = string.IsNullOrWhiteSpace(_settings.Emulation.SaveDirectory)
            ? Path.Combine(SettingsStore.Directory, "saves")
            : _settings.Emulation.SaveDirectory;
        return Path.Combine(folder, "gamewave-memory.bin");
    }

    /// <summary>Asks for a disc with the system Open dialog, or the nearest thing to one.</summary>
    private void ChooseDisc()
    {
        var owner = OperatingSystem.IsWindows() ? WindowHandle() : 0;
        if (owner == 0)
        {
            OpenMenu(Menus.Library(BuildContext()));
            _menu.Toast("Drop a disc image onto the window to open it", 4);
            return;
        }

        var folder = _settings.Emulation.GameDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            var recent = _settings.RecentDiscs.FirstOrDefault();
            folder = recent is null ? null : Path.GetDirectoryName(recent);
            if (folder is not null && !Directory.Exists(folder)) folder = null;
        }

        _sdl.ShowCursor(1);
        var path = Win32.ShowOpenFileDialog(owner, "Open a Game Wave disc", DiscFiles.DialogFilter, folder);
        _sdl.ShowCursor(_settings.Video.Fullscreen ? 0 : 1);

        if (path is not null) LoadDisc(path);
    }

    private void ChooseGameFolder()
    {
        var owner = OperatingSystem.IsWindows() ? WindowHandle() : 0;
        if (owner == 0)
        {
            _menu.Toast("Set emulation.gameDirectory with 'gamewave config set'", 5);
            return;
        }

        _sdl.ShowCursor(1);
        var folder = Win32.ShowFolderDialog(owner, "Choose the folder that holds your Game Wave discs");
        _sdl.ShowCursor(_settings.Video.Fullscreen ? 0 : 1);
        if (folder is null) return;

        _settings.Emulation.GameDirectory = folder;
        SaveSettings();
        _menuBarStale = true;
        _menu.Toast($"{DiscFiles.Find(folder).Count} discs in {folder}", 4);
    }

    /// <summary>A file dropped on the window: opened if it looks like a disc.</summary>
    private void OnDropFile(byte* file)
    {
        if (file is null) return;

        var path = Marshal.PtrToStringUTF8((nint)file);
        _sdl.Free(file);
        if (string.IsNullOrEmpty(path)) return;

        if (!DiscFiles.IsDiscFile(path))
        {
            _menu.Toast($"Not a disc: {Path.GetFileName(path)}. Drop an .iso or .zip.", 4);
            return;
        }

        _deferred.Enqueue(() => LoadDisc(path));
    }

    private void ToggleTray()
    {
        if (_machine is null) return;
        if (_machine.State == MachineState.TrayOpen)
        {
            _machine.CloseTray();
            _menu.Toast("Tray closed");
        }
        else
        {
            _machine.OpenTrayFromHost();
            _menu.Toast("Tray open: close it to play again, or open another disc", 5);
        }
    }

    /// <summary>The Win32 handle behind the SDL window, whether or not it has a menu bar.</summary>
    private nint WindowHandle()
    {
        if (_nativeWindow != 0) return _nativeWindow;

        var version = new Silk.NET.SDL.Version();
        _sdl.GetVersion(ref version);

        var info = new SysWMInfo { Version = version };
        if (!_sdl.GetWindowWMInfo(_window, &info) || info.Subsystem != SysWMType.Windows) return 0;

        return info.Info.Win.Hwnd;
    }

    private void UpdateTitle()
        => _sdl.SetWindowTitle(_window, _machine is null ? "Game Wave" : $"{DiscFiles.DisplayName(_discPath ?? _machine.Title)} - Game Wave");

    private void RunDeferred()
    {
        for (var count = _deferred.Count; count > 0 && _deferred.TryDequeue(out var work); count--) work();
    }

    // ------------------------------------------------------------ native menu bar

    /// <summary>Puts a native menu bar on the window, where the platform has one.</summary>
    /// <remarks>
    /// The bar is built from the same pages the in-window menu draws. The in-window menu
    /// is still what full screen, controllers and rebinding use.
    /// </remarks>
    private void CreateNativeMenu(int desiredClientHeight)
    {
        if (!OperatingSystem.IsWindows() || !_settings.Interface.NativeMenuBar) return;

        _nativeWindow = WindowHandle();
        if (_nativeWindow == 0) return;

        try
        {
            _menuBar = new Win32MenuBar(_nativeWindow, Menus.Bar(BuildContext()), OnNativeMenuChanged);
        }
        catch (InvalidOperationException)
        {
            _nativeWindow = 0;
            return;
        }

        // Windows runs its own modal loop while a menu is open, so menu messages have to be
        // taken as they arrive rather than from SDL's event queue.
        _messageHook = new Win32WindowHook(_nativeWindow, OnWindowsMessage);
        if (!_messageHook.Installed)
        {
            _messageHook.Dispose();
            _messageHook = null;
            _menuBar.Dispose();
            _menuBar = null;
            _nativeWindow = 0;
            return;
        }

        _menuBar.Attach();
        _menuBar.PreserveClientHeight(desiredClientHeight);
    }

    private void OnWindowsMessage(uint message, nuint wParam, nint lParam)
    {
        if (_menuBar is not { } bar) return;

        switch (message)
        {
            case Win32.WmCommand when (wParam >> 16) == 0 && lParam == 0:
                bar.Invoke((int)(wParam & 0xFFFF));
                break;

            case Win32.WmInitMenuPopup:
                bar.RefreshPopup((nint)wParam);
                break;

            case Win32.WmEnterMenuLoop:
                _inMenuLoop = true;
                Win32.SetTimer(_nativeWindow, MenuLoopTimerId, 15, 0);
                break;

            case Win32.WmExitMenuLoop:
                _inMenuLoop = false;
                Win32.KillTimer(_nativeWindow, MenuLoopTimerId);
                break;

            case Win32.WmTimer when _inMenuLoop && wParam == MenuLoopTimerId:
                PumpWhileMenuIsOpen();
                break;

            case Win32.WmSysKeyDown:
                OnSystemKeyDown(wParam, lParam);
                break;

            case Win32.WmSysKeyUp when wParam == Win32.VkMenu:
                if (!_altUsed) ActivateMenuBar('\0');
                _altUsed = false;
                break;
        }
    }

    private void OnSystemKeyDown(nuint wParam, nint lParam)
    {
        if (wParam == Win32.VkMenu)
        {
            if ((lParam & Win32.KeyWasDown) == 0) _altUsed = false;
            return;
        }

        _altUsed = true;

        if (wParam == Win32.VkF10)
        {
            ActivateMenuBar('\0');
            return;
        }

        var key = (char)wParam;
        if (_menuBar?.HasMnemonic(key) == true) ActivateMenuBar(key);
    }

    private void ActivateMenuBar(char mnemonic)
    {
        if (_nativeWindow == 0 || _settings.Video.Fullscreen) return;
        Win32.PostMessage(_nativeWindow, Win32.WmSysCommand, Win32.ScKeyMenu, mnemonic);
    }

    /// <summary>Keeps the picture moving while an open menu holds the thread.</summary>
    private void PumpWhileMenuIsOpen()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            Render();
        }
        finally
        {
            _pumping = false;
        }
    }

    private void OnNativeMenuChanged()
    {
        ApplySettings();
        SaveSettings();
    }

    private void RebuildNativeMenu()
    {
        _menuBarStale = false;
        if (_menuBar is not { } previous || _nativeWindow == 0 || _inMenuLoop) return;

        Win32MenuBar replacement;
        try
        {
            replacement = new Win32MenuBar(_nativeWindow, Menus.Bar(BuildContext()), OnNativeMenuChanged);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!_settings.Video.Fullscreen) replacement.Attach();
        _menuBar = replacement;
        previous.Dispose();
    }

    /// <summary>
    /// Binds SDL2. Silk.NET tries the system's copy first and then the one shipped beside
    /// the executable, so this fails only when neither is there.
    /// </summary>
    private static Sdl LoadSdl()
    {
        try
        {
            return Sdl.GetApi();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException)
        {
            var hint = OperatingSystem.IsWindows() ? "SDL2.dll belongs beside gamewave.exe"
                : OperatingSystem.IsMacOS() ? "libSDL2-2.0.dylib belongs beside gamewave, or install it with: brew install sdl2"
                : "libSDL2-2.0.so belongs beside gamewave, or install your distribution's SDL2 package";
            throw new SdlException($"SDL2 could not be loaded: {hint}.");
        }
    }

    // ------------------------------------------------------------ main loop

    /// <summary>Runs until the player quits.</summary>
    public void Run()
    {
        var last = _sdl.GetPerformanceCounter();
        var frequency = (double)_sdl.GetPerformanceFrequency();

        while (_running)
        {
            if (_menuBarStale) RebuildNativeMenu();

            PumpEvents();
            RepeatHeldDirections();
            RunDeferred();
            PollUnpack();
            UpdatePause();
            Render();

            var now = _sdl.GetPerformanceCounter();
            var elapsed = (now - last) / frequency;
            last = now;
            if (elapsed > 0) _measuredFps = _measuredFps * 0.9 + 1.0 / elapsed * 0.1;

            // Without vertical sync nothing else paces the loop.
            if (!_settings.Video.VSync && elapsed < 1 / 120.0) _sdl.Delay((uint)Math.Max(1, (1 / 120.0 - elapsed) * 1000));
        }
    }

    /// <summary>Pushes the current settings into the console and the renderer.</summary>
    private void ApplySettings()
    {
        if (_machine is { } m)
        {
            var audio = _settings.Audio;
            m.Audio.MasterVolume = audio.Volume / 100f;
            m.Audio.Muted = audio.Muted;
            m.Audio.MovieVolume = audio.MovieVolume / 100f;
            m.Audio.EffectsVolume = audio.EffectsVolume / 100f;
            m.Audio.ExternalDevice = _audioDevice != 0;
            m.Audio.DeviceLatency = _deviceLatency;
            if (m.Video.Deinterlace != _settings.Video.Deinterlace)
            {
                m.Video.Deinterlace = _settings.Video.Deinterlace;
                m.Video.Invalidate();
            }
            m.LogLevel = Math.Clamp(_settings.Emulation.GameLogLevel, 0, 5);
        }

        SetHint("SDL_RENDER_SCALE_QUALITY", _settings.Video.Filter == ScaleFilter.Linear ? "linear" : "nearest");
        CreateVideoTexture();
        _menuBarStale = true;
    }

    private void SetHint(string name, string value)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + '\0');
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        fixed (byte* hint = nameBytes)
        fixed (byte* setting = valueBytes)
            _sdl.SetHint(hint, setting);
    }

    private ScaleFilter _textureFilter = (ScaleFilter)(-1);

    private void CreateVideoTexture()
    {
        // The scale quality hint is read when a texture is made, so a filter change needs
        // a new one.
        if (_videoTexture is not null && _textureFilter == _settings.Video.Filter) return;
        if (_videoTexture is not null) _sdl.DestroyTexture(_videoTexture);
        _textureFilter = _settings.Video.Filter;
        _videoTexture = _sdl.CreateTexture(_renderer, (uint)PixelFormatEnum.Argb8888, (int)TextureAccess.Streaming, Osd.Width, Osd.Height);
    }

    /// <summary>Pauses the console for the player, the menu, or the window going to the background.</summary>
    private void UpdatePause()
    {
        if (_machine is null) return;
        _pausedByMenu = _menu.IsOpen && _settings.Emulation.PauseInMenu;
        _pausedByFocus = !_focused && _settings.Emulation.PauseInBackground;
        bool pause = _pausedByUser || _pausedByMenu || _pausedByFocus;
        if (_machine.Paused != pause) _machine.Paused = pause;
    }

    // ------------------------------------------------------------ input

    private void PumpEvents()
    {
        Event e = default;
        while (_sdl.PollEvent(ref e) != 0)
        {
            switch ((EventType)e.Type)
            {
                case EventType.Quit:
                    RequestQuit();
                    break;

                case EventType.Keydown:
                    OnKeyDown(e.Key.Keysym.Sym, Translate((Keymod)e.Key.Keysym.Mod), e.Key.Repeat != 0);
                    break;

                case EventType.Controllerbuttondown:
                    OnControllerButton(e.Cbutton.Which, e.Cbutton.Button, pressed: true);
                    break;

                case EventType.Controllerbuttonup:
                    OnControllerButton(e.Cbutton.Which, e.Cbutton.Button, pressed: false);
                    break;

                case EventType.Controlleraxismotion:
                    OnControllerAxis(e.Caxis.Which, e.Caxis.Axis, e.Caxis.Value);
                    break;

                case EventType.Controllerdeviceadded:
                case EventType.Controllerdeviceremoved:
                    OpenControllers();
                    _menuBarStale = true;
                    break;

                case EventType.Dropfile:
                    OnDropFile(e.Drop.File);
                    break;

                case EventType.Windowevent:
                    if ((WindowEventID)e.Window.Event == WindowEventID.FocusGained) _focused = true;
                    else if ((WindowEventID)e.Window.Event == WindowEventID.FocusLost) _focused = false;
                    break;
            }
        }
    }

    private static KeyModifiers Translate(Keymod mod)
    {
        var result = KeyModifiers.None;
        if ((mod & (Keymod.Ctrl | Keymod.Lctrl | Keymod.Rctrl)) != 0) result |= KeyModifiers.Control;
        if ((mod & (Keymod.Shift | Keymod.Lshift | Keymod.Rshift)) != 0) result |= KeyModifiers.Shift;
        if ((mod & (Keymod.Alt | Keymod.Lalt | Keymod.Ralt)) != 0) result |= KeyModifiers.Alt;
        return result;
    }

    private void OnKeyDown(int keycode, KeyModifiers modifiers, bool repeat)
    {
        if (_menu.IsCapturing && !repeat)
        {
            if (_menu.Capture(Binding.Key(keycode, modifiers), _input)) return;
        }

        Dispatch(_input.MatchKey(keycode, modifiers), null, repeat);
    }

    private void OnControllerButton(int instance, byte button, bool pressed)
    {
        if (pressed && _menu.IsCapturing && _menu.Capture(Binding.Button((ControllerButton)button), _input)) return;

        var actions = ForController(instance, _input.MatchButton(button));
        if (pressed)
        {
            Dispatch(actions, instance, repeat: false);
            ScheduleRepeat(instance, actions);
        }
        else
        {
            foreach (var a in actions) _repeatDue.Remove((instance, a));
        }
    }

    private void OnControllerAxis(int instance, byte axis, short value)
    {
        int threshold = 32767 * Math.Clamp(_settings.Controllers.StickThreshold, 10, 90) / 100;
        Handle(axis * 2, value > threshold, true);
        Handle(axis * 2 + 1, value < -threshold, false);

        void Handle(int slot, bool active, bool positive)
        {
            if (active == _heldAxes.Contains((instance, slot))) return;
            if (active) _heldAxes.Add((instance, slot));
            else _heldAxes.Remove((instance, slot));

            if (active && _menu.IsCapturing && _menu.Capture(Binding.Axis((ControllerAxis)axis, positive), _input)) return;

            var actions = ForController(instance, _input.MatchAxis(axis, positive));
            if (active)
            {
                Dispatch(actions, instance, repeat: false);
                ScheduleRepeat(instance, actions);
            }
            else
            {
                foreach (var a in actions) _repeatDue.Remove((instance, a));
            }
        }
    }

    /// <summary>
    /// Keeps only the remote keys that belong to the remote this controller plays as; the
    /// emulator's own actions apply to every controller.
    /// </summary>
    private IReadOnlyList<InputAction> ForController(int instance, IReadOnlyList<InputAction> actions)
    {
        int index = _controllers.FindIndex(c => c.Instance == instance);
        var remote = _settings.RemoteForController(Math.Max(0, index));
        return actions.Where(a => !InputActions.IsRemoteKey(a) || InputActions.RemoteOf(a) == remote).ToArray();
    }

    private void ScheduleRepeat(int device, IReadOnlyList<InputAction> actions)
    {
        if (!_settings.Controllers.RepeatDirections || _menu.IsOpen) return;
        foreach (var a in actions)
        {
            if (InputActions.IsRemoteKey(a) && InputActions.KeyOf(a) is RemoteKey.Up or RemoteKey.Down or RemoteKey.Left or RemoteKey.Right)
                _repeatDue[(device, a)] = Environment.TickCount64 + 400;
        }
    }

    /// <summary>Presses held controller directions again, as holding a remote's button did.</summary>
    private void RepeatHeldDirections()
    {
        if (_repeatDue.Count == 0) return;
        long now = Environment.TickCount64;
        foreach (var (key, due) in _repeatDue.ToArray())
        {
            if (now < due) continue;
            _repeatDue[key] = now + 120;
            PressRemote(key.Action);
        }
    }

    /// <summary>
    /// Runs the one action a control means right now: menu navigation while a menu is up,
    /// otherwise a remote key or an emulator command.
    /// </summary>
    private void Dispatch(IReadOnlyList<InputAction> actions, int? controller, bool repeat)
    {
        if (InputRouter.Resolve(actions, _menu.IsOpen) is not { } resolved) return;

        if (_menu.IsOpen)
        {
            _menu.Handle(resolved);
            if (!_menu.IsOpen) SaveSettings();
            return;
        }

        if (InputActions.IsRemoteKey(resolved))
        {
            // A keyboard key repeats only for directions, the way a held remote button did;
            // repeating A or SEL would answer questions twice.
            if (repeat && InputActions.KeyOf(resolved) is not (RemoteKey.Up or RemoteKey.Down or RemoteKey.Left or RemoteKey.Right))
                return;

            // Every remote key the control is bound to is pressed, so one key can drive two
            // remotes if the player wants it to.
            foreach (var action in actions.Where(InputActions.IsRemoteKey))
                PressRemote(action);
            return;
        }

        Perform(resolved, repeat);
    }

    private void PressRemote(InputAction action)
    {
        if (_machine is not { State: MachineState.Running } m) return;
        m.Input.Push(InputActions.KeyOf(action), InputActions.RemoteOf(action), m.Clock.Now);
    }

    private void Perform(InputAction action, bool repeat)
    {
        if (_machine is null && InputActions.NeedsDisc(action))
        {
            if (!repeat) _menu.Toast(NoDiscHint(), 3);
            return;
        }

        switch (action)
        {
            case InputAction.OpenDisc:
                _deferred.Enqueue(ChooseDisc);
                break;

            case InputAction.ToggleMenu:
                if (_unpack is { } job)
                {
                    job.Cancel.Cancel();
                    break;
                }
                OpenMenu(Menus.Root(BuildContext()));
                break;

            case InputAction.TogglePause:
                _pausedByUser = !_pausedByUser;
                _menu.Toast(_pausedByUser ? "Paused" : "Running");
                break;

            case InputAction.Reset:
                _pausedByUser = false;
                _machine?.Reset();
                _menu.Toast("Reset");
                break;

            case InputAction.ToggleTray:
                ToggleTray();
                break;

            case InputAction.VolumeUp:
                ChangeVolume(5);
                break;

            case InputAction.VolumeDown:
                ChangeVolume(-5);
                break;

            case InputAction.ToggleMute:
                _settings.Audio.Muted = !_settings.Audio.Muted;
                _menu.Toast(_settings.Audio.Muted ? "Muted" : "Sound on");
                ApplySettings();
                SaveSettings();
                break;

            case InputAction.ToggleOverlay:
                _settings.Interface.Overlay = _settings.Interface.Overlay switch
                {
                    OverlayMode.Hidden => OverlayMode.Auto,
                    OverlayMode.Auto => OverlayMode.Always,
                    _ => OverlayMode.Hidden,
                };
                _menu.Toast($"Status overlay: {_settings.Interface.Overlay}");
                SaveSettings();
                break;

            case InputAction.ToggleFullscreen:
                SetFullscreen(!_settings.Video.Fullscreen);
                break;

            case InputAction.Screenshot:
                TakeScreenshot();
                break;

            case InputAction.Quit:
                RequestQuit();
                break;
        }

        if (!repeat) _overlay.Flash(_settings.Interface);
    }

    private void ChangeVolume(int delta)
    {
        _settings.Audio.Volume = Math.Clamp(_settings.Audio.Volume + delta, 0, 100);
        _settings.Audio.Muted = false;
        _menu.Toast($"Volume {_settings.Audio.Volume}");
        ApplySettings();
        SaveSettings();
    }

    private MenuContext BuildContext() => new()
    {
        Settings = _settings,
        Input = _input,
        Machine = _machine,
        Controllers = _controllers.Select(c => c.Name).ToArray(),
        CloseMenu = () =>
        {
            _menu.Close();
            SaveSettings();
        },
        ToggleFullscreen = () => SetFullscreen(!_settings.Video.Fullscreen),
        Quit = RequestQuit,
        Toast = message => _menu.Toast(message),
        Perform = action => Perform(action, repeat: false),
        OpenPage = OpenMenu,
        ShowInfo = ShowInfo,
        OpenScreenshots = OpenScreenshotFolder,
        OpenDisc = path => _deferred.Enqueue(() => LoadDisc(path)),
        ChooseGameFolder = () => _deferred.Enqueue(ChooseGameFolder),
        EraseSaves = () =>
        {
            new SaveStore(SaveFilePath()).Clear();
            _menu.Toast("Save memory erased; reset the game to see it");
        },
    };

    private string NoDiscHint()
    {
        var open = _input.BindingsFor(InputAction.OpenDisc);
        return open.Count == 0 ? "No disc in the console" : $"No disc in the console - {open[0]} opens one";
    }

    private void ShowInfo(string title, string body)
    {
        if (OperatingSystem.IsWindows() && _nativeWindow != 0)
        {
            Win32.MessageBox(_nativeWindow, body, title, Win32.MbOk | Win32.MbIconInformation);
            return;
        }

        OpenMenu(new MenuPage
        {
            Title = title,
            Items = body.ReplaceLineEndings("\n").Split('\n')
                .Select(line => (MenuItem)new MenuHeading { Label = line })
                .ToArray(),
        });
    }

    private void OpenScreenshotFolder()
    {
        try
        {
            var directory = ScreenshotDirectory();
            Directory.CreateDirectory(directory);
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _menu.Toast($"Could not open the folder: {ex.Message}", 4);
        }
    }

    private string ScreenshotDirectory()
        => string.IsNullOrWhiteSpace(_settings.Interface.ScreenshotDirectory)
            ? Path.Combine(SettingsStore.Directory, "screenshots")
            : _settings.Interface.ScreenshotDirectory;

    private void OpenMenu(MenuPage page) => _menu.Push(page);

    private void RequestQuit() => _running = false;

    private void SetFullscreen(bool on)
    {
        _settings.Video.Fullscreen = on;
        if (on) _menuBar?.Detach();
        _sdl.SetWindowFullscreen(_window, on ? (uint)WindowFlags.FullscreenDesktop : 0);
        if (!on) _menuBar?.Attach();
        _sdl.ShowCursor(on ? 0 : 1);
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.StoreInputMap(_input);
            _session.Save(_settings);
        }
        catch (IOException)
        {
            // A settings file that cannot be written should not interrupt the game.
        }
    }

    private void TakeScreenshot()
    {
        if (_machine is null) return;
        try
        {
            var directory = ScreenshotDirectory();
            Directory.CreateDirectory(directory);

            var name = $"{DiscFiles.DisplayName(_discPath ?? _machine.Title)}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            var path = Path.Combine(directory, name);

            // Saved at the shape it was shown at: 640 x 480 for the standard 4:3 picture.
            var (w, h) = WindowSize(_settings.Video, 1);
            PngWriter.Write(path, Resample(_frame, Osd.Width, Osd.Height, w, h), w, h);
            _menu.Toast($"Saved {Path.GetFileName(path)}", 3);
        }
        catch (IOException ex)
        {
            _menu.Toast($"Screenshot failed: {ex.Message}", 4);
        }
    }

    /// <summary>Bilinear resize, for screenshots at the displayed shape.</summary>
    private static uint[] Resample(uint[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (uint[])src.Clone();
        var dst = new uint[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            double fy = (y + 0.5) * sh / dh - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
            double wy = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < dw; x++)
            {
                double fx = (x + 0.5) * sw / dw - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                double wx = Math.Clamp(fx - x0, 0, 1);
                uint Mix(int shift)
                {
                    double a = (src[y0 * sw + x0] >> shift & 0xFF) * (1 - wx) + (src[y0 * sw + x1] >> shift & 0xFF) * wx;
                    double b = (src[y1 * sw + x0] >> shift & 0xFF) * (1 - wx) + (src[y1 * sw + x1] >> shift & 0xFF) * wx;
                    return (uint)Math.Round(a * (1 - wy) + b * wy);
                }
                dst[y * dw + x] = 0xFF000000 | Mix(16) << 16 | Mix(8) << 8 | Mix(0);
            }
        }
        return dst;
    }

    // ------------------------------------------------------------ drawing

    private void Render()
    {
        int windowWidth, windowHeight;
        _sdl.GetRendererOutputSize(_renderer, &windowWidth, &windowHeight);

        var background = Rgba.Parse(_settings.Video.BackgroundColor);
        _sdl.SetRenderDrawColor(_renderer, background.R, background.G, background.B, 255);
        _sdl.RenderClear(_renderer);

        if (_machine is not null)
        {
            _machine.RenderFrame(_frame);
            fixed (uint* pixels = _frame)
                _sdl.UpdateTexture(_videoTexture, (Rectangle<int>*)null, pixels, Osd.Width * 4);

            int crop = Math.Clamp(_settings.Video.Overscan, 0, 64);
            var source = new Rectangle<int>(crop, crop * 2 / 3, Osd.Width - crop * 2, Osd.Height - crop * 4 / 3);
            var destination = ComputeDestination(windowWidth, windowHeight);
            _sdl.RenderCopy(_renderer, _videoTexture, ref source, ref destination);
        }

        DrawOverlay(windowWidth, windowHeight);
        _sdl.RenderPresent(_renderer);
    }

    private Rectangle<int> ComputeDestination(int windowWidth, int windowHeight)
    {
        var (shapeW, shapeH) = WindowSize(_settings.Video, 1);
        double sourceWidth = shapeW, sourceHeight = shapeH;

        switch (_settings.Video.ScaleMode)
        {
            case Config.ScaleMode.Stretch:
                return new Rectangle<int>(0, 0, windowWidth, windowHeight);

            case Config.ScaleMode.IntegerScale:
            {
                var factor = Math.Max(1, Math.Min((int)(windowWidth / sourceWidth), (int)(windowHeight / sourceHeight)));
                return Centred((int)(sourceWidth * factor), (int)(sourceHeight * factor));
            }

            default:
            {
                var factor = Math.Min(windowWidth / sourceWidth, windowHeight / sourceHeight);
                return Centred((int)Math.Round(sourceWidth * factor), (int)Math.Round(sourceHeight * factor));
            }
        }

        Rectangle<int> Centred(int width, int height)
            => new((windowWidth - width) / 2, (windowHeight - height) / 2, width, height);
    }

    private void DrawOverlay(int windowWidth, int windowHeight)
    {
        var scale = _settings.Interface.FontScale > 0
            ? _settings.Interface.FontScale
            : Math.Clamp(windowWidth / 480, 1, 4);

        if (_canvas.Resize(windowWidth, windowHeight)) RecreateOverlayTexture();

        if (_unpack is { } unpacking)
        {
            _canvas.Clear();
            if (_machine is not null) _canvas.Dim(160);
            DrawUnpacking(unpacking, windowWidth, windowHeight);
            UploadOverlay();
            return;
        }

        if (_machine is null)
        {
            _canvas.Clear();
            DrawEmptyConsole(windowWidth, windowHeight);
            _menu.Draw(_canvas, scale, _settings.Interface.DimBehindMenu);
            UploadOverlay();
            return;
        }

        var showStatus = _overlay.ShouldDraw(_settings.Interface, _machine);
        if (!showStatus && !_menu.IsOpen && !_menu.HasToast) return;

        _canvas.Clear();
        if (showStatus)
        {
            var controllers = string.Join("  ", _controllers.Select((c, i) => $"Pad {i + 1}: {_settings.RemoteForController(i)}"));
            _overlay.Draw(_canvas, scale, _machine, _settings,
                new HostStatus(_settings.Audio.Volume, _settings.Audio.Muted, _measuredFps, controllers));
        }

        _menu.Draw(_canvas, scale, _settings.Interface.DimBehindMenu);
        UploadOverlay();
    }

    private void UploadOverlay()
    {
        if (_overlayTexture is null) RecreateOverlayTexture();
        if (_overlayTexture is null) return;

        fixed (byte* pixels = _canvas.Pixels)
            _sdl.UpdateTexture(_overlayTexture, (Rectangle<int>*)null, pixels, _canvas.Width * 4);

        var destination = new Rectangle<int>(0, 0, _canvas.Width, _canvas.Height);
        _sdl.RenderCopy(_renderer, _overlayTexture, (Rectangle<int>*)null, ref destination);
    }

    private void DrawUnpacking(UnpackJob job, int windowWidth, int windowHeight)
    {
        var scale = _settings.Interface.FontScale > 0 ? _settings.Interface.FontScale + 1 : Math.Clamp(windowWidth / 320, 1, 4);
        var gap = BitmapFont.LineAdvance * scale;
        var y = windowHeight / 2 - gap * 2;
        var name = DiscFiles.DisplayName(job.Path);
        _canvas.TextCentred(windowWidth / 2, y, BitmapFont.Fit($"Unpacking {name}", windowWidth - 16 * scale, scale), scale, Rgba.Accent);
        y += gap * 2;
        var barWidth = Math.Min(windowWidth - 32 * scale, 200 * scale);
        var barX = (windowWidth - barWidth) / 2;
        _canvas.Outline(barX, y, barWidth, 6 * scale, Rgba.Grey, Math.Max(1, scale / 2));
        _canvas.Fill(barX + scale, y + scale, (int)((barWidth - 2 * scale) * Math.Clamp(job.Progress, 0, 1)), 4 * scale, Rgba.Accent);
        y += gap * 2;
        _canvas.TextCentred(windowWidth / 2, y, $"{job.Progress * 100:0}%  (first time only; Escape cancels)", scale, Rgba.Grey);
    }

    /// <summary>The screen with no disc in: how to put one in, and why the last one would not open.</summary>
    private void DrawEmptyConsole(int windowWidth, int windowHeight)
    {
        var scale = _settings.Interface.FontScale > 0
            ? _settings.Interface.FontScale + 1
            : Math.Clamp(windowWidth / 260, 1, 4);

        var lines = new List<(string Text, int Scale, Rgba Color)> { ("Game Wave", scale * 2, Rgba.Accent) };

        var open = _input.BindingsFor(InputAction.OpenDisc);
        var menu = _input.BindingsFor(InputAction.ToggleMenu);
        lines.Add((open.Count > 0 ? $"{open[0]} to open a disc" : "Open a disc from the menu", scale, Rgba.White));
        lines.Add(("or drop an .iso or .zip on this window", scale, Rgba.Grey));
        if (menu.Count > 0) lines.Add(($"{menu[0]} for the menu and your games", scale, Rgba.Grey));

        var margin = 8 * scale;
        var gap = BitmapFont.LineAdvance;
        var errorFrom = lines.Count;

        if (_loadError is not null)
        {
            foreach (var line in Wrap(_loadError, windowWidth - margin * 2, scale).Take(3))
                lines.Add((line, scale, Rgba.Warn));
        }

        var height = lines.Sum(l => l.Scale * gap) + gap * scale;
        var y = (windowHeight - height) / 2;

        for (var i = 0; i < lines.Count; i++)
        {
            if (i == 1 || (i == errorFrom && i < lines.Count)) y += gap * scale / 2;
            var (text, size, color) = lines[i];
            _canvas.TextCentred(windowWidth / 2, y, BitmapFont.Fit(text, windowWidth - margin * 2, size), size, color);
            y += size * gap;
        }
    }

    private static IEnumerable<string> Wrap(string text, int pixels, int scale)
    {
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length > 0 && BitmapFont.Measure(candidate, scale) > pixels)
            {
                yield return line;
                candidate = word;
            }
            line = candidate;
        }
        if (line.Length > 0) yield return line;
    }

    private void RecreateOverlayTexture()
    {
        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);
        _overlayTexture = _sdl.CreateTexture(_renderer, (uint)PixelFormatEnum.Abgr8888, (int)TextureAccess.Streaming, _canvas.Width, _canvas.Height);
        if (_overlayTexture is not null) _sdl.SetTextureBlendMode(_overlayTexture, BlendMode.Blend);
    }

    // ------------------------------------------------------------ controllers

    /// <summary>
    /// Opens every game controller, keeping the ones already open in their places so a
    /// player's remote does not change when another controller is plugged in.
    /// </summary>
    private void OpenControllers()
    {
        for (int i = _controllers.Count - 1; i >= 0; i--)
        {
            var c = (GameController*)_controllers[i].Handle;
            if (_sdl.GameControllerGetAttached(c) == SdlBool.False)
            {
                _sdl.GameControllerClose(c);
                _controllers.RemoveAt(i);
            }
        }

        for (var i = 0; i < _sdl.NumJoysticks(); i++)
        {
            if (_sdl.IsGameController(i) == SdlBool.False) continue;
            int instance = _sdl.JoystickGetDeviceInstanceID(i);
            if (_controllers.Any(c => c.Instance == instance)) continue;

            var controller = _sdl.GameControllerOpen(i);
            if (controller is null) continue;
            var name = _sdl.GameControllerNameS(controller) ?? $"Controller {_controllers.Count + 1}";
            _controllers.Add((instance, (nint)controller, name));
            _menu.Toast($"{name} plays as {_settings.RemoteForController(_controllers.Count - 1)}", 3);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unpack?.Cancel.Cancel();

        _menu.Changed -= ApplySettings;

        var menuBar = _menuBar;
        _menuBar = null;
        if (_nativeWindow != 0) Win32.KillTimer(_nativeWindow, MenuLoopTimerId);
        menuBar?.Dispose();
        _messageHook?.Dispose();
        _messageHook = null;
        _nativeWindow = 0;

        if (_audioDevice != 0) _sdl.CloseAudioDevice(_audioDevice);
        var machine = _machine;
        _machine = null;
        machine?.Dispose();

        foreach (var c in _controllers) _sdl.GameControllerClose((GameController*)c.Handle);
        if (_overlayTexture is not null) _sdl.DestroyTexture(_overlayTexture);
        if (_videoTexture is not null) _sdl.DestroyTexture(_videoTexture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);

        _sdl.Quit();
        _sdl.Dispose();
        GC.KeepAlive(_audioCallback);
    }
}

/// <summary>SDL could not do something the window depends on.</summary>
public sealed class SdlException(string message) : Exception(message);
