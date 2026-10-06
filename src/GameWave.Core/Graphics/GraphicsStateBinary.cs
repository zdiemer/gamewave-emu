using GameWave.Engine;

namespace GameWave.Graphics;

public sealed partial class Font
{
    internal void WriteState(BinaryWriter w)
    {
        StateIO.Text(w, Family); StateIO.Text(w, SheetName); w.Write(FirstChar); w.Write(LastChar); w.Write(LineHeight);
        StateIO.Array(w, Glyphs, g =>
        {
            w.Write(g.Left); w.Write(g.Top); w.Write(g.Right); w.Write(g.Bottom);
            w.Write(g.Advance); w.Write(g.Bearing); w.Write(g.Width); w.Write(g.RightBearing);
        });
        StateIO.Array(w, _kerning, p => { w.Write(p.Key.Item1); w.Write(p.Key.Item2); w.Write(p.Value); });
    }
    internal static Font ReadState(BinaryReader r)
    {
        var font = new Font { Family = StateIO.Text(r), SheetName = StateIO.Text(r), FirstChar = r.ReadInt32(), LastChar = r.ReadInt32(), LineHeight = r.ReadInt32() };
        font.Glyphs = StateIO.Array(r, () => new Glyph(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()), 65536);
        int count = StateIO.Count(r); for (int i = 0; i < count; i++) font._kerning.Add((r.ReadInt32(), r.ReadInt32()), r.ReadInt32());
        return font;
    }
}

public sealed partial class TextureAnimation
{
    internal void WriteState(BinaryWriter w)
    {
        StateIO.Array(w, _steps, s => { w.Write(s.Op); w.Write(s.Arg); w.Write(s.Time); });
        w.Write(_start); w.Write(_index); w.Write(_stepStart); w.Write(_started); w.Write(Finished);
    }
    internal static TextureAnimation ReadState(BinaryReader r)
    {
        var steps = StateIO.Array(r, () => new Step(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        var animation = new TextureAnimation(steps, r.ReadInt64())
        { _index = r.ReadInt32(), _stepStart = r.ReadInt64(), _started = r.ReadBoolean(), Finished = r.ReadBoolean() };
        if (animation._started && !animation.Finished && (animation._index < 0 || animation._index >= steps.Length))
            throw new InvalidDataException("Invalid texture animation position.");
        return animation;
    }
}
