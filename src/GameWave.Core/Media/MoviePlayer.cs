using System.Collections.Concurrent;
using GameWave.Disc;
using GameWave.Engine;

namespace GameWave.Media;

/// <summary>
/// Plays one .mpg at a time, as the console's movie module does: load, play (optionally
/// looping), pause (<c>Stop(1)</c>) and resume, stop. A background thread demultiplexes and
/// decodes ahead; pictures are shown against the soundtrack's clock.
/// </summary>
public sealed class MoviePlayer : IDisposable
{
    internal sealed record Snapshot(DiscFile? File, bool Loop, bool Playing, bool Paused, double Time, Picture? Shown, byte[]? Decoder = null);
    readonly AudioMixer _mixer;
    readonly object _gate = new();

    DiscFile? _file;
    DiscFile? _playingFile;
    bool _loop;
    Thread? _thread;
    volatile bool _cancel;
    volatile bool _paused;
    volatile bool _playing;
    volatile bool _demuxDone;

    // Decoded pictures waiting to be shown, with timeline times (seconds from movie start).
    readonly Queue<(Picture Pic, double Time, double Duration)> _frames = new();
    readonly ConcurrentQueue<Picture> _returned = new();
    const int MaxQueuedFrames = 40;

    Picture? _shown;
    double _shownUntil;
    double _endTime;
    double _seekTime;
    readonly bool _frameDriven;
    FrameMovieDecoder? _decoder;

    public MoviePlayer(AudioMixer mixer, bool frameDriven = false) { _mixer = mixer; _frameDriven = frameDriven; }

    /// <summary>What <c>movie.GetState</c> reports: 1 while a movie is playing or paused, 0 otherwise.</summary>
    public int State => _playing ? 1 : 0;

    public bool IsPaused => _paused;

    public string? CurrentFile => _file?.Path;

    /// <summary>Remembers the movie to play next. Returns false if it is not on the disc.</summary>
    public bool Load(DiscFile? file)
    {
        Stop(true);
        // Loading clears the loop flag, as the engine does: a looping menu movie must not
        // make the next movie loop too.
        _loop = false;
        _file = file;
        return file is not null;
    }

    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    public void Play() => PlayAt(0);

    void PlayAt(double time)
    {
        // Games call Play in their input loops while a movie loops; asking for the movie that
        // is already playing changes nothing.
        if (_playing && !_paused && _file is not null && ReferenceEquals(_file, _playingFile))
            return;
        Stop(true);
        if (_file is null)
            return;
        _playingFile = _file;
        _seekTime = time;
        lock (_gate)
        {
            _frames.Clear();
            _shown = null;
            _shownUntil = 0;
            _endTime = double.MaxValue;
        }
        _cancel = false;
        _paused = false;
        _demuxDone = false;
        _playing = true;
        _mixer.MovieBegin();
        if (time > 0)
            _mixer.SeekMovie(time);
        if (_frameDriven)
        {
            _decoder = new FrameMovieDecoder(_file);
            if (time > 0) _decoder.Pump(_mixer, _loop, QueuePicture, seek: time);
            Pump();
            _mixer.MovieClockRunning = true;
            return;
        }
        var file = _file;
        _thread = new Thread(() => DecodeThread(file)) { IsBackground = true, Name = "Game Wave movie" };
        _thread.Start();
    }

    /// <summary>Pauses on the current picture (the console's <c>Stop(1)</c>).</summary>
    public void Pause()
    {
        if (!_playing)
            return;
        _paused = true;
        _mixer.MovieClockRunning = false;
    }

    public void Resume()
    {
        if (!_playing || !_paused)
            return;
        _paused = false;
        _mixer.MovieClockRunning = true;
    }

    /// <summary>Stops playback. The last picture stays on the video plane.</summary>
    public void Stop(bool wait)
    {
        var t = _thread;
        _cancel = true;
        _playing = false;
        _paused = false;
        _mixer.MovieEnd();
        if (t is not null && wait)
            t.Join(2000);
        _thread = null;
        _decoder?.Dispose(); _decoder = null;
        lock (_gate)
        {
            _returned.Clear();
            while (_frames.Count > 0)
                _frames.Dequeue();
        }
    }

    public void Dispose() => Stop(true);

