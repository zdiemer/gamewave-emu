using GameWave.Disc;
using GameWave.Engine;
using GameWave.Graphics;

namespace GameWave.Cli;

/// <summary>
/// <c>gamewave run</c>: boots a disc with no window and no sound device, optionally pressing
/// keys and saving screenshots at given times. Useful for checking a disc and for tests.
/// </summary>
internal static class RunCommand
{
    public static int Run(string[] args)
    {
        string? disc = null;
        double seconds = 10;
        var presses = new List<(long At, RemoteKey Key, Remote Remote)>();
        var shots = new List<(long At, string Path)>();
        int logLevel = 3;
        int monkey = 0;
        string? saves = null;
        int? seed = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds":
                    seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--press":
                    // time:key[:remote], e.g. 5000:select or 8000:a:blue
                    foreach (var spec in args[++i].Split(','))
                    {
                        var parts = spec.Split(':');
                        var key = Enum.Parse<RemoteKey>(parts[1], true);
                        var remote = parts.Length > 2 ? Enum.Parse<Remote>(parts[2], true) : Remote.Red;
                        presses.Add((long.Parse(parts[0]), key, remote));
                    }
                    break;
                case "--shot":
                    // time:path.png
                    foreach (var spec in args[++i].Split(','))
                    {
                        int c = spec.IndexOf(':');
                        shots.Add((long.Parse(spec[..c]), spec[(c + 1)..]));
                    }
                    break;
                case "--monkey":
                    // Random key presses every so many milliseconds, for soak testing.
                    monkey = int.Parse(args[++i]);
                    break;
                case "--saves":
                    saves = args[++i];
                    break;
                case "--seed":
                    seed = int.Parse(args[++i]);
                    break;
                case "--log":
                    logLevel = int.Parse(args[++i]);
                    break;
                default:
                    disc = args[i];
                    break;
            }
        }
        if (disc is null)
        {
            Console.Error.WriteLine("usage: gamewave run <disc> [--seconds N] [--press ms:key[:remote],...] [--shot ms:file.png,...] [--log 0-5]");
            return 2;
        }

        using var machine = new Machine(CliDisc.Open(disc), new SaveStore(saves));
        machine.LogLevel = logLevel;
        machine.Log = s => Console.WriteLine($"[{machine.Clock.Now,7}] {s}");
        machine.Start();

        var frame = new uint[Osd.Width * Osd.Height];
        presses.Sort((a, b) => a.At.CompareTo(b.At));
        shots.Sort((a, b) => a.At.CompareTo(b.At));
        int pi = 0, si = 0;
        var random = seed is { } s0 ? new Random(s0) : new Random();
        long nextMonkey = 5000;
        long end = (long)(seconds * 1000);
        while (machine.Clock.Now < end && machine.State is MachineState.Running or MachineState.TrayOpen)
        {
            if (machine.State == MachineState.TrayOpen)
            {
                Console.WriteLine($"[{machine.Clock.Now,7}] (tray opened; closing it)");
                machine.CloseTray();
            }
            long now = machine.Clock.Now;
            while (pi < presses.Count && presses[pi].At <= now)
            {
                machine.Input.Push(presses[pi].Key, presses[pi].Remote, now);
                Console.WriteLine($"[{now,7}] (pressed {presses[pi].Key} on {presses[pi].Remote})");
                pi++;
            }
            if (monkey > 0 && now >= nextMonkey)
            {
                nextMonkey = now + monkey / 2 + random.Next(monkey);
                var key = (RemoteKey)random.Next(0, 21);
                // Mostly the red remote, sometimes another player.
                var remote = random.Next(4) == 0 ? (Remote)random.Next(1, 7) : Remote.Red;
                machine.Input.Push(key, remote, now);
            }
            machine.RenderFrame(frame);
            while (si < shots.Count && shots[si].At <= now)
            {
                PngWriter.Write(shots[si].Path, frame, Osd.Width, Osd.Height);
                Console.WriteLine($"[{now,7}] (saved {shots[si].Path})");
                si++;
            }
            Thread.Sleep(16);
        }
        Console.WriteLine($"[{machine.Clock.Now,7}] machine state: {machine.State}{(machine.CrashMessage is { } m ? " - " + m : "")}");
        return machine.State == MachineState.Crashed ? 1 : 0;
    }
}
