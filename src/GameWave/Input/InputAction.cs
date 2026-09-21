using GameWave.Engine;

namespace GameWave.Input;

/// <summary>
/// Everything that can be bound to a key or controller: the emulator's own commands, then
/// every key on each of the six player remotes. Each is separately bindable.
/// </summary>
public enum InputAction
{
    /// <summary>Pause or continue the console.</summary>
    TogglePause,

    /// <summary>Press the console's reset: the game boots again.</summary>
    Reset,

    /// <summary>Capture the running game in the single quicksave slot.</summary>
    QuickSave,

    /// <summary>Return to the game in the single quicksave slot.</summary>
    QuickLoad,

    /// <summary>Open or close the disc tray.</summary>
    ToggleTray,

    /// <summary>Choose a disc to put in the console.</summary>
    OpenDisc,

    /// <summary>Make the keyboard play as the red remote.</summary>
    KeyboardRed,

    /// <summary>Make the keyboard play as the yellow remote.</summary>
    KeyboardYellow,

    /// <summary>Make the keyboard play as the blue remote.</summary>
    KeyboardBlue,

    /// <summary>Make the keyboard play as the green remote.</summary>
    KeyboardGreen,

    /// <summary>Make the keyboard play as the purple remote.</summary>
    KeyboardPurple,

    /// <summary>Make the keyboard play as the orange remote.</summary>
    KeyboardOrange,

    /// <summary>Raise the volume.</summary>
    VolumeUp,

    /// <summary>Lower the volume.</summary>
    VolumeDown,

    /// <summary>Silence or restore the sound.</summary>
    ToggleMute,

    /// <summary>Switch between windowed and full screen.</summary>
    ToggleFullscreen,

    /// <summary>Show or hide the status overlay.</summary>
    ToggleOverlay,

    /// <summary>Write the current picture to a PNG file.</summary>
    Screenshot,

    /// <summary>Open or close the menu.</summary>
    ToggleMenu,

    /// <summary>Quit the emulator.</summary>
    Quit,

    /// <summary>Move up in a menu.</summary>
    MenuUp,

    /// <summary>Move down in a menu.</summary>
    MenuDown,

    /// <summary>Decrease a value, or move left in a menu.</summary>
    MenuLeft,

    /// <summary>Increase a value, or move right in a menu.</summary>
    MenuRight,

    /// <summary>Activate the highlighted menu item.</summary>
    MenuSelect,

    /// <summary>Leave the current menu.</summary>
    MenuBack,

    /// <summary>Jump a page up in a long list.</summary>
    MenuPageUp,

    /// <summary>Jump a page down in a long list.</summary>
    MenuPageDown,

    /// <summary>Restore the highlighted setting to its default.</summary>
    MenuResetItem,

