using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using RewiredJoystick = Rewired.Joystick;

namespace NOVR.VrUi.Menu;

public enum MenuCommand
{
    Up,
    Down,
    Left,
    Right,
    Select,
    Back,
    PreviousTab,
    NextTab
}

public enum MenuDevice
{
    Keyboard,
    Gamepad,
    Joystick
}

// The controls of NOVR's menus, kept apart from the game's flight bindings so they are few and plain: each command has a
// keyboard key, a gamepad button and a joystick button, each of which can be set from Menu Controls or in the config file
// ("Menu Controls" section, keyboard | gamepad | joystick). Keyboard and gamepad are read through Unity's Input System, as
// its control paths. Joysticks are read through Rewired, the way the game reads them, as the Input System misreads some
// sticks' hats (a VKB stick's hat Left came in as Up, an S-TECS throttle's hat pressed Up at rest). A joystick binding is
// "rewired/<button>/<joystick name>": the button as Rewired numbers that stick's buttons (hat directions are buttons there)
// on the stick of that name. They start empty. The gamepad's left stick also moves, and Space and the keypad's Enter also
// select. Only listened to while one of NOVR's menu screens is up. The directions repeat while held after a press, like a
// held arrow key in a text box.
// A control that keeps pressing itself is ignored for the session: when it presses twice before the hand can have got to
// it while listening for a binding, or many times a second on its own, and a joystick binding to it is dropped.
internal static class MenuInput
{
    public const int SlotCount = 3;
    public const string Section = "Menu Controls";

    private const string RewiredPrefix = "rewired/";
    private const float RepeatDelaySeconds = 0.4f;
    private const float RepeatIntervalSeconds = 0.11f;
    private const float RebindMagnitude = 0.6f;
    // Time for the hand to get from the binding box to the stick; joystick buttons pressing before that are left alone.
    private const float RebindSettleSeconds = 0.5f;
    private const int NoisyPresses = 8;
    private const float NoisyWindowSeconds = 1f;

    public static readonly MenuCommand[] Commands = (MenuCommand[])Enum.GetValues(typeof(MenuCommand));
    private static readonly string[] SlotDevices = { "<Keyboard>", "<Gamepad>", "" };

    private static readonly InputAction?[] Actions = new InputAction?[Commands.Length];
    // Each command's joystick binding, read from its path when the map is built; BoundButton is -1 for none.
    private static readonly int[] BoundButton = new int[Commands.Length];
    private static readonly string[] BoundJoystick = new string[Commands.Length];
    private static readonly string[] BoundPath = new string[Commands.Length];
    private static readonly bool[] Fired = new bool[Commands.Length];
    private static readonly bool[] Held = new bool[Commands.Length];
    private static readonly float[] NextRepeat = new float[Commands.Length];
    private static InputActionMap? _map;
    private static InputActionRebindingExtensions.RebindingOperation? _rebind;
    // A joystick binding being listened for, which Update polls Rewired for.
    private static Action<bool>? _joystickListen;
    private static MenuCommand _joystickListenCommand;
    private static bool _dirty = true;
    private static bool _subscribed;
    private static bool _loggedJoysticks;
    private static int _lastFrame = -1;
    private static float _rebindStarted;
    // Ignored controls by key (an Input System control path, or a joystick binding's path), with their names to show.
    private static readonly Dictionary<string, string> Noisy = new();
    private static readonly Dictionary<string, List<float>> RecentPresses = new();
    private static readonly Dictionary<string, int> SettlePresses = new();

    // Rewired's reads of a single controller's buttons, which the game doesn't call itself (so they aren't among the
    // references NOVR is built against); looked up by name once. Null when this Rewired lacks them: no joystick column then.
    private static Func<RewiredJoystick, int, bool>? _buttonDown;
    private static Func<RewiredJoystick, int, bool>? _button;
    private static Func<RewiredJoystick, int>? _buttonCount;
    private static PropertyInfo? _buttons;
    private static bool _lookedUpRewired;
    private static bool _stoppedJoysticks;

