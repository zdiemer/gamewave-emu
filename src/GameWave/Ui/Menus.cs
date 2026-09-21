using GameWave.Config;
using GameWave.Engine;
using GameWave.Input;
using GameWave.Media;
using GameWave.Ui.Native;

namespace GameWave.Ui;

/// <summary>What the menus need from the host to do their work.</summary>
public sealed class MenuContext
{
    /// <summary>The settings being edited.</summary>
    public required GameWaveSettings Settings { get; init; }

    /// <summary>The control bindings being edited.</summary>
    public required InputMap Input { get; init; }

    /// <summary>The running console, or null when no disc is in.</summary>
    public required Machine? Machine { get; init; }

    /// <summary>Controllers connected now, in connection order, by name.</summary>
    public required IReadOnlyList<string> Controllers { get; init; }

    /// <summary>Closes the menu and returns to the game.</summary>
    public required Action CloseMenu { get; init; }

    /// <summary>Switches between windowed and full screen.</summary>
    public required Action ToggleFullscreen { get; init; }

    /// <summary>Quits the emulator.</summary>
    public required Action Quit { get; init; }

    /// <summary>Shows a short message.</summary>
    public required Action<string> Toast { get; init; }

    /// <summary>
    /// Runs an action through the host, so a menu row and the key bound to the same thing
    /// take exactly the same path.
    /// </summary>
    public required Action<InputAction> Perform { get; init; }

    /// <summary>
    /// Opens a page in the in-window menu. The native menu bar uses this for the one thing
    /// it cannot do itself, which is wait for a keypress to bind.
    /// </summary>
    public required Action<MenuPage> OpenPage { get; init; }

    /// <summary>Shows a block of text in whatever dialog the host has.</summary>
    public required Action<string, string> ShowInfo { get; init; }

    /// <summary>Opens the screenshot folder in the system file browser.</summary>
    public required Action OpenScreenshots { get; init; }

    /// <summary>Puts the disc at a path in the console.</summary>
    public required Action<string> OpenDisc { get; init; }

    /// <summary>Asks for a folder of disc images for the Games page.</summary>
    public required Action ChooseGameFolder { get; init; }

    /// <summary>Clears the console's save memory.</summary>
    public required Action EraseSaves { get; init; }
}

/// <summary>Builds every menu page.</summary>
/// <remarks>
/// These pages feed both menus: the in-window one drawn on a <see cref="Canvas"/>, and
/// the native menu bar. Each setting is built by one factory used from wherever it needs
/// to appear, so a setting that shows up in two menus cannot drift between them.
/// </remarks>
public static class Menus
{
    private const string OpenHelp = "Choose a disc image (.iso or .zip) or a folder of disc files.";

    /// <summary>The page shown when the menu key is pressed.</summary>
    public static MenuPage Root(MenuContext context)
    {
        var running = context.Machine is not null;
        var items = new List<MenuItem>();

        if (running)
        {
            items.AddRange(
            [
                new MenuAction
                {
                    Label = "Resume",
                    Help = "Return to the game.",
                    OnActivate = context.CloseMenu,
                },
                Command(context, "Quicksave", InputAction.QuickSave, "Remember this point until another disc is opened."),
                Command(context, "Quickload", InputAction.QuickLoad, "Return to the last quicksave for this game."),
                Command(context, "Reset", InputAction.Reset, "Press the console's reset: the game starts again."),
                Command(context, TrayLabel(context), InputAction.ToggleTray, "Open the disc tray, or close it to boot the disc again."),
                new MenuHeading { Label = "" },
            ]);
        }

        items.Add(new MenuSubmenu
        {
            Label = "Games",
            Help = "The discs in your game folder.",
            Open = () => Library(context),
        });
        items.Add(Command(context, running ? "Open another disc..." : "Open a disc...", InputAction.OpenDisc, OpenHelp));
        items.Add(RecentItem(context));

        items.AddRange(
        [
            new MenuHeading { Label = "" },
            new MenuSubmenu { Label = "Picture", Help = "Size, shape and filtering.", Open = () => Video(context) },
            new MenuSubmenu { Label = "Sound", Help = "Volume and output buffering.", Open = () => Audio(context) },
            new MenuSubmenu { Label = "Console", Help = "Pausing, saves and the game folder.", Open = () => Emulation(context) },
            new MenuSubmenu { Label = "Controllers", Help = "Which remote each controller plays as.", Open = () => Controllers(context) },
            new MenuSubmenu { Label = "Controls", Help = "Rebind the keyboard and game controllers.", Open = () => Controls(context) },
            new MenuSubmenu { Label = "On-screen display", Help = "The status overlay and menu appearance.", Open = () => Interface(context) },
            new MenuHeading { Label = "" },
        ]);

        if (running)
            items.Add(Command(context, "Take a screenshot", InputAction.Screenshot, "Write the picture on screen to a PNG file."));

        items.Add(new MenuAction { Label = "Quit", Help = "Close the emulator.", OnActivate = context.Quit });

        return new MenuPage
        {
            Title = "Game Wave",
            Subtitle = context.Machine?.Title ?? "No disc",
            Items = items,
        };
    }

