using GameWave.Disc;
using GameWave.Engine;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;
using System.Security.Cryptography;

namespace GameWave.Libretro;

/// <summary>Content and console lifetime, independent of native callback marshalling.</summary>
sealed class CoreSession : IDisposable
{
    public const double Fps = 60;
    public const int AudioFrames = AudioMixer.SampleRate / 60;
    // Fixed for the entire content session, as required by libretro. State files carry
    // their compressed payload length; frontends can compress the zero-filled tail.
    public const int StateSize = 16 << 20;
    public readonly uint[] Frame = new uint[Osd.Width * Osd.Height];
    public readonly float[] Mix = new float[AudioFrames * 2];
    public readonly short[] Pcm = new short[AudioFrames * 2];
    public Machine Machine { get; private set; } = null!;
    public List<string?> Images { get; } = [];
    public uint ImageIndex { get; private set; }
    public bool Ejected => _ejected || Machine.State == MachineState.TrayOpen;
    readonly string _cache;
    readonly SaveStore _saves;
    readonly Action<string> _log;
    string? _loadedPath;
    bool _ejected;
    bool _speculative;

    public CoreSession(string content, string saveDirectory, Action<string> log)
    {
        _log = log;
        _cache = Path.Combine(saveDirectory, "gamewave", "unpacked");
        _saves = new SaveStore(Path.Combine(saveDirectory, "gamewave", "gamewave.saves"));
        content = Path.GetFullPath(content);
        if (Path.GetExtension(content).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(content))
            {
                var entry = line.Trim();
                if (entry.Length == 0 || entry.StartsWith('#'))
                    continue;
                Images.Add(Path.GetFullPath(entry, Path.GetDirectoryName(content)!));
            }
            if (Images.Count == 0)
                throw new InvalidDataException("The disc playlist is empty.");
        }
        else
            Images.Add(content);
        Load(Images[0]!);
    }

    IDisc Open(string path)
    {
        if (Path.GetFileName(path).Equals("gamewave.diz", StringComparison.OrdinalIgnoreCase))
            path = Path.GetDirectoryName(path)!;
        return DiscLoader.Open(path, _cache);
    }

    void Load(string path)
    {
        var disc = Open(path);
        if (Machine is not null)
            Machine.ChangeDisc(disc);
        else
        {
            try
            {
                // Reject broken content during retro_load_game, before starting a worker.
                var info = disc.Find("/gamewave.diz") ?? throw new InvalidDataException("There is no gamewave.diz on the disc.");
                var game = GameInfo.Parse(System.Text.Encoding.Latin1.GetString(info.ReadAll()));
                var program = disc.Find(game.AppFile) ?? throw new InvalidDataException("The game program is missing.");
                ZbcLoader.Load(program.ReadAll(), game.AppFile);
                Machine = new Machine(disc, _saves, frameDriven: true) { Log = _log };
                Machine.Start();
            }
            catch
            {
                disc.Dispose();
                throw;
            }
        }
        _loadedPath = path;
    }

    public void Run(bool hardDisableAudio = false)
    {
        bool speculative = _speculative || hardDisableAudio;
        // Runahead's first frame is committed even when A/V is disabled. Serialize
        // marks the following frames speculative; loading rolls those writes back.
        _saves.WriteThrough = false;
        Machine.SuppressSideEffects = speculative;
        if (!Ejected)
            Machine.RunFrame(1 / Fps);
        Machine.Video.Movie.Pump();
        Machine.Audio.Mix(Mix);
        for (int i = 0; i < Mix.Length; i++)
            Pcm[i] = (short)Math.Clamp((int)Math.Round(Mix[i] * 32768), short.MinValue, short.MaxValue);
        Machine.RenderFrame(Frame);
        if (!speculative) _saves.Flush();
    }

    public void Reset()
    {
        _speculative = false;
        Machine.Input.Clear();
        if (!Ejected)
            Machine.Reset();
    }

    public byte[] SaveState(RetroInput input)
    {
        if (Ejected) throw new InvalidOperationException("Insert a disc before saving a state.");
        byte[] machine = Machine.SaveState();
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, true))
        {
            writer.Write(ImageIndex); input.WriteState(writer); writer.Write(machine);
        }
        byte[] bytes = payload.ToArray();
        using var result = new MemoryStream();
        using (var writer = new BinaryWriter(result, System.Text.Encoding.UTF8, true))
        {
            writer.Write("GWRETRO1"u8); writer.Write(bytes.Length);
            writer.Write(SHA256.HashData(bytes)); writer.Write(bytes);
        }
        return result.ToArray();
    }

    public void StateSaved(int context)
    {
        if (context == 1)
        {
            if (!_speculative) _saves.Flush();
            _speculative = true;
        }
    }

    public void LoadState(ReadOnlySpan<byte> data, RetroInput input, bool persistSaves = true, int context = 0)
    {
        if (_ejected) throw new InvalidOperationException("Insert a disc before loading a state.");
        using var stream = new MemoryStream(data.ToArray(), false);
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("GWRETRO1"u8)) throw new InvalidDataException("Unsupported core state format.");
        int length = reader.ReadInt32();
        if (length < 1120 || length > (128 << 20) - 44) throw new InvalidDataException("Invalid core state length.");
        byte[] checksum = reader.ReadBytes(32), bytes = reader.ReadBytes(length);
        if (bytes.Length != length || !checksum.AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            throw new InvalidDataException("The save state is truncated or damaged.");
        using var payload = new MemoryStream(bytes, false);
        using var stateReader = new BinaryReader(payload);
        uint index = stateReader.ReadUInt32();
        if (index != ImageIndex) throw new InvalidDataException("Select the saved disc before loading this state.");
        var savedInput = RetroInput.ReadState(stateReader);
        Machine.LoadState(bytes.AsSpan((int)payload.Position), persistSaves);
        input.RestoreState(savedInput);
        _speculative = context == 2;
    }

    public bool SetEject(bool eject)
    {
        if (eject)
        {
            if (!Ejected)
                Machine.Stop();
            _ejected = true;
            return true;
        }
        if (!Ejected)
            return true;
        if (ImageIndex >= Images.Count || Images[(int)ImageIndex] is not { } path)
            return false;
        if (path != _loadedPath)
            Load(path);
        else if (Machine.State == MachineState.TrayOpen)
            Machine.CloseTray();
        else
            Machine.Start();
        _ejected = false;
        Machine.Input.Clear();
        return true;
    }

    public bool SetIndex(uint index)
    {
        if (!Ejected)
            return false;
        // Any index at or beyond the list denotes an empty drive in the libretro API.
        ImageIndex = Math.Min(index, (uint)Images.Count);
        return true;
    }

    public bool Replace(uint index, string? path)
    {
        if (!Ejected || index >= Images.Count)
            return false;
        if (path is null)
        {
            Images.RemoveAt((int)index);
            if (ImageIndex > index)
                ImageIndex--;
        }
        else
            Images[(int)index] = Path.GetFullPath(path);
        return true;
    }

    public void Dispose() => Machine?.Dispose();
}
