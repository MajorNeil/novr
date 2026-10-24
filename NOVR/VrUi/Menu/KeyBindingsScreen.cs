using System;
using System.Collections.Generic;
using Rewired;
using TMPro;
using UnityEngine;
using Keyboard = UnityEngine.InputSystem.Keyboard;

namespace NOVR.VrUi.Menu;

// The game's key bindings, built and used like the settings (RailScreen) instead of the game's own binding screen: the
// keyboard, the mouse and each stick down the left-hand panel, and beside it a Category row and every action the game
// lets you bind, with what it is bound to on that device. Select on an action opens Set, Add Another, Clear and, for an
// axis, Invert. Setting one listens for the next key, button or axis on that device, as the game's own screen does
// (Rewired's input mapper); Escape gives up, and so does waiting eight seconds. A binding that another action had moves to
// this one. The changes are saved as the game saves them. The game's own screen is still there, for calibrating sticks
// and putting the defaults back.
// Which entry is which follows NativeSettingsPanel's binding list: one row per action, or per direction of an axis that
// has one name for each, the bindings in the device's maps that the game lets players change.
internal sealed class KeyBindingsScreen : RailScreen
{
    private const int ListRows = 10;
    private const int MaxJoysticks = 6;
    private const float CaptureDelaySeconds = 0.3f;
    private const float CaptureTimeoutSeconds = 8f;

    private sealed class Device
    {
        public Device(string label, string title, IList<ControllerMap> maps, bool keyboard, bool mouse)
        {
            Label = label;
            Title = title;
            Maps = maps;
            Keyboard = keyboard;
            Mouse = mouse;
        }

        public string Label { get; }
        public string Title { get; }
        public IList<ControllerMap> Maps { get; }
        public bool Keyboard { get; }
        public bool Mouse { get; }
    }

    private sealed class Slot
    {
        public Slot(InputAction action, AxisRange range, string name, string category)
        {
            Action = action;
            Range = range;
            Name = name;
            Category = category;
        }

        public InputAction Action { get; }
        public AxisRange Range { get; }
        public string Name { get; }
        public string Category { get; }
        public List<(ControllerMap Map, ActionElementMap Element)> Bindings { get; } = new();
        public bool HasAxis => Bindings.Exists(binding => binding.Element.elementType == ControllerElementType.Axis && binding.Element.axisRange == AxisRange.Full);
    }

    private readonly Action _close;
    private readonly Action _openGameScreen;
    private readonly List<MenuButton> _deviceButtons = new();
    private readonly List<Device> _devices = new();
    private readonly List<Slot> _slots = new();
    private readonly List<Slot> _shown = new();
    private readonly List<string> _categories = new();
    private readonly MenuButton _gameScreen;
    private readonly MenuButton _back;
    private readonly Section _section;
    private readonly MenuList _list;
    private int _device;
    private int _category;
    private InputMapper? _mapper;
    private Slot? _capturing;
    private bool _replace;
    private float _captureAt = float.PositiveInfinity;

    public KeyBindingsScreen(RectTransform canvas, Action close, Action openGameScreen) : base(canvas, "NOVR Key Bindings", "KEY BINDINGS")
    {
        _close = close;
        _openGameScreen = openGameScreen;
        for (var index = 0; index < MaxJoysticks + 2; index++)
        {
            var device = index;
            _deviceButtons.Add(AddRailButton("", () => ShowDevice(device)));
        }

        _gameScreen = AddRailButton("GAME'S BINDING SCREEN", OpenGameScreen, gapBefore: true);
        _back = AddRailButton("BACK", Leave, gapBefore: true, primary: true);

        _section = AddSection("KEYBOARD");
        AddSetting(_section, "CATEGORY", () => _categories.Count > 0 ? _categories[_category].ToUpperInvariant() : "ALL",
            direction =>
            {
                _category = _categories.Count == 0 ? 0 : (_category + direction + _categories.Count) % _categories.Count;
                ApplyCategory();
            }, description: "Only the actions of one part of the game: flying, weapons, the camera and so on.");
        _list = AddList(_section, ListRows, FillRow);
        ShowSection(_section);
    }

