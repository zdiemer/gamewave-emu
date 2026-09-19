using System.Text.Json.Serialization;
using GameWave.Engine;
using GameWave.Input;
using GameWave.Media;

namespace GameWave.Config;

/// <summary>How the picture is fitted into the window.</summary>
public enum ScaleMode
{
    /// <summary>Fill the window as far as the aspect ratio allows.</summary>
    FitWindow,

    /// <summary>Largest whole-number multiple that fits. No uneven pixels, some letterboxing.</summary>
    IntegerScale,

    /// <summary>Fill the window completely, distorting the picture if need be.</summary>
    Stretch,
}

/// <summary>Texture filtering used when the picture is scaled.</summary>
public enum ScaleFilter
{
    /// <summary>Smoothed, as a television shows it.</summary>
    Linear,

    /// <summary>Hard pixel edges.</summary>
    Nearest,
}

/// <summary>The shape the 720 x 480 picture is shown at.</summary>
public enum AspectRatio
{
    /// <summary>4:3, as the console showed it on a television.</summary>
    Standard,

    /// <summary>16:9, for a television set to stretch the picture.</summary>
    Widescreen,

    /// <summary>One screen pixel per stored pixel, a little too wide.</summary>
    SquarePixels,
}

/// <summary>Where the status overlay sits.</summary>
public enum OverlayCorner
{
    /// <summary>Top left.</summary>
    TopLeft,

    /// <summary>Top right.</summary>
    TopRight,

    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom right.</summary>
    BottomRight,
}

/// <summary>How long the status overlay stays up.</summary>
public enum OverlayMode
{
    /// <summary>Never shown.</summary>
    Hidden,

    /// <summary>Appears briefly when something changes, then goes.</summary>
    Auto,

    /// <summary>Always on screen.</summary>
    Always,
}

/// <summary>Picture settings.</summary>
public sealed class VideoSettings
{
    /// <summary>How the picture is fitted into the window.</summary>
    public ScaleMode ScaleMode { get; set; } = ScaleMode.FitWindow;

    /// <summary>Filtering used when scaling.</summary>
    public ScaleFilter Filter { get; set; } = ScaleFilter.Linear;

    /// <summary>The shape of the picture.</summary>
    public AspectRatio Aspect { get; set; } = AspectRatio.Standard;

    /// <summary>How interlaced movies are shown.</summary>
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Blend;

    /// <summary>Window size at startup, as a multiple of 640 x 480.</summary>
    public int WindowScale { get; set; } = 2;

    /// <summary>Start the window full screen.</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Wait for vertical blank before presenting.</summary>
    public bool VSync { get; set; } = true;

    /// <summary>
    /// Trim the edges a television hid (overscan), in pixels of the 720 x 480 picture on
    /// each side. The games keep their graphics inside the safe area, so little is lost.
    /// </summary>
    public int Overscan { get; set; }

    /// <summary>Colour of the letterbox area around the picture, as #RRGGBB.</summary>
    public string BackgroundColor { get; set; } = "#000000";
}

/// <summary>Sound settings.</summary>
public sealed class AudioSettings
{
    /// <summary>Output volume, 0 to 100.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Silence the output without disturbing the volume setting.</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Size of the sound device's buffer, in milliseconds. Lower is more responsive; too
    /// low and the sound breaks up.
    /// </summary>
    public int BufferMilliseconds { get; set; } = 40;

    /// <summary>Movie soundtrack level, 0 to 100, relative to the volume.</summary>
    public int MovieVolume { get; set; } = 100;

    /// <summary>Sound effect level, 0 to 100, relative to the volume.</summary>
    public int EffectsVolume { get; set; } = 100;

    /// <summary>Keep the sound on when the window loses focus.</summary>
    public bool PlayInBackground { get; set; } = true;
}

/// <summary>How the console behaves.</summary>
public sealed class EmulationSettings
{
    /// <summary>Pause the console while the window is in the background.</summary>
    public bool PauseInBackground { get; set; }

    /// <summary>Pause the console while the menu is open.</summary>
    public bool PauseInMenu { get; set; } = true;

    /// <summary>Folder holding disc images, listed on the Games page.</summary>
    public string GameDirectory { get; set; } = string.Empty;

    /// <summary>Folder for the console's save memory. Empty means beside the settings file.</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>Folder zipped discs are unpacked into. Empty means the local application data folder.</summary>
    public string UnpackDirectory { get; set; } = string.Empty;

    /// <summary>How many unpacked discs to keep, most recently played first. Each is about 4 GB.</summary>
    public int UnpackedDiscsKept { get; set; } = 3;