    internal Snapshot CaptureState()
    {
        lock (_gate)
        {
            byte[]? bytes = null;
            if (_decoder is not null)
            {
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                _decoder.WriteState(writer);
                StateIO.Array(writer, _frames, f => { f.Pic.WriteState(writer); writer.Write(f.Time); writer.Write(f.Duration); });
                writer.Write(_shownUntil); writer.Write(_endTime); writer.Write(_demuxDone);
                bytes = stream.ToArray();
            }
            return new(_file, _loop, _playing, _paused, _mixer.MovieTime, _shown?.Clone(), bytes);
        }
    }

    internal void RestoreState(Snapshot state)
    {
        Stop(true);
        _file = state.File;
        _loop = state.Loop;
        if (_frameDriven && state.Decoder is not null && state.File is not null)
        {
            using var stream = new MemoryStream(state.Decoder, false); using var reader = new BinaryReader(stream);
            _decoder = FrameMovieDecoder.ReadState(reader, state.File);
            _frames.Clear();
            foreach (var frame in StateIO.Array(reader, () => (Picture.ReadState(reader), reader.ReadDouble(), reader.ReadDouble()), 256)) _frames.Enqueue(frame);
            _shownUntil = reader.ReadDouble(); _endTime = reader.ReadDouble(); _demuxDone = reader.ReadBoolean();
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected movie decoder data.");
            _playingFile = state.File; _playing = state.Playing; _paused = state.Paused; _cancel = false;
            _shown = state.Shown?.Clone();
            return;
        }
        if (!state.Playing || state.File is null)
        {
            lock (_gate)
                _shown = state.Shown?.Clone();
            return;
        }
        PlayAt(state.Time);
        _loop = state.Loop;
        lock (_gate)
            _shown = state.Shown?.Clone();
        if (state.Paused)
            Pause();
    }

    /// <summary>
    /// Advances to the picture due now and hands it to <paramref name="use"/> (under the
    /// player's lock, so it cannot be recycled meanwhile). Also retires the movie once its
    /// last picture and sound are done. Returns false when there is no picture yet.
    /// </summary>
    public bool WithCurrentPicture(Action<Picture> use)
    {
        Pump();
        double now = _mixer.MovieTime;
        lock (_gate)
        {
            while (_frames.Count > 0)
            {
                var (pic, time, dur) = _frames.Peek();
                // Show a frame from its time until the next frame's time.
                if (time > now && _shown is not null)
                    break;
                _frames.Dequeue();
                if (_shown is not null)
                    _returned.Enqueue(_shown);
                _shown = pic;
                _shownUntil = time + dur;
                if (_frames.Count == 0 || _frames.Peek().Time > now)
                    break;
            }
            if (_playing && !_paused && _demuxDone && _frames.Count == 0 && now >= _endTime)
            {
                _playing = false;
                _mixer.MovieEnd();
            }
            if (_shown is null)
                return false;
            use(_shown);
            return true;
        }
    }

    void QueuePicture(Picture picture, double time, double duration) => _frames.Enqueue((picture, time, duration));
    public void Pump()
    {
        if (!_frameDriven || _decoder is null || !_playing || _paused) return;
        lock (_gate)
        {
            while (_returned.TryDequeue(out var picture)) _decoder.Recycle(picture);
            _decoder.Pump(_mixer, _loop, QueuePicture);
            _demuxDone = _decoder.Done; _endTime = _decoder.LastEnd;
        }
    }

    // ------------------------------------------------------------------ decoding