    private static string TrayLabel(MenuContext context)
        => context.Machine?.State == MachineState.TrayOpen ? "Close tray" : "Open tray";

    /// <summary>The discs opened most recently, newest first, each a row that opens it again.</summary>
    public static MenuPage RecentDiscs(MenuContext context)
    {
        var recent = context.Settings.RecentDiscs;
        if (recent.Count == 0)
        {
            return new MenuPage
            {
                Title = "Recent discs",
                Items = [new MenuHeading { Label = "No discs opened yet" }],
            };
        }

        var items = new List<MenuItem>();
        foreach (var path in recent.ToArray())
        {
            items.Add(new MenuAction
            {
                Label = DiscFiles.DisplayName(path),
                Help = path,
                OnActivate = () => context.OpenDisc(path),
            });
        }

        items.Add(new MenuHeading { Label = "" });
        items.Add(new MenuAction
        {
            Label = "Clear list",
            Help = "Forget the discs opened so far.",
            OnActivate = () =>
            {
                context.Settings.RecentDiscs.Clear();
                context.Toast("Recent discs cleared");
            },
        });

        return new MenuPage { Title = "Recent discs", Items = items };
    }

    private static MenuItem RecentItem(MenuContext context, string label = "Recent discs") => new MenuSubmenu
    {
        Label = label,
        Help = "Open one of the discs played before.",
        Open = () => RecentDiscs(context),
    };

    /// <summary>Every disc in the game folder.</summary>
    public static MenuPage Library(MenuContext context)
    {
        var folder = context.Settings.Emulation.GameDirectory;
        var items = new List<MenuItem>();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            items.Add(new MenuHeading { Label = "No game folder chosen yet" });
        }
        else
        {
            var discs = DiscFiles.Find(folder);
            if (discs.Count == 0)
                items.Add(new MenuHeading { Label = "No discs found in the game folder" });

            foreach (var path in discs)
            {
                items.Add(new MenuAction
                {
                    Label = DiscFiles.DisplayName(path),
                    Help = path,
                    OnActivate = () => context.OpenDisc(path),
                });
            }
        }

        items.Add(new MenuHeading { Label = "" });
        items.Add(new MenuAction
        {
            Label = "Choose game folder...",
            Help = string.IsNullOrWhiteSpace(folder) ? "Pick the folder that holds your disc images." : folder,
            OnActivate = context.ChooseGameFolder,
        });

