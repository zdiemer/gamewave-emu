using GameWave.Engine;

namespace GameWave.Input;

/// <summary>
/// The binding table: which controls trigger which actions. Every action can carry
/// several bindings, so keyboard and controller can drive the same thing.
/// </summary>
public sealed class InputMap
{
    private readonly Dictionary<InputAction, List<Binding>> _bindings = new();

    private InputMap()
    {
    }

    /// <summary>The bindings the emulator ships with.</summary>
    /// <remarks>
    /// The keyboard is the red remote: arrows, Enter for SEL, and the letters and digits
    /// for the keys that carry them, so an on-screen "press A" means the A key. Every
    /// remote gets the same controller layout, and each controller drives the remote it
    /// is assigned to (the first controller red, the second yellow, and so on).
    /// </remarks>
    public static InputMap CreateDefault()
    {
        var map = new InputMap();

        void Bind(InputAction action, params Binding[] bindings) => map._bindings[action] = [.. bindings];

        int Key(string name) => KeyNames.TryParse(name, out var code) ? code : 0;

        Bind(InputAction.TogglePause, Binding.Key(Key("P")), Binding.Key(Key("Pause")));
        Bind(InputAction.Reset, Binding.Key(Key("R"), KeyModifiers.Control), Binding.Key(Key("F5")));
        Bind(InputAction.QuickSave, Binding.Key(Key("F6")));
        Bind(InputAction.QuickLoad, Binding.Key(Key("F7")));
        Bind(InputAction.ToggleTray, Binding.Key(Key("E"), KeyModifiers.Control));
        Bind(InputAction.OpenDisc, Binding.Key(Key("O"), KeyModifiers.Control));

        for (int i = 0; i < 6; i++)
            Bind(InputAction.KeyboardRed + i, Binding.Key(Key((i + 1).ToString()), KeyModifiers.Control));

        Bind(InputAction.VolumeUp, Binding.Key(Key("Equals")), Binding.Key(Key("NumpadPlus")));
        Bind(InputAction.VolumeDown, Binding.Key(Key("Minus")), Binding.Key(Key("NumpadMinus")));
        Bind(InputAction.ToggleMute, Binding.Key(Key("M"), KeyModifiers.Control));

        Bind(InputAction.ToggleFullscreen, Binding.Key(Key("F11")), Binding.Key(Key("Return"), KeyModifiers.Alt));
        Bind(InputAction.ToggleOverlay, Binding.Key(Key("Tab")));
        Bind(InputAction.Screenshot, Binding.Key(Key("F12")));

        Bind(InputAction.ToggleMenu, Binding.Key(Key("Escape")), Binding.Button(ControllerButton.Guide), Binding.Button(ControllerButton.RightStick));
        Bind(InputAction.MenuUp, Binding.Key(Key("Up")), Binding.Button(ControllerButton.DPadUp));
        Bind(InputAction.MenuDown, Binding.Key(Key("Down")), Binding.Button(ControllerButton.DPadDown));
        Bind(InputAction.MenuLeft, Binding.Key(Key("Left")), Binding.Button(ControllerButton.DPadLeft));
        Bind(InputAction.MenuRight, Binding.Key(Key("Right")), Binding.Button(ControllerButton.DPadRight));
        Bind(InputAction.MenuSelect, Binding.Key(Key("Return")), Binding.Key(Key("NumpadEnter")), Binding.Button(ControllerButton.A));
        Bind(InputAction.MenuBack, Binding.Key(Key("Escape")), Binding.Key(Key("Backspace")), Binding.Button(ControllerButton.B));
        Bind(InputAction.MenuPageUp, Binding.Key(Key("PageUp")), Binding.Button(ControllerButton.LeftShoulder));
        Bind(InputAction.MenuPageDown, Binding.Key(Key("PageDown")), Binding.Button(ControllerButton.RightShoulder));
        Bind(InputAction.MenuResetItem, Binding.Key(Key("Delete")), Binding.Button(ControllerButton.Y));

        Bind(InputAction.Quit, Binding.Key(Key("Q"), KeyModifiers.Control));

        foreach (var remote in Enum.GetValues<Remote>())
        {
            Binding[] Pad(RemoteKey key) => key switch
            {
                RemoteKey.Up => [Binding.Button(ControllerButton.DPadUp), Binding.Axis(ControllerAxis.LeftY, positive: false)],
                RemoteKey.Down => [Binding.Button(ControllerButton.DPadDown), Binding.Axis(ControllerAxis.LeftY, positive: true)],
                RemoteKey.Left => [Binding.Button(ControllerButton.DPadLeft), Binding.Axis(ControllerAxis.LeftX, positive: false)],
                RemoteKey.Right => [Binding.Button(ControllerButton.DPadRight), Binding.Axis(ControllerAxis.LeftX, positive: true)],
                RemoteKey.Select => [Binding.Button(ControllerButton.Start), Binding.Button(ControllerButton.RightShoulder)],
                RemoteKey.A => [Binding.Button(ControllerButton.A)],
                RemoteKey.B => [Binding.Button(ControllerButton.B)],
                RemoteKey.C => [Binding.Button(ControllerButton.X)],
                RemoteKey.D => [Binding.Button(ControllerButton.Y)],
                RemoteKey.GameMenu => [Binding.Button(ControllerButton.Back)],
                RemoteKey.DvdMenu => [Binding.Button(ControllerButton.LeftShoulder)],
                _ => [],
            };

            foreach (var action in InputActions.KeysOf(remote))
            {
                var key = InputActions.KeyOf(action);
                var bindings = new List<Binding>(Pad(key));
                if (remote == Remote.Red)
                {
                    switch (key)
                    {
                        case >= RemoteKey.Num0 and <= RemoteKey.Num9:
                            int digit = (int)key;
                            bindings.Insert(0, Binding.Key(Key(digit.ToString())));
                            bindings.Insert(1, Binding.Key(Key("Numpad" + digit)));
                            break;
                        case RemoteKey.Up or RemoteKey.Down or RemoteKey.Left or RemoteKey.Right:
                            bindings.Insert(0, Binding.Key(Key(key.ToString())));
                            break;
                        case RemoteKey.Select:
                            bindings.Insert(0, Binding.Key(Key("Return")));
                            bindings.Insert(1, Binding.Key(Key("Space")));
                            break;
                        case RemoteKey.A or RemoteKey.B or RemoteKey.C or RemoteKey.D:
                            bindings.Insert(0, Binding.Key(Key(key.ToString())));
                            break;
                        case RemoteKey.GameMenu:
                            bindings.Insert(0, Binding.Key(Key("G")));
                            break;
                        case RemoteKey.DvdMenu:
                            bindings.Insert(0, Binding.Key(Key("Home")));
                            break;
                    }
                }
                map._bindings[action] = bindings;
            }
        }

        return map;
    }

