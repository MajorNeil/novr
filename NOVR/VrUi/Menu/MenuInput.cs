using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

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
// ("Menu Controls" section, keyboard | gamepad | joystick as Input System paths). The joystick ones start empty and are
// bound to the one device pressed: a generic "any joystick's hat" matched every stick, throttle and rudder plugged in,
// and one whose hat rests off centre held Up down. The gamepad's left stick also moves, and Space and the keypad's Enter
// also select. Only listened to while one of NOVR's menu screens is up. The directions repeat while held after a press,
// like a held arrow key in a text box.
// Some sticks have a control that keeps pressing itself (a throttle hat that flickers to Up at rest). Such a control is
// ignored for the session: when it presses within the first moment of listening for a binding, or many times a second
// on its own, and a binding to it is dropped.
internal static class MenuInput
{
    public const int SlotCount = 3;
    public const string Section = "Menu Controls";

    private const float RepeatDelaySeconds = 0.4f;
    private const float RepeatIntervalSeconds = 0.11f;
    private const float RebindMagnitude = 0.6f;
    // Time for the hand to get from the binding box to the stick; joystick controls pressing before that are noise.
    private const float RebindSettleSeconds = 0.5f;
    private const int NoisyPresses = 8;
    private const float NoisyWindowSeconds = 1f;

    public static readonly MenuCommand[] Commands = (MenuCommand[])Enum.GetValues(typeof(MenuCommand));
    private static readonly string[] SlotDevices = { "<Keyboard>", "<Gamepad>", "<Joystick>" };

    private static readonly InputAction?[] Actions = new InputAction?[Commands.Length];
    private static readonly bool[] Fired = new bool[Commands.Length];
    private static readonly bool[] Held = new bool[Commands.Length];
    private static readonly float[] NextRepeat = new float[Commands.Length];
    private static InputActionMap? _map;
    private static InputActionRebindingExtensions.RebindingOperation? _rebind;
    private static bool _dirty = true;
    private static bool _subscribed;
    private static bool _loggedDevices;
    private static int _lastFrame = -1;
    private static float _rebindStarted;
    private static readonly HashSet<InputControl> Noisy = new();
    private static readonly Dictionary<InputControl, List<float>> RecentPresses = new();

    public static MenuDevice LastDevice { get; private set; } = MenuDevice.Keyboard;
    public static bool IsRebinding => _rebind != null;

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

    // The defaults of 0.4.21 to 0.4.23, whose generic joystick part a saved value still holding them drops (Migrate).
    private static string? OldDefault(MenuCommand command) => command switch
    {
        MenuCommand.Up => "<Keyboard>/upArrow | <Gamepad>/dpad/up | <Joystick>/hat/up",
        MenuCommand.Down => "<Keyboard>/downArrow | <Gamepad>/dpad/down | <Joystick>/hat/down",
        MenuCommand.Left => "<Keyboard>/leftArrow | <Gamepad>/dpad/left | <Joystick>/hat/left",
        MenuCommand.Right => "<Keyboard>/rightArrow | <Gamepad>/dpad/right | <Joystick>/hat/right",
        MenuCommand.Select => "<Keyboard>/enter | <Gamepad>/buttonSouth | <Joystick>/trigger",
        _ => null
    };

    public static void Migrate(ConfigEntry<string> entry, MenuCommand command)
    {
        var old = OldDefault(command);
        if (old == null) return;

        var saved = ReadSlots(entry.Value);
        var oldSlots = ReadSlots(old);
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if (saved[slot] != oldSlots[slot]) return;
        }

        entry.Value = DefaultBindings(command);
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
            if (IsRebinding)
            {
                Array.Clear(Fired, 0, Fired.Length);
                return;
            }

