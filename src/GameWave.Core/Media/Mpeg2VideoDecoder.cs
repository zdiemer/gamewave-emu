using System.Runtime.CompilerServices;
using static GameWave.Media.Mpeg2Tables;

namespace GameWave.Media;

/// <summary>
/// An MPEG-2 video decoder for 4:2:0 main profile streams: I, P and B pictures, frame and
/// field pictures, frame, field, 16x8 and dual-prime motion compensation. Elementary stream
/// bytes go in with <see cref="Feed"/>; pictures come out in display order.
/// </summary>
public sealed class Mpeg2VideoDecoder
{
    // ------------------------------------------------------------------ input buffering

    byte[] _buf = new byte[1 << 20];
    int _len;
    int _scan;
    int _pictureStart = -1;
    readonly Queue<(int Offset, double Pts)> _ptsMarks = new();

    readonly Queue<Picture> _output = new();
    readonly BitReader _r = new();

    // ------------------------------------------------------------------ sequence state

    int _width, _height, _mbWidth, _mbHeightFrame;
    int _codedWidth, _codedHeight;
    double _frameRate = 30000.0 / 1001;
    bool _progressiveSequence;
    bool _haveSequence;
    readonly int[] _intraMatrix = new int[64];
    readonly int[] _nonIntraMatrix = new int[64];

    // ------------------------------------------------------------------ picture state

    int _pictureType;
    readonly int[,] _fCode = new int[2, 2];
    int _intraDcPrecision;
    int _pictureStructure = 3;
    bool _topFieldFirst;
    bool _framePredFrameDct = true;
    bool _concealmentMotionVectors;
    bool _qScaleType;
    bool _intraVlcFormat;
    bool _alternateScan;
    bool _repeatFirstField;
    bool _progressiveFrame = true;
    double _picturePts = double.NaN;

    Picture? _past;      // older reference frame (forward prediction)
    Picture? _future;    // newer reference frame (backward prediction)
    Picture? _current;   // frame being decoded
    Picture? _bFrame;    // scratch frame for B pictures
    bool _secondField;
    bool _currentIsReference;
    bool _pendingFuture;  // _future has not been output yet
    double _lastOutputPts = double.NaN;
    double _lastOutputDuration;

    readonly Stack<Picture> _pool = new();

    // ------------------------------------------------------------------ macroblock state

    int _quantScale;
    readonly int[] _dcPred = new int[3];
    readonly int[,,] _pmv = new int[2, 2, 2];       // [r][s][t]
    readonly int[,,] _mv = new int[2, 2, 2];        // [r][s][t] decoded vectors
    readonly int[,] _fieldSelect = new int[2, 2];   // [r][s]
    readonly int[] _dmVector = new int[2];
    int _motionType;
    int _prevMbType;
    int _prevMotionType;
    readonly int[,,] _prevMv = new int[2, 2, 2];
    readonly int[,] _prevFieldSelect = new int[2, 2];

    bool[] _covered = [];

    readonly int[] _block = new int[64];
    readonly byte[] _predTemp = new byte[16 * 16];


    public int Width => _width;
    public int Height => _height;
    public double FrameRate => _frameRate;

    /// <summary>Drops all buffered data and reference pictures (for a seek or a new stream).</summary>
    public void Reset()
    {
        _len = 0;
        _scan = 0;
        _pictureStart = -1;
        _ptsMarks.Clear();
        _output.Clear();
        _past = _future = _current = null;
        _secondField = false;
        _pendingFuture = false;
        _lastOutputPts = double.NaN;
    }

    public bool TryGetPicture(out Picture p) => _output.TryDequeue(out p!);

    public int PendingPictures => _output.Count;

    /// <summary>Returns a picture from <see cref="TryGetPicture"/> for reuse.</summary>
    public void Recycle(Picture p)
    {
        if (p.Width == _width && p.Height == _height && _pool.Count < 8)
            _pool.Push(p);
    }

