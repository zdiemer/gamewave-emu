using GameWave.Config;
using GameWave.Engine;

namespace GameWave.Ui;

/// <summary>Facts the host feeds the overlay each frame.</summary>
/// <param name="Volume">Output level, 0 to 100.</param>
/// <param name="Muted">Whether sound is silenced.</param>
/// <param name="FramesPerSecond">Presentation rate, for the performance line.</param>
/// <param name="Controllers">Controllers connected, with the remote each plays as.</param>
public readonly record struct HostStatus(int Volume, bool Muted, double FramesPerSecond, string Controllers);

/// <summary>Draws the status panel: the game, the console's state, and sound.</summary>
public sealed class StatusOverlay
{
    private DateTime _visibleUntil = DateTime.MinValue;

    /// <summary>Shows the overlay for the configured time, used when something changes.</summary>
    public void Flash(InterfaceSettings settings)
        => _visibleUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, settings.OverlaySeconds));

    /// <summary>Whether the overlay should be drawn right now.</summary>
    public bool ShouldDraw(InterfaceSettings settings, Machine machine)
    {
        if (settings.Overlay == OverlayMode.Hidden) return false;
        if (settings.Overlay == OverlayMode.Always) return true;

        // States worth seeing, not passing events.
        if (machine.Paused || machine.State != MachineState.Running) return true;

        return DateTime.UtcNow < _visibleUntil;
    }

    /// <summary>Draws the panel in the configured corner.</summary>
    public void Draw(Canvas canvas, int scale, Machine machine, GameWaveSettings settings, HostStatus host)
    {
        var lines = new List<(string Text, Rgba Color)>
        {
            (machine.Title, Rgba.Accent),
        };

        switch (machine.State)
        {
            case MachineState.TrayOpen:
                lines.Add(("Tray open: close it to boot the disc again, or open another", Rgba.Warn));
                break;
            case MachineState.Crashed:
                lines.Add(($"Stopped: {machine.CrashMessage}", Rgba.Warn));
                break;
            case MachineState.Finished:
                lines.Add(("The game program has ended", Rgba.Warn));
                break;
            default:
                if (machine.Paused) lines.Add(("Paused", Rgba.White));
                break;
        }

        lines.Add((host.Muted ? "Sound muted" : $"Volume {host.Volume}%", Rgba.Grey));
        if (host.Controllers.Length > 0) lines.Add((host.Controllers, Rgba.Grey));
        if (settings.Interface.ShowPerformance) lines.Add(($"{host.FramesPerSecond:0} fps", Rgba.Grey));

        var pad = 4 * scale;
        var lineHeight = BitmapFont.LineAdvance * scale;
        var maxWidth = canvas.Width - pad * 4;
        var width = Math.Min(maxWidth, lines.Max(l => BitmapFont.Measure(l.Text, scale))) + pad * 2;
        var height = lines.Count * lineHeight + pad * 2 - (BitmapFont.LineAdvance - BitmapFont.GlyphHeight) * scale;

        var corner = settings.Interface.OverlayCorner;
        var x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? pad : canvas.Width - width - pad;
        var y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? pad : canvas.Height - height - pad;

        canvas.Fill(x, y, width, height, Rgba.Panel);
        var ty = y + pad;
        foreach (var (text, color) in lines)
        {
            canvas.Text(x + pad, ty, BitmapFont.Fit(text, width - pad * 2, scale), scale, color);
            ty += lineHeight;
        }
    }
}
