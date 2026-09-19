namespace GameWave.Media;

public enum StreamKind
{
    Video,
    MpegAudio,
    Other,
}

public readonly record struct PesPacket(StreamKind Kind, int StreamId, double Pts, byte[] Data, int Offset, int Length);

/// <summary>
/// Reads PES packets out of an MPEG program stream (the .mpg files): pack headers, system
/// headers and padding are skipped; video and MPEG audio payloads are returned with their
/// presentation times.
/// </summary>
public sealed class ProgramStreamReader
{
    readonly Stream _s;
    readonly byte[] _buf = new byte[65536 + 64];
    int _pos;
    int _len;
    bool _eof;

    public ProgramStreamReader(Stream s) => _s = s;

    public long Position => _s.Position - (_len - _pos);

    bool Fill(int need)
    {
        if (_len - _pos >= need)
            return true;
        if (_eof)
            return false;
        if (_pos > 0)
        {
            Array.Copy(_buf, _pos, _buf, 0, _len - _pos);
            _len -= _pos;
            _pos = 0;
        }
        while (_len < need)
        {
            int n = _s.Read(_buf, _len, _buf.Length - _len);
            if (n <= 0)
            {
                _eof = true;
                return _len - _pos >= need;
            }
            _len += n;
        }
        return true;
    }

    /// <summary>Returns the next audio or video packet, or false at the end of the stream.</summary>
    public bool Next(out PesPacket packet)
    {
        while (true)
        {
            if (!Fill(4))
            {
                packet = default;
                return false;
            }
            // Resynchronise on a start code.
            if (!(_buf[_pos] == 0 && _buf[_pos + 1] == 0 && _buf[_pos + 2] == 1))
            {
                _pos++;
                continue;
            }
            int id = _buf[_pos + 3];
            if (id == 0xB9)
            {
                _pos += 4;
                packet = default;
                return false;
            }
            if (id == 0xBA)
            {
                if (!Fill(14))
                {
                    packet = default;
                    return false;
                }
                if ((_buf[_pos + 4] & 0xC0) == 0x40)
                {
                    int stuffing = _buf[_pos + 13] & 7;
                    _pos += 14 + stuffing;
                }
                else
                {
                    _pos += 12; // MPEG-1 pack header
                }
                continue;
            }
            if (id < 0xBB)
            {
                _pos += 4;
                continue;
            }
            if (!Fill(6))
            {
                packet = default;
                return false;
            }
            int length = (_buf[_pos + 4] << 8) | _buf[_pos + 5];
            if (!Fill(6 + length))
            {
                packet = default;
                return false;
            }
            int start = _pos + 6;
            int end = start + length;
            _pos = end;

            StreamKind kind = id >= 0xE0 && id <= 0xEF ? StreamKind.Video
                : id >= 0xC0 && id <= 0xDF ? StreamKind.MpegAudio
                : StreamKind.Other;
            if (kind == StreamKind.Other || id == 0xBB || id == 0xBE || id == 0xBF)
                continue;

            double pts = double.NaN;
            int p = start;
            if ((_buf[p] & 0xC0) == 0x80)
            {
                // MPEG-2 PES header.
                int flags = _buf[p + 1];
                int headerLength = _buf[p + 2];
                if ((flags & 0x80) != 0)
                    pts = ReadTimestamp(p + 3);
                p += 3 + headerLength;
            }
            else
            {
                // MPEG-1 style header: stuffing, optional STD buffer, then timestamps.
                while (p < end && _buf[p] == 0xFF)
                    p++;
                if ((_buf[p] & 0xC0) == 0x40)
                    p += 2;
                if ((_buf[p] & 0xF0) == 0x20)
                {
                    pts = ReadTimestamp(p);
                    p += 5;
                }
                else if ((_buf[p] & 0xF0) == 0x30)
                {
                    pts = ReadTimestamp(p);
                    p += 10;
                }
                else
                {
                    p++;
                }
            }
            if (p > end)
                continue;
            var data = new byte[end - p];
            Array.Copy(_buf, p, data, 0, data.Length);
            packet = new PesPacket(kind, id, pts, data, 0, data.Length);
            return true;
        }
    }

    double ReadTimestamp(int p)
    {
        long v = ((long)(_buf[p] >> 1) & 7) << 30;
        v |= (long)_buf[p + 1] << 22;
        v |= (long)(_buf[p + 2] >> 1) << 15;
        v |= (long)_buf[p + 3] << 7;
        v |= (long)(_buf[p + 4] >> 1);
        return v / 90000.0;
    }

    /// <summary>Scans a stream for its first and last timestamps, giving its length in seconds.</summary>
    public static double Duration(Stream s)
    {
        double first = double.NaN, last = double.NaN;
        var r = new ProgramStreamReader(s);
        int count = 0;
        while (count < 200 && r.Next(out var pk))
        {
            count++;
            if (!double.IsNaN(pk.Pts))
            {
                first = double.IsNaN(first) ? pk.Pts : Math.Min(first, pk.Pts);
            }
        }
        long tail = Math.Max(0, s.Length - 512 * 1024);
        s.Position = tail;
        var t = new ProgramStreamReader(s);
        while (t.Next(out var pk))
        {
            if (!double.IsNaN(pk.Pts))
                last = double.IsNaN(last) ? pk.Pts : Math.Max(last, pk.Pts);
        }
        if (double.IsNaN(first) || double.IsNaN(last))
            return 0;
        return Math.Max(0, last - first);
    }
}