    public void Feed(ReadOnlySpan<byte> data, double pts = double.NaN)
    {
        if (_len + data.Length > _buf.Length)
        {
            Compact();
            if (_len + data.Length > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length + 4096));
        }
        if (!double.IsNaN(pts))
            _ptsMarks.Enqueue((_len, pts));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        Process(false);
    }

    /// <summary>Decodes whatever is buffered and releases the last reference picture.</summary>
    public void Flush()
    {
        Process(true);
        if (_pendingFuture && _future is not null)
        {
            Emit(_future);
            _pendingFuture = false;
        }
    }

    void Compact()
    {
        int keep = _pictureStart >= 0 ? _pictureStart : Math.Max(0, _scan - 3);
        if (keep <= 0)
            return;
        Array.Copy(_buf, keep, _buf, 0, _len - keep);
        _len -= keep;
        _scan -= keep;
        if (_pictureStart >= 0)
            _pictureStart -= keep;
        int n = _ptsMarks.Count;
        for (int i = 0; i < n; i++)
        {
            var (o, p) = _ptsMarks.Dequeue();
            if (o - keep >= 0)
                _ptsMarks.Enqueue((o - keep, p));
            else
                _ptsMarks.Enqueue((0, p));
        }
    }

    /// <summary>Walks start codes; a picture is decoded once the next non-slice unit begins.</summary>
    void Process(bool final)
    {
        while (true)
        {
            int sc = FindStartCode(_scan);
            if (sc < 0)
            {
                _scan = Math.Max(_scan, _len - 3);
                if (final && _pictureStart >= 0)
                {
                    DecodePicture(_pictureStart, _len);
                    _pictureStart = -1;
                }
                return;
            }
            int code = _buf[sc + 3];
            _scan = sc + 4;
            bool isSlice = code >= 0x01 && code <= 0xAF;
            if (isSlice || code == 0xB5 || code == 0xB2)
                continue;

            // A picture, GOP, sequence header or end code ends the picture in progress.
            if (_pictureStart >= 0)
            {
                DecodePicture(_pictureStart, sc);
                _pictureStart = -1;
            }
            if (code == 0x00)
            {
                _pictureStart = sc;
                _picturePts = TakePts(sc);
            }
            else if (code == 0xB3)
            {
                int end = FindStartCode(sc + 4);
                // Wait for the sequence header's extension, which may not have arrived yet.
                if (end < 0 && !final)
                {
                    _scan = sc;
                    return;
                }
                ParseSequenceHeader(sc + 4, end < 0 ? _len : end);
                // Its extension follows as a separate start code.
                int ext = end;
                while (ext >= 0 && _buf[ext + 3] == 0xB5)
                {
                    int next = FindStartCode(ext + 4);
                    ParseExtension(ext + 4, next < 0 ? _len : next);
                    ext = next;
                }
            }
            else if (code == 0xB7)
            {
                // Sequence end: the last reference frame can be shown now.
                if (_pendingFuture && _future is not null)
                {
                    Emit(_future);
                    _pendingFuture = false;
                }
            }
        }
    }

    double TakePts(int offset)
    {
        double pts = double.NaN;
        while (_ptsMarks.Count > 0 && _ptsMarks.Peek().Offset <= offset)
            pts = _ptsMarks.Dequeue().Pts;
        return pts;
    }

    int FindStartCode(int from)
    {
        var span = _buf.AsSpan(0, _len);
        int i = Math.Max(from, 0);
        while (i + 3 < _len)
        {
            int z = span[i..].IndexOf((byte)1);
            if (z < 0)
                return -1;
            int p = i + z;
            if (p >= 2 && span[p - 1] == 0 && span[p - 2] == 0 && p - 2 >= from && p + 1 < _len)
                return p - 2;
            i = p + 1;
        }
        return -1;
    }

    // ------------------------------------------------------------------ headers

    void ParseSequenceHeader(int start, int end)
    {
        _r.Reset(_buf, start, end);
        int w = (int)_r.Read(12);
        int h = (int)_r.Read(12);
        _r.Skip(4); // aspect ratio
        int rate = (int)_r.Read(4);
        _r.Skip(18 + 1 + 10 + 1); // bit rate, marker, vbv buffer size, constrained flag
        if (_r.ReadBit())
            for (int i = 0; i < 64; i++)
                _intraMatrix[ZigZag[i]] = (int)_r.Read(8);
        else
            for (int i = 0; i < 64; i++)
                _intraMatrix[i] = DefaultIntraMatrix[i];
        if (_r.ReadBit())
            for (int i = 0; i < 64; i++)
                _nonIntraMatrix[ZigZag[i]] = (int)_r.Read(8);
        else
            Array.Fill(_nonIntraMatrix, 16);

        if (FrameRates[rate] > 0)
            _frameRate = FrameRates[rate];
        if (w != _width || h != _height || !_haveSequence)
        {
            _width = w;
            _height = h;
            _mbWidth = (w + 15) / 16;
            _mbHeightFrame = (h + 15) / 16;
            _codedWidth = _mbWidth * 16;
            _codedHeight = ((h + 31) / 32) * 32;
            _pool.Clear();
            _past = _future = _current = _bFrame = null;
            _pendingFuture = false;
        }
        _haveSequence = true;
    }

    void ParseExtension(int start, int end)
    {
        _r.Reset(_buf, start, end);
        int id = (int)_r.Read(4);
        switch (id)
        {
            case 1: // sequence extension
                _r.Skip(8); // profile and level
                _progressiveSequence = _r.ReadBit();
                _r.Skip(2); // chroma format
                int hExt = (int)_r.Read(2), vExt = (int)_r.Read(2);
                if (hExt != 0 || vExt != 0)
                {
                    _width |= hExt << 12;
                    _height |= vExt << 12;
                }
                if (_progressiveSequence)
                    _mbHeightFrame = (_height + 15) / 16;
                break;
            case 3: // quant matrix extension
                if (_r.ReadBit())
                    for (int i = 0; i < 64; i++)
                        _intraMatrix[ZigZag[i]] = (int)_r.Read(8);
                if (_r.ReadBit())
                    for (int i = 0; i < 64; i++)
                        _nonIntraMatrix[ZigZag[i]] = (int)_r.Read(8);
                break;
            case 8: // picture coding extension
                _fCode[0, 0] = (int)_r.Read(4);
                _fCode[0, 1] = (int)_r.Read(4);
                _fCode[1, 0] = (int)_r.Read(4);
                _fCode[1, 1] = (int)_r.Read(4);
                _intraDcPrecision = (int)_r.Read(2);
                _pictureStructure = (int)_r.Read(2);
                _topFieldFirst = _r.ReadBit();
                _framePredFrameDct = _r.ReadBit();
                _concealmentMotionVectors = _r.ReadBit();
                _qScaleType = _r.ReadBit();
                _intraVlcFormat = _r.ReadBit();
                _alternateScan = _r.ReadBit();
                _repeatFirstField = _r.ReadBit();
                _r.Skip(1); // chroma_420_type
                _progressiveFrame = _r.ReadBit();
                break;
        }
    }

    // ------------------------------------------------------------------ pictures

    Picture NewFrame()
    {
        if (_pool.Count > 0)
            return _pool.Pop();
        return new Picture(_width, _height);
    }

    void DecodePicture(int start, int end)
    {
        if (!_haveSequence)
            return;
        _r.Reset(_buf, start + 4, end);
        _r.Skip(10); // temporal reference
        _pictureType = (int)_r.Read(3);
        _r.Skip(16); // vbv delay
        if (_pictureType == 2 || _pictureType == 3)
            _r.Skip(4);
        if (_pictureType == 3)
            _r.Skip(4);
        // Defaults in case the picture coding extension is missing (MPEG-1 style).
        _pictureStructure = 3;
        _framePredFrameDct = true;

        // Extensions and slices.
        int pos = start + 4;
        var slices = new List<(int Start, int End, int Row)>();
        while (true)
        {
            int sc = FindStartCodeIn(pos, end);
            if (sc < 0)
                break;
            int code = _buf[sc + 3];
            int next = FindStartCodeIn(sc + 4, end);
            int unitEnd = next < 0 ? end : next;
            if (code == 0xB5)
                ParseExtension(sc + 4, unitEnd);
            else if (code >= 0x01 && code <= 0xAF)
                slices.Add((sc + 4, unitEnd, code - 1));
            pos = sc + 4;
        }

        if (_pictureType < 1 || _pictureType > 3)
            return;

        bool isField = _pictureStructure != 3;
        bool reference = _pictureType != 3;

        if (!isField || !_secondField)
        {
            // Start a new frame.
            if (reference)
            {
                if (_pendingFuture && _future is not null)
                {
                    Emit(_future);
                    _pendingFuture = false;
                }
                if (_past is not null && _past != _future)
                    Recycle(_past);
                _past = _future;
                _future = NewFrame();
                _current = _future;
            }
            else
            {
                if (_past is null || _future is null)
                {
                    // B pictures before the first two references cannot be decoded.
                    if (isField)
                        _secondField = !_secondField;
                    return;
                }
                _bFrame ??= NewFrame();
                _current = _bFrame;
            }
            _currentIsReference = reference;
            _current.Pts = _picturePts;
            _current.TopFieldFirst = _topFieldFirst;
            _current.Progressive = _progressiveFrame;
            double frameDur = 1.0 / _frameRate;
            _current.Duration = _repeatFirstField && !_progressiveSequence ? frameDur * 1.5 : (_repeatFirstField ? frameDur * 2 : frameDur);
            if (_pictureType == 2 && _past is null)
            {
                // A P picture with nothing to predict from: predict from grey.
                _past = NewFrame();
                _past.FillBlack();
            }
        }

        int mbCount = _mbWidth * (_pictureStructure == 3 ? _mbHeightFrame : _codedHeight / 32);
        if (_covered.Length < mbCount)
            _covered = new bool[mbCount];
        Array.Clear(_covered, 0, mbCount);
        foreach (var (s, e, row) in slices)
        {
            try
            {
                DecodeSlice(s, e, row);
            }
            catch (IndexOutOfRangeException)
            {
                // A damaged slice; carry on with the next one.
            }
        }
        ConcealUncovered(mbCount);

        if (isField)
        {
            if (!_secondField)
            {
                _secondField = true;
                return;
            }
            _secondField = false;
        }

        if (_currentIsReference)
        {
            _pendingFuture = true;
        }
        else
        {
            Emit(_current!);
        }
    }

    int FindStartCodeIn(int from, int end)
    {
        for (int i = from; i + 3 < end; i++)
        {
            if (_buf[i] == 0 && _buf[i + 1] == 0 && _buf[i + 2] == 1)
                return i;
        }
        return -1;
    }

    void Emit(Picture src)
    {
        var p = _pool.Count > 0 ? _pool.Pop() : new Picture(_width, _height);
        p.CopyFrom(src);
        if (double.IsNaN(p.Pts) && !double.IsNaN(_lastOutputPts))
            p.Pts = _lastOutputPts + _lastOutputDuration;
        _lastOutputPts = p.Pts;
        _lastOutputDuration = p.Duration;
        _output.Enqueue(p);
    }

    // ------------------------------------------------------------------ slices

    void DecodeSlice(int start, int end, int row)
    {
        _r.Reset(_buf, start, end);
        int qcode = (int)_r.Read(5);
        _quantScale = _qScaleType ? NonLinearQuantScale[qcode] : qcode * 2;
        if (_r.ReadBit())
        {
            _r.Skip(1 + 7);
            while (_r.ReadBit())
                _r.Skip(8);
        }

        ResetDcPredictors();
        Array.Clear(_pmv);
        _prevMbType = 0;

        bool isField = _pictureStructure != 3;
        int mbHeight = isField ? (_mbHeightFrame + 1) / 2 : _mbHeightFrame;
        if (isField)
            mbHeight = (_codedHeight / 32);
        int address = row * _mbWidth - 1;
        bool first = true;
        int total = _mbWidth * mbHeight;

        while (true)
        {
            int inc = 0;
            while (true)
            {
                int v = AddressIncrement.Read(_r);
                if (v == int.MinValue)
                return;
                if (v == Escape33)
                {
                    inc += 33;
                    continue;
                }
                if (v == 0)
                    continue; // stuffing
                inc += v;
                break;
            }
            if (first)
            {
                address += inc;
                first = false;
            }
            else
            {
                for (int k = 1; k < inc; k++)
                {
                    address++;
                    if (address >= total)
                        return;
                    _covered[address] = true;
                    SkippedMacroblock(address);
                }
                address++;
            }
            if (address >= total)
                return;
            _covered[address] = true;
            if (!DecodeMacroblock(address))
                return;
            if (_r.BitsLeft <= 0 || _r.NextBitsAreZero23)
                return;
        }
    }

    /// <summary>
    /// Macroblocks no slice covered (a damaged or truncated picture) are concealed with a
    /// zero-motion copy from the previous reference.
    /// </summary>
    void ConcealUncovered(int mbCount)
    {
        if (_pictureType == 1)
            return;
        for (int a = 0; a < mbCount; a++)
        {
            if (_covered[a])
                continue;
            Array.Clear(_mv);
            int parity = _pictureStructure == 2 ? 1 : 0;
            _fieldSelect[0, 0] = _fieldSelect[0, 1] = parity;
            _motionType = _pictureStructure == 3 ? MotionFrame : MotionField;
            int row = a / _mbWidth;
            Predict(a % _mbWidth, a / _mbWidth, MbForward);
        }
    }

    void ResetDcPredictors()
    {
        int v = 1 << (7 + _intraDcPrecision);
        _dcPred[0] = _dcPred[1] = _dcPred[2] = v;
    }

    // ------------------------------------------------------------------ macroblocks

    const int MotionField = 1;
    const int MotionFrame = 2;
    const int Motion16x8 = 2; // in field pictures
    const int MotionDualPrime = 3;

    bool DecodeMacroblock(int address)
    {
        int mbType = _pictureType switch
        {
            1 => MbTypeI.Read(_r),
            2 => MbTypeP.Read(_r),
            _ => MbTypeB.Read(_r),
        };
        if (mbType == int.MinValue)
            return false;

        bool frame = _pictureStructure == 3;
        bool intra = (mbType & MbIntra) != 0;
        int motionType;
        if ((mbType & (MbForward | MbBackward)) != 0)
        {
            if (frame)
                motionType = _framePredFrameDct ? MotionFrame : (int)_r.Read(2);
            else
                motionType = (int)_r.Read(2);
        }
        else
        {
            motionType = frame ? MotionFrame : MotionField;
        }
        _motionType = motionType;

        bool dctField = false;
        if (frame && !_framePredFrameDct && (intra || (mbType & MbPattern) != 0))
            dctField = _r.ReadBit();

        if ((mbType & MbQuant) != 0)
        {
            int q = (int)_r.Read(5);
            _quantScale = _qScaleType ? NonLinearQuantScale[q] : q * 2;
        }

        if ((mbType & MbForward) != 0 || (intra && _concealmentMotionVectors))
            ReadMotionVectors(0, intra ? (frame ? MotionFrame : MotionField) : motionType);
        if ((mbType & MbBackward) != 0)
            ReadMotionVectors(1, motionType);
        if (intra && _concealmentMotionVectors)
            _r.Skip(1);

        int cbp;
        if ((mbType & MbPattern) != 0)
        {
            cbp = CodedBlockPattern.Read(_r);
            if (cbp == int.MinValue)
                return false;
        }
        else
        {
            cbp = intra ? 63 : 0;
        }

        int mbx = address % _mbWidth;
        int mby = address / _mbWidth;

        if (intra)
        {
            if (!_concealmentMotionVectors)
                Array.Clear(_pmv);
            for (int b = 0; b < 6; b++)
            {
                if (!DecodeIntraBlock(b))
                    return false;
                PutBlock(mbx, mby, b, dctField, true);
            }
        }
        else
        {
            ResetDcPredictors();
            if (_pictureType == 2 && (mbType & MbForward) == 0)
            {
                // "No MC" in a P picture: zero vector, from the same-parity field.
                Array.Clear(_pmv);
                Array.Clear(_mv);
                _fieldSelect[0, 0] = _pictureStructure == 2 ? 1 : 0;
                _motionType = frame ? MotionFrame : MotionField;
                mbType |= MbForward;
            }
            Predict(mbx, mby, mbType);
            for (int b = 0; b < 6; b++)
            {
                if ((cbp & (32 >> b)) == 0)
                    continue;
                if (!DecodeNonIntraBlock(b))
                    return false;
                PutBlock(mbx, mby, b, dctField, false);
            }
        }

        _prevMbType = mbType;
        _prevMotionType = _motionType;
        Array.Copy(_mv, _prevMv, _mv.Length);
        Array.Copy(_fieldSelect, _prevFieldSelect, _fieldSelect.Length);
        return true;
    }

    void SkippedMacroblock(int address)
    {
        int mbx = address % _mbWidth;
        int mby = address / _mbWidth;
        ResetDcPredictors();
        bool frame = _pictureStructure == 3;
        if (_pictureType == 2)
        {
            Array.Clear(_pmv);
            Array.Clear(_mv);
            _fieldSelect[0, 0] = _pictureStructure == 2 ? 1 : 0;
            _motionType = frame ? MotionFrame : MotionField;
            Predict(mbx, mby, MbForward);
        }
        else
        {
            // B: repeat the previous macroblock's prediction.
            Array.Copy(_prevMv, _mv, _mv.Length);
            Array.Copy(_prevFieldSelect, _fieldSelect, _fieldSelect.Length);
            _motionType = _prevMotionType;
            int t = _prevMbType & (MbForward | MbBackward);
            if (t == 0)
                t = MbForward;
            Predict(mbx, mby, t);
        }
    }

    // ------------------------------------------------------------------ motion vectors

    void ReadMotionVectors(int s, int motionType)
    {
        bool frame = _pictureStructure == 3;
        int count;
        bool fieldFormat;
        bool dmv = motionType == MotionDualPrime;
        if (frame)
        {
            count = motionType == MotionField ? 2 : 1;
            fieldFormat = motionType != MotionFrame;
        }
        else
        {
            count = motionType == Motion16x8 ? 2 : 1;
            fieldFormat = true;
        }

        if (count == 1)
        {
            if (fieldFormat && !dmv)
                _fieldSelect[0, s] = (int)_r.Read(1);
            ReadMotionVector(0, s, frame && fieldFormat, dmv);
            if (!(frame && fieldFormat))
            {
                // One vector serves both prediction slots.
                _pmv[1, s, 0] = _pmv[0, s, 0];
                _pmv[1, s, 1] = _pmv[0, s, 1];
            }
            else if (dmv)
            {
                _pmv[1, s, 0] = _pmv[0, s, 0];
                _pmv[1, s, 1] = _pmv[0, s, 1];
            }
        }
        else
        {
            _fieldSelect[0, s] = (int)_r.Read(1);
            ReadMotionVector(0, s, frame, false);
            _fieldSelect[1, s] = (int)_r.Read(1);
            ReadMotionVector(1, s, frame, false);
        }
    }

    void ReadMotionVector(int r, int s, bool halveVertical, bool dmv)
    {
        for (int t = 0; t < 2; t++)
        {
            int code = MotionCode.Read(_r);
            if (code == int.MinValue)
                code = 0;
            if (code != 0 && _r.ReadBit())
                code = -code;
            int fcode = _fCode[s, t];
            if (fcode == 15 || fcode == 0)
                fcode = 1;
            int rSize = fcode - 1;
            int residual = 0;
            if (rSize > 0 && code != 0)
                residual = (int)_r.Read(rSize);
            if (dmv)
                _dmVector[t] = DmVector.Read(_r) is var dv && dv != int.MinValue ? dv : 0;

            int f = 1 << rSize;
            int delta;
            if (f == 1 || code == 0)
                delta = code;
            else
            {
                delta = (Math.Abs(code) - 1) * f + residual + 1;
                if (code < 0)
                    delta = -delta;
            }
            int high = 16 * f - 1, low = -16 * f, range = 32 * f;
            int pred = _pmv[r, s, t];
            if (t == 1 && halveVertical)
                pred >>= 1;
            int v = pred + delta;
            if (v < low) v += range;
            if (v > high) v -= range;
            _mv[r, s, t] = v;
            _pmv[r, s, t] = t == 1 && halveVertical ? v * 2 : v;
        }
    }

    // ------------------------------------------------------------------ prediction

    void Predict(int mbx, int mby, int mbType)
    {
        bool fwd = (mbType & MbForward) != 0;
        bool bwd = (mbType & MbBackward) != 0;
        var cur = _current!;
        if (fwd)
            PredictDirection(cur, mbx, mby, 0, false);
        if (bwd)
            PredictDirection(cur, mbx, mby, 1, fwd);
    }

    /// <summary>The frame that holds the reference field <paramref name="field"/> for direction s.</summary>
    Picture? ReferenceFor(int s, int field)
    {
        if (_pictureStructure != 3 && _pictureType == 2 && s == 0 && _secondField)
        {
            // The second field of a P frame may predict from the first field of its own frame.
            int currentParity = _pictureStructure == 2 ? 1 : 0;
            if (field != currentParity)
                return _current;
        }
        return s == 0 ? _past : _future;
    }

    void PredictDirection(Picture cur, int mbx, int mby, int s, bool average)
    {
        int w = _codedWidth;
        int cw = w / 2;
        if (_pictureStructure == 3)
        {
            if (_motionType == MotionFrame)
            {
                var refp = s == 0 ? _past : _future;
                if (refp is null)
                    return;
                int mvx = _mv[0, s, 0], mvy = _mv[0, s, 1];
                MotionCompensate(refp, cur, 0, w, mbx * 16, mby * 16, mvx, mvy, 16, 16, 0, w, average);
            }
            else if (_motionType == MotionField)
            {
                for (int r = 0; r < 2; r++)
                {
                    int sel = _fieldSelect[r, s];
                    var refp = s == 0 ? _past : _future;
                    if (refp is null)
                        return;
                    // Destination: field r of this macroblock; source: field sel of the reference.
                    MotionCompensate(refp, cur, sel, 2 * w, mbx * 16, mby * 8, _mv[r, s, 0], _mv[r, s, 1], 16, 8, r, 2 * w, average);
                }
            }
            else
            {
                DualPrimeFrame(cur, mbx, mby, s, average);
            }
        }
        else
        {
            int parity = _pictureStructure == 2 ? 1 : 0;
            if (_motionType == MotionField)
            {
                int sel = _fieldSelect[0, s];
                var refp = ReferenceFor(s, sel);
                if (refp is null)
                    return;
                MotionCompensate(refp, cur, sel, 2 * w, mbx * 16, mby * 16, _mv[0, s, 0], _mv[0, s, 1], 16, 16, parity, 2 * w, average);
            }
            else if (_motionType == Motion16x8)
            {
                for (int r = 0; r < 2; r++)
                {
                    int sel = _fieldSelect[r, s];
                    var refp = ReferenceFor(s, sel);
                    if (refp is null)
                        return;
                    MotionCompensate(refp, cur, sel, 2 * w, mbx * 16, mby * 16 + r * 8, _mv[r, s, 0], _mv[r, s, 1], 16, 8, parity, 2 * w, average, r * 8);
                }
            }
            else
            {
                DualPrimeField(cur, mbx, mby, s, average);
            }
        }
    }

    /// <summary>
    /// Predicts a luma block of bw x bh (and its chroma) into the current picture.
    /// (x, y) is the block origin in source field/frame coordinates, which match the
    /// destination's; srcField/dstField pick the field line offset when stride is doubled.
    /// </summary>
    void MotionCompensate(Picture src, Picture dst, int srcField, int srcStride, int x, int y, int mvx, int mvy,
        int bw, int bh, int dstField, int dstStride, bool average, int dstYOffsetInMb = 0)
    {
        int w = _codedWidth;
        int planeH = srcStride == w ? _codedHeight : _codedHeight / 2;
        // Luma. The destination row y is in the same coordinate space as the source.
        int dstY = y;
        McPlane(src.Y, srcField * w, srcStride, w, planeH, x + (mvx >> 1), y + (mvy >> 1), mvx & 1, mvy & 1,
            dst.Y, dstField * w + dstY * dstStride + x, dstStride, bw, bh, average);

        // Chroma at half resolution, with vectors halved toward zero.
        int cw = w / 2;
        int cmvx = mvx / 2, cmvy = mvy / 2;
        int cx = x / 2, cy = y / 2;
        int cStride = srcStride / 2;
        int cDstStride = dstStride / 2;
        int cPlaneH = planeH / 2;
        McPlane(src.Cb, srcField * cw, cStride, cw, cPlaneH, cx + (cmvx >> 1), cy + (cmvy >> 1), cmvx & 1, cmvy & 1,
            dst.Cb, dstField * cw + cy * cDstStride + cx, cDstStride, bw / 2, bh / 2, average);
        McPlane(src.Cr, srcField * cw, cStride, cw, cPlaneH, cx + (cmvx >> 1), cy + (cmvy >> 1), cmvx & 1, cmvy & 1,
            dst.Cr, dstField * cw + cy * cDstStride + cx, cDstStride, bw / 2, bh / 2, average);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void McPlane(byte[] src, int srcBase, int srcStride, int planeW, int planeH, int sx, int sy, int hx, int hy,
        byte[] dst, int dstOffset, int dstStride, int bw, int bh, bool average)
    {
        bool inside = sx >= 0 && sy >= 0 && sx + bw + hx <= planeW && sy + bh + hy <= planeH;
        for (int j = 0; j < bh; j++)
        {
            int d = dstOffset + j * dstStride;
            if (inside)
            {
                int s0 = srcBase + (sy + j) * srcStride + sx;
                int s1 = s0 + srcStride;
                if (hx == 0 && hy == 0)
                {
                    if (average)
                        for (int i = 0; i < bw; i++) dst[d + i] = (byte)((dst[d + i] + src[s0 + i] + 1) >> 1);
                    else
                        Buffer.BlockCopy(src, s0, dst, d, bw);
                }
                else if (hy == 0)
                {
                    for (int i = 0; i < bw; i++)
                    {
                        int p = (src[s0 + i] + src[s0 + i + 1] + 1) >> 1;
                        dst[d + i] = average ? (byte)((dst[d + i] + p + 1) >> 1) : (byte)p;
                    }
                }
                else if (hx == 0)
                {
                    for (int i = 0; i < bw; i++)
                    {
                        int p = (src[s0 + i] + src[s1 + i] + 1) >> 1;
                        dst[d + i] = average ? (byte)((dst[d + i] + p + 1) >> 1) : (byte)p;
                    }
                }
                else
                {
                    for (int i = 0; i < bw; i++)
                    {
                        int p = (src[s0 + i] + src[s0 + i + 1] + src[s1 + i] + src[s1 + i + 1] + 2) >> 2;
                        dst[d + i] = average ? (byte)((dst[d + i] + p + 1) >> 1) : (byte)p;
                    }
                }
            }
            else
            {
                for (int i = 0; i < bw; i++)
                {
                    int a = Fetch(src, srcBase, srcStride, planeW, planeH, sx + i, sy + j);
                    int p;
                    if (hx == 0 && hy == 0)
                        p = a;
                    else if (hy == 0)
                        p = (a + Fetch(src, srcBase, srcStride, planeW, planeH, sx + i + 1, sy + j) + 1) >> 1;
                    else if (hx == 0)
                        p = (a + Fetch(src, srcBase, srcStride, planeW, planeH, sx + i, sy + j + 1) + 1) >> 1;
                    else
                        p = (a + Fetch(src, srcBase, srcStride, planeW, planeH, sx + i + 1, sy + j)
                               + Fetch(src, srcBase, srcStride, planeW, planeH, sx + i, sy + j + 1)
                               + Fetch(src, srcBase, srcStride, planeW, planeH, sx + i + 1, sy + j + 1) + 2) >> 2;
                    dst[d + i] = average ? (byte)((dst[d + i] + p + 1) >> 1) : (byte)p;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Fetch(byte[] src, int srcBase, int stride, int w, int h, int x, int y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        return src[srcBase + y * stride + x];
    }

    // Dual prime (7.6.3.6). Rare in practice; implemented for completeness.
    void DualPrimeFrame(Picture cur, int mbx, int mby, int s, bool average)
    {
        var refp = _past;
        if (refp is null)
            return;
        int w = _codedWidth;
        int mvx = _mv[0, s, 0], mvy = _mv[0, s, 1];
        for (int field = 0; field < 2; field++)
        {
            // Same parity prediction.
            MotionCompensate(refp, cur, field, 2 * w, mbx * 16, mby * 8, mvx, mvy, 16, 8, field, 2 * w, average);
            // Opposite parity prediction, averaged in.
            int m = field == 0 ? 1 : 3;
            int e = field == 0 ? -1 : 1;
            int dx = ((mvx * m + (mvx > 0 ? 1 : 0)) >> 1) + _dmVector[0];
            int dy = ((mvy * m + (mvy > 0 ? 1 : 0)) >> 1) + e + _dmVector[1];
            MotionCompensate(refp, cur, 1 - field, 2 * w, mbx * 16, mby * 8, dx, dy, 16, 8, field, 2 * w, true);
        }
    }

    void DualPrimeField(Picture cur, int mbx, int mby, int s, bool average)
    {
        int w = _codedWidth;
        int parity = _pictureStructure == 2 ? 1 : 0;
        int mvx = _mv[0, s, 0], mvy = _mv[0, s, 1];
        var same = ReferenceFor(0, parity);
        var opp = ReferenceFor(0, 1 - parity);
        if (same is null || opp is null)
            return;
        MotionCompensate(same, cur, parity, 2 * w, mbx * 16, mby * 16, mvx, mvy, 16, 16, parity, 2 * w, average);
        int e = parity == 0 ? -1 : 1;
        int dx = ((mvx + (mvx > 0 ? 1 : 0)) >> 1) + _dmVector[0];
        int dy = ((mvy + (mvy > 0 ? 1 : 0)) >> 1) + e + _dmVector[1];
        MotionCompensate(opp, cur, 1 - parity, 2 * w, mbx * 16, mby * 16, dx, dy, 16, 16, parity, 2 * w, true);
    }

    // ------------------------------------------------------------------ blocks

    bool DecodeIntraBlock(int b)
    {
        Array.Clear(_block);
        int cc = b < 4 ? 0 : b - 3;
        int size = (cc == 0 ? DcSizeLuma : DcSizeChroma).Read(_r);
        if (size == int.MinValue)
            return false;
        int diff = 0;
        if (size > 0)
        {
            int bits = (int)_r.Read(size);
            diff = bits < (1 << (size - 1)) ? bits - (1 << size) + 1 : bits;
        }
        _dcPred[cc] += diff;
        _block[0] = _dcPred[cc] << (3 - _intraDcPrecision);
        int sum = _block[0];

        var table = _intraVlcFormat ? DctOne : DctZero;
        var scan = _alternateScan ? AlternateScan : ZigZag;
        int n = 1;
        while (true)
        {
            int v = table.Read(_r);
            int run, level;
            if (v == Eob)
                break;
            if (v == int.MinValue)
                return false;
            if (v == Escape)
            {
                run = (int)_r.Read(6);
                level = (int)_r.Read(12);
                if (level >= 2048)
                    level -= 4096;
            }
            else
            {
                run = v >> 8;
                level = v & 0xFF;
                if (_r.ReadBit())
                    level = -level;
            }
            n += run;
            if (n > 63)
                return false;
            int idx = scan[n];
            int val = level * _intraMatrix[idx] * _quantScale / 16;
            val = Math.Clamp(val, -2048, 2047);
            _block[idx] = val;
            sum += val;
            n++;
        }
        MismatchControl(sum);
        return true;
    }

    bool DecodeNonIntraBlock(int b)
    {
        Array.Clear(_block);
        var scan = _alternateScan ? AlternateScan : ZigZag;
        int n = 0;
        int sum = 0;
        bool first = true;
        while (true)
        {
            int run, level;
            if (first && _r.Peek(1) == 1)
            {
                _r.Skip(1);
                run = 0;
                level = _r.ReadBit() ? -1 : 1;
            }
            else
            {
                int v = DctZero.Read(_r);
                if (v == Eob)
                    break;
                if (v == int.MinValue)
                    return false;
                if (v == Escape)
                {
                    run = (int)_r.Read(6);
                    level = (int)_r.Read(12);
                    if (level >= 2048)
                        level -= 4096;
                }
                else
                {
                    run = v >> 8;
                    level = v & 0xFF;
                    if (_r.ReadBit())
                        level = -level;
                }
            }
            first = false;
            n += run;
            if (n > 63)
                return false;
            int idx = scan[n];
            int val = (2 * level + Math.Sign(level)) * _nonIntraMatrix[idx] * _quantScale / 32;
            val = Math.Clamp(val, -2048, 2047);
            _block[idx] = val;
            sum += val;
            n++;
        }
        MismatchControl(sum);
        return true;
    }

    void MismatchControl(int sum)
    {
        if ((sum & 1) == 0)
            _block[63] ^= 1;
    }

    void PutBlock(int mbx, int mby, int b, bool dctField, bool intra)
    {
        var cur = _current!;
        int w = _codedWidth;
        byte[] plane;
        int offset, stride;
        bool fieldPic = _pictureStructure != 3;
        int parity = _pictureStructure == 2 ? 1 : 0;
        if (b < 4)
        {
            plane = cur.Y;
            int bx = mbx * 16 + (b & 1) * 8;
            if (fieldPic)
            {
                stride = 2 * w;
                offset = parity * w + (mby * 16 + (b >> 1) * 8) * stride + bx;
            }
            else if (dctField)
            {
                stride = 2 * w;
                offset = (mby * 16 + (b >> 1)) * w + bx;
            }
            else
            {
                stride = w;
                offset = (mby * 16 + (b >> 1) * 8) * w + bx;
            }
        }
        else
        {
            plane = b == 4 ? cur.Cb : cur.Cr;
            int cw = w / 2;
            if (fieldPic)
            {
                stride = 2 * cw;
                offset = parity * cw + mby * 8 * stride + mbx * 8;
            }
            else
            {
                stride = cw;
                offset = mby * 8 * cw + mbx * 8;
            }
        }
        Idct.Transform(_block);
        if (offset < 0 || offset + 7 * stride + 8 > plane.Length)
            return;
        if (intra)
        {
            for (int y = 0; y < 8; y++)
            {
                int o = offset + y * stride;
                for (int x = 0; x < 8; x++)
                    plane[o + x] = Clip(_block[y * 8 + x]);
            }
        }
        else
        {
            for (int y = 0; y < 8; y++)
            {
                int o = offset + y * stride;
                for (int x = 0; x < 8; x++)
                    plane[o + x] = Clip(plane[o + x] + _block[y * 8 + x]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte Clip(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
}
