using GameWave.Config;
using GameWave.Input;

namespace GameWave.Cli;

/// <summary>Reads and writes the settings file and the bindings from the command line.</summary>
internal static class ConfigCommands
{
    public static int Config(string[] args)
    {
        var verb = (args.ElementAtOrDefault(0) ?? "list").ToLowerInvariant();
        var settings = SettingsStore.Load();

        switch (verb)
        {
            case "path":
                Console.WriteLine(SettingsStore.FilePath);
                return 0;

            case "list":
            {
                var filter = args.ElementAtOrDefault(1);
                var entries = SettingsStore.Enumerate(settings)
                    .Where(e => filter is null || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var width = entries.Length == 0 ? 0 : entries.Max(e => e.Path.Length);
                foreach (var entry in entries)
                {
                    var choices = entry.Choices.Count > 0 ? $"   ({string.Join(" | ", entry.Choices)})" : string.Empty;
                    Console.WriteLine($"{entry.Path.PadRight(width)}  {entry.Value}{choices}");
                }
                return 0;
            }

            case "get":
            {
                var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("config get <setting>");
                var entry = SettingsStore.Find(settings, path) ?? throw new ArgumentException($"No setting called '{path}'.");
                Console.WriteLine(entry.Value);
                return 0;
            }

            case "set":
            {
                var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("config set <setting> <value>");
                var value = args.ElementAtOrDefault(2) ?? throw new ArgumentException("config set <setting> <value>");
                var entry = SettingsStore.Find(settings, path) ?? throw new ArgumentException($"No setting called '{path}'.");
                SettingsStore.Set(entry, value);
                SettingsStore.Save(settings);
                Console.WriteLine($"{entry.Path} = {entry.Value}");
                return 0;
            }

            case "reset":
            {
                var path = args.ElementAtOrDefault(1) ?? "all";
                if (path == "all")
                {
                    var fresh = new GameWaveSettings { Bindings = settings.Bindings, RecentDiscs = settings.RecentDiscs };
                    SettingsStore.Save(fresh);
                    Console.WriteLine("All settings reset.");
                    return 0;
                }
                var entry = SettingsStore.Find(settings, path) ?? throw new ArgumentException($"No setting called '{path}'.");
                var defaults = SettingsStore.Find(new GameWaveSettings(), path)!;
                entry.Property.SetValue(entry.Owner, defaults.Property.GetValue(defaults.Owner));
                SettingsStore.Save(settings);
                Console.WriteLine($"{entry.Path} = {entry.Value}");
                return 0;
            }

            default:
                throw new ArgumentException($"Unknown config command '{verb}'. Use list, get, set, reset or path.");
        }
    }

    public static int Bind(string[] args)
    {
        var verb = (args.ElementAtOrDefault(0) ?? "list").ToLowerInvariant();
        var settings = SettingsStore.Load();
        var map = settings.BuildInputMap();

        InputAction Action(int index)
        {
            var name = args.ElementAtOrDefault(index) ?? throw new ArgumentException($"bind {verb} <action>");
            if (!InputActions.TryParse(name, out var action))
                throw new ArgumentException($"No action called '{name}'. 'gamewave bind list' shows them.");
            return action;
        }

        Binding Control(int index)
        {
            var text = args.ElementAtOrDefault(index) ?? throw new ArgumentException($"bind {verb} <action> <control>");
            if (!Binding.TryParse(text, out var binding))
                throw new ArgumentException($"'{text}' is not a key or controller input. 'gamewave bind keys' lists key names.");
            return binding;
        }

        switch (verb)
        {
            case "list":
            {
                var filter = args.ElementAtOrDefault(1);
                foreach (var action in InputActions.All)
                {
                    if (filter is not null && !action.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Console.WriteLine($"{action,-20} {map.Describe(action)}");
                }
                return 0;
            }

            case "keys":
                foreach (var name in KeyNames.KnownNames)
                    Console.WriteLine(name);
                return 0;

            case "set":
                map.Rebind(Action(1), Control(2));
                break;

            case "add":
                map.AddBinding(Action(1), Control(2));
                break;

            case "clear":
                map.Clear(Action(1));
                break;

            case "reset":
                if ((args.ElementAtOrDefault(1) ?? "all") == "all")
                    map.ResetAll();
                else
                    map.ResetToDefault(Action(1));
                break;

            default:
                throw new ArgumentException($"Unknown bind command '{verb}'. Use list, set, add, clear, reset or keys.");
        }

        settings.StoreInputMap(map);
        SettingsStore.Save(settings);
        if (verb is "set" or "add" or "clear")
        {
            var action = Action(1);
            Console.WriteLine($"{action} = {map.Describe(action)}");
        }
        return 0;
    }
}