            if (!_map!.enabled) _map.Enable();
            var now = Time.unscaledTime;
            for (var index = 0; index < Commands.Length; index++)
            {
                var action = Actions[index]!;
                Fired[index] = false;
                if (action.WasPressedThisFrame())
                {
                    if (IsNoisy(action.activeControl, now)) continue;

                    Fired[index] = true;
                    Held[index] = true;
                    NextRepeat[index] = now + RepeatDelaySeconds;
                    NoteDevice(action.activeControl?.device);
                }
                else if (!action.IsPressed())
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
        _rebind?.Cancel();
        if (_map != null && _map.enabled) _map.Disable();
        Array.Clear(Fired, 0, Fired.Length);
        Array.Clear(Held, 0, Held.Length);
    }

    public static bool Pressed(MenuCommand command) => Fired[(int)command];

    public static string GetPath(MenuCommand command, MenuDevice device) =>
        ReadSlots(ConfigFor(command)?.Value ?? DefaultBindings(command))[(int)device];

    // How the binding reads on screen, e.g. "Up Arrow", "A", "Hat/Up [VKBsim Gladiator EVO R]"; empty when there is none.
    // Joystick ones name their device, as there can be several.
    public static string GetDisplay(MenuCommand command, MenuDevice device)
    {
        var path = GetPath(command, device);
        if (path.Length == 0) return "";

        try
        {
            var options = InputControlPath.HumanReadableStringOptions.UseShortNames;
            if (device != MenuDevice.Joystick) options |= InputControlPath.HumanReadableStringOptions.OmitDevice;
            var display = InputControlPath.ToHumanReadableString(path, options);
            if (string.IsNullOrEmpty(display)) return path;

            while (display.Contains("  ")) display = display.Replace("  ", " ");
            return display.Replace(" ]", "]").Replace("[ ", "[");
        }
        catch (Exception)
        {
            return path;
        }
    }

    // For the prompt bar: the binding on the device last used, else on the keyboard.
    public static string GetPrompt(MenuCommand command)
    {
        var display = GetDisplay(command, LastDevice);
        return display.Length > 0 ? display : GetDisplay(command, MenuDevice.Keyboard);
    }

    public static void SetPath(MenuCommand command, MenuDevice device, string path)
    {
        var entry = ConfigFor(command);
        if (entry == null) return;

        var slots = ReadSlots(entry.Value);
        slots[(int)device] = path.Trim();
        // Without trailing spaces, which the config file drops.
        entry.Value = string.Join(" | ", slots).TrimEnd();
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

        try
        {
            EnsureMap();
            var action = Actions[(int)command]!;
            _map!.Disable();
            var operation = action.PerformInteractiveRebinding((int)device)
                .WithExpectedControlType("Button")
                .WithControlsHavingToMatchPath(SlotDevices[(int)device])
                .WithControlsExcluding("<Keyboard>/anyKey")
                // Delete clears the binding instead (MenuControlsScreen).
                .WithControlsExcluding("<Keyboard>/delete")
                .WithControlsExcluding("<Mouse>")
                .WithCancelingThrough("<Keyboard>/escape")
                .WithMagnitudeHavingToBeGreaterThan(RebindMagnitude)
                .OnPotentialMatch(candidate => Choose(candidate, device))
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
            // Kept to the device pressed, instead of widened to "any joystick".
            if (device == MenuDevice.Joystick) operation.WithoutGeneralizingPathOfSelectedControl();
            _rebindStarted = Time.realtimeSinceStartup;
            _rebind = operation;
            _rebind.Start();
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not listen for a menu binding: {exception.Message}");
            EndRebind();
            finished(false);
        }
    }

    public static void CancelRebind() => _rebind?.Cancel();

    // Takes the best candidate that isn't noise; a joystick control pressing during the settle time is noise.
    private static void Choose(InputActionRebindingExtensions.RebindingOperation operation, MenuDevice device)
    {
        var settling = device == MenuDevice.Joystick && Time.realtimeSinceStartup - _rebindStarted < RebindSettleSeconds;
        foreach (var control in new List<InputControl>(operation.candidates))
        {
            if (settling) MarkNoisy(control, "pressed by itself while listening for a binding");
            if (Noisy.Contains(control)) operation.RemoveCandidate(control);
        }

        if (operation.candidates.Count > 0) operation.Complete();
    }

