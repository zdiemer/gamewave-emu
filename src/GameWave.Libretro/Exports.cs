using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GameWave.Graphics;
using GameWave.Media;

namespace GameWave.Libretro;

/// <summary>Libretro v1 C ABI. Frontend callbacks only run on the frontend thread.</summary>
static unsafe class Exports
{
    static Exports() => LibraryLifetime.Pin((nint)(delegate* unmanaged[Cdecl]<uint>)&ApiVersion);
    static delegate* unmanaged[Cdecl]<uint, void*, byte> _environment;
    static delegate* unmanaged[Cdecl]<void*, uint, uint, nuint, void> _video;
    static delegate* unmanaged[Cdecl]<short, short, void> _audio;
    static delegate* unmanaged[Cdecl]<short*, nuint, nuint> _audioBatch;
    static delegate* unmanaged[Cdecl]<void> _poll;
    static delegate* unmanaged[Cdecl]<uint, uint, uint, uint, short> _inputState;
    static CoreSession? _session;
    static readonly RetroInput Input = new();
    static readonly ConcurrentQueue<string> Messages = new();
    static bool _failed;
    static uint _initialIndex;
    static string? _initialPath;

    // The frontend may retain these pointers until deinit, and request info before init.
    // Small immutable ABI metadata allocations live for the lifetime of the library.
    static readonly byte* Name = String("gamewave");
    static readonly byte* Version = String("0.1.0");
    static readonly byte* Extensions = String("iso|zip|m3u|diz");
    static readonly byte* DeinterlaceKey = String("gamewave_deinterlace");
    static readonly byte* KeyboardKey = String("gamewave_keyboard_remote");
    static readonly RetroVariable* Variables = MakeVariables();
    static readonly RetroControllerInfo* Controllers = MakeControllers();
    static readonly RetroInputDescriptor* Descriptors = MakeDescriptors();
    static readonly RetroOptions Options = MakeOptions();
    static readonly RetroOptionDefinitionV1* OptionsV1 = MakeOptionsV1();

    static RetroOptions MakeOptions()
    {
        var categories = (RetroOptionCategory*)NativeMemory.AllocZeroed(3, (nuint)sizeof(RetroOptionCategory));
        categories[0] = new() { Key = String("video"), Description = String("Video"), Info = String("Picture presentation.") };
        categories[1] = new() { Key = String("input"), Description = String("Input"), Info = String("Game Wave remote controls.") };
        var definitions = (RetroOptionDefinition*)NativeMemory.AllocZeroed(3, (nuint)sizeof(RetroOptionDefinition));
        definitions[0] = new() { Key = DeinterlaceKey, Description = String("Deinterlacing"),
            Info = String("Blend interlaced movie fields for a smoother picture, or show fields without blending."), Category = categories[0].Key, Default = String("blend") };
        definitions[0].Values[0] = new() { Value = definitions[0].Default, Label = String("Blend") };
        definitions[0].Values[1] = new() { Value = String("off"), Label = String("Off") };
        definitions[1] = new() { Key = KeyboardKey, Description = String("Keyboard remote"),
            Info = String("Select the remote controlled by the keyboard. RetroPad ports always control their corresponding remotes."), Category = categories[1].Key, Default = String("1") };
        for (int i = 0; i < 6; i++) definitions[1].Values[i] = new() { Value = String((i + 1).ToString()), Label = String($"Remote {i + 1}") };
        return new() { Categories = categories, Definitions = definitions };
    }

    static RetroOptionDefinitionV1* MakeOptionsV1()
    {
        var definitions = (RetroOptionDefinitionV1*)NativeMemory.AllocZeroed(3, (nuint)sizeof(RetroOptionDefinitionV1));
        for (int i = 0; i < 2; i++)
        {
            var source = Options.Definitions[i];
            definitions[i] = new() { Key = source.Key, Description = source.Description, Info = source.Info, Values = source.Values, Default = source.Default };
        }
        return definitions;
    }