    // Remote keys: RemoteFirst + (remote - 1) * RemoteKeyCount + key. The order matches
    // Engine.Remote and Engine.RemoteKey, which InputActions relies on.
    RedNum0 = InputActions.RemoteFirst,
    RedNum1,
    RedNum2,
    RedNum3,
    RedNum4,
    RedNum5,
    RedNum6,
    RedNum7,
    RedNum8,
    RedNum9,
    RedUp,
    RedDown,
    RedRight,
    RedLeft,
    RedSelect,
    RedDvdMenu,
    RedA,
    RedB,
    RedC,
    RedD,
    RedGameMenu,
    YellowNum0,
    YellowNum1,
    YellowNum2,
    YellowNum3,
    YellowNum4,
    YellowNum5,
    YellowNum6,
    YellowNum7,
    YellowNum8,
    YellowNum9,
    YellowUp,
    YellowDown,
    YellowRight,
    YellowLeft,
    YellowSelect,
    YellowDvdMenu,
    YellowA,
    YellowB,
    YellowC,
    YellowD,
    YellowGameMenu,
    BlueNum0,
    BlueNum1,
    BlueNum2,
    BlueNum3,
    BlueNum4,
    BlueNum5,
    BlueNum6,
    BlueNum7,
    BlueNum8,
    BlueNum9,
    BlueUp,
    BlueDown,
    BlueRight,
    BlueLeft,
    BlueSelect,
    BlueDvdMenu,
    BlueA,
    BlueB,
    BlueC,
    BlueD,
    BlueGameMenu,
    GreenNum0,
    GreenNum1,
    GreenNum2,
    GreenNum3,
    GreenNum4,
    GreenNum5,
    GreenNum6,
    GreenNum7,
    GreenNum8,
    GreenNum9,
    GreenUp,
    GreenDown,
    GreenRight,
    GreenLeft,
    GreenSelect,
    GreenDvdMenu,
    GreenA,
    GreenB,
    GreenC,
    GreenD,
    GreenGameMenu,
    PurpleNum0,
    PurpleNum1,
    PurpleNum2,
    PurpleNum3,
    PurpleNum4,
    PurpleNum5,
    PurpleNum6,
    PurpleNum7,
    PurpleNum8,
    PurpleNum9,
    PurpleUp,
    PurpleDown,
    PurpleRight,
    PurpleLeft,
    PurpleSelect,
    PurpleDvdMenu,
    PurpleA,
    PurpleB,
    PurpleC,
    PurpleD,
    PurpleGameMenu,
    OrangeNum0,
    OrangeNum1,
    OrangeNum2,
    OrangeNum3,
    OrangeNum4,
    OrangeNum5,
    OrangeNum6,
    OrangeNum7,
    OrangeNum8,
    OrangeNum9,
    OrangeUp,
    OrangeDown,
    OrangeRight,
    OrangeLeft,
    OrangeSelect,
    OrangeDvdMenu,
    OrangeA,
    OrangeB,
    OrangeC,
    OrangeD,
    OrangeGameMenu,
}

/// <summary>Grouping used to lay out the controls pages.</summary>
public enum ActionCategory
{
    /// <summary>The console: pause, reset, the disc tray.</summary>
    Console,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Windowing, overlay and screenshots.</summary>
    Display,

    /// <summary>Moving around the menus.</summary>
    Menu,

    /// <summary>Everything else.</summary>
    General,

    /// <summary>A player remote; see <see cref="InputActions.RemoteOf"/>.</summary>
    Remote,
}

/// <summary>Descriptions and grouping for <see cref="InputAction"/>.</summary>
public static class InputActions
{
    /// <summary>The first remote key action.</summary>
    public const int RemoteFirst = 1000;

    /// <summary>Keys on one remote.</summary>
    public const int RemoteKeyCount = 21;

    /// <summary>Every action, in the order the controls pages show them.</summary>
    public static readonly IReadOnlyList<InputAction> All = Enum.GetValues<InputAction>();

    private static readonly Dictionary<InputAction, (string Label, ActionCategory Category)> Info = new()
    {
        [InputAction.TogglePause] = ("Pause", ActionCategory.Console),
        [InputAction.Reset] = ("Reset", ActionCategory.Console),
        [InputAction.QuickSave] = ("Quicksave", ActionCategory.Console),
        [InputAction.QuickLoad] = ("Quickload", ActionCategory.Console),
        [InputAction.ToggleTray] = ("Open / close tray", ActionCategory.Console),
        [InputAction.OpenDisc] = ("Open disc", ActionCategory.Console),

        [InputAction.KeyboardRed] = ("Keyboard plays red", ActionCategory.Console),
        [InputAction.KeyboardYellow] = ("Keyboard plays yellow", ActionCategory.Console),
        [InputAction.KeyboardBlue] = ("Keyboard plays blue", ActionCategory.Console),
        [InputAction.KeyboardGreen] = ("Keyboard plays green", ActionCategory.Console),
        [InputAction.KeyboardPurple] = ("Keyboard plays purple", ActionCategory.Console),
        [InputAction.KeyboardOrange] = ("Keyboard plays orange", ActionCategory.Console),

        [InputAction.VolumeUp] = ("Volume up", ActionCategory.Audio),
        [InputAction.VolumeDown] = ("Volume down", ActionCategory.Audio),
        [InputAction.ToggleMute] = ("Mute", ActionCategory.Audio),

        [InputAction.ToggleFullscreen] = ("Full screen", ActionCategory.Display),
        [InputAction.ToggleOverlay] = ("Status overlay", ActionCategory.Display),
        [InputAction.Screenshot] = ("Screenshot", ActionCategory.Display),

        [InputAction.ToggleMenu] = ("Open menu", ActionCategory.Menu),
        [InputAction.MenuUp] = ("Menu up", ActionCategory.Menu),
        [InputAction.MenuDown] = ("Menu down", ActionCategory.Menu),
        [InputAction.MenuLeft] = ("Menu left", ActionCategory.Menu),
        [InputAction.MenuRight] = ("Menu right", ActionCategory.Menu),
        [InputAction.MenuSelect] = ("Menu select", ActionCategory.Menu),
        [InputAction.MenuBack] = ("Menu back", ActionCategory.Menu),
        [InputAction.MenuPageUp] = ("Menu page up", ActionCategory.Menu),
        [InputAction.MenuPageDown] = ("Menu page down", ActionCategory.Menu),
        [InputAction.MenuResetItem] = ("Reset setting", ActionCategory.Menu),

        [InputAction.Quit] = ("Quit", ActionCategory.General),
    };

