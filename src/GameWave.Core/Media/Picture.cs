namespace GameWave.Media;

/// <summary>A decoded 4:2:0 picture. Plane sizes are rounded up to whole macroblocks.</summary>
public sealed class Picture
{
    public readonly int Width;
    public readonly int Height;
    public readonly int CodedWidth;
    public readonly int CodedHeight;
    public readonly byte[] Y;
    public readonly byte[] Cb;
    public readonly byte[] Cr;

    /// <summary>Presentation time in seconds, or NaN when the stream gave none.</summary>
    public double Pts = double.NaN;
    /// <summary>How long the picture shows, in seconds (longer for repeat_first_field).</summary>
    public double Duration;
    public bool Progressive = true;
    public bool TopFieldFirst = true;

    public Picture(int width, int height)
    {
        Width = width;
        Height = height;
        CodedWidth = (width + 15) & ~15;
        CodedHeight = (height + 31) & ~31;
        Y = new byte[CodedWidth * CodedHeight];
        Cb = new byte[CodedWidth * CodedHeight / 4];
        Cr = new byte[CodedWidth * CodedHeight / 4];
    }

    public Picture Clone()
    {
        var p = new Picture(Width, Height) { Pts = Pts, Duration = Duration, Progressive = Progressive, TopFieldFirst = TopFieldFirst };
        Y.CopyTo(p.Y, 0);
        Cb.CopyTo(p.Cb, 0);
        Cr.CopyTo(p.Cr, 0);
        return p;
    }

    public void CopyFrom(Picture p)
    {
        p.Y.CopyTo(Y, 0);
        p.Cb.CopyTo(Cb, 0);
        p.Cr.CopyTo(Cr, 0);
        Pts = p.Pts;
        Duration = p.Duration;
        Progressive = p.Progressive;
        TopFieldFirst = p.TopFieldFirst;
    }

    public void FillBlack()
    {
        Array.Fill(Y, (byte)16);
        Array.Fill(Cb, (byte)128);
        Array.Fill(Cr, (byte)128);
    }
}