    public static MenuDevice LastDevice { get; private set; } = MenuDevice.Keyboard;
    public static bool IsRebinding => _rebind != null || _joystickListen != null;

    public static string Name(MenuCommand command) => command switch
    {
        MenuCommand.PreviousTab => "Previous Tab",
        MenuCommand.NextTab => "Next Tab",
        _ => command.ToString()
    };

    public static string DefaultBindings(MenuCommand command) => command switch
    {
        MenuCommand.Up => "<Keyboard>/upArrow | <Gamepad>/dpad/up |",
        MenuCommand.Down => "<Keyboard>/downArrow | <Gamepad>/dpad/down |",
        MenuCommand.Left => "<Keyboard>/leftArrow | <Gamepad>/dpad/left |",
        MenuCommand.Right => "<Keyboard>/rightArrow | <Gamepad>/dpad/right |",
        MenuCommand.Select => "<Keyboard>/enter | <Gamepad>/buttonSouth |",
        MenuCommand.Back => "<Keyboard>/escape | <Gamepad>/buttonEast |",
        MenuCommand.PreviousTab => "<Keyboard>/q | <Gamepad>/leftShoulder |",
        MenuCommand.NextTab => "<Keyboard>/e | <Gamepad>/rightShoulder |",
        _ => "| |"
    };

    // Before 0.4.26 the joystick part was an Input System path (the defaults of 0.4.21 to 0.4.23 held generic ones);
    // those are dropped, to be bound again through Rewired.
    public static void Migrate(ConfigEntry<string> entry)
    {
        var slots = ReadSlots(entry.Value);
        var joystick = slots[(int)MenuDevice.Joystick];
        if (joystick.Length == 0 || joystick.StartsWith(RewiredPrefix, StringComparison.Ordinal)) return;

        slots[(int)MenuDevice.Joystick] = "";
        entry.Value = JoinSlots(slots);
    }

    private static string[] Extras(MenuCommand command) => command switch
    {
        MenuCommand.Up => new[] { "<Gamepad>/leftStick/up" },
        MenuCommand.Down => new[] { "<Gamepad>/leftStick/down" },
        MenuCommand.Left => new[] { "<Gamepad>/leftStick/left" },
        MenuCommand.Right => new[] { "<Gamepad>/leftStick/right" },
        MenuCommand.Select => new[] { "<Keyboard>/space", "<Keyboard>/numpadEnter" },
        MenuCommand.Back => new[] { "<Keyboard>/backspace" },
        _ => Array.Empty<string>()
    };

    private static bool Repeats(MenuCommand command) => command <= MenuCommand.Right;

    // Called every frame one of NOVR's menu screens is up; Pressed reads what this left.
    public static void Update()
    {
        if (Time.frameCount == _lastFrame) return;
        _lastFrame = Time.frameCount;

        try
        {
            EnsureMap();
            var now = Time.unscaledTime;
            if (IsRebinding)
            {
                Array.Clear(Fired, 0, Fired.Length);
                if (_joystickListen != null) ListenForJoystick(now);
                return;
            }

            if (!_map!.enabled) _map.Enable();
            var joysticks = Joysticks();
            for (var index = 0; index < Commands.Length; index++)
            {
                var action = Actions[index]!;
                var stick = BoundButton[index] >= 0 ? FindJoystick(joysticks, BoundJoystick[index]) : null;
                Fired[index] = false;

                MenuDevice? pressedOn = null;
                if (action.WasPressedThisFrame())
                {
                    var control = action.activeControl;
                    if (control == null || !IsNoisy(control, now)) pressedOn = DeviceOf(control?.device);
                }

                if (pressedOn == null && stick != null && StickButton(stick, BoundButton[index], down: true) &&
                    !IsNoisy(BoundPath[index], DescribeJoystickButton(stick, BoundButton[index]), now))
                {
                    pressedOn = MenuDevice.Joystick;
                }

                if (pressedOn is { } device)
                {
                    Fired[index] = true;
                    Held[index] = true;
                    NextRepeat[index] = now + RepeatDelaySeconds;
                    LastDevice = device;
                }
                else if (!action.IsPressed() && (stick == null || !StickButton(stick, BoundButton[index], down: false)))
                {
                    Held[index] = false;
                }
                // Only a press seen here repeats; a control already down when the menu opened doesn't.
                else if (Held[index] && Repeats(Commands[index]) && now >= NextRepeat[index])
                {
                    Fired[index] = true;
                    NextRepeat[index] = now + RepeatIntervalSeconds;
                }
            }
        }
        catch (Exception exception)
        {
            Array.Clear(Fired, 0, Fired.Length);
            if (_map == null) return;

            NOVRPlugin.Log.LogWarning($"[NOVR] Menu controls stopped: {exception.Message}");
            _map = null;
        }
    }

