using GameWave.Graphics;

namespace GameWave.Tests;

public class OsdTests
{
    static Texture Solid(int w, int h, uint argb)
    {
        var t = new Texture(w, h);
        Array.Fill(t.Pixels, argb);
        return t;
    }

    [Fact]
    public void HigherZDrawsOnTop()
    {
        var osd = new Osd();
        int red = osd.CreateOverlay(Solid(4, 4, 0xFFFF0000));
        int blue = osd.CreateOverlay(Solid(4, 4, 0xFF0000FF));
        osd.Modify(red, o => { o.Z = 5; o.Visible = true; });
        osd.Modify(blue, o => { o.Z = 1; o.Visible = true; });
        var frame = new uint[Osd.Width * Osd.Height];
        osd.Composite(frame);
        Assert.Equal(0xFFFF0000u, frame[0]);
    }

    [Fact]
    public void ScenesAppearOnlyWhenEnded()
    {
        var osd = new Osd();
        int id = osd.CreateOverlay(Solid(2, 2, 0xFF00FF00));
        var frame = new uint[Osd.Width * Osd.Height];

        osd.BeginScene();
        osd.Modify(id, o => o.Visible = true);
        osd.Composite(frame);
        Assert.Equal(0u, frame[0]);

        osd.EndScene();
        osd.Composite(frame);
        Assert.Equal(0xFF00FF00u, frame[0]);
    }

    [Fact]
    public void PositionAnimationsInterpolate()
    {
        var o = new Overlay(1);
        o.Animations.Add(new PositionAnimation { X0 = 0, Y0 = 0, X1 = 100, Y1 = 50, Start = 1000, Duration = 100 });
        o.Update(1050);
        Assert.Equal(50, o.X);
        Assert.Equal(25, o.Y);
        o.Update(1200);
        Assert.Equal(100, o.X);
        Assert.Empty(o.Animations);
    }

    [Fact]
    public void TextureAnimationsStepAndLoop()
    {
        var o = new Overlay(1);
        o.Frames.Add(new Texture(1, 1));
        o.Frames.Add(new Texture(1, 1));
        o.TextureAnim = new TextureAnimation(
        [
            new(TextureAnimation.DisplayTexture, 0, 100),
            new(TextureAnimation.DisplayTexture, 1, 100),
            new(TextureAnimation.Jump, 0, 0),
        ], 1000);
        o.Update(1000);
        Assert.Equal(0, o.ActiveFrame);
        o.Update(1150);
        Assert.Equal(1, o.ActiveFrame);
        o.Update(1250);
        Assert.Equal(0, o.ActiveFrame);
        Assert.Equal(1, o.AnimationCount);
    }

    [Fact]
    public void FadeOutLeavesTheOverlayTransparent()
    {
        var o = new Overlay(1) { Visible = true };
        o.Animations.Add(new AlphaAnimation { Kind = 2, Start = 0, Duration = 100 });
        o.Update(50);
        Assert.InRange(o.Opacity, 0.4f, 0.6f);
        o.Update(200);
        Assert.Equal(0f, o.Opacity);
    }
}