    /// <summary>
    /// Builds a map from stored settings, falling back to the default binding for any
    /// action the file does not mention. An action bound to an empty list stays unbound.
    /// </summary>
    public static InputMap FromSettings(IReadOnlyDictionary<string, List<string>> stored)
    {
        var map = CreateDefault();

        foreach (var (name, values) in stored)
        {
            if (!InputActions.TryParse(name, out var action)) continue;

            var parsed = new List<Binding>(values.Count);
            foreach (var value in values)
            {
                if (Binding.TryParse(value, out var binding)) parsed.Add(binding);
            }

            map._bindings[action] = parsed;
        }

        return map;
    }

    /// <summary>Renders the map back into the form stored in the settings file.</summary>
    public Dictionary<string, List<string>> ToSettings()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var action in InputActions.All)
            result[action.ToString()] = BindingsFor(action).Select(b => b.ToString()).ToList();

        return result;
    }

    /// <summary>The bindings currently assigned to an action.</summary>
    public IReadOnlyList<Binding> BindingsFor(InputAction action)
        => _bindings.TryGetValue(action, out var list) ? list : [];

    /// <summary>A readable summary of an action's bindings, for the controls page.</summary>
    public string Describe(InputAction action)
    {
        var bindings = BindingsFor(action);
        return bindings.Count == 0 ? "unbound" : string.Join(", ", bindings.Select(b => b.ToString()));
    }

    /// <summary>Replaces every binding for an action with a single control.</summary>
    public void Rebind(InputAction action, Binding binding) => _bindings[action] = [binding];

    /// <summary>
    /// Replaces an action's bindings of the same kind (keyboard, or controller) with one
    /// control, keeping the rest: rebinding a remote's key leaves its controller button alone.
    /// </summary>
    public void ReplaceSameKind(InputAction action, Binding binding)
    {
        bool keyboard = binding.Kind == BindingKind.Key;
        var list = _bindings.TryGetValue(action, out var existing) ? existing : _bindings[action] = [];
        list.RemoveAll(b => (b.Kind == BindingKind.Key) == keyboard);
        list.Insert(0, binding);
    }

    /// <summary>Adds another control for an action, ignoring duplicates.</summary>
    public void AddBinding(InputAction action, Binding binding)
    {
        var list = _bindings.TryGetValue(action, out var existing) ? existing : _bindings[action] = [];
        if (!list.Contains(binding)) list.Add(binding);
    }

    /// <summary>Removes every binding for an action.</summary>
    public void Clear(InputAction action) => _bindings[action] = [];

    /// <summary>Restores one action to its shipped bindings.</summary>
    public void ResetToDefault(InputAction action)
        => _bindings[action] = [.. CreateDefault().BindingsFor(action)];

    /// <summary>Restores every action to its shipped bindings.</summary>
    public void ResetAll()
    {
        _bindings.Clear();
        foreach (var (action, bindings) in CreateDefault()._bindings) _bindings[action] = bindings;
    }

    /// <summary>Actions that <paramref name="binding"/> would also trigger, other than <paramref name="excluding"/>.</summary>
    public IReadOnlyList<InputAction> Conflicts(Binding binding, InputAction excluding)
        => _bindings
            .Where(kv => kv.Key != excluding && kv.Value.Contains(binding))
            .Select(kv => kv.Key)
            .ToArray();

    /// <summary>
    /// Every action a keyboard event triggers. Bindings with modifiers are preferred, so
    /// binding Shift+Left to seek does not also fire the plain Left binding.
    /// </summary>
    public IReadOnlyList<InputAction> MatchKey(int keycode, KeyModifiers modifiers)
    {
        var exact = new List<InputAction>();
        var plain = new List<InputAction>();

        foreach (var (action, bindings) in _bindings)
        {
            foreach (var binding in bindings)
            {
                if (binding.Kind != BindingKind.Key || binding.Code != keycode) continue;

                if (binding.Modifiers == modifiers) exact.Add(action);
                else if (binding.Modifiers == KeyModifiers.None) plain.Add(action);
            }
        }

        return exact.Count > 0 ? exact : modifiers == KeyModifiers.None ? plain : [];
    }

    /// <summary>Every action a controller button triggers.</summary>
    public IReadOnlyList<InputAction> MatchButton(int button)
        => _bindings
            .Where(kv => kv.Value.Any(b => b.Kind == BindingKind.ControllerButton && b.Code == button))
            .Select(kv => kv.Key)
            .ToArray();

    /// <summary>Every action a controller axis deflection triggers.</summary>
    public IReadOnlyList<InputAction> MatchAxis(int axis, bool positive)
        => _bindings
            .Where(kv => kv.Value.Any(b =>
                b.Kind == BindingKind.ControllerAxis && b.Code == axis && b.Positive == positive))
            .Select(kv => kv.Key)
            .ToArray();
}