    protected override bool Capturing => _capturing != null;

    protected override void OnShown()
    {
        base.OnShown();
        LoadDevices();
        ShowDevice(Mathf.Clamp(_device, 0, Math.Max(0, _devices.Count - 1)));
        SetStatus(_devices.Count == 0 ? "The game's controls aren't loaded yet." : "");
    }

    protected override void OnHidden()
    {
        CancelCapture(null);
        base.OnHidden();
    }

    protected override void OnTick()
    {
        base.OnTick();
        if (_capturing == null) return;

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            CancelCapture("Left as it was.");
            return;
        }

        if (_mapper == null && Time.unscaledTime >= _captureAt) StartCapture();
    }

    protected override void OnTab(int direction)
    {
        if (PopupOpen || _devices.Count == 0) return;

        ShowDevice((_device + direction + _devices.Count) % _devices.Count);
        Focus(_deviceButtons[_device], sound: false);
        MenuSounds.Move();
    }

    protected override void Leave()
    {
        CancelCapture(null);
        _close();
    }

    protected override void Describe(Section section, MenuButton? row, int entry)
    {
        if (entry >= 0 && entry < _shown.Count)
        {
            var slot = _shown[entry];
            var bound = slot.Bindings.Count == 0 ? "NOT BOUND" : $"BOUND TO {string.Join(" / ", slot.Bindings.ConvertAll(binding => BindingName(binding.Element)))}";
            ShowDetails(slot.Name, slot.Category.ToUpperInvariant(), bound, "Select to set it, add another or clear it.");
            return;
        }

        if (row != null)
        {
            base.Describe(section, row, entry);
            return;
        }

        var device = _devices.Count > 0 ? _devices[_device] : null;
        ShowDetails(device?.Title ?? "KEY BINDINGS", $"{_shown.Count} ACTIONS", "",
            "Pick an action to change what it is bound to. The game's own binding screen calibrates sticks and puts the defaults back.");
    }

    protected override void Open(Section section, int entry)
    {
        if (entry < 0 || entry >= _shown.Count) return;

        var slot = _shown[entry];
        var actions = new List<(string, Func<bool>)>
        {
            (slot.Bindings.Count == 0 ? "SET" : "SET NEW", () => QueueCapture(slot, replace: true))
        };
        if (slot.Bindings.Count > 0)
        {
            actions.Add(("ADD ANOTHER", () => QueueCapture(slot, replace: false)));
            actions.Add(("CLEAR", () => Clear(slot)));
        }

        if (slot.HasAxis) actions.Add((IsInverted(slot) ? "INVERT AXIS: ON" : "INVERT AXIS: OFF", () => ToggleInvert(slot)));
        var bound = slot.Bindings.Count == 0 ? "Not bound." : $"Bound to {string.Join(" / ", slot.Bindings.ConvertAll(binding => BindingName(binding.Element)))}.";
        OpenPopup(slot.Name, $"{_devices[_device].Title}   {slot.Category.ToUpperInvariant()}", bound, picture: false, fieldLabel: null, actions.ToArray());
    }

    // ---- Devices and their actions.

    private void LoadDevices()
    {
        _devices.Clear();
        try
        {
            var player = GameManager.playerInput;
            if (ReInput.isReady && player != null)
            {
                _devices.Add(new Device("KEYBOARD", "KEYBOARD", Assignable(player.controllers.maps.GetMaps<KeyboardMap>(0)), keyboard: true, mouse: false));
                _devices.Add(new Device("MOUSE", "MOUSE", Assignable(player.controllers.maps.GetMaps<MouseMap>(0)), keyboard: false, mouse: true));
                foreach (var joystick in player.controllers.Joysticks)
                {
                    if (joystick == null || _devices.Count >= MaxJoysticks + 2) continue;

                    var name = string.IsNullOrWhiteSpace(joystick.name) ? $"STICK {_devices.Count - 1}" : joystick.name.Trim();
                    _devices.Add(new Device(name.ToUpperInvariant(), name.ToUpperInvariant(), Assignable(player.controllers.maps.GetMaps<JoystickMap>(joystick.id)),
                        keyboard: false, mouse: false));
                }
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not read the game's controls: {exception.Message}");
        }

        // The panel holds as many devices as there are, the rest moved up under them.
        var y = _deviceButtons[0].Rect.anchoredPosition.y;
        for (var index = 0; index < _deviceButtons.Count; index++)
        {
            var button = _deviceButtons[index];
            var shown = index < _devices.Count;
            button.gameObject.SetActive(shown);
            if (!shown) continue;

            button.SetLabel(_devices[index].Label);
            button.Rect.anchoredPosition = new Vector2(0f, y);
            y -= RailStep;
        }

        y -= RailGap;
        _gameScreen.Rect.anchoredPosition = new Vector2(0f, y);
        y -= RailStep + RailGap;
        _back.Rect.anchoredPosition = new Vector2(0f, y);
    }

    private static IList<ControllerMap> Assignable<TMap>(IEnumerable<TMap> maps) where TMap : ControllerMap
    {
        var assignable = new List<ControllerMap>();
        foreach (var map in maps)
        {
            if (map == null) continue;

            var category = ReInput.mapping.GetMapCategory(map.categoryId);
            if (category == null || category.userAssignable) assignable.Add(map);
        }

        return assignable;
    }

    private void ShowDevice(int device)
    {
        if (device < 0 || device >= _devices.Count)
        {
            _slots.Clear();
            ApplyCategory();
            return;
        }

        _device = device;
        CurrentRailButton = _deviceButtons[device];
        for (var index = 0; index < _deviceButtons.Count; index++) _deviceButtons[index].Highlight = index == device;
        _section.Title = _devices[device].Title;
        ShowSection(_section);
        LoadSlots();
    }

    // Every action the game lets players bind, in a row per direction where an axis has a name for each, with what is
    // bound to it in this device's maps.
    private void LoadSlots()
    {
        var category = _categories.Count > 0 ? _categories[_category] : null;
        _slots.Clear();
        _categories.Clear();
        _categories.Add("All");
        try
        {
            var device = _devices[_device];
            foreach (var action in ReInput.mapping.UserAssignableActions)
            {
                if (action == null || !action.userAssignable) continue;

                var categoryName = CategoryName(action);
                if (!_categories.Contains(categoryName)) _categories.Add(categoryName);
                foreach (var range in Ranges(action))
                {
                    var slot = new Slot(action, range, ActionName(action, range), categoryName);
                    foreach (var map in device.Maps)
                    {
                        foreach (var element in map.AllMaps)
                        {
                            if (element != null && element.enabled && element.actionId == action.id && Covers(element, action, range)) slot.Bindings.Add((map, element));
                        }
                    }

                    _slots.Add(slot);
                }
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not read the game's bindings: {exception.Message}");
        }

        _category = Mathf.Max(0, category != null ? _categories.IndexOf(category) : 0);
        ApplyCategory();
    }

    private void ApplyCategory()
    {
        _shown.Clear();
        var category = _category > 0 && _category < _categories.Count ? _categories[_category] : null;
        foreach (var slot in _slots)
        {
            if (category == null || slot.Category == category) _shown.Add(slot);
        }

        _list.SetCount(_shown.Count);
        _list.Refresh();
        SetCount($"{_shown.Count} ACTIONS");
        RefreshRows(_section);
        if (!PopupOpen) RefreshDetails();
    }

    private void FillRow(int entry, TextMeshProUGUI name, TextMeshProUGUI value)
    {
        var slot = _shown[entry];
        name.text = slot.Name;
        value.text = slot.Bindings.Count == 0 ? "-" : string.Join(" / ", slot.Bindings.ConvertAll(binding => BindingName(binding.Element)));
    }

    // ---- Changing them.

    // Listens a moment later, once the button that chose this is let go.
    private bool QueueCapture(Slot slot, bool replace)
    {
        if (!ReInput.isReady) return true;

        _capturing = slot;
        _replace = replace;
        _captureAt = Time.unscaledTime + CaptureDelaySeconds;
        var device = _devices[_device];
        var prompt = device.Keyboard ? "PRESS A KEY" : device.Mouse ? "PRESS A MOUSE BUTTON" : "PRESS A BUTTON OR MOVE AN AXIS";
        OpenPopup(prompt, slot.Name, $"On {device.Title}. Escape leaves it as it was.", picture: false, fieldLabel: null);
        OnPopupClosed(() => CancelCapture(null));
        return false;
    }

    private void StartCapture()
    {
        var slot = _capturing;
        if (slot == null) return;

        var device = _devices[_device];
        var map = MapFor(slot, device);
        if (map == null)
        {
            CancelCapture("This device has no map for that action.");
            return;
        }

        var mapper = new InputMapper
        {
            options = new InputMapper.Options
            {
                allowAxes = true,
                allowButtons = true,
                allowButtonsOnFullAxisAssignment = true,
                timeout = CaptureTimeoutSeconds,
                checkForConflicts = true,
                checkForConflictsWithAllPlayers = false,
                checkForConflictsWithSelf = true,
                checkForConflictsWithSystemPlayer = true,
                defaultActionWhenConflictFound = InputMapper.ConflictResponse.Replace,
                ignoreMouseXAxis = true,
                ignoreMouseYAxis = true,
                allowKeyboardKeysWithModifiers = true,
                allowKeyboardModifierKeyAsPrimary = true
            }
        };
        mapper.InputMappedEvent += OnMapped;
        mapper.CanceledEvent += _ => CancelCapture("Left as it was.");
        mapper.ErrorEvent += data => CancelCapture(string.IsNullOrWhiteSpace(data.message) ? "That didn't work." : data.message);
        mapper.TimedOutEvent += _ => CancelCapture("Nothing was pressed; left as it was.");
        mapper.ConflictFoundEvent += data => data.responseCallback(InputMapper.ConflictResponse.Replace);
        _mapper = mapper;

        var context = new InputMapper.Context
        {
            actionId = slot.Action.id,
            controllerMap = map,
            actionElementMapToReplace = _replace && slot.Bindings.Count > 0 ? slot.Bindings[0].Element : null,
            actionRange = slot.Range
        };
        if (!mapper.Start(context)) CancelCapture("The game couldn't listen for that.");
    }

    private void OnMapped(InputMapper.InputMappedEventData data)
    {
        var name = _capturing?.Name ?? "It";
        var bound = data.actionElementMap != null ? BindingName(data.actionElementMap) : "that";
        EndCapture();
        Save();
        ClosePopup();
        LoadSlots();
        SetStatus($"{name} is now on {bound}.");
    }

    private void CancelCapture(string? status)
    {
        if (_capturing == null) return;

        var mapper = _mapper;
        EndCapture();
        if (mapper != null)
        {
            mapper.RemoveAllEventListeners();
            mapper.Clear();
        }

        ClosePopup();
        if (status != null) SetStatus(status);
    }

    private void EndCapture()
    {
        _mapper?.RemoveAllEventListeners();
        _mapper = null;
        _capturing = null;
        _captureAt = float.PositiveInfinity;
    }

    private bool Clear(Slot slot)
    {
        try
        {
            foreach (var (map, element) in slot.Bindings) map.DeleteElementMap(element.id);
            Save();
            SetStatus($"{slot.Name} isn't bound on {_devices[_device].Title} any more.");
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not clear '{slot.Name}': {exception.Message}");
            SetStatus("That didn't work.");
        }

        LoadSlots();
        return true;
    }

    private bool ToggleInvert(Slot slot)
    {
        var invert = !IsInverted(slot);
        foreach (var (_, element) in slot.Bindings)
        {
            if (element.elementType == ControllerElementType.Axis && element.axisRange == AxisRange.Full) element.invert = invert;
        }

        Save();
        LoadSlots();
        return true;
    }

    private static bool IsInverted(Slot slot) =>
        slot.Bindings.Exists(binding => binding.Element.elementType == ControllerElementType.Axis && binding.Element.axisRange == AxisRange.Full && binding.Element.invert);

    private void OpenGameScreen()
    {
        CancelCapture(null);
        _openGameScreen();
    }

    // The map a new binding goes in: one already holding actions of the same category, else the device's first.
    private static ControllerMap? MapFor(Slot slot, Device device)
    {
        if (slot.Bindings.Count > 0) return slot.Bindings[0].Map;

        foreach (var map in device.Maps)
        {
            foreach (var element in map.AllMaps)
            {
                if (element == null) continue;

                var action = ReInput.mapping.GetAction(element.actionId);
                if (action != null && action.categoryId == slot.Action.categoryId) return map;
            }
        }

        return device.Maps.Count > 0 ? device.Maps[0] : null;
    }

    private static void Save()
    {
        try
        {
            ReInput.userDataStore?.Save();
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not save the bindings: {exception.Message}");
        }
    }

    // ---- Names, as NativeSettingsPanel gives them.

    private static IEnumerable<AxisRange> Ranges(InputAction action)
    {
        if (action.type != InputActionType.Axis)
        {
            yield return AxisRange.Positive;
            yield break;
        }

        var negative = !string.IsNullOrWhiteSpace(action.negativeDescriptiveName);
        var positive = !string.IsNullOrWhiteSpace(action.positiveDescriptiveName);
        if (!negative && !positive)
        {
            yield return AxisRange.Full;
            yield break;
        }

        if (negative) yield return AxisRange.Negative;
        if (positive) yield return AxisRange.Positive;
    }

    // Whether a binding drives this row: a button any button row; a whole axis both its directions.
    private static bool Covers(ActionElementMap element, InputAction action, AxisRange range)
    {
        if (action.type != InputActionType.Axis) return true;

        var bound = RangeOf(element);
        return bound == range || bound == AxisRange.Full || range == AxisRange.Full;
    }

    private static AxisRange RangeOf(ActionElementMap element)
    {
        if (element.axisRange is AxisRange.Full or AxisRange.Positive or AxisRange.Negative && element.elementType == ControllerElementType.Axis)
        {
            return element.axisRange;
        }

        return element.axisContribution == Pole.Negative ? AxisRange.Negative : AxisRange.Positive;
    }

    private static string ActionName(InputAction action, AxisRange range)
    {
        if (action.type == InputActionType.Axis)
        {
            if (range == AxisRange.Negative && !string.IsNullOrWhiteSpace(action.negativeDescriptiveName)) return action.negativeDescriptiveName;
            if (range == AxisRange.Positive && !string.IsNullOrWhiteSpace(action.positiveDescriptiveName)) return action.positiveDescriptiveName;
        }

        if (!string.IsNullOrWhiteSpace(action.descriptiveName)) return action.descriptiveName;
        return !string.IsNullOrWhiteSpace(action.name) ? action.name : $"Action {action.id}";
    }

    private static string CategoryName(InputAction action)
    {
        var category = ReInput.mapping.GetActionCategory(action.categoryId);
        if (category != null)
        {
            if (!string.IsNullOrWhiteSpace(category.descriptiveName)) return category.descriptiveName;
            if (!string.IsNullOrWhiteSpace(category.name)) return category.name;
        }

        return "General";
    }

    private static string BindingName(ActionElementMap element)
    {
        var name = !string.IsNullOrWhiteSpace(element.elementIdentifierName)
            ? element.elementIdentifierName
            : element.keyCode != KeyCode.None ? element.keyCode.ToString() : $"Element {element.elementIdentifierId}";
        if (element.elementType != ControllerElementType.Axis) return name;

        return element.axisRange switch
        {
            AxisRange.Positive => $"{name} +",
            AxisRange.Negative => $"{name} -",
            _ => element.invert ? $"{name} (INVERTED)" : name
        };
    }
}