    // No NOVR menu screen is up.
    public static void Release()
    {
        CancelRebind();
        if (_map != null && _map.enabled) _map.Disable();
        Array.Clear(Fired, 0, Fired.Length);
        Array.Clear(Held, 0, Held.Length);
    }

    public static bool Pressed(MenuCommand command) => Fired[(int)command];

    public static string GetPath(MenuCommand command, MenuDevice device) =>
        ReadSlots(ConfigFor(command)?.Value ?? DefaultBindings(command))[(int)device];

    // How the binding reads on screen, e.g. "Up Arrow", "A", "Hat 1 Left [VKBsim Gladiator EVO R]"; empty when there is
    // none. Joystick ones name their stick (withDevice), as there can be several.
    public static string GetDisplay(MenuCommand command, MenuDevice device, bool withDevice = true)
    {
        var path = GetPath(command, device);
        if (path.Length == 0) return "";

        if (device == MenuDevice.Joystick)
        {
            if (!ReadJoystickPath(path, out var button, out var name)) return path;

            var stick = FindJoystick(Joysticks(), name);
            var buttonName = stick != null ? ButtonName(stick, button) : $"Button {button + 1}";
            return withDevice ? $"{buttonName} [{name}]" : buttonName;
        }

        try
        {
            var options = InputControlPath.HumanReadableStringOptions.UseShortNames | InputControlPath.HumanReadableStringOptions.OmitDevice;
            var display = InputControlPath.ToHumanReadableString(path, options);
            return string.IsNullOrEmpty(display) ? path : display;
        }
        catch (Exception)
        {
            return path;
        }
    }

    // For the prompt bar: the binding on the device last used, else on the keyboard.
    public static string GetPrompt(MenuCommand command)
    {
        var display = GetDisplay(command, LastDevice, withDevice: false);
        return display.Length > 0 ? display : GetDisplay(command, MenuDevice.Keyboard);
    }

    public static void SetPath(MenuCommand command, MenuDevice device, string path)
    {
        var entry = ConfigFor(command);
        if (entry == null) return;

        var slots = ReadSlots(entry.Value);
        slots[(int)device] = path.Trim();
        entry.Value = JoinSlots(slots);
        _dirty = true;
    }

    public static void ResetAll()
    {
        foreach (var command in Commands)
        {
            var entry = ConfigFor(command);
            if (entry != null) entry.Value = DefaultBindings(command);
        }

        _dirty = true;
    }

