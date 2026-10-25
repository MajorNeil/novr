using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NOVR.VrUi.Menu;

// Sets the controls of NOVR's menus (MenuInput): a row per command with its keyboard, gamepad and joystick binding.
// Activating a box listens for the next key or button on that kind of device; Escape gives up and Delete clears it.
// Which joysticks the game has is shown at the top, since those are the ones that can be bound.
internal sealed class MenuControlsScreen : MenuScreen
{
    private const float NameX = -520f;
    private const float FirstColumnX = -150f;
    private const float ColumnStep = 300f;
    private static readonly Vector2 CellSize = new(280f, 54f);
    private const float RowStep = 64f;

    private readonly Action _close;
    private readonly MenuButton[,] _cells = new MenuButton[MenuInput.Commands.Length, MenuInput.SlotCount];
    private readonly TextMeshProUGUI _status;
    private MenuButton? _listening;
    private bool _clearRequested;

    public MenuControlsScreen(RectTransform canvas, Action close) : base(canvas, "NOVR Menu Controls")
    {
        _close = close;

        var panel = MenuKit.Panel(Root, "Panel", Vector2.zero, new Vector2(1320f, 940f)).rectTransform;
        MenuKit.Text(panel, "Title", "MENU CONTROLS", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, 410f), new Vector2(1220f, 56f), heading: true);
        MenuKit.Text(panel, "Help", "Used in these menus. Select a box, then press the key or button for it. Esc gives up, Delete clears.",
            MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left, new Vector2(0f, 362f), new Vector2(1220f, 32f));
        _status = MenuKit.Text(panel, "Joysticks", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, 326f), new Vector2(1220f, 32f));

        var headerY = 266f;
        string[] headers = { "KEYBOARD", "GAMEPAD", "JOYSTICK" };
        for (var slot = 0; slot < MenuInput.SlotCount; slot++)
        {
            MenuKit.Text(panel, headers[slot], headers[slot], MenuStyle.SmallSize, MenuStyle.Accent, TextAlignmentOptions.Center,
                new Vector2(FirstColumnX + slot * ColumnStep, headerY), new Vector2(CellSize.x, 32f), heading: true);
        }

        for (var row = 0; row < MenuInput.Commands.Length; row++)
        {
            var command = MenuInput.Commands[row];
            var y = headerY - 58f - row * RowStep;
            MenuKit.Text(panel, MenuInput.Name(command), MenuInput.Name(command).ToUpperInvariant(), MenuStyle.BodySize, MenuStyle.Text,
                TextAlignmentOptions.Left, new Vector2(NameX, y), new Vector2(300f, CellSize.y));
            for (var slot = 0; slot < MenuInput.SlotCount; slot++)
            {
                var device = (MenuDevice)slot;
                MenuButton? cell = null;
                cell = Add(MenuKit.Button(panel, $"{command} {device}", "", new Vector2(FirstColumnX + slot * ColumnStep, y), CellSize,
                    () => Listen(cell!, command, device), MenuButton.Look.Cell, MenuStyle.SmallSize));
                _cells[row, slot] = cell;
            }
        }

        var footerY = -410f;
        Add(MenuKit.Button(panel, "Reset", "RESET TO DEFAULTS", new Vector2(-330f, footerY), new Vector2(440f, 58f), ResetAll,
            MenuButton.Look.Secondary));
        Add(MenuKit.Button(panel, "Back", "BACK", new Vector2(330f, footerY), new Vector2(440f, 58f), () => OnBack()));
    }

    protected override void OnShown()
    {
        Refresh();
    }

    protected override void OnHidden()
    {
        MenuInput.CancelRebind();
    }

    protected override void OnTick()
    {
        if (_listening == null) return;

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.deleteKey.wasPressedThisFrame)
        {
            _clearRequested = true;
            MenuInput.CancelRebind();
        }
    }

    protected override bool OnBack()
    {
        _close();
        return true;
    }

    private void Listen(MenuButton cell, MenuCommand command, MenuDevice device)
    {
        if (MenuInput.IsRebinding) return;

        _listening = cell;
        _clearRequested = false;
        cell.Highlight = true;
        cell.SetLabel("PRESS...");
        MenuInput.Rebind(command, device, bound =>
        {
            if (!bound && _clearRequested) MenuInput.SetPath(command, device, "");
            cell.Highlight = false;
            _listening = null;
            _clearRequested = false;
            Refresh();
        });
    }

    private void ResetAll()
    {
        MenuInput.ResetAll();
        Refresh();
    }

    private void Refresh()
    {
        var joysticks = MenuInput.JoystickNames();
        var noisy = MenuInput.NoisyNames();
        _status.text = joysticks.Length == 0
            ? "No joystick seen. Sticks the game flies with can be bound here."
            : noisy.Length > 0
                ? $"Ignored, as they press by themselves: {noisy}."
                : $"Joysticks: {joysticks}. Bind your stick's hat and a button in the joystick column.";

        for (var row = 0; row < MenuInput.Commands.Length; row++)
        {
            for (var slot = 0; slot < MenuInput.SlotCount; slot++)
            {
                var display = MenuInput.GetDisplay(MenuInput.Commands[row], (MenuDevice)slot);
                _cells[row, slot].SetLabel(display.Length > 0 ? display.ToUpperInvariant() : "-");
            }
        }
    }
}
