using GameWave.Engine;
namespace GameWave.Media;

public sealed partial class Mpeg2VideoDecoder
{
    internal void WriteState(BinaryWriter w)
    {
        w.Write(_buf.Length);
        StateIO.Bytes(w, _buf.AsSpan(0, _len).ToArray());
        w.Write(_len);
        w.Write(_scan);
        w.Write(_pictureStart);
        w.Write(_width);
        w.Write(_height);
        w.Write(_mbWidth);
        w.Write(_mbHeightFrame);
        w.Write(_codedWidth);
        w.Write(_codedHeight);
        w.Write(_pictureType);
        w.Write(_intraDcPrecision);
        w.Write(_pictureStructure);
        w.Write(_quantScale);
        w.Write(_motionType);
        w.Write(_prevMbType);
        w.Write(_prevMotionType);
        w.Write(_progressiveSequence);
        w.Write(_haveSequence);
        w.Write(_topFieldFirst);
        w.Write(_framePredFrameDct);
        w.Write(_concealmentMotionVectors);
        w.Write(_qScaleType);
        w.Write(_intraVlcFormat);
        w.Write(_alternateScan);
        w.Write(_repeatFirstField);
        w.Write(_progressiveFrame);
        w.Write(_secondField);
        w.Write(_currentIsReference);
        w.Write(_pendingFuture);
        w.Write(_frameRate);
        w.Write(_picturePts);
        w.Write(_lastOutputPts);
        w.Write(_lastOutputDuration);
        foreach (int value in _intraMatrix) w.Write(value);
        foreach (int value in _nonIntraMatrix) w.Write(value);
        foreach (int value in _fCode) w.Write(value);
        foreach (int value in _dcPred) w.Write(value);
        foreach (int value in _pmv) w.Write(value);
        foreach (int value in _mv) w.Write(value);
        foreach (int value in _fieldSelect) w.Write(value);
        foreach (int value in _dmVector) w.Write(value);
        foreach (int value in _prevMv) w.Write(value);
        foreach (int value in _prevFieldSelect) w.Write(value);
        foreach (int value in _block) w.Write(value);
        foreach (byte value in _predTemp) w.Write(value);
        StateIO.Array(w, _covered, w.Write);
        StateIO.Array(w, _ptsMarks, p => { w.Write(p.Offset); w.Write(p.Pts); });
        var pictures = new Dictionary<Picture, int>(ReferenceEqualityComparer.Instance);
        void PictureRef(Picture? picture)
        {
            if (picture is null) { w.Write(0); return; }
            if (pictures.TryGetValue(picture, out int id)) { w.Write(id); return; }
            id = pictures.Count + 1; pictures.Add(picture, id); w.Write(-id); picture.WriteState(w);
        }
        PictureRef(_past); PictureRef(_future); PictureRef(_current); PictureRef(_bFrame);
        StateIO.Array(w, _output, PictureRef);
    }
    internal static Mpeg2VideoDecoder ReadState(BinaryReader r)
    {
        var decoder = new Mpeg2VideoDecoder();
        int capacity = StateIO.Count(r, 16 << 20);
        var buffer = StateIO.Bytes(r, capacity);
        decoder._buf = new byte[capacity]; buffer.CopyTo(decoder._buf, 0);
        decoder._len = r.ReadInt32();
        decoder._scan = r.ReadInt32();
        decoder._pictureStart = r.ReadInt32();
        decoder._width = r.ReadInt32();
        decoder._height = r.ReadInt32();
        decoder._mbWidth = r.ReadInt32();
        decoder._mbHeightFrame = r.ReadInt32();
        decoder._codedWidth = r.ReadInt32();
        decoder._codedHeight = r.ReadInt32();
        decoder._pictureType = r.ReadInt32();
        decoder._intraDcPrecision = r.ReadInt32();
        decoder._pictureStructure = r.ReadInt32();
        decoder._quantScale = r.ReadInt32();
        decoder._motionType = r.ReadInt32();
        decoder._prevMbType = r.ReadInt32();
        decoder._prevMotionType = r.ReadInt32();
        decoder._progressiveSequence = r.ReadBoolean();
        decoder._haveSequence = r.ReadBoolean();
        decoder._topFieldFirst = r.ReadBoolean();
        decoder._framePredFrameDct = r.ReadBoolean();
        decoder._concealmentMotionVectors = r.ReadBoolean();
        decoder._qScaleType = r.ReadBoolean();
        decoder._intraVlcFormat = r.ReadBoolean();
        decoder._alternateScan = r.ReadBoolean();
        decoder._repeatFirstField = r.ReadBoolean();
        decoder._progressiveFrame = r.ReadBoolean();
        decoder._secondField = r.ReadBoolean();
        decoder._currentIsReference = r.ReadBoolean();
        decoder._pendingFuture = r.ReadBoolean();
        decoder._frameRate = r.ReadDouble();
        decoder._picturePts = r.ReadDouble();
        decoder._lastOutputPts = r.ReadDouble();
        decoder._lastOutputDuration = r.ReadDouble();
        for (int i = 0; i < decoder._intraMatrix.Length; i++) decoder._intraMatrix[i] = r.ReadInt32();
        for (int i = 0; i < decoder._nonIntraMatrix.Length; i++) decoder._nonIntraMatrix[i] = r.ReadInt32();
        for (int i0 = 0; i0 < 2; i0++) for (int i1 = 0; i1 < 2; i1++) decoder._fCode[i0, i1] = r.ReadInt32();
        for (int i = 0; i < decoder._dcPred.Length; i++) decoder._dcPred[i] = r.ReadInt32();
        for (int i0 = 0; i0 < 2; i0++) for (int i1 = 0; i1 < 2; i1++) for (int i2 = 0; i2 < 2; i2++) decoder._pmv[i0, i1, i2] = r.ReadInt32();
        for (int i0 = 0; i0 < 2; i0++) for (int i1 = 0; i1 < 2; i1++) for (int i2 = 0; i2 < 2; i2++) decoder._mv[i0, i1, i2] = r.ReadInt32();
        for (int i0 = 0; i0 < 2; i0++) for (int i1 = 0; i1 < 2; i1++) decoder._fieldSelect[i0, i1] = r.ReadInt32();
        for (int i = 0; i < decoder._dmVector.Length; i++) decoder._dmVector[i] = r.ReadInt32();
        for (int i0 = 0; i0 < 2; i0++) for (int i1 = 0; i1 < 2; i1++) for (int i2 = 0; i2 < 2; i2++) decoder._prevMv[i0, i1, i2] = r.ReadInt32();
        for (int i0 = 0; i0 < 2; i0++) for (int i1 = 0; i1 < 2; i1++) decoder._prevFieldSelect[i0, i1] = r.ReadInt32();
        for (int i = 0; i < decoder._block.Length; i++) decoder._block[i] = r.ReadInt32();
        for (int i = 0; i < decoder._predTemp.Length; i++) decoder._predTemp[i] = r.ReadByte();
        decoder._covered = StateIO.Array(r, r.ReadBoolean, 1 << 20);
        foreach (var mark in StateIO.Array(r, () => (r.ReadInt32(), r.ReadDouble()))) decoder._ptsMarks.Enqueue(mark);
        var pictures = new Dictionary<int, Picture>();
        Picture? PictureRef()
        {
            int id = r.ReadInt32();
            if (id == 0) return null;
            if (id > 0) return pictures.GetValueOrDefault(id) ?? throw new InvalidDataException("Invalid decoder picture reference.");
            var picture = Picture.ReadState(r); pictures.Add(checked(-id), picture); return picture;
        }
        decoder._past = PictureRef(); decoder._future = PictureRef(); decoder._current = PictureRef(); decoder._bFrame = PictureRef();
        foreach (var picture in StateIO.Array(r, () => PictureRef() ?? throw new InvalidDataException("Missing decoder picture."), 128)) decoder._output.Enqueue(picture);
        if (decoder._len != buffer.Length || decoder._scan < 0 || decoder._scan > decoder._len || decoder._pictureStart < -1 || decoder._pictureStart >= decoder._len)
            throw new InvalidDataException("Invalid video decoder buffer.");
        return decoder;
    }
}