    static byte* String(string value) => (byte*)Marshal.StringToCoTaskMemUTF8(value);
    static string? Read(byte* value) => Marshal.PtrToStringUTF8((nint)value);
    static byte Bool(bool value) => value ? (byte)1 : (byte)0;
    static bool Env(uint command, void* data) => _environment != null && _environment(command, data) != 0;

    static RetroVariable* MakeVariables()
    {
        var vars = (RetroVariable*)NativeMemory.AllocZeroed(3, (nuint)sizeof(RetroVariable));
        vars[0] = new() { Key = DeinterlaceKey, Value = String("Deinterlacing; blend|off") };
        vars[1] = new() { Key = KeyboardKey, Value = String("Keyboard remote; 1|2|3|4|5|6") };
        return vars;
    }

    static RetroControllerInfo* MakeControllers()
    {
        var types = (RetroControllerDescription*)NativeMemory.AllocZeroed(1, (nuint)sizeof(RetroControllerDescription));
        *types = new() { Description = String("Game Wave remote"), Id = 1 };
        var ports = (RetroControllerInfo*)NativeMemory.AllocZeroed(7, (nuint)sizeof(RetroControllerInfo));
        for (int i = 0; i < 6; i++)
            ports[i] = new() { Types = types, Count = 1 };
        return ports;
    }

    static RetroInputDescriptor* MakeDescriptors()
    {
        string[] labels = ["B / R2: 2", "D / R2: 4", "Game menu", "SEL",
            "Up / R2: 5", "Down / R2: 7", "Left / R2: 8", "Right / R2: 6",
            "A / R2: 1", "C / R2: 3", "DVD menu / R2: 9", "SEL / R2: 0",
            "Unused", "Hold for numbers", "Unused", "Unused"];
        var items = (RetroInputDescriptor*)NativeMemory.AllocZeroed(97, (nuint)sizeof(RetroInputDescriptor));
        for (uint port = 0; port < 6; port++)
            for (uint id = 0; id < 16; id++)
                items[port * 16 + id] = new() { Port = port, Device = 1, Id = id, Description = String(labels[id]) };
        return items;
    }

    static void QueueLog(string text)
    {
        if (text.StartsWith("Game script error:", StringComparison.Ordinal) || text.StartsWith("Emulator error:", StringComparison.Ordinal))
            Messages.Enqueue(text);
    }

    static void ShowMessages()
    {
        while (Messages.TryDequeue(out var text))
        {
            var ptr = String(text);
            try
            {
                var message = new RetroMessage { Text = ptr, Frames = 300 };
                Env(6, &message); // SET_MESSAGE
            }
            finally { Marshal.FreeCoTaskMem((nint)ptr); }
        }
    }

    static void Error(Exception error)
    {
        Messages.Enqueue("gamewave: " + error.Message);
        ShowMessages();
    }

    static void ReadOptions()
    {
        var variable = new RetroVariable { Key = DeinterlaceKey };
        if (Env(15, &variable) && _session is { } session)
        {
            session.Machine.Video.Deinterlace = Read(variable.Value) == "off" ? DeinterlaceMode.Off : DeinterlaceMode.Blend;
            session.Machine.Video.Invalidate();
        }
        variable = new() { Key = KeyboardKey };
        if (Env(15, &variable) && int.TryParse(Read(variable.Value), out int remote) && remote is >= 1 and <= 6)
            Input.KeyboardRemote = remote;
    }

    static void Unload()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
        Input.Clear();
        Messages.Clear();
        _failed = false;
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_api_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint ApiVersion() => 1;

