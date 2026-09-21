namespace GameWave.Graphics;

/// <summary>
/// An OSD overlay: a positioned, z-ordered sprite showing one of the textures attached to
/// it, with any running animations.
/// </summary>
public sealed class Overlay
{
    public int Id { get; }
    public List<Texture> Frames { get; } = new();
    public int ActiveFrame { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public bool Visible { get; set; }
    /// <summary>Opacity from an alpha animation, 0..1.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Creation order, the tie breaker between overlays at the same z.</summary>
    public long Sequence { get; }

    public readonly List<OverlayAnimation> Animations = new();
    public TextureAnimation? TextureAnim { get; set; }

    static long _nextSequence;

    public Overlay(int id)
    {
        Id = id;
        Sequence = Interlocked.Increment(ref _nextSequence);
    }

    public Texture? CurrentTexture
        => Frames.Count == 0 ? null : Frames[Math.Clamp(ActiveFrame, 0, Frames.Count - 1)];

    public int Width => Frames.Count > 0 ? Frames[0].Width : 0;
    public int Height => Frames.Count > 0 ? Frames[0].Height : 0;

    public int AnimationCount => Animations.Count + (TextureAnim is { Finished: false } ? 1 : 0);

    internal Overlay Clone(IReadOnlyDictionary<Texture, Texture> textures)
    {
        var copy = new Overlay(Id)
        {
            ActiveFrame = ActiveFrame,
            X = X,
            Y = Y,
            Z = Z,
            Visible = Visible,
            Opacity = Opacity,
            TextureAnim = TextureAnim?.Clone(),
        };
        foreach (var frame in Frames)
            copy.Frames.Add(textures[frame]);
        foreach (var animation in Animations)
            copy.Animations.Add(animation.Clone());
        return copy;
    }

    /// <summary>Advances animations to <paramref name="now"/> (milliseconds).</summary>
    public void Update(long now)
    {
        for (int i = 0; i < Animations.Count; i++)
        {
            if (Animations[i].Apply(this, now))
            {
                Animations.RemoveAt(i);
                i--;
            }
        }
        TextureAnim?.Apply(this, now);
    }
}

public abstract class OverlayAnimation
{
    public long Start;
    public long Duration;

    /// <summary>Applies the animation at time <paramref name="now"/>; returns true once finished.</summary>
    public abstract bool Apply(Overlay o, long now);

    protected float Progress(long now)
    {
        if (Duration <= 0)
            return 1f;
        return Math.Clamp((now - Start) / (float)Duration, 0f, 1f);
    }

    internal OverlayAnimation Clone() => (OverlayAnimation)MemberwiseClone();
}

public sealed class PositionAnimation : OverlayAnimation
{
    public int X0, Y0, X1, Y1;

    public override bool Apply(Overlay o, long now)
    {
        if (now < Start)
            return false;
        float p = Progress(now);
        o.X = X0 + (int)MathF.Round((X1 - X0) * p);
        o.Y = Y0 + (int)MathF.Round((Y1 - Y0) * p);
        return p >= 1f;
    }
}

/// <summary>A move along a parabola: straight from start to end, lifted by a peak height.</summary>
public sealed class ParabolaAnimation : OverlayAnimation
{
    public int X0, Y0, X1, Y1, Height;

    public override bool Apply(Overlay o, long now)
    {
        if (now < Start)
            return false;
        float p = Progress(now);
        o.X = X0 + (int)MathF.Round((X1 - X0) * p);
        o.Y = Y0 + (int)MathF.Round((Y1 - Y0) * p - 4 * Height * p * (1 - p));
        return p >= 1f;
    }
}

public sealed class VisibilityAnimation : OverlayAnimation
{
    public bool Visible;

    public override bool Apply(Overlay o, long now)
    {
        if (now < Start)
            return false;
        o.Visible = Visible;
        return true;
    }
}

public sealed class AlphaAnimation : OverlayAnimation
{
    /// <summary>1 fades in, 2 fades out.</summary>
    public int Kind;

    public override bool Apply(Overlay o, long now)
    {
        if (now < Start)
        {
            if (Kind == 1)
                o.Opacity = 0f;
            return false;
        }
        // Opacity persists after the fade: the games follow a fade out with a very short fade
        // in (10 ms) when they want the overlay back.
        float p = Progress(now);
        o.Opacity = Kind == 2 ? 1f - p : p;
        return p >= 1f;
    }
}

public sealed class BlinkAnimation : OverlayAnimation
{
    public long Period;

    public override bool Apply(Overlay o, long now)
    {
        if (now < Start)
            return false;
        if (Duration > 0 && now >= Start + Duration)
        {
            o.Visible = true;
            return true;
        }
        long half = Math.Max(1, Period / 2);
        o.Visible = ((now - Start) / half) % 2 == 0;
        return false;
    }
}

/// <summary>
/// A frame script for an overlay's attached textures, as built by the games:
/// <c>{1, frame, ms}</c> shows a frame for a time, <c>{3, index}</c> jumps to a step
/// (0 based), <c>{2}</c> ends.
/// </summary>
public sealed class TextureAnimation
{
    public const int DisplayTexture = 1;
    public const int End = 2;
    public const int Jump = 3;

    public readonly record struct Step(int Op, int Arg, int Time);

    readonly Step[] _steps;
    int _index;
    long _stepStart;
    bool _started;
    readonly long _start;

    public bool Finished { get; private set; }

    public TextureAnimation(Step[] steps, long start)
    {
        _steps = steps;
        _start = start;
    }

    internal TextureAnimation Clone() => (TextureAnimation)MemberwiseClone();

    public void Apply(Overlay o, long now)
    {
        if (Finished || now < _start)
            return;
        if (!_started)
        {
            _started = true;
            _stepStart = Math.Max(_start, 0);
            if (_stepStart == 0)
                _stepStart = now;
            _index = 0;
            if (!Enter(o))
                return;
        }
        int guard = 0;
        while (!Finished && guard++ < 1000)
        {
            var step = _steps[_index];
            if (step.Op != DisplayTexture)
                break;
            long end = _stepStart + Math.Max(step.Time, 1);
            if (now < end)
                break;
            _stepStart = end;
            _index++;
            if (!Enter(o))
                break;
        }
    }

    /// <summary>Runs non-timed steps from the current index; false when the script ended.</summary>
    bool Enter(Overlay o)
    {
        int guard = 0;
        while (guard++ < 1000)
        {
            if (_index < 0 || _index >= _steps.Length)
            {
                Finished = true;
                return false;
            }
            var s = _steps[_index];
            switch (s.Op)
            {
                case DisplayTexture:
                    o.ActiveFrame = s.Arg;
                    return true;
                case Jump:
                    _index = s.Arg;
                    continue;
                default:
                    Finished = true;
                    return false;
            }
        }
        Finished = true;
        return false;
    }
}