public sealed partial class Mp2Decoder
{
    internal void WriteState(BinaryWriter w)
    {
        StateIO.Bytes(w, _buf.AsSpan(0, _len).ToArray());
        StateIO.Array(w, _ptsMarks, p => { w.Write(p.Offset); w.Write(p.Pts); });
        w.Write(_consumed); w.Write(SampleRate);
        StateIO.Array(w, _history, bytes => StateIO.Bytes(w, bytes));
    }
    internal static Mp2Decoder ReadState(BinaryReader r)
    {
        var decoder = new Mp2Decoder();
        var buffer = StateIO.Bytes(r, 1 << 20);
        var marks = StateIO.Array(r, () => (r.ReadInt32(), r.ReadDouble()));
        long consumed = r.ReadInt64(); int rate = r.ReadInt32();
        var history = StateIO.Array(r, () => StateIO.Bytes(r, 65536), 64);
        // Layer II has no bit reservoir: two complete frames reconstruct its finite
        // synthesis-filter history. Layer III retains a longer bounded warm-up window.
        foreach (var bytes in history) decoder.Feed(bytes, double.NaN, (_, _, _) => { });
        decoder._buf = buffer; decoder._len = buffer.Length; decoder._ptsMarks.Clear();
        foreach (var mark in marks) decoder._ptsMarks.Enqueue(mark);
        decoder._history.Clear(); foreach (var bytes in history) decoder._history.Enqueue(bytes);
        decoder._consumed = consumed; decoder.SampleRate = rate;
        return decoder;
    }
}

public sealed partial class Picture
{
    internal void WriteState(BinaryWriter w)
    {
        w.Write(Width); w.Write(Height); w.Write(Pts); w.Write(Duration);
        w.Write(Progressive); w.Write(TopFieldFirst);
        StateIO.Bytes(w, Y); StateIO.Bytes(w, Cb); StateIO.Bytes(w, Cr);
    }
    internal static Picture ReadState(BinaryReader r)
    {
        int width = StateIO.Count(r, 4096), height = StateIO.Count(r, 4096);
        if (width == 0 || height == 0) throw new InvalidDataException("Invalid picture dimensions.");
        var p = new Picture(width, height) { Pts = r.ReadDouble(), Duration = r.ReadDouble(), Progressive = r.ReadBoolean(), TopFieldFirst = r.ReadBoolean() };
        void Plane(byte[] plane)
        {
            byte[] bytes = StateIO.Bytes(r, plane.Length);
            if (bytes.Length != plane.Length) throw new InvalidDataException("Invalid picture plane.");
            bytes.CopyTo(plane, 0);
        }
        Plane(p.Y); Plane(p.Cb); Plane(p.Cr); return p;
    }
}
