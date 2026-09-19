using GameWave.Engine;

namespace GameWave.Media;

/// <summary>A loaded sound effect: mono samples at the mixer rate.</summary>
public sealed class Sound
{
    public readonly float[] Samples;
    public readonly string Name;

    public Sound(float[] samples, string name)
    {
        Samples = samples;
        Name = name;
    }

    /// <summary>
    /// Decodes a <c>.zwf</c> sound: a 20-byte header (magic 0x7C90EE02, sample count,
    /// channel count, compressed size, a checksum) and zlib-compressed 16-bit big-endian PCM
    /// at 44.1 kHz.
    /// </summary>
    public static Sound FromZwf(byte[] d, string name)
    {
        if (d.Length < 20)
            throw new InvalidDataException("zwf file is too short");
        int samples = BitConverter.ToInt32(d, 4);
        int channels = Math.Max(1, BitConverter.ToInt32(d, 8));
        int compressed = BitConverter.ToInt32(d, 12);
        var raw = new byte[samples * channels * 2];
        using (var z = new System.IO.Compression.ZLibStream(new MemoryStream(d, 20, Math.Min(compressed, d.Length - 20)),
                   System.IO.Compression.CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
        }
        var mono = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (i * channels + c) * 2;
                sum += (short)((raw[o] << 8) | raw[o + 1]) / 32768f;
            }
            mono[i] = sum / channels;
        }
        return new Sound(mono, name);
    }
}