    [UnmanagedCallersOnly(EntryPoint = "retro_set_environment", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetEnvironment(delegate* unmanaged[Cdecl]<uint, void*, byte> callback)
    {
        _environment = callback;
        byte noGame = 0;
        Env(18, &noGame);
        uint version = 0;
        Env(52, &version);
        if (version >= 2)
        {
            var options = Options;
            Env(67, &options); // false means no categories, not registration failure.
        }
        else if (version >= 1) Env(53, OptionsV1);
        else Env(16, Variables);
        Env(35, Controllers);
        Env(11, Descriptors);
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_set_video_refresh", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetVideo(delegate* unmanaged[Cdecl]<void*, uint, uint, nuint, void> callback) => _video = callback;
    [UnmanagedCallersOnly(EntryPoint = "retro_set_audio_sample", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetAudio(delegate* unmanaged[Cdecl]<short, short, void> callback) => _audio = callback;
    [UnmanagedCallersOnly(EntryPoint = "retro_set_audio_sample_batch", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetAudioBatch(delegate* unmanaged[Cdecl]<short*, nuint, nuint> callback) => _audioBatch = callback;
    [UnmanagedCallersOnly(EntryPoint = "retro_set_input_poll", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetPoll(delegate* unmanaged[Cdecl]<void> callback) => _poll = callback;
    [UnmanagedCallersOnly(EntryPoint = "retro_set_input_state", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetInput(delegate* unmanaged[Cdecl]<uint, uint, uint, uint, short> callback) => _inputState = callback;

    [UnmanagedCallersOnly(EntryPoint = "retro_init", CallConvs = [typeof(CallConvCdecl)])]
    public static void Init()
    {
        ulong quirks = 0;
        if (!Env(87, &quirks)) Env(44, &quirks); // Historical stable command number.
        try
        {
            Unload();
            var keyboard = new RetroKeyboard { Callback = &Keyboard };
            Env(12, &keyboard);
            var disk = new RetroDiskControl
            {
                SetEject = &SetEject, GetEject = &GetEject, GetIndex = &GetIndex,
                SetIndex = &SetIndex, GetCount = &GetCount, Replace = &Replace, Add = &Add,
            };
            uint diskVersion = 0;
            Env(57, &diskVersion);
            var extended = new RetroDiskControlExt { Basic = disk, SetInitial = &SetInitial, GetPath = &GetImagePath, GetLabel = &GetImageLabel };
            if (diskVersion < 1 || !Env(58, &extended)) Env(13, &disk);
        }
        catch (Exception error) { Error(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_deinit", CallConvs = [typeof(CallConvCdecl)])]
    public static void Deinit()
    {
        try { Unload(); }
        catch (Exception error) { Error(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_get_system_info", CallConvs = [typeof(CallConvCdecl)])]
    public static void GetSystemInfo(RetroSystemInfo* info)
    {
        if (info != null)
            *info = new() { Name = Name, Version = Version, Extensions = Extensions, NeedFullPath = 1, BlockExtract = 1 };
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_get_system_av_info", CallConvs = [typeof(CallConvCdecl)])]
    public static void GetAvInfo(RetroAvInfo* info)
    {
        if (info != null)
            *info = new()
            {
                Geometry = new() { Width = Osd.Width, Height = Osd.Height, MaxWidth = Osd.Width, MaxHeight = Osd.Height, Aspect = 4f / 3 },
                Timing = new() { Fps = CoreSession.Fps, SampleRate = AudioMixer.SampleRate },
            };
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_load_game", CallConvs = [typeof(CallConvCdecl)])]
    public static byte LoadGame(RetroGameInfo* game)
    {
        try
        {
            Unload();
            if (game == null || Read(game->Path) is not { Length: > 0 } path)
                return 0;
            int pixelFormat = 1; // XRGB8888
            if (!Env(10, &pixelFormat))
                throw new NotSupportedException("The frontend must support XRGB8888 video.");
            byte* savePath = null;
            string saveDirectory = Env(31, &savePath) && Read(savePath) is { Length: > 0 } directory
                ? directory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gamewave-libretro");
            _session = new CoreSession(path, saveDirectory, QueueLog, _initialIndex, _initialPath);
            ReadOptions();
            return 1;
        }
        catch (Exception error) { Error(error); return 0; }
        finally { _initialIndex = 0; _initialPath = null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_unload_game", CallConvs = [typeof(CallConvCdecl)])]
    public static void UnloadGame()
    {
        try { Unload(); }
        catch (Exception error) { Error(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_reset", CallConvs = [typeof(CallConvCdecl)])]
    public static void Reset()
    {
        try { _session?.Reset(); Input.Clear(); _failed = false; }
        catch (Exception error) { Error(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_set_controller_port_device", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetDevice(uint port, uint device)
    {
        if (port < 6)
        {
            Input.Devices[port] = (device & 255) == 1 ? 1u : 0u;
            Input.Clear();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "retro_run", CallConvs = [typeof(CallConvCdecl)])]
    public static void Run()
    {
        try
        {
            if (_session is not { } session)
                return;
            byte changed = 0;
            if (Env(17, &changed) && changed != 0)
                ReadOptions();
            if (_poll != null)
                _poll();
            uint av = 3;
            Env(47 | 0x10000, &av);
            if (!_failed && !session.Ejected)
            {
                for (uint port = 0; port < 6; port++)
                {
                    int buttons = 0;
                    if (Input.Devices[port] != 0 && _inputState != null)
                        for (uint id = 0; id < 16; id++)
                            if (_inputState(port, 1, 0, id) != 0)
                                buttons |= 1 << (int)id;
                    Input.Poll(port, buttons, session.Machine);
                }
                session.Run(hardDisableAudio: (av & 8) != 0);
            }
            else if (session.Ejected)
                session.Run(hardDisableAudio: (av & 8) != 0);
            ShowMessages();
            fixed (uint* frame = session.Frame)
                if (_video != null)
                    _video((av & 1) != 0 ? frame : null, Osd.Width, Osd.Height, Osd.Width * sizeof(uint));
            fixed (short* pcm = session.Pcm)
            {
                if ((av & 2) != 0 && _audioBatch != null)
                    _audioBatch(pcm, CoreSession.AudioFrames);
                else if ((av & 2) != 0 && _audio != null)
                    for (int i = 0; i < CoreSession.AudioFrames; i++)
                        _audio(pcm[i * 2], pcm[i * 2 + 1]);
            }
        }
        catch (Exception error)
        {
            _failed = true;
            _session?.Machine.Stop();
            if (_session is { } session)
                Array.Clear(session.Pcm);
            Error(error);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Keyboard(byte down, uint key, uint character, ushort modifiers)
    {
        try
        {
            // Do not translate frontend shortcuts using Ctrl/Alt/Meta.
            if (down != 0 && (modifiers & (2 | 4 | 8)) == 0 && _session is { } session && !session.Ejected)
                Input.Keyboard(key, session.Machine);
        }
        catch (Exception error) { Messages.Enqueue("gamewave: " + error.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte SetEject(byte eject)
    {
        try { bool ok = _session?.SetEject(eject != 0) == true; Input.Clear(); return Bool(ok); }
        catch (Exception error) { Error(error); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte GetEject() => Bool(_session?.Ejected == true);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static uint GetIndex() => _session?.ImageIndex ?? 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static uint GetCount() => (uint)(_session?.Images.Count ?? 0);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte SetIndex(uint index) => Bool(_session?.SetIndex(index) == true);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte Replace(uint index, RetroGameInfo* game)
    {
        try
        {
            if (game != null && Read(game->Path) is not { Length: > 0 })
                return 0;
            return Bool(_session?.Replace(index, game == null ? null : Read(game->Path)) == true);
        }
        catch (Exception error) { Error(error); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte Add()
    {
        try
        {
            if (_session is not { } session)
                return 0;
            session.AddImage();
            return 1;
        }
        catch (Exception error) { Error(error); return 0; }
    }

    static byte CopyText(string? text, byte* destination, nuint size)
    {
        if (destination == null || size == 0 || size > int.MaxValue) return 0;
        destination[0] = 0;
        if (text is null) return 0;
        int length = System.Text.Encoding.UTF8.GetByteCount(text);
        if ((nuint)length >= size) return 0;
        System.Text.Encoding.UTF8.GetBytes(text, new Span<byte>(destination, length));
        destination[length] = 0;
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte SetInitial(uint index, byte* path)
    {
        try
        {
            if (_session is not null || Read(path) is not { Length: > 0 } value) return 0;
            _initialPath = Path.GetFullPath(value); _initialIndex = index; return 1;
        }
        catch (Exception error) { Error(error); return 0; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte GetImagePath(uint index, byte* destination, nuint size)
        => CopyText(_session?.ImagePath(index), destination, size);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte GetImageLabel(uint index, byte* destination, nuint size)
        => CopyText(_session?.ImageLabel(index), destination, size);

    [UnmanagedCallersOnly(EntryPoint = "retro_serialize_size", CallConvs = [typeof(CallConvCdecl)])]
    public static nuint SerializeSize() => _session is null ? 0 : (nuint)CoreSession.StateSize;
    [UnmanagedCallersOnly(EntryPoint = "retro_serialize", CallConvs = [typeof(CallConvCdecl)])]
    public static byte Serialize(void* data, nuint size)
    {
        if (data == null || size < CoreSession.StateSize || size > int.MaxValue || _session is null) return 0;
        try
        {
            int context = 0;
            Env(72 | 0x10000, &context);
            byte[] state = _session.SaveState(Input);
            if (state.Length > CoreSession.StateSize) return 0;
            var destination = new Span<byte>(data, CoreSession.StateSize);
            destination.Clear(); state.CopyTo(destination);
            _session.StateSaved(context);
            return 1;
        }
        catch (Exception error) { Error(error); return 0; }
    }
    [UnmanagedCallersOnly(EntryPoint = "retro_unserialize", CallConvs = [typeof(CallConvCdecl)])]
    public static byte Unserialize(void* data, nuint size)
    {
        if (data == null || size < 84 || size > (128u << 20) || _session is null) return 0;
        try
        {
            int context = 0;
            uint av = 3;
            bool hasContext = Env(72 | 0x10000, &context);
            if (!hasContext) Env(47 | 0x10000, &av);
            _session.LoadState(new ReadOnlySpan<byte>(data, (int)size), Input, persistSaves: context == 0 && (av & 4) == 0, context);
            _failed = false;
            return 1;
        }
        catch (Exception error) { Error(error); return 0; }
    }
    [UnmanagedCallersOnly(EntryPoint = "retro_get_memory_data", CallConvs = [typeof(CallConvCdecl)])]
    public static void* GetMemoryData(uint id) => null;
    [UnmanagedCallersOnly(EntryPoint = "retro_get_memory_size", CallConvs = [typeof(CallConvCdecl)])]
    public static nuint GetMemorySize(uint id) => 0;
    [UnmanagedCallersOnly(EntryPoint = "retro_load_game_special", CallConvs = [typeof(CallConvCdecl)])]
    public static byte LoadSpecial(uint type, RetroGameInfo* games, nuint count) => 0;
    [UnmanagedCallersOnly(EntryPoint = "retro_get_region", CallConvs = [typeof(CallConvCdecl)])]
    public static uint GetRegion() => 0; // NTSC
    [UnmanagedCallersOnly(EntryPoint = "retro_cheat_reset", CallConvs = [typeof(CallConvCdecl)])]
    public static void CheatReset() { }
    [UnmanagedCallersOnly(EntryPoint = "retro_cheat_set", CallConvs = [typeof(CallConvCdecl)])]
    public static void CheatSet(uint index, byte enabled, byte* code) { }
}
