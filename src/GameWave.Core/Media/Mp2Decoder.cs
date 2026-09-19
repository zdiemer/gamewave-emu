using NLayer;

namespace GameWave.Media;

/// <summary>
/// Decodes an MPEG audio elementary stream (the movies carry MPEG-1 Layer II) fed in
/// arbitrary pieces, producing interleaved stereo float samples.
/// </summary>
public sealed class Mp2Decoder
{
    readonly MpegFrameDecoder _decoder = new();
    byte[] _buf = new byte[16384];
    int _len;
    readonly float[] _out = new float[1152 * 2];
    readonly Queue<(int Offset, double Pts)> _ptsMarks = new();
    long _consumed;

    public int SampleRate { get; private set; } = 44100;

    public void Reset()
    {
        _len = 0;
        _ptsMarks.Clear();
        _decoder.Reset();
    }

    /// <summary>
    /// Adds bytes; <paramref name="onFrame"/> receives each decoded frame's stereo samples
    /// (interleaved, 1152 frames) with the timestamp of the packet it started in.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, double pts, Action<float[], int, double> onFrame)
    {
        if (_len + data.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        if (!double.IsNaN(pts))
            _ptsMarks.Enqueue((_len, pts));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        int p = 0;
        while (p + 4 <= _len)
        {
            if (_buf[p] != 0xFF || (_buf[p + 1] & 0xE0) != 0xE0 || !Mp2Frame.TryParse(_buf, p, out var frame))
            {
                p++;
                continue;
            }
            if (p + frame.FrameLength > _len)
                break;
            double framePts = double.NaN;
            while (_ptsMarks.Count > 0 && _ptsMarks.Peek().Offset <= p)
                framePts = _ptsMarks.Dequeue().Pts;
            int n;
            try
            {
                n = _decoder.DecodeFrame(frame, _out, 0);
            }
            catch (Exception)
            {
                n = 0;
            }
            SampleRate = frame.SampleRate;
            if (n > 0)
            {
                int frames = frame.ChannelMode == MpegChannelMode.Mono ? n : n / 2;
                if (frame.ChannelMode == MpegChannelMode.Mono)
                {
                    // Spread mono to both channels, back to front so nothing is overwritten.
                    for (int i = frames - 1; i >= 0; i--)
                    {
                        _out[2 * i + 1] = _out[i];
                        _out[2 * i] = _out[i];
                    }
                }
                onFrame(_out, frames, framePts);
            }
            p += frame.FrameLength;
        }
        if (p > 0)
        {
            Array.Copy(_buf, p, _buf, 0, _len - p);
            _len -= p;
            int count = _ptsMarks.Count;
            for (int i = 0; i < count; i++)
            {
                var (o, t) = _ptsMarks.Dequeue();
                _ptsMarks.Enqueue((Math.Max(0, o - p), t));
            }
            _consumed += p;
        }
    }

    sealed class Mp2Frame : IMpegFrame
    {
        static readonly int[] BitratesV1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0];
        static readonly int[] BitratesV1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0];
        static readonly int[] BitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        static readonly int[] BitratesV2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0];
        static readonly int[] BitratesV2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
        static readonly int[] Rates = [44100, 48000, 32000, 0];

        byte[] _data = [];
        int _start;
        int _bitPos;

        public int SampleRate { get; private set; }
        public int SampleRateIndex { get; private set; }
        public int FrameLength { get; private set; }
        public int BitRate { get; private set; }
        public MpegVersion Version { get; private set; }
        public MpegLayer Layer { get; private set; }
        public MpegChannelMode ChannelMode { get; private set; }
        public int ChannelModeExtension { get; private set; }
        public int SampleCount { get; private set; }
        public int BitRateIndex { get; private set; }
        public bool IsCopyrighted { get; private set; }
        public bool HasCrc { get; private set; }
        public bool IsCorrupted => false;

        public static bool TryParse(byte[] b, int p, out Mp2Frame frame)
        {
            frame = new Mp2Frame();
            uint h = (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
            int ver = (int)(h >> 19) & 3;
            int layer = (int)(h >> 17) & 3;
            int brIndex = (int)(h >> 12) & 15;
            int srIndex = (int)(h >> 10) & 3;
            if (ver == 1 || layer == 0 || brIndex == 0 || brIndex == 15 || srIndex == 3)
                return false;
            frame.Version = ver switch { 3 => MpegVersion.Version1, 2 => MpegVersion.Version2, _ => MpegVersion.Version25 };
            frame.Layer = layer switch { 3 => MpegLayer.LayerI, 2 => MpegLayer.LayerII, _ => MpegLayer.LayerIII };
            frame.HasCrc = ((h >> 16) & 1) == 0;
            frame.BitRateIndex = brIndex;
            frame.SampleRateIndex = srIndex;
            int rate = Rates[srIndex];
            if (frame.Version == MpegVersion.Version2) rate /= 2;
            else if (frame.Version == MpegVersion.Version25) rate /= 4;
            frame.SampleRate = rate;
            bool v1 = frame.Version == MpegVersion.Version1;
            int kbps = frame.Layer switch
            {
                MpegLayer.LayerI => v1 ? BitratesV1L1[brIndex] : BitratesV2L1[brIndex],
                MpegLayer.LayerII => v1 ? BitratesV1L2[brIndex] : BitratesV2L23[brIndex],
                _ => v1 ? BitratesV1L3[brIndex] : BitratesV2L23[brIndex],
            };
            frame.BitRate = kbps * 1000;
            int padding = (int)(h >> 9) & 1;
            frame.ChannelMode = (MpegChannelMode)((h >> 6) & 3);
            frame.ChannelModeExtension = (int)(h >> 4) & 3;
            frame.IsCopyrighted = ((h >> 3) & 1) != 0;
            if (frame.Layer == MpegLayer.LayerI)
            {
                frame.SampleCount = 384;
                frame.FrameLength = (12 * frame.BitRate / rate + padding) * 4;
            }
            else
            {
                frame.SampleCount = frame.Layer == MpegLayer.LayerIII && !v1 ? 576 : 1152;
                int factor = frame.Layer == MpegLayer.LayerIII && !v1 ? 72 : 144;
                frame.FrameLength = factor * frame.BitRate / rate + padding;
            }
            if (frame.FrameLength < 8)
                return false;
            frame._data = b;
            frame._start = p;
            frame.Reset();
            return true;
        }

        public void Reset() => _bitPos = (_start + 4 + (HasCrc ? 2 : 0)) * 8;

        public int ReadBits(int bitCount)
        {
            int v = 0;
            for (int i = 0; i < bitCount; i++)
            {
                int byteIndex = _bitPos >> 3;
                int bit = byteIndex < _data.Length && byteIndex < _start + FrameLength
                    ? (_data[byteIndex] >> (7 - (_bitPos & 7))) & 1
                    : 0;
                v = (v << 1) | bit;
                _bitPos++;
            }
            return v;
        }
    }
}
