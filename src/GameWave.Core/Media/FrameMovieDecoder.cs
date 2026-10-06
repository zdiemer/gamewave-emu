using GameWave.Disc;
using GameWave.Engine;

namespace GameWave.Media;

/// <summary>A synchronous, checkpointable decoder used by frame-driven frontends.</summary>
sealed class FrameMovieDecoder : IDisposable
{
    readonly DiscFile _file;
    readonly Stream _stream;
    ProgramStreamReader _program;
    Mpeg2VideoDecoder _video = new();
    Mp2Decoder _audio = new();
    MoviePlayer.Resampler _resampler = new();
    readonly Queue<(long At, float[] Samples)> _pendingAudio = new();
    double _segmentOffset, _firstPts = double.NaN;
    long _audioAt = -1;
    bool _audioSeen;
    public bool Done { get; private set; }
    public double LastEnd { get; private set; }
    double _videoEnd;

    public FrameMovieDecoder(DiscFile file)
    {
        _file = file; _stream = file.Open(); _program = new(_stream);
    }

    public void Pump(AudioMixer mixer, bool loop, Action<Picture, double, double> queue, double? seek = null)
    {
        double target = seek ?? mixer.MovieTime + .12;
        void Drain()
        {
            while (_video.TryGetPicture(out var picture))
            {
                double time = double.IsNaN(picture.Pts) || double.IsNaN(_firstPts) ? LastEnd : picture.Pts - _firstPts + _segmentOffset;
                double duration = picture.Duration > 0 ? picture.Duration : 1 / 29.97;
                _videoEnd = Math.Max(_videoEnd, time + duration);
                LastEnd = Math.Max(LastEnd, time + duration);
                if (seek.HasValue && time + duration <= seek.Value) _video.Recycle(picture);
                else queue(picture, time, duration);
            }
        }
        void FlushAudio()
        {
            while (_pendingAudio.TryPeek(out var chunk) && mixer.MovieWrite(chunk.At, chunk.Samples, chunk.Samples.Length / 2))
                _pendingAudio.Dequeue();
        }
        while (!Done)
        {
            FlushAudio();
            if (_videoEnd >= target && (!_audioSeen || mixer.MovieBuffered >= (target - mixer.MovieTime) * AudioMixer.SampleRate)) break;
            if (_pendingAudio.Count > 0) break;
            if (!_program.Next(out var packet))
            {
                _video.Flush(); Drain();
                if (!loop || LastEnd <= _segmentOffset) { Done = true; break; }
                _segmentOffset = LastEnd; _firstPts = double.NaN; _audioAt = -1;
                _stream.Position = 0; _program = new(_stream); _video.Reset(); _audio.Reset();
                continue;
            }
            if (double.IsNaN(_firstPts) && !double.IsNaN(packet.Pts)) _firstPts = packet.Pts;
            if (packet.Kind == StreamKind.Video)
            {
                _video.Feed(packet.Data.AsSpan(packet.Offset, packet.Length), packet.Pts); Drain();
            }
            else if (packet.Kind == StreamKind.MpegAudio && packet.StreamId == 0xC0)
            {
                _audioSeen = true;
                _audio.Feed(packet.Data.AsSpan(packet.Offset, packet.Length), packet.Pts, (samples, frames, pts) =>
                {
                    if (!double.IsNaN(pts) && !double.IsNaN(_firstPts)) _audioAt = (long)Math.Round((pts - _firstPts + _segmentOffset) * AudioMixer.SampleRate);
                    if (_audioAt < 0) _audioAt = (long)Math.Round(_segmentOffset * AudioMixer.SampleRate);
                    var (buffer, count) = _resampler.Convert(samples, frames, _audio.SampleRate);
                    _pendingAudio.Enqueue((_audioAt, buffer.AsSpan(0, count * 2).ToArray()));
                    _audioAt += count; LastEnd = Math.Max(LastEnd, _audioAt / (double)AudioMixer.SampleRate);
                });
            }
        }
        FlushAudio();
    }

    public void Recycle(Picture picture) => _video.Recycle(picture);
    public void WriteState(BinaryWriter writer)
    {
        writer.Write(_program.Position); writer.Write(_segmentOffset); writer.Write(_firstPts); writer.Write(_audioAt);
        writer.Write(_audioSeen); writer.Write(Done); writer.Write(LastEnd); writer.Write(_videoEnd);
        _video.WriteState(writer); _audio.WriteState(writer); _resampler.WriteState(writer);
        StateIO.Array(writer, _pendingAudio, chunk => { writer.Write(chunk.At); StateIO.Numbers(writer, chunk.Samples); });
    }
    public static FrameMovieDecoder ReadState(BinaryReader reader, DiscFile file)
    {
        var decoder = new FrameMovieDecoder(file);
        try
        {
            long position = reader.ReadInt64();
            if (position < 0 || position > file.Length) throw new InvalidDataException("Invalid movie stream position.");
            decoder._stream.Position = position; decoder._program = new(decoder._stream);
            decoder._segmentOffset = reader.ReadDouble(); decoder._firstPts = reader.ReadDouble(); decoder._audioAt = reader.ReadInt64();
            decoder._audioSeen = reader.ReadBoolean(); decoder.Done = reader.ReadBoolean();
            decoder.LastEnd = reader.ReadDouble(); decoder._videoEnd = reader.ReadDouble();
            decoder._video = Mpeg2VideoDecoder.ReadState(reader); decoder._audio = Mp2Decoder.ReadState(reader);
            decoder._resampler = MoviePlayer.Resampler.ReadState(reader);
            foreach (var chunk in StateIO.Array(reader, () => (reader.ReadInt64(), StateIO.Numbers<float>(reader, 1 << 20)), 256))
                decoder._pendingAudio.Enqueue(chunk);
            return decoder;
        }
        catch { decoder.Dispose(); throw; }
    }
    public void Dispose() => _stream.Dispose();
}