    void DecodeThread(DiscFile file)
    {
        var video = new Mpeg2VideoDecoder();
        var audio = new Mp2Decoder();
        double segmentOffset = 0;   // timeline position where the current pass starts
        double firstPts = double.NaN;
        double lastEnd = 0;
        bool clockStarted = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resampleState = new Resampler();

        try
        {
            while (!_cancel)
            {
                using var stream = file.Open();
                var ps = new ProgramStreamReader(stream);
                video.Reset();
                audio.Reset();
                firstPts = double.NaN;
                long audioAt = -1;

                while (!_cancel && ps.Next(out var pk))
                {
                    if (double.IsNaN(firstPts) && !double.IsNaN(pk.Pts))
                        firstPts = pk.Pts;

                    if (pk.Kind == StreamKind.Video)
                    {
                        video.Feed(pk.Data.AsSpan(pk.Offset, pk.Length), pk.Pts);
                        DrainVideo(video, firstPts, segmentOffset, ref lastEnd);
                    }
                    else if (pk.Kind == StreamKind.MpegAudio && pk.StreamId == 0xC0)
                    {
                        audio.Feed(pk.Data.AsSpan(pk.Offset, pk.Length), pk.Pts, (samples, frames, pts) =>
                        {
                            if (!double.IsNaN(pts) && !double.IsNaN(firstPts))
                                audioAt = (long)Math.Round((pts - firstPts + segmentOffset) * AudioMixer.SampleRate);
                            if (audioAt < 0)
                                audioAt = (long)Math.Round(segmentOffset * AudioMixer.SampleRate);
                            var (buf, n) = resampleState.Convert(samples, frames, audio.SampleRate);
                            while (!_cancel && !_mixer.MovieWrite(audioAt, buf, n))
                                Thread.Sleep(5);
                            audioAt += n;
                            lastEnd = Math.Max(lastEnd, audioAt / (double)AudioMixer.SampleRate);
                        });
                    }

                    // Start the clock once a little is buffered, so the movie opens cleanly.
                    if (!clockStarted && (FrameCount >= 3 || sw.ElapsedMilliseconds > 500))
                    {
                        clockStarted = true;
                        if (!_paused)
                            _mixer.MovieClockRunning = true;
                    }

                    // Don't run too far ahead of what is being shown.
                    while (!_cancel && FrameCount >= MaxQueuedFrames)
                    {
                        if (!clockStarted)
                        {
                            clockStarted = true;
                            _mixer.MovieClockRunning = !_paused;
                        }
                        Thread.Sleep(5);
                    }
                    ReturnPictures(video);
                }
                video.Flush();
                DrainVideo(video, firstPts, segmentOffset, ref lastEnd);

                if (!_loop || _cancel)
                    break;
                segmentOffset = lastEnd;
            }
        }
        catch (Exception)
        {
            // A damaged movie simply ends.
        }
        if (!clockStarted && !_cancel)
            _mixer.MovieClockRunning = !_paused;
        lock (_gate)
            _endTime = lastEnd;
        _demuxDone = true;
    }

    int FrameCount
    {
        get { lock (_gate) return _frames.Count; }
    }

    void ReturnPictures(Mpeg2VideoDecoder video)
    {
        while (_returned.TryDequeue(out var p))
            video.Recycle(p);
    }

    void DrainVideo(Mpeg2VideoDecoder video, double firstPts, double segmentOffset, ref double lastEnd)
    {
        while (video.TryGetPicture(out var pic))
        {
            double t = double.IsNaN(pic.Pts) || double.IsNaN(firstPts) ? lastEnd : pic.Pts - firstPts + segmentOffset;
            double dur = pic.Duration > 0 ? pic.Duration : 1 / 29.97;
            lastEnd = Math.Max(lastEnd, t + dur);
            if (t + dur <= _seekTime)
            {
                video.Recycle(pic);
                continue;
            }
            lock (_gate)
                _frames.Enqueue((pic, t, dur));
        }
    }

    /// <summary>Linear resampling of stereo frames to the mixer rate.</summary>
    internal sealed class Resampler
    {
        float[] _out = new float[4096];
        double _phase;
        float _lastL, _lastR;
        internal void WriteState(BinaryWriter writer) { writer.Write(_phase); writer.Write(_lastL); writer.Write(_lastR); }
        internal static Resampler ReadState(BinaryReader reader) => new() { _phase = reader.ReadDouble(), _lastL = reader.ReadSingle(), _lastR = reader.ReadSingle() };

        public (float[], int) Convert(float[] input, int frames, int rate)
        {
            if (rate == AudioMixer.SampleRate)
            {
                if (_out.Length < frames * 2)
                    _out = new float[frames * 2];
                Array.Copy(input, _out, frames * 2);
                return (_out, frames);
            }
            double step = rate / (double)AudioMixer.SampleRate;
            int max = (int)(frames / step) + 4;
            if (_out.Length < max * 2)
                _out = new float[max * 2];
            int n = 0;
            while (_phase < frames)
            {
                int i = (int)_phase;
                float f = (float)(_phase - i);
                float l0 = i == 0 ? _lastL : input[2 * (i - 1)], r0 = i == 0 ? _lastR : input[2 * (i - 1) + 1];
                float l1 = input[2 * i], r1 = input[2 * i + 1];
                _out[2 * n] = l0 + (l1 - l0) * f;
                _out[2 * n + 1] = r0 + (r1 - r0) * f;
                n++;
                _phase += step;
            }
            _phase -= frames;
            _lastL = input[2 * (frames - 1)];
            _lastR = input[2 * (frames - 1) + 1];
            return (_out, n);
        }
    }
}
