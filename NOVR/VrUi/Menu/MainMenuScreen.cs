using System;
using NOVR.VrUi.Native;
using TMPro;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// The game's main menu, rebuilt for a headset: the game's entries down a panel on the left, in the game's own font and
// colours, and the rest of the view left open onto the hangar. Each entry still goes through the game's own button
// (NativeGameActionAdapter), so it does exactly what the game's menu does. Under it, what the controls do on the device
// last used, and the game's own hints, as on its flat menu.
internal sealed class MainMenuScreen : MenuScreen
{
    private const float RailX = -620f;
    private static readonly Vector2 RailSize = new(500f, 960f);
    private const float ButtonWidth = 440f;
    private const float PrimaryHeight = 62f;
    private const float PrimaryStep = 72f;
    private const float SecondaryHeight = 52f;
    private const float SecondaryStep = 60f;
    private const float HintRefreshSeconds = 0.5f;

    private readonly NativeGameActionAdapter _actions;
    private readonly Func<GameObject?> _mainCanvas;
    private readonly TextMeshProUGUI _prompts;
    private readonly RectTransform _hint;
    private readonly TextMeshProUGUI _hintTitle;
    private readonly TextMeshProUGUI _hintBody;
    private readonly TextMeshProUGUI _caption;
    private HintsTipsDisplay? _hints;
    private float _nextHintRead;
    private MenuDevice _promptDevice = (MenuDevice)(-1);

    public MainMenuScreen(RectTransform canvas, NativeGameActionAdapter actions, Func<GameObject?> mainCanvas,
        Action openVrSettings, Action openMenuControls)
        : base(canvas, "NOVR Main Menu")
    {
        _actions = actions;
        _mainCanvas = mainCanvas;

        var rail = MenuKit.Panel(Root, "Rail", new Vector2(RailX, 0f), RailSize).rectTransform;
        var top = RailSize.y / 2f;
        MenuKit.Text(rail, "Title", "NUCLEAR OPTION", MenuStyle.TitleSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, top - 78f), new Vector2(ButtonWidth, 80f), heading: true);
        MenuKit.Image(rail, "Title Rule", MenuStyle.TextDim.WithAlpha(0.35f), new Vector2(0f, top - 128f), new Vector2(ButtonWidth, 2f));

        var y = top - 186f;
        AddPrimary(rail, "SINGLE PLAYER", NativeGameAction.SinglePlayer, ref y);
        AddPrimary(rail, "MULTIPLAYER", NativeGameAction.Multiplayer, ref y);
        if (actions.HasMainMenuButton(NativeGameAction.MissionEditor)) AddPrimary(rail, "MISSION EDITOR", NativeGameAction.MissionEditor, ref y);
        AddPrimary(rail, "ENCYCLOPEDIA", NativeGameAction.Encyclopedia, ref y);
        AddPrimary(rail, "WORKSHOP", NativeGameAction.Workshop, ref y);
        AddPrimary(rail, "SETTINGS", NativeGameAction.Settings, ref y);

        y -= 18f;
        AddSecondary(rail, "VR SETTINGS", openVrSettings, ref y);
        AddSecondary(rail, "MENU CONTROLS", openMenuControls, ref y);

        y -= 18f;
        Add(MenuKit.Button(rail, "Exit Game", "EXIT GAME", new Vector2(0f, y), new Vector2(ButtonWidth, PrimaryHeight),
            () => _actions.QuitGame(), MenuButton.Look.Danger));

        _prompts = MenuKit.Text(rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 58f), new Vector2(ButtonWidth, 34f));
        MenuKit.Text(rail, "Version", Application.version, MenuStyle.SmallSize * 0.85f, MenuStyle.TextDim.WithAlpha(0.6f),
            TextAlignmentOptions.Left, new Vector2(0f, -top + 24f), new Vector2(ButtonWidth, 28f));

        _hint = MenuKit.Panel(Root, "Hint", new Vector2(330f, -440f), new Vector2(820f, 150f)).rectTransform;
        _hintTitle = MenuKit.Text(_hint, "Hint Title", "", MenuStyle.SmallSize, MenuStyle.Accent, TextAlignmentOptions.Left,
            new Vector2(0f, 44f), new Vector2(770f, 32f), heading: true);
        _hintBody = MenuKit.Text(_hint, "Hint Text", "", MenuStyle.SmallSize, MenuStyle.Text, TextAlignmentOptions.TopLeft,
            new Vector2(0f, -18f), new Vector2(770f, 84f), wrap: true);
        _hint.gameObject.SetActive(false);

        _caption = MenuKit.Text(Root, "Hangar Caption", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Right,
            new Vector2(560f, 470f), new Vector2(800f, 34f));
    }

    protected override void OnShown()
    {
        _promptDevice = (MenuDevice)(-1);
        _nextHintRead = 0f;
    }

    protected override void OnTick()
    {
        if (_promptDevice != MenuInput.LastDevice)
        {
            _promptDevice = MenuInput.LastDevice;
            _prompts.text = $"{Prompt(MenuCommand.Select)} Select    {Prompt(MenuCommand.Back)} Back";
        }

        var featured = MenuEnvironment.FeaturedAircraft;
        var caption = string.IsNullOrEmpty(featured) ? "" : $"IN THE HANGAR   <color=#{MenuStyle.Hex(MenuStyle.Text)}>{featured}</color>";
        if (_caption.text != caption) _caption.text = caption;

        if (Time.unscaledTime >= _nextHintRead)
        {
            _nextHintRead = Time.unscaledTime + HintRefreshSeconds;
            ReadHint();
        }
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";

    // Mirrors the game's own hints panel, which keeps cycling through its tips while its menu is hidden behind this one.
    private void ReadHint()
    {
        if (_hints == null)
        {
            var mainCanvas = _mainCanvas();
            if (mainCanvas != null) _hints = mainCanvas.GetComponentInChildren<HintsTipsDisplay>(true);
        }

        var hints = _hints;
        var text = hints != null && hints.gameObject.activeInHierarchy && hints.hintText != null ? hints.hintText.text : null;
        var showing = !string.IsNullOrEmpty(text);
        if (_hint.gameObject.activeSelf != showing) _hint.gameObject.SetActive(showing);
        if (!showing) return;

        var title = hints!.hintType != null ? hints.hintType.text.ToUpperInvariant() : "";
        if (_hintTitle.text != title) _hintTitle.text = title;
        if (_hintBody.text != text) _hintBody.text = text;
    }

    private void AddPrimary(RectTransform rail, string label, NativeGameAction action, ref float y)
    {
        Add(MenuKit.Button(rail, label, label, new Vector2(0f, y), new Vector2(ButtonWidth, PrimaryHeight),
            () => _actions.TryInvoke(action)));
        y -= PrimaryStep;
    }

    private void AddSecondary(RectTransform rail, string label, Action open, ref float y)
    {
        Add(MenuKit.Button(rail, label, label, new Vector2(0f, y), new Vector2(ButtonWidth, SecondaryHeight), open,
            MenuButton.Look.Secondary));
        y -= SecondaryStep;
    }
}