    // A control pressing many times a second with nothing done is noise.
    private static bool IsNoisy(InputControl? control, float now)
    {
        if (control == null) return false;
        if (Noisy.Contains(control)) return true;
        if (control.device is Keyboard or Mouse) return false;

        if (!RecentPresses.TryGetValue(control, out var presses)) RecentPresses[control] = presses = new List<float>();
        presses.Add(now);
        presses.RemoveAll(time => now - time > NoisyWindowSeconds);
        if (presses.Count < NoisyPresses) return false;

        MarkNoisy(control, $"pressed {presses.Count} times in a second by itself");
        return true;
    }

    // Ignored from now on, and any joystick binding to it dropped.
    private static void MarkNoisy(InputControl control, string why)
    {
        if (!Noisy.Add(control)) return;

        NOVRPlugin.Log.LogInfo($"[NOVR] Menu controls: ignoring {control.path}, {why}.");
        foreach (var command in Commands)
        {
            var path = GetPath(command, MenuDevice.Joystick);
            try
            {
                if (path.Length > 0 && InputControlPath.Matches(path, control)) SetPath(command, MenuDevice.Joystick, "");
            }
            catch (Exception)
            {
                // A path Unity can't read binds nothing anyway.
            }
        }
    }

    // For Menu Controls: what is being ignored.
    public static string NoisyNames()
    {
        var names = new StringBuilder();
        foreach (var control in Noisy)
        {
            if (names.Length > 0) names.Append(", ");
            names.Append($"{control.device.displayName.Trim()} {control.displayName}");
        }

        return names.ToString();
    }

    public static string JoystickNames()
    {
        var names = new StringBuilder();
        foreach (var device in InputSystem.devices)
        {
            if (device is not Joystick) continue;

            if (names.Length > 0) names.Append(", ");
            names.Append(device.displayName);
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

    private static void NoteDevice(InputDevice? device)
    {
        LastDevice = device switch
        {
            Gamepad => MenuDevice.Gamepad,
            Joystick => MenuDevice.Joystick,
            _ => MenuDevice.Keyboard
        };
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

    private static void EnsureMap()
    {
        if (!_subscribed && ModConfiguration.Instance?.MenuBindings is { } entries)
        {
            _subscribed = true;
            foreach (var entry in entries.Values) entry.SettingChanged += (_, _) => _dirty = true;
        }

        // Not under a binding being listened for; it is rebuilt once that ends.
        if (_map != null && (!_dirty || IsRebinding)) return;

        LogDevicesOnce();
        var enabled = _map?.enabled == true;
        _map?.Disable();
        _map?.Dispose();
        _dirty = false;

        _map = new InputActionMap("NOVR Menu");
        for (var index = 0; index < Commands.Length; index++)
        {
            var command = Commands[index];
            var action = _map.AddAction(Name(command), InputActionType.Button);
            // The three settable bindings come first, so a slot is also its binding index.
            for (var slot = 0; slot < SlotCount; slot++) action.AddBinding(GetPath(command, (MenuDevice)slot));
            foreach (var extra in Extras(command)) action.AddBinding(extra);
            Actions[index] = action;
        }

        if (enabled) _map.Enable();
    }

    // Whether a HOTAS shows up to Unity's Input System at all decides whether its buttons can be bound here.
    private static void LogDevicesOnce()
    {
        if (_loggedDevices) return;
        _loggedDevices = true;

        var joysticks = JoystickNames();
        NOVRPlugin.Log.LogInfo(joysticks.Length > 0
            ? $"[NOVR] Menu controls: joysticks seen: {joysticks}"
            : "[NOVR] Menu controls: no joystick seen by Unity's Input System; keyboard and gamepad only.");
    }
}