    /// <summary>Whether the action is a key on a player remote.</summary>
    public static bool IsRemoteKey(InputAction action) => (int)action >= RemoteFirst;

    /// <summary>The remote a remote key action belongs to.</summary>
    public static Remote RemoteOf(InputAction action)
        => (Remote)(((int)action - RemoteFirst) / RemoteKeyCount + 1);

    /// <summary>The key a remote key action presses.</summary>
    public static RemoteKey KeyOf(InputAction action)
        => (RemoteKey)(((int)action - RemoteFirst) % RemoteKeyCount);

    /// <summary>The action for a key on a remote.</summary>
    public static InputAction For(Remote remote, RemoteKey key)
        => (InputAction)(RemoteFirst + ((int)remote - 1) * RemoteKeyCount + (int)key);

    /// <summary>Every key action on one remote, in the remote's own order.</summary>
    public static IEnumerable<InputAction> KeysOf(Remote remote)
        => Enumerable.Range(0, RemoteKeyCount).Select(k => For(remote, (RemoteKey)k));

    /// <summary>
    /// Whether the action only means anything with a game running. With none these are
    /// turned away; settings, the menu and opening a disc still work.
    /// </summary>
    public static bool NeedsDisc(InputAction action)
        => IsRemoteKey(action) || action is InputAction.TogglePause or InputAction.Reset
            or InputAction.QuickSave or InputAction.QuickLoad
            or InputAction.ToggleTray or InputAction.Screenshot;

    /// <summary>A human-readable name for a remote key.</summary>
    public static string KeyLabel(RemoteKey key) => key switch
    {
        >= RemoteKey.Num0 and <= RemoteKey.Num9 => ((int)key).ToString(),
        RemoteKey.Select => "SEL",
        RemoteKey.DvdMenu => "DVD menu",
        RemoteKey.GameMenu => "Game menu",
        _ => key.ToString(),
    };

    /// <summary>A human-readable name for the action.</summary>
    public static string Label(InputAction action)
    {
        if (IsRemoteKey(action))
            return $"{RemoteOf(action)} {KeyLabel(KeyOf(action))}";
        return Info.TryGetValue(action, out var info) ? info.Label : action.ToString();
    }

    /// <summary>Which group the action belongs to.</summary>
    public static ActionCategory Category(InputAction action)
    {
        if (IsRemoteKey(action))
            return ActionCategory.Remote;
        return Info.TryGetValue(action, out var info) ? info.Category : ActionCategory.General;
    }

    /// <summary>Actions in a category, in declaration order.</summary>
    public static IEnumerable<InputAction> InCategory(ActionCategory category)
        => All.Where(a => Category(a) == category);

    /// <summary>Parses an action name, accepting any capitalisation.</summary>
    public static bool TryParse(string name, out InputAction action)
        => Enum.TryParse(name, ignoreCase: true, out action) && Enum.IsDefined(action);
}
