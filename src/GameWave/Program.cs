using GameWave;
using GameWave.Cli;
using GameWave.Ui.Native;

// On Windows gamewave is a GUI program, so it has no console until it borrows its parent's.
// This comes first: Console picks up the standard handles on first use.
if (OperatingSystem.IsWindows()) ParentConsole.Attach();

// Double-clicked, or started with nothing to say: open the console with no disc in it.
if (args.Length == 0) args = ["play"];

var command = args[0].ToLowerInvariant();
string[] commands = ["play", "run", "info", "disasm", "config", "bind", "help", "-h", "--help", "version", "--version"];
var named = commands.Contains(command);
var rest = named ? args[1..] : args;

try
{
    return command switch
    {
        "play" => PlayCommand.Run(rest),
        "run" => RunCommand.Run(rest),
        "info" => InfoCommand.Run(rest),
        "disasm" => InfoCommand.Disassemble(rest),
        "config" => ConfigCommands.Config(rest),
        "bind" => ConfigCommands.Bind(rest),
        "help" or "-h" or "--help" => Usage.Print(),
        "version" or "--version" => Usage.Version(),

        // Anything else is taken as a disc to play, so "gamewave disc.iso" just works.
        _ => PlayCommand.Run(rest),
    };
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or IOException or SdlException or NotSupportedException)
{
    Console.Error.WriteLine($"gamewave: {ex.Message}");

    // Started from Explorer with a disc that will not open, there is no console to print
    // to and the window never appears; without this it would simply not start.
    if (OperatingSystem.IsWindows() && !ParentConsole.ErrorsAreVisible)
        Win32.MessageBox(0, ex.Message, "Game Wave", Win32.MbOk | Win32.MbIconError);

    return 1;
}

/// <summary>The help text.</summary>
internal static class Usage
{
    public static int Version()
    {
        var version = typeof(Usage).Assembly.GetName().Version;
        Console.WriteLine($"gamewave {version?.ToString(3) ?? "unknown"}");
        return 0;
    }

    public static int Print()
    {
        Console.WriteLine("""
            gamewave - a Game Wave Family Entertainment System emulator

            PLAYING
              gamewave                              Open the console with no disc in it.
              gamewave <disc> [options]             Play a disc: an .iso, a .zip holding
                                                    one, or a folder of the disc's files.
              gamewave play <disc> [options]        The same, spelled out.

              Another disc can be opened at any time with Ctrl+O, File > Open Disc, the
              Games page, or by dropping a file on the window. Options are for this run
              only and are not saved.

                --fullscreen       Start full screen. --windowed does the opposite.
                --scale N          Window size as a multiple of 640 x 480.
                --mute             Start silent.
                --volume N         Volume, 0 to 100.
                --log N            Show the game's own log, 0 (none) to 5 (debug).
                --no-config        Use defaults; neither read nor write the settings file.

            CHECKING A DISC
              gamewave info <disc>                  What the disc is and what it holds.
              gamewave run <disc> [--seconds N] [--press MS:KEY[:REMOTE],...]
                                  [--shot MS:FILE.png,...] [--log N]
                                                    Run with no window or sound device,
                                                    pressing keys and taking screenshots
                                                    at the given times (milliseconds).
                                                    Keys: 0-9, up, down, left, right,
                                                    select, a, b, c, d, gamemenu, dvdmenu.
                                                    --monkey MS presses random keys; --saves FILE
                                                    keeps save memory in a file.
              gamewave disasm <file.zbc>            Disassemble a game program.

            SETTINGS
              gamewave config list [filter]
              gamewave config get <setting>
              gamewave config set <setting> <value>
              gamewave config reset [<setting>|all]
              gamewave config path

              gamewave bind list [filter]
              gamewave bind set <action> <control>  e.g. gamewave bind set RedSelect Space
              gamewave bind add <action> <control>  e.g. gamewave bind add RedA Pad:A
              gamewave bind clear <action>
              gamewave bind reset [<action>|all]
              gamewave bind keys                    List every bindable key name.

            Press Escape while playing for the menu: your games, settings, controllers
            and controls. The keyboard is the red remote: arrows, Enter for SEL, the A,
            B, C and D keys, the digits, and G for the game menu.
            """);

        return 0;
    }
}