    /// <summary>How much of a game's own logging to show on the console, 0 (none) to 5 (debug).</summary>
    public int GameLogLevel { get; set; } = 1;
}

/// <summary>Controller settings.</summary>
public sealed class ControllerSettings
{
    /// <summary>
    /// The remote each connected controller plays as, in the order they were connected.
    /// Controllers beyond the list take the remaining colours in order.
    /// </summary>
    public List<Remote> Remotes { get; set; } = [Remote.Red, Remote.Yellow, Remote.Blue, Remote.Green, Remote.Purple, Remote.Orange];

    /// <summary>How far a stick must move to count as a direction, 10 to 90 percent.</summary>
    public int StickThreshold { get; set; } = 50;

    /// <summary>Repeat a held direction, as holding a key on the remote did.</summary>
    public bool RepeatDirections { get; set; } = true;
}

/// <summary>On-screen display settings.</summary>
public sealed class InterfaceSettings
{
    /// <summary>How long the status overlay stays up.</summary>
    public OverlayMode Overlay { get; set; } = OverlayMode.Auto;

    /// <summary>Which corner the status overlay sits in.</summary>
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopLeft;

    /// <summary>Seconds the overlay stays visible in <see cref="OverlayMode.Auto"/>.</summary>
    public int OverlaySeconds { get; set; } = 3;

    /// <summary>Text size for the overlay and menus, 1 to 6. Zero picks a size from the window.</summary>
    public int FontScale { get; set; }

    /// <summary>Show frame rate and timing.</summary>
    public bool ShowPerformance { get; set; }

    /// <summary>Dim the picture behind an open menu.</summary>
    public bool DimBehindMenu { get; set; } = true;

    /// <summary>
    /// Put a native menu bar on the window. Windows only, and ignored elsewhere, where
    /// the in-window menu is the only one. Full screen hides the bar either way.
    /// </summary>
    public bool NativeMenuBar { get; set; } = true;

    /// <summary>Ask before quitting.</summary>
    public bool ConfirmQuit { get; set; }

    /// <summary>Where screenshots are written. Empty means a Screenshots folder beside the settings file.</summary>
    public string ScreenshotDirectory { get; set; } = string.Empty;
}

/// <summary>The complete emulator configuration, as stored on disk.</summary>
public sealed class GameWaveSettings
{
    /// <summary>Schema version, so older files can be migrated.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Picture settings.</summary>
    public VideoSettings Video { get; set; } = new();

    /// <summary>Sound settings.</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary>How the console behaves.</summary>
    public EmulationSettings Emulation { get; set; } = new();

    /// <summary>Controller settings.</summary>
    public ControllerSettings Controllers { get; set; } = new();

    /// <summary>On-screen display settings.</summary>
    public InterfaceSettings Interface { get; set; } = new();

    /// <summary>Control bindings, keyed by action name.</summary>
    public Dictionary<string, List<string>> Bindings { get; set; } = new();

    /// <summary>Discs opened recently, most recent first.</summary>
    public List<string> RecentDiscs { get; set; } = new();

    /// <summary>Largest number of entries kept in <see cref="RecentDiscs"/>.</summary>
    [JsonIgnore]
    public const int MaxRecentDiscs = 12;

    /// <summary>Records that a disc was opened, moving it to the head of the recent list.</summary>
    public void RecordRecentDisc(string path)
    {
        var full = Path.GetFullPath(path);
        RecentDiscs.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentDiscs.Insert(0, full);
        if (RecentDiscs.Count > MaxRecentDiscs) RecentDiscs.RemoveRange(MaxRecentDiscs, RecentDiscs.Count - MaxRecentDiscs);
    }

    /// <summary>Reads the bindings into a usable input map, filling any gaps with defaults.</summary>
    public InputMap BuildInputMap() => InputMap.FromSettings(Bindings);

    /// <summary>Writes an input map back into <see cref="Bindings"/> ready to save.</summary>
    public void StoreInputMap(InputMap map) => Bindings = map.ToSettings();

    /// <summary>The remote the controller at <paramref name="index"/> (0 based) plays as.</summary>
    public Remote RemoteForController(int index)
    {
        if (index < Controllers.Remotes.Count)
            return Controllers.Remotes[index];
        var used = new HashSet<Remote>(Controllers.Remotes);
        var free = Enum.GetValues<Remote>().Where(r => !used.Contains(r)).ToList();
        int extra = index - Controllers.Remotes.Count;
        return extra < free.Count ? free[extra] : (Remote)(index % 6 + 1);
    }
}