        return new MenuPage { Title = "Games", Subtitle = string.IsNullOrWhiteSpace(folder) ? null : folder, Items = items };
    }

    // ---------------------------------------------------------------- menu bar

    /// <summary>
    /// The top-level menus of the native menu bar, in bar order.
    /// </summary>
    /// <remarks>
    /// Titles carry their own <c>&amp;</c> mnemonic because they are shown verbatim;
    /// everything inside a page is escaped, since disc titles contain ampersands of their own.
    /// </remarks>
    public static IReadOnlyList<MenuBarSection> Bar(MenuContext context) =>
    [
        new("&File", () => File(context)),
        new("&Console", () => ConsoleMenu(context)),
        new("&View", () => View(context)),
        new("&Settings", () => Settings(context)),
        new("&Help", () => Help(context)),
    ];

    /// <summary>The File menu.</summary>
    public static MenuPage File(MenuContext context) => new()
    {
        Title = "File",
        Items =
        [
            Command(context, "Open Disc...", InputAction.OpenDisc, OpenHelp),
            RecentItem(context, "Recent Discs"),
            new MenuSubmenu
            {
                Label = "Games",
                Help = "The discs in your game folder.",
                Open = () => Library(context),
            },
            new MenuHeading { Label = "" },
            Command(context, "Take Screenshot", InputAction.Screenshot, "Write the picture on screen to a PNG file."),
            new MenuAction
            {
                Label = "Open Screenshots Folder",
                Help = "Show the folder screenshots are written to.",
                OnActivate = context.OpenScreenshots,
            },
            new MenuHeading { Label = "" },
            Command(context, "Exit", InputAction.Quit, "Close the emulator."),
        ],
    };

    /// <summary>The Console menu: the console's own buttons.</summary>
    public static MenuPage ConsoleMenu(MenuContext context) => new()
    {
        Title = "Console",
        Items =
        [
            Command(context, "Pause", InputAction.TogglePause, "Stop the console where it is, and continue."),
            Command(context, "Quicksave", InputAction.QuickSave, "Remember this point until another disc is opened."),
            Command(context, "Quickload", InputAction.QuickLoad, "Return to the last quicksave for this game."),
            new MenuHeading { Label = "" },
            Command(context, "Reset", InputAction.Reset, "Press the console's reset: the game starts again."),
            Command(context, "Open / Close Tray", InputAction.ToggleTray, "Open the disc tray, or close it to boot the disc again."),
            new MenuHeading { Label = "" },
            VolumeItem(context),
            MuteItem(context),
            new MenuHeading { Label = "" },
            new MenuAction
            {
                Label = "Game Information...",
                Help = "What is on this disc.",
                OnActivate = () => context.ShowInfo("Game information", GameFacts(context.Machine)),
            },
        ],
    };

    /// <summary>The View menu: how the window and the overlay look.</summary>
    public static MenuPage View(MenuContext context) => new()
    {
        Title = "View",
        Items =
        [
            FullscreenItem(context),
            new MenuHeading { Label = "" },
            WindowScaleItem(context),
            ScalingItem(context),
            AspectItem(context),
            FilteringItem(context),
            DeinterlaceItem(context),
            new MenuHeading { Label = "" },
            OverlayModeItem(context),
            OverlayCornerItem(context),
            PerformanceItem(context),
        ],
    };

    /// <summary>The Settings menu of the bar.</summary>
    public static MenuPage Settings(MenuContext context) => new()
    {
        Title = "Settings",
        Items =
        [
            new MenuSubmenu { Label = "Picture", Help = "Size, shape and filtering.", Open = () => Video(context) },
            new MenuSubmenu { Label = "Sound", Help = "Volume and output buffering.", Open = () => Audio(context) },
            new MenuSubmenu { Label = "Console", Help = "Pausing, saves and the game folder.", Open = () => Emulation(context) },
            new MenuSubmenu { Label = "Controllers", Help = "Which remote each controller plays as.", Open = () => Controllers(context) },
            new MenuSubmenu { Label = "On-screen Display", Help = "The status overlay and menu appearance.", Open = () => Interface(context) },
            new MenuHeading { Label = "" },

            // Binding needs a captured keypress, which a menu bar has nowhere to take,
            // so this hands over to the page that can.
            new MenuAction
            {
                Label = "Controls...",
                Help = "Rebind the keyboard and game controllers.",
                OnActivate = () => context.OpenPage(Controls(context)),
            },
            new MenuToggle
            {
                Label = "Use Native Menu Bar",
                Help = "Turn this off to use the in-window menu only. Takes effect on restart.",
                Get = () => context.Settings.Interface.NativeMenuBar,
                Set = v => context.Settings.Interface.NativeMenuBar = v,
                Default = true,
            },
        ],
    };

    /// <summary>The Help menu of the bar.</summary>
    public static MenuPage Help(MenuContext context) => new()
    {
        Title = "Help",
        Items =
        [
            new MenuAction
            {
                Label = "Controls...",
                Help = "Rebind the keyboard and game controllers.",
                OnActivate = () => context.OpenPage(Controls(context)),
            },
            new MenuAction
            {
                Label = "Keyboard Remote...",
                Help = "Which keys press which buttons on the red remote.",
                OnActivate = () => context.ShowInfo("Keyboard remote", KeyboardSummary(context.Input)),
            },
            new MenuHeading { Label = "" },
            new MenuAction
            {
                Label = "About Game Wave Emulator",
                OnActivate = () => context.ShowInfo("About", About()),
            },
        ],
    };

    /// <summary>Version and credits for the About box.</summary>
    public static string About()
    {
        var version = typeof(Menus).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        return $"""
            Game Wave Emulator {version}

            An emulator for the Game Wave Family Entertainment System,
            the DVD game console from ZAPiT Games.

            The games' own programs run on a Lua 5.0 interpreter, with the
            console's engine rebuilt around them: MPEG-2 movies and stills,
            the on-screen display, sound, remotes and save memory.

            Game Wave is a trademark of its owners; this project is not
            affiliated with or endorsed by them.
            """;
    }

    /// <summary>Facts about the disc in the console.</summary>
    public static string GameFacts(Machine? machine)
    {
        if (machine is null)
            return "No disc is in the console.";
        var info = machine.Info;
        var lines = new List<string>
        {
            $"Title          {machine.Title}",
            $"Disc           {machine.Disc.Label}",
            $"Game program   {info.AppFile}",
        };
        if (!string.IsNullOrEmpty(info.Version))
            lines.Add($"Game version   {info.Version}");
        if (!string.IsNullOrEmpty(info.EngineVersion))
            lines.Add($"Engine         {info.EngineVersion}");
        lines.Add($"State          {machine.State}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A plain list of the keyboard bindings for the red remote.</summary>
    public static string KeyboardSummary(InputMap map)
    {
        var lines = new List<string>();
        foreach (var action in InputActions.KeysOf(Remote.Red))
        {
            var keys = map.BindingsFor(action).Where(b => b.Kind == BindingKind.Key).Select(b => b.ToString()).ToArray();
            lines.Add($"{InputActions.KeyLabel(InputActions.KeyOf(action)),-10} {(keys.Length == 0 ? "unbound" : string.Join(", ", keys))}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ------------------------------------------------------------ setting pages

    /// <summary>Picture settings.</summary>
    public static MenuPage Video(MenuContext context)
    {
        var video = context.Settings.Video;
        return new MenuPage
        {
            Title = "Picture",
            Items =
            [
                FullscreenItem(context),
                WindowScaleItem(context),
                ScalingItem(context),
                AspectItem(context),
                FilteringItem(context),
                DeinterlaceItem(context),
                new MenuNumber
                {
                    Label = "Overscan",
                    Help = "Trim the edges a television hid, in pixels on each side.",
                    Get = () => video.Overscan,
                    Set = v => video.Overscan = v,
                    Minimum = 0, Maximum = 32, Step = 2, Default = 0,
                },
                new MenuToggle
                {
                    Label = "Vertical sync",
                    Help = "Wait for the display before showing each frame. Takes effect on restart.",
                    Get = () => video.VSync,
                    Set = v => video.VSync = v,
                    Default = true,
                },
            ],
        };
    }

    /// <summary>Sound settings.</summary>
    public static MenuPage Audio(MenuContext context)
    {
        var audio = context.Settings.Audio;
        return new MenuPage
        {
            Title = "Sound",
            Items =
            [
                VolumeItem(context),
                MuteItem(context),
                new MenuNumber
                {
                    Label = "Movie volume",
                    Help = "The movies' soundtracks, relative to the volume.",
                    Get = () => audio.MovieVolume,
                    Set = v => audio.MovieVolume = v,
                    Minimum = 0, Maximum = 100, Step = 5, Default = 100,
                    Format = v => $"{v}%",
                },
                new MenuNumber
                {
                    Label = "Effects volume",
                    Help = "The games' sound effects, relative to the volume.",
                    Get = () => audio.EffectsVolume,
                    Set = v => audio.EffectsVolume = v,
                    Minimum = 0, Maximum = 100, Step = 5, Default = 100,
                    Format = v => $"{v}%",
                },
                new MenuNumber
                {
                    Label = "Buffer",
                    Help = "Sound device buffer. Raise it if the sound crackles. Takes effect on restart.",
                    Get = () => audio.BufferMilliseconds,
                    Set = v => audio.BufferMilliseconds = v,
                    Minimum = 10, Maximum = 250, Step = 10, Default = 40,
                    Format = v => $"{v} ms",
                },
                new MenuToggle
                {
                    Label = "Sound in background",
                    Help = "Keep the sound on when the window is not in front.",
                    Get = () => audio.PlayInBackground,
                    Set = v => audio.PlayInBackground = v,
                    Default = true,
                },
            ],
        };
    }

    /// <summary>How the console behaves.</summary>
    public static MenuPage Emulation(MenuContext context)
    {
        var emulation = context.Settings.Emulation;
        return new MenuPage
        {
            Title = "Console",
            Items =
            [
                new MenuToggle
                {
                    Label = "Pause in menu",
                    Help = "Stop the game while this menu is open.",
                    Get = () => emulation.PauseInMenu,
                    Set = v => emulation.PauseInMenu = v,
                    Default = true,
                },
                new MenuToggle
                {
                    Label = "Pause in background",
                    Help = "Stop the game while the window is not in front.",
                    Get = () => emulation.PauseInBackground,
                    Set = v => emulation.PauseInBackground = v,
                },
                new MenuAction
                {
                    Label = "Game folder...",
                    Help = string.IsNullOrWhiteSpace(emulation.GameDirectory) ? "Pick the folder that holds your disc images." : emulation.GameDirectory,
                    OnActivate = context.ChooseGameFolder,
                },
                new MenuNumber
                {
                    Label = "Game log level",
                    Help = "How much of a game's own logging reaches the console window: 0 none, 5 everything.",
                    Get = () => emulation.GameLogLevel,
                    Set = v => emulation.GameLogLevel = v,
                    Minimum = 0, Maximum = 5, Default = 1,
                },
                new MenuHeading { Label = "" },
                new MenuAction
                {
                    Label = "Erase save memory",
                    Help = "Forget every game's saved scores and settings. There is no undo.",
                    OnActivate = context.EraseSaves,
                },
            ],
        };
    }

    /// <summary>Which remote each controller plays as.</summary>
    public static MenuPage Controllers(MenuContext context)
    {
        var settings = context.Settings;
        var remotes = Enum.GetValues<Remote>();
        var items = new List<MenuItem>();

        for (int i = 0; i < 6; i++)
        {
            int slot = i;
            string name = slot < context.Controllers.Count ? context.Controllers[slot] : "not connected";
            items.Add(new MenuChoice
            {
                Label = $"Controller {slot + 1}",
                Help = $"{name}. The remote this controller plays as.",
                Options = remotes.Select(r => r.ToString()).ToArray(),
                Get = () => Array.IndexOf(remotes, settings.RemoteForController(slot)),
                Set = v =>
                {
                    while (settings.Controllers.Remotes.Count <= slot)
                        settings.Controllers.Remotes.Add(settings.RemoteForController(settings.Controllers.Remotes.Count));
                    settings.Controllers.Remotes[slot] = remotes[v];
                },
                Default = slot,
            });
        }

        items.Add(new MenuHeading { Label = "" });
        items.Add(new MenuNumber
        {
            Label = "Stick threshold",
            Help = "How far a stick must move to press a direction.",
            Get = () => settings.Controllers.StickThreshold,
            Set = v => settings.Controllers.StickThreshold = v,
            Minimum = 10, Maximum = 90, Step = 5, Default = 50,
            Format = v => $"{v}%",
        });
        items.Add(new MenuToggle
        {
            Label = "Repeat held directions",
            Help = "Holding a direction repeats it, as holding a button on the remote did.",
            Get = () => settings.Controllers.RepeatDirections,
            Set = v => settings.Controllers.RepeatDirections = v,
            Default = true,
        });

        return new MenuPage { Title = "Controllers", Subtitle = "Each controller is one player's remote", Items = items };
    }

    /// <summary>On-screen display settings.</summary>
    public static MenuPage Interface(MenuContext context)
    {
        var ui = context.Settings.Interface;
        return new MenuPage
        {
            Title = "On-screen display",
            Items =
            [
                OverlayModeItem(context),
                OverlayCornerItem(context),
                new MenuNumber
                {
                    Label = "Overlay time",
                    Help = "How long the overlay stays up in auto mode.",
                    Get = () => ui.OverlaySeconds,
                    Set = v => ui.OverlaySeconds = v,
                    Minimum = 1, Maximum = 10, Default = 3,
                    Format = v => $"{v} s",
                },
                PerformanceItem(context),
                new MenuNumber
                {
                    Label = "Text size",
                    Help = "Size of the menu and overlay text. Auto follows the window.",
                    Get = () => ui.FontScale,
                    Set = v => ui.FontScale = v,
                    Minimum = 0, Maximum = 6, Default = 0,
                    Format = v => v == 0 ? "Auto" : $"{v}x",
                },
                new MenuToggle
                {
                    Label = "Dim behind menu",
                    Get = () => ui.DimBehindMenu,
                    Set = v => ui.DimBehindMenu = v,
                    Default = true,
                },
                new MenuToggle
                {
                    Label = "Ask before quitting",
                    Get = () => ui.ConfirmQuit,
                    Set = v => ui.ConfirmQuit = v,
                },
            ],
        };
    }

    /// <summary>The controls: the emulator's own, then one page per remote.</summary>
    public static MenuPage Controls(MenuContext context)
    {
        var items = new List<MenuItem>();

        foreach (var remote in Enum.GetValues<Remote>())
        {
            var r = remote;
            items.Add(new MenuSubmenu
            {
                Label = $"{r} remote",
                Help = r == Remote.Red ? "The keyboard and the first controller play as red." : $"Keys for the {r.ToString().ToLowerInvariant()} player.",
                Open = () => RemoteControls(context, r),
            });
        }

        items.Add(new MenuHeading { Label = "" });

        foreach (var category in new[] { ActionCategory.Console, ActionCategory.Audio, ActionCategory.Display, ActionCategory.Menu, ActionCategory.General })
        {
            var actions = InputActions.InCategory(category).ToArray();
            if (actions.Length == 0)
                continue;

            items.Add(new MenuHeading { Label = category.ToString() });
            foreach (var action in actions)
                items.Add(BindingItem(context, action));
        }

        items.Add(new MenuHeading { Label = "" });
        items.Add(new MenuAction
        {
            Label = "Restore all defaults",
            Help = "Put every control back the way it shipped.",
            OnActivate = () =>
            {
                context.Input.ResetAll();
                context.Toast("All controls reset");
            },
        });

        return new MenuPage
        {
            Title = "Controls",
            Subtitle = "Keyboard and game controllers",
            Items = items,
        };
    }

    /// <summary>The 21 keys of one remote.</summary>
    public static MenuPage RemoteControls(MenuContext context, Remote remote)
    {
        var items = new List<MenuItem>();
        foreach (var action in InputActions.KeysOf(remote))
            items.Add(BindingItem(context, action, InputActions.KeyLabel(InputActions.KeyOf(action))));
        return new MenuPage
        {
            Title = $"{remote} remote",
            Subtitle = "Controller bindings apply to controllers playing as this remote",
            Items = items,
        };
    }

    private static MenuBinding BindingItem(MenuContext context, InputAction action, string? label = null) => new()
    {
        Label = label ?? InputActions.Label(action),
        Help = "Enter rebinds, Right adds another, Left clears, Delete restores the default.",
        Action = action,
        Map = context.Input,
    };

    // --------------------------------------------------------- setting factories

    private static MenuItem ScalingItem(MenuContext context)
    {
        var video = context.Settings.Video;
        return EnumChoice("Scaling", () => video.ScaleMode, v => video.ScaleMode = v,
            "How the picture is fitted into the window.");
    }

    private static MenuItem AspectItem(MenuContext context)
    {
        var video = context.Settings.Video;
        return EnumChoice("Shape", () => video.Aspect, v => video.Aspect = v,
            "Standard is 4:3, the shape the console drew for a television.");
    }

    private static MenuItem FilteringItem(MenuContext context)
    {
        var video = context.Settings.Video;
        return EnumChoice("Filtering", () => video.Filter, v => video.Filter = v,
            "Linear smooths the picture as a television would; nearest keeps hard pixel edges.");
    }

    private static MenuItem DeinterlaceItem(MenuContext context)
    {
        var video = context.Settings.Video;
        return EnumChoice("Deinterlace", () => video.Deinterlace, v => video.Deinterlace = v,
            "Blend hides the combing interlaced movies show on a computer screen.");
    }

    private static MenuItem WindowScaleItem(MenuContext context)
    {
        var video = context.Settings.Video;
        return new MenuNumber
        {
            Label = "Window scale",
            Help = "Size of the window as a multiple of 640 x 480.",
            Get = () => video.WindowScale,
            Set = v => video.WindowScale = v,
            Minimum = 1, Maximum = 4, Default = 2,
            Format = v => $"{v}x  ({640 * v}x{480 * v})",
        };
    }

    private static MenuItem FullscreenItem(MenuContext context) => new MenuToggle
    {
        Label = "Full screen",
        Help = "Fill the display.",
        Get = () => context.Settings.Video.Fullscreen,
        Set = _ => context.ToggleFullscreen(),
    };

    private static MenuItem OverlayModeItem(MenuContext context)
    {
        var ui = context.Settings.Interface;
        return EnumChoice("Status overlay", () => ui.Overlay, v => ui.Overlay = v,
            "Auto shows the overlay briefly whenever something changes.");
    }

    private static MenuItem OverlayCornerItem(MenuContext context)
    {
        var ui = context.Settings.Interface;
        return EnumChoice("Overlay corner", () => ui.OverlayCorner, v => ui.OverlayCorner = v,
            "Where the status overlay sits.");
    }

    private static MenuItem PerformanceItem(MenuContext context)
    {
        var ui = context.Settings.Interface;
        return new MenuToggle
        {
            Label = "Show performance",
            Help = "Frame rate in the status overlay.",
            Get = () => ui.ShowPerformance,
            Set = v => ui.ShowPerformance = v,
        };
    }

    private static MenuItem VolumeItem(MenuContext context)
    {
        var audio = context.Settings.Audio;
        return new MenuNumber
        {
            Label = "Volume",
            Help = "Output level.",
            Get = () => audio.Volume,
            Set = v => audio.Volume = v,
            Minimum = 0, Maximum = 100, Step = 5, Default = 80,
            Format = v => $"{v}%",
        };
    }

    private static MenuItem MuteItem(MenuContext context)
    {
        var audio = context.Settings.Audio;
        return new MenuToggle
        {
            Label = "Mute",
            Help = "Silence the output without losing the volume setting.",
            Get = () => audio.Muted,
            Set = v => audio.Muted = v,
        };
    }

    /// <summary>A row that runs an action, labelled with whatever control is bound to it.</summary>
    private static MenuAction Command(MenuContext context, string label, InputAction action, string? help = null)
        => new()
        {
            Label = label,
            Help = help ?? InputActions.Label(action),
            Detail = () => context.Input.Describe(action),
            OnActivate = () => context.Perform(action),
        };

    private static MenuItem EnumChoice<T>(string label, Func<T> get, Action<T> set, string? help = null)
        where T : struct, Enum
    {
        var values = Enum.GetValues<T>();

        return new MenuChoice
        {
            Label = label,
            Help = help,
            Options = values.Select(Humanise).ToArray(),
            Get = () => Math.Max(0, Array.IndexOf(values, get())),
            Set = i => set(values[i]),
            Default = 0,
        };
    }

    /// <summary>Turns an enum name such as <c>FitWindow</c> into <c>Fit window</c>.</summary>
    private static string Humanise<T>(T value) where T : struct, Enum
    {
        var name = value.ToString()!;
        var text = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                text.Append(name[i]);
            }
        }

        return text.ToString();
    }
}