/// <summary>
/// Mixes the movie soundtrack and sound effects into 44.1 kHz stereo. The host's audio
/// device pulls with <see cref="Mix"/>; with no device, a background thread pulls in real
/// time so movies still keep time.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int SampleRate = 44100;
    const int RingFrames = SampleRate * 2;
    const int MaxVoices = 8;

    readonly object _gate = new();
    readonly EmuClock _clock;

    // Movie soundtrack on an absolute timeline of sample frames since the movie started.
    readonly float[] _ring = new float[RingFrames * 2];
    long _written;    // highest frame index written + 1
    long _played;     // next frame index to play
    bool _movieActive;
    bool _movieClockRunning;

    sealed class Voice
    {
        public Sound Sound = null!;
        public int Position;
        public bool Loop;
        public int Id;
    }

    readonly List<Voice> _voices = new();
    readonly Queue<Voice> _waiting = new();

    Thread? _virtualDevice;
    volatile bool _disposed;
    volatile bool _externalDevice;

    public float MasterVolume { get; set; } = 1f;
    public float MovieVolume { get; set; } = 1f;
    public float EffectsVolume { get; set; } = 1f;
    public bool Muted { get; set; }
    public bool Paused { get; set; }

    /// <summary>Seconds of output the device buffers, so video can be shown in step with what is heard.</summary>
    public double DeviceLatency { get; set; }

    public AudioMixer(EmuClock clock)
    {
        _clock = clock;
        _virtualDevice = new Thread(VirtualDevice) { IsBackground = true, Name = "Game Wave audio clock" };
        _virtualDevice.Start();
    }

    /// <summary>Set when a real audio device is pulling samples; the stand-in clock then stops.</summary>
    public bool ExternalDevice
    {
        get => _externalDevice;
        set => _externalDevice = value;
    }

    void VirtualDevice()
    {
        var buf = new float[512 * 2];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long framesDone = 0;
        while (!_disposed)
        {
            Thread.Sleep(5);
            if (_externalDevice)
            {
                sw.Restart();
                framesDone = 0;
                continue;
            }
            long due = (long)(sw.Elapsed.TotalSeconds * SampleRate);
            while (framesDone + 512 <= due)
            {
                Mix(buf);
                framesDone += 512;
            }
        }
    }

    // ------------------------------------------------------------------ movie soundtrack

    /// <summary>Starts a new movie timeline at frame zero.</summary>
    public void MovieBegin()
    {
        lock (_gate)
        {
            _written = 0;
            _played = 0;
            _movieActive = true;
            _movieClockRunning = false;
            Array.Clear(_ring);
        }
    }

    public void MovieEnd()
    {
        lock (_gate)
        {
            _movieActive = false;
            _movieClockRunning = false;
        }
    }

    /// <summary>Lets the movie clock run (after pre-roll) or holds it.</summary>
    public bool MovieClockRunning
    {
        get { lock (_gate) return _movieClockRunning; }
        set { lock (_gate) _movieClockRunning = value; }
    }

    /// <summary>The movie clock: seconds of the timeline heard so far.</summary>
    public double MovieTime
    {
        get
        {
            lock (_gate)
                return Math.Max(0, _played / (double)SampleRate - DeviceLatency);
        }
    }

    /// <summary>
    /// Writes stereo frames at timeline position <paramref name="at"/>. Frames already
    /// played are dropped. Returns false (writing nothing) if the ring cannot hold them yet.
    /// </summary>
    public bool MovieWrite(long at, float[] stereo, int frames)
    {
        lock (_gate)
        {
            if (!_movieActive)
                return true;
            if (at + frames - _played > RingFrames)
                return false;
            for (int i = 0; i < frames; i++)
            {
                long idx = at + i;
                if (idx < _played)
                    continue;
                int slot = (int)(idx % RingFrames) * 2;
                _ring[slot] = stereo[2 * i];
                _ring[slot + 1] = stereo[2 * i + 1];
            }
            _written = Math.Max(_written, at + frames);
            return true;
        }
    }

    /// <summary>How far ahead of the playhead the soundtrack is buffered, in frames.</summary>
    public long MovieBuffered
    {
        get { lock (_gate) return _written - _played; }
    }

    // ------------------------------------------------------------------ sound effects

    int _nextVoiceId;

    public int Play(Sound s, bool loop)
    {
        lock (_gate)
        {
            var v = new Voice { Sound = s, Loop = loop, Id = ++_nextVoiceId };
            if (_voices.Count < MaxVoices)
                _voices.Add(v);
            else
                _waiting.Enqueue(v);
            return v.Id;
        }
    }

    public void StopSound(Sound s)
    {
        lock (_gate)
        {
            _voices.RemoveAll(v => v.Sound == s);
            var keep = _waiting.Where(v => v.Sound != s).ToList();
            _waiting.Clear();
            foreach (var v in keep)
                _waiting.Enqueue(v);
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            _voices.Clear();
            _waiting.Clear();
            _movieActive = false;
            _movieClockRunning = false;
        }
    }

    // ------------------------------------------------------------------ mixing

    /// <summary>Fills an interleaved stereo buffer. Called on the audio device's thread.</summary>
    public void Mix(Span<float> output)
    {
        output.Clear();
        int frames = output.Length / 2;
        lock (_gate)
        {
            if (Paused)
                return;
            float mv = MovieVolume * MasterVolume;
            if (_movieActive && _movieClockRunning)
            {
                for (int i = 0; i < frames; i++)
                {
                    long idx = _played + i;
                    if (idx < _written)
                    {
                        int slot = (int)(idx % RingFrames) * 2;
                        output[2 * i] += _ring[slot] * mv;
                        output[2 * i + 1] += _ring[slot + 1] * mv;
                        _ring[slot] = 0;
                        _ring[slot + 1] = 0;
                    }
                }
                _played += frames;
            }

            float ev = EffectsVolume * MasterVolume;
            for (int vi = 0; vi < _voices.Count; vi++)
            {
                var v = _voices[vi];
                var s = v.Sound.Samples;
                for (int i = 0; i < frames; i++)
                {
                    if (v.Position >= s.Length)
                    {
                        if (!v.Loop || s.Length == 0)
                            break;
                        v.Position = 0;
                    }
                    float x = s[v.Position++] * ev;
                    output[2 * i] += x;
                    output[2 * i + 1] += x;
                }
                if (v.Position >= s.Length && !v.Loop)
                {
                    _voices.RemoveAt(vi);
                    vi--;
                    if (_waiting.Count > 0)
                        _voices.Add(_waiting.Dequeue());
                }
            }
        }
        if (Muted)
        {
            output.Clear();
            return;
        }
        for (int i = 0; i < output.Length; i++)
            output[i] = Math.Clamp(output[i], -1f, 1f);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