    // Waits for a key or button on that kind of device and binds it; finished(true) once bound, false when given up
    // (Escape, or CancelRebind). Axes that aren't buttons (throttles, twist) are left out, as they would trigger at rest;
    // a key already held when it starts, like the Enter that started it, counts once it is let go and pressed again.
    public static void Rebind(MenuCommand command, MenuDevice device, Action<bool> finished)
    {
        if (IsRebinding) return;

        if (device == MenuDevice.Joystick)
        {
            if (Joysticks() == null)
            {
                finished(false);
                return;
            }

            LogJoysticksOnce();
            _joystickListenCommand = command;
            _rebindStarted = Time.unscaledTime;
            SettlePresses.Clear();
            _joystickListen = finished;
            return;
        }

        try
        {
            EnsureMap();
            var action = Actions[(int)command]!;
            _map!.Disable();
            _rebind = action.PerformInteractiveRebinding((int)device)
                .WithExpectedControlType("Button")
                .WithControlsHavingToMatchPath(SlotDevices[(int)device])
                .WithControlsExcluding("<Keyboard>/anyKey")
                // Delete clears the binding instead (MenuControlsScreen).
                .WithControlsExcluding("<Keyboard>/delete")
                .WithControlsExcluding("<Mouse>")
                .WithCancelingThrough("<Keyboard>/escape")
                .WithMagnitudeHavingToBeGreaterThan(RebindMagnitude)
                .OnPotentialMatch(Choose)
                .OnComplete(completed =>
                {
                    var path = completed.action.bindings[(int)device].effectivePath ?? "";
                    EndRebind();
                    SetPath(command, device, path);
                    NOVRPlugin.Log.LogInfo($"[NOVR] Menu controls: {Name(command)} on {device} is now {path}");
                    finished(true);
                })
                .OnCancel(_ =>
                {
                    EndRebind();
                    finished(false);
                });
            _rebind.Start();
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not listen for a menu binding: {exception.Message}");
            EndRebind();
            finished(false);
        }
    }

    public static void CancelRebind()
    {
        _rebind?.Cancel();
        if (_joystickListen != null) EndJoystickListen(false);
    }

