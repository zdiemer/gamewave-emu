using GameWave.Disc;
using GameWave.Engine;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Libretro;

/// <summary>Content and console lifetime, independent of native callback marshalling.</summary>
sealed class CoreSession : IDisposable
{
    public const double Fps = 60;
    public const int AudioFrames = AudioMixer.SampleRate / 60;
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

    public void Run()
    {
        if (!Ejected)
            Machine.RunFrame(1 / Fps);
        Machine.Audio.Mix(Mix);
        for (int i = 0; i < Mix.Length; i++)
            Pcm[i] = (short)Math.Clamp((int)Math.Round(Mix[i] * 32768), short.MinValue, short.MaxValue);
        Machine.RenderFrame(Frame);
    }

    public void Reset()
    {
        Machine.Input.Clear();
        if (!Ejected)
            Machine.Reset();
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
