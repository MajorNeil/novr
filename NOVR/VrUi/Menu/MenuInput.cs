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
// keyboard key, a gamepad button and a joystick button (a HOTAS hat and trigger to start with), each of which can be set
// from Menu Controls or in the config file ("Menu Controls" section, keyboard | gamepad | joystick as Input System paths).
// The gamepad's left stick also moves, and Space and the keypad's Enter also select. Only listened to while one of NOVR's
// menu screens is up. The directions repeat while held, like a held arrow key in a text box.
internal static class MenuInput
{
    public const int SlotCount = 3;
    public const string Section = "Menu Controls";

    private const float RepeatDelaySeconds = 0.4f;
    private const float RepeatIntervalSeconds = 0.11f;
    private const float RebindMagnitude = 0.6f;

    public static readonly MenuCommand[] Commands = (MenuCommand[])Enum.GetValues(typeof(MenuCommand));
    private static readonly string[] SlotDevices = { "<Keyboard>", "<Gamepad>", "<Joystick>" };

    private static readonly InputAction?[] Actions = new InputAction?[Commands.Length];
    private static readonly bool[] Fired = new bool[Commands.Length];
    private static readonly float[] NextRepeat = new float[Commands.Length];
    private static InputActionMap? _map;
    private static InputActionRebindingExtensions.RebindingOperation? _rebind;
    private static bool _dirty = true;
    private static bool _subscribed;
    private static bool _loggedDevices;
    private static int _lastFrame = -1;

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
        MenuCommand.Up => "<Keyboard>/upArrow | <Gamepad>/dpad/up | <Joystick>/hat/up",
        MenuCommand.Down => "<Keyboard>/downArrow | <Gamepad>/dpad/down | <Joystick>/hat/down",
        MenuCommand.Left => "<Keyboard>/leftArrow | <Gamepad>/dpad/left | <Joystick>/hat/left",
        MenuCommand.Right => "<Keyboard>/rightArrow | <Gamepad>/dpad/right | <Joystick>/hat/right",
        MenuCommand.Select => "<Keyboard>/enter | <Gamepad>/buttonSouth | <Joystick>/trigger",
        MenuCommand.Back => "<Keyboard>/escape | <Gamepad>/buttonEast |",
        MenuCommand.PreviousTab => "<Keyboard>/q | <Gamepad>/leftShoulder |",
        MenuCommand.NextTab => "<Keyboard>/e | <Gamepad>/rightShoulder |",
        _ => "| |"
    };

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
                    Fired[index] = true;
                    NextRepeat[index] = now + RepeatDelaySeconds;
                    NoteDevice(action.activeControl?.device);
                }
                else if (Repeats(Commands[index]) && action.IsPressed() && now >= NextRepeat[index])
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
    }

    public static bool Pressed(MenuCommand command) => Fired[(int)command];

    public static string GetPath(MenuCommand command, MenuDevice device) =>
        ReadSlots(ConfigFor(command)?.Value ?? DefaultBindings(command))[(int)device];

    // How the binding reads on screen, e.g. "Up Arrow", "A", "Trigger"; empty when there is none.
    public static string GetDisplay(MenuCommand command, MenuDevice device)
    {
        var path = GetPath(command, device);
        if (path.Length == 0) return "";

        try
        {
            var display = InputControlPath.ToHumanReadableString(path,
                InputControlPath.HumanReadableStringOptions.OmitDevice | InputControlPath.HumanReadableStringOptions.UseShortNames);
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
            _rebind = action.PerformInteractiveRebinding((int)device)
                .WithExpectedControlType("Button")
                .WithControlsHavingToMatchPath(SlotDevices[(int)device])
                .WithControlsExcluding("<Keyboard>/anyKey")
                // Delete clears the binding instead (MenuControlsScreen).
                .WithControlsExcluding("<Keyboard>/delete")
                .WithControlsExcluding("<Mouse>")
                .WithCancelingThrough("<Keyboard>/escape")
                .WithMagnitudeHavingToBeGreaterThan(RebindMagnitude)
                .OnMatchWaitForAnother(0.1f)
                .OnComplete(operation =>
                {
                    var path = operation.action.bindings[(int)device].effectivePath ?? "";
                    EndRebind();
                    SetPath(command, device, path);
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

    public static void CancelRebind() => _rebind?.Cancel();

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

        if (_map != null && !_dirty) return;

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