    // Takes the first joystick button to go down that isn't noise. One pressing during the settle time is left alone;
    // twice then, it is a control pressing by itself.
    private static void ListenForJoystick(float now)
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            EndJoystickListen(false);
            return;
        }

        var joysticks = Joysticks();
        if (joysticks == null) return;

        var settling = now - _rebindStarted < RebindSettleSeconds;
        foreach (var stick in joysticks)
        {
            if (stick == null) continue;

            var count = ButtonCount(stick);
            for (var button = 0; button < count; button++)
            {
                if (!StickButton(stick, button, down: true)) continue;

                var path = JoystickPath(button, CleanName(stick.name));
                if (Noisy.ContainsKey(path)) continue;

                if (settling)
                {
                    SettlePresses.TryGetValue(path, out var presses);
                    SettlePresses[path] = ++presses;
                    if (presses >= 2) MarkNoisy(path, DescribeJoystickButton(stick, button), "pressed by itself while listening for a binding");
                    continue;
                }

                var command = _joystickListenCommand;
                SetPath(command, MenuDevice.Joystick, path);
                NOVRPlugin.Log.LogInfo($"[NOVR] Menu controls: {Name(command)} on Joystick is now {DescribeJoystickButton(stick, button)} ({path})");
                EndJoystickListen(true);
                return;
            }
        }
    }

    private static void EndJoystickListen(bool bound)
    {
        var finished = _joystickListen;
        _joystickListen = null;
        SettlePresses.Clear();
        _dirty = true;
        finished?.Invoke(bound);
    }

    // Takes the best candidate that isn't noise.
    private static void Choose(InputActionRebindingExtensions.RebindingOperation operation)
    {
        foreach (var control in new List<InputControl>(operation.candidates))
        {
            if (Noisy.ContainsKey(control.path)) operation.RemoveCandidate(control);
        }

        if (operation.candidates.Count > 0) operation.Complete();
    }

    private static bool IsNoisy(InputControl control, float now) =>
        control.device is not (Keyboard or Mouse) && IsNoisy(control.path, $"{control.device.displayName.Trim()} {control.displayName}", now);

    // A control pressing many times a second with nothing done is noise.
    private static bool IsNoisy(string key, string name, float now)
    {
        if (Noisy.ContainsKey(key)) return true;

        if (!RecentPresses.TryGetValue(key, out var presses)) RecentPresses[key] = presses = new List<float>();
        presses.Add(now);
        presses.RemoveAll(time => now - time > NoisyWindowSeconds);
        if (presses.Count < NoisyPresses) return false;

        MarkNoisy(key, name, $"pressed {presses.Count} times in a second by itself");
        return true;
    }

    // Ignored from now on, and any joystick binding to it dropped.
    private static void MarkNoisy(string key, string name, string why)
    {
        if (Noisy.ContainsKey(key)) return;

        Noisy[key] = name;
        NOVRPlugin.Log.LogInfo($"[NOVR] Menu controls: ignoring {name}, {why}.");
        foreach (var command in Commands)
        {
            if (GetPath(command, MenuDevice.Joystick) == key) SetPath(command, MenuDevice.Joystick, "");
        }
    }

    // For Menu Controls: what is being ignored.
    public static string NoisyNames() => string.Join(", ", Noisy.Values);

    // For Menu Controls: the joysticks the game has, which are the ones that can be bound.
    public static string JoystickNames()
    {
        var joysticks = Joysticks();
        if (joysticks == null) return "";

        var names = new StringBuilder();
        foreach (var stick in joysticks)
        {
            if (stick == null) continue;

            if (names.Length > 0) names.Append(", ");
            names.Append(CleanName(stick.name));
        }

        return names.ToString();
    }

    private static void EndRebind()
    {
        var operation = _rebind;
        _rebind = null;
        operation?.Dispose();
        _dirty = true;
    }

    private static MenuDevice DeviceOf(InputDevice? device) => device switch
    {
        Gamepad => MenuDevice.Gamepad,
        Joystick => MenuDevice.Joystick,
        _ => MenuDevice.Keyboard
    };

    // The joysticks the game's player has, which are the ones it flies with; null when they can't be read.
    private static IList<RewiredJoystick>? Joysticks()
    {
        if (!LookUpRewired()) return null;

        try
        {
            var player = GameManager.playerInput;
            return Rewired.ReInput.isReady && player != null ? player.controllers?.Joysticks : null;
        }
        catch (Exception exception)
        {
            StopJoysticks(exception.Message);
            return null;
        }
    }

    private static RewiredJoystick? FindJoystick(IList<RewiredJoystick>? joysticks, string name)
    {
        if (joysticks == null) return null;

        foreach (var stick in joysticks)
        {
            if (stick != null && CleanName(stick.name) == name) return stick;
        }

        return null;
    }

    private static bool LookUpRewired()
    {
        if (_lookedUpRewired) return _buttonDown != null;

        _lookedUpRewired = true;
        try
        {
            var type = typeof(RewiredJoystick);
            _buttonDown = Bind<Func<RewiredJoystick, int, bool>>(type.GetMethod("GetButtonDown", new[] { typeof(int) }));
            _button = Bind<Func<RewiredJoystick, int, bool>>(type.GetMethod("GetButton", new[] { typeof(int) }));
            _buttonCount = Bind<Func<RewiredJoystick, int>>(type.GetProperty("buttonCount")?.GetGetMethod());
            _buttons = type.GetProperty("Buttons");
        }
        catch (Exception exception)
        {
            StopJoysticks(exception.Message);
            return false;
        }

        if (_buttonDown == null || _button == null || _buttonCount == null) StopJoysticks("Rewired has no per-button reads");
        return _buttonDown != null;
    }

    private static T? Bind<T>(MethodInfo? method) where T : Delegate =>
        method == null ? null : Delegate.CreateDelegate(typeof(T), method, false) as T;

    // For the session, with one line in the log.
    private static void StopJoysticks(string why)
    {
        _buttonDown = null;
        _button = null;
        _lookedUpRewired = true;
        if (_stoppedJoysticks) return;

        _stoppedJoysticks = true;
        NOVRPlugin.Log.LogWarning($"[NOVR] Menu controls: joysticks can't be read ({why}); keyboard and gamepad only.");
    }

    private static int ButtonCount(RewiredJoystick stick)
    {
        try
        {
            return _buttonCount?.Invoke(stick) ?? 0;
        }
        catch (Exception exception)
        {
            StopJoysticks(exception.Message);
            return 0;
        }
    }

    private static bool StickButton(RewiredJoystick stick, int button, bool down)
    {
        var read = down ? _buttonDown : _button;
        if (read == null || button >= ButtonCount(stick)) return false;

        try
        {
            return read(stick, button);
        }
        catch (Exception exception)
        {
            StopJoysticks(exception.Message);
            return false;
        }
    }

    // Rewired's name for the button, like "Button 3" or "Hat 1 Left".
    private static string ButtonName(RewiredJoystick stick, int button)
    {
        try
        {
            if (_buttons?.GetValue(stick, null) is IEnumerable buttons)
            {
                var index = 0;
                foreach (var element in buttons)
                {
                    if (index++ != button) continue;

                    if (element?.GetType().GetProperty("name")?.GetValue(element, null) is string { Length: > 0 } name) return name.Trim();
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Named by its number instead.
        }

        return $"Button {button + 1}";
    }

    private static string DescribeJoystickButton(RewiredJoystick stick, int button) => $"{CleanName(stick.name)} {ButtonName(stick, button)}";

    // Without the spaces some sticks pad their names with, or the config file's separator.
    private static string CleanName(string? name) => (name ?? "").Replace('|', ' ').Trim();

    private static string JoystickPath(int button, string joystick) => $"{RewiredPrefix}{button.ToString(CultureInfo.InvariantCulture)}/{joystick}";

    private static bool ReadJoystickPath(string path, out int button, out string joystick)
    {
        button = -1;
        joystick = "";
        if (!path.StartsWith(RewiredPrefix, StringComparison.Ordinal)) return false;

        var rest = path.Substring(RewiredPrefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0 || !int.TryParse(rest.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out button)) return false;

        joystick = rest.Substring(slash + 1).Trim();
        return joystick.Length > 0;
    }

    // Joysticks the player has, once, when a joystick binding is first listened for.
    private static void LogJoysticksOnce()
    {
        if (_loggedJoysticks) return;
        _loggedJoysticks = true;

        var names = new StringBuilder();
        foreach (var stick in Joysticks() ?? Array.Empty<RewiredJoystick>())
        {
            if (stick == null) continue;

            if (names.Length > 0) names.Append(", ");
            names.Append($"{CleanName(stick.name)} ({ButtonCount(stick)} buttons)");
        }

        NOVRPlugin.Log.LogInfo(names.Length > 0
            ? $"[NOVR] Menu controls: joysticks: {names}"
            : "[NOVR] Menu controls: the game has no joystick.");
    }

    private static ConfigEntry<string>? ConfigFor(MenuCommand command)
    {
        var bindings = ModConfiguration.Instance?.MenuBindings;
        return bindings != null && bindings.TryGetValue(command, out var entry) ? entry : null;
    }

    private static string[] ReadSlots(string value)
    {
        var slots = new string[SlotCount];
        var parts = value.Split('|');
        for (var index = 0; index < SlotCount; index++)
        {
            slots[index] = index < parts.Length ? parts[index].Trim() : "";
        }

        return slots;
    }

    // Without trailing spaces, which the config file drops.
    private static string JoinSlots(string[] slots) => string.Join(" | ", slots).TrimEnd();

    private static void EnsureMap()
    {
        if (!_subscribed && ModConfiguration.Instance?.MenuBindings is { } entries)
        {
            _subscribed = true;
            foreach (var entry in entries.Values) entry.SettingChanged += (_, _) => _dirty = true;
        }

        // Not under a binding being listened for; it is rebuilt once that ends.
        if (_map != null && (!_dirty || IsRebinding)) return;

        var enabled = _map?.enabled == true;
        _map?.Disable();
        _map?.Dispose();
        _dirty = false;

        _map = new InputActionMap("NOVR Menu");
        for (var index = 0; index < Commands.Length; index++)
        {
            var command = Commands[index];
            var action = _map.AddAction(Name(command), InputActionType.Button);
            // The three slots come first, so a slot is also its binding index; the joystick one is read from Rewired.
            action.AddBinding(GetPath(command, MenuDevice.Keyboard));
            action.AddBinding(GetPath(command, MenuDevice.Gamepad));
            action.AddBinding("");
            foreach (var extra in Extras(command)) action.AddBinding(extra);
            Actions[index] = action;

            BoundPath[index] = GetPath(command, MenuDevice.Joystick);
            BoundButton[index] = ReadJoystickPath(BoundPath[index], out var button, out var joystick) ? button : -1;
            BoundJoystick[index] = joystick;
        }

        if (enabled) _map.Enable();
    }
}
