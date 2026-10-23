using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using NOVR.VrUi.Native;
using NuclearOption.Effects;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NOVR.VrUi.Menu;

// The game's settings and NOVR's own, in one screen in the game's menu style. Categories run down the same panel as the main
// menu's entries; the settings of the chosen one fill a panel to the right, two columns of rows. Each row is one button:
// Left and Right (or the arrows at its ends) change it, Select or a click on it toggles or steps it on, and what the focused
// row does is spelled out under the columns. Up and Down run down the first column and on into the second, Back returns
// to the categories and then to the main menu, and the tab bindings switch category from anywhere.
// The game's settings are read and written as NativeSettingsPanel does, straight to PlayerSettings and PlayerPrefs; NOVR's
// are its config entries, which apply as they change. Key Bindings opens the game's own binding screen.
internal sealed class SettingsScreen : MenuScreen
{
    public enum Tab
    {
        Audio,
        Graphics,
        Gameplay,
        Controls,
        Hud,
        Chat,
        VrView,
        Threats,
        EyeTracking
    }

    private const float ContentX = 300f;
    private static readonly Vector2 ContentSize = new(1200f, 960f);
    private const int RowsPerColumn = 10;
    private static readonly float[] ColumnX = { -296f, 296f };
    private static readonly Vector2 RowSize = new(570f, 56f);
    private const float FirstRowY = 330f;
    private const float RowStep = 64f;
    private const float ValueWidth = 190f;
    private const float ArrowWidth = 40f;
    private const float TabWidth = 440f;
    private const float TabHeight = 48f;
    private const float TabStep = 56f;

    private static readonly (Tab Tab, string Label)[] Tabs =
    {
        (Tab.Audio, "AUDIO"),
        (Tab.Graphics, "GRAPHICS"),
        (Tab.Gameplay, "GAMEPLAY"),
        (Tab.Controls, "CONTROLS"),
        (Tab.Hud, "HUD"),
        (Tab.Chat, "CHAT"),
        (Tab.VrView, "VR VIEW"),
        (Tab.Threats, "THREAT AWARENESS"),
        (Tab.EyeTracking, "EYE TRACKING")
    };

    private readonly Action _close;
    private readonly Action _recenter;
    private readonly Dictionary<Tab, MenuButton> _tabButtons = new();
    private readonly List<Row> _rows = new();
    private readonly List<MenuButton> _order = new();
    private readonly RectTransform _content;
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI _description;
    private readonly TextMeshProUGUI _prompts;
    private Tab _tab = Tab.Audio;
    private bool _gameSettingsChanged;
    private MenuDevice _promptDevice = (MenuDevice)(-1);

    private sealed class Row
    {
        public MenuButton Button = null!;
        public TextMeshProUGUI Value = null!;
        public Func<string> Read = null!;
        public string Description = "";
    }

    public SettingsScreen(RectTransform canvas, Action close, Action openMenuControls, Action openKeyBindings, Action recenter)
        : base(canvas, "NOVR Settings")
    {
        _close = close;
        _recenter = recenter;

        var rail = MenuKit.Panel(Root, "Categories", MainMenuScreen.RailCenter, MainMenuScreen.RailSize).rectTransform;
        var top = MainMenuScreen.RailSize.y / 2f;
        MenuKit.Text(rail, "Title", "SETTINGS", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, top - 70f), new Vector2(TabWidth, 64f), heading: true);
        MenuKit.Image(rail, "Title Rule", MenuStyle.TextDim.WithAlpha(0.35f), new Vector2(0f, top - 112f), new Vector2(TabWidth, 2f));

        var y = top - 160f;
        for (var index = 0; index < Tabs.Length; index++)
        {
            if (Tabs[index].Tab == Tab.VrView) y -= 14f;
            var tab = Tabs[index].Tab;
            _tabButtons[tab] = Add(MenuKit.Button(rail, Tabs[index].Label, Tabs[index].Label, new Vector2(0f, y),
                new Vector2(TabWidth, TabHeight), () => ShowTab(tab, focusTab: false), MenuButton.Look.Secondary));
            y -= TabStep;
        }

        y -= 14f;
        Add(MenuKit.Button(rail, "Menu Controls", "MENU CONTROLS", new Vector2(0f, y), new Vector2(TabWidth, TabHeight), openMenuControls,
            MenuButton.Look.Secondary));
        y -= TabStep;
        Add(MenuKit.Button(rail, "Key Bindings", "KEY BINDINGS", new Vector2(0f, y), new Vector2(TabWidth, TabHeight), openKeyBindings,
            MenuButton.Look.Secondary));
        y -= TabStep + 18f;
        Add(MenuKit.Button(rail, "Back", "BACK", new Vector2(0f, y), new Vector2(TabWidth, 56f), Close));

        _prompts = MenuKit.Text(rail, "Prompts", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.Left,
            new Vector2(0f, -top + 40f), new Vector2(TabWidth, 34f));

        _content = MenuKit.Panel(Root, "Settings", new Vector2(ContentX, 0f), ContentSize).rectTransform;
        _title = MenuKit.Text(_content, "Title", "", MenuStyle.HeadingSize, MenuStyle.Text, TextAlignmentOptions.Left,
            new Vector2(0f, ContentSize.y / 2f - 70f), new Vector2(ContentSize.x - 80f, 64f), heading: true);
        _description = MenuKit.Text(_content, "Description", "", MenuStyle.SmallSize, MenuStyle.TextDim, TextAlignmentOptions.TopLeft,
            new Vector2(0f, -ContentSize.y / 2f + 85f), new Vector2(ContentSize.x - 80f, 110f), wrap: true);

        ShowTab(Tab.Audio, focusTab: false);
    }

    // Called before the screen is shown.
    public void Open(Tab tab)
    {
        NativeSettingsPanel.ReloadPlayerSettings();
        _gameSettingsChanged = false;
        ShowTab(tab, focusTab: true);
    }

    protected override void OnShown()
    {
        _promptDevice = (MenuDevice)(-1);
        Refresh();
    }

    protected override void OnTick()
    {
        if (_promptDevice == MenuInput.LastDevice) return;

        _promptDevice = MenuInput.LastDevice;
        _prompts.text = $"{Prompt(MenuCommand.PreviousTab)}/{Prompt(MenuCommand.NextTab)} Category   {Prompt(MenuCommand.Back)} Back";
    }

    protected override MenuButton? FirstFocus() => _tabButtons[_tab];

    protected override bool OnBack()
    {
        var focused = Focused;
        if (focused != null && _order.Contains(focused))
        {
            Focus(_tabButtons[_tab], sound: false);
            return true;
        }

        Close();
        return true;
    }

    protected override void OnTab(int direction)
    {
        var index = Array.FindIndex(Tabs, entry => entry.Tab == _tab);
        ShowTab(Tabs[(index + direction + Tabs.Length) % Tabs.Length].Tab, focusTab: true);
        MenuSounds.Move();
    }

    protected override void OnFocused(MenuButton? button)
    {
        var row = _rows.Find(candidate => candidate.Button == button);
        _description.text = row?.Description ?? "";
    }

    private static string Prompt(MenuCommand command) =>
        $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>{MenuInput.GetPrompt(command).ToUpperInvariant()}</color>";

    private void Close()
    {
        if (_gameSettingsChanged)
        {
            PlayerPrefs.Save();
            NativeSettingsPanel.ApplyPlayerSettings();
            NativeSettingsPanel.ApplyHudSettings();
            _gameSettingsChanged = false;
        }

        _close();
    }

    private void ShowTab(Tab tab, bool focusTab)
    {
        var focusWasInRows = Focused != null && _order.Contains(Focused);
        foreach (var row in _rows)
        {
            Remove(row.Button);
            Object.Destroy(row.Button.gameObject);
        }

        _rows.Clear();
        _order.Clear();
        _tab = tab;
        foreach (var pair in _tabButtons) pair.Value.Highlight = pair.Key == tab;
        _title.text = Array.Find(Tabs, entry => entry.Tab == tab).Label;
        _description.text = "";

        switch (tab)
        {
            case Tab.Audio:
                AddAudioRows();
                break;
            case Tab.Graphics:
                AddGraphicsRows();
                break;
            case Tab.Gameplay:
                AddGameplayRows();
                break;
            case Tab.Controls:
                AddControlsRows();
                break;
            case Tab.Hud:
                AddHudRows();
                break;
            case Tab.Chat:
                AddChatRows();
                break;
            case Tab.VrView:
                AddVrViewRows();
                break;
            case Tab.Threats:
                AddThreatRows();
                break;
            case Tab.EyeTracking:
                AddEyeTrackingRows();
                break;
        }

        SetOrder(_order);
        if (focusTab || focusWasInRows) Focus(_tabButtons[tab], sound: false);
    }

    private void AddAudioRows()
    {
        AddAudio("Master", AudioMixerVolume.Master);
        AddAudio("Music", AudioMixerVolume.Music);
        AddAudio("Interface", AudioMixerVolume.Interface);
        AddAudio("Effects", AudioMixerVolume.Effects);
        AddAudio("Menu", AudioMixerVolume.Menu);
        AddAudio("Radar Warning", AudioMixerVolume.RadarWarning);
        AddAudio("Missile Alert", AudioMixerVolume.MissileAlert);
        AddAudio("Jammed Noise", AudioMixerVolume.JammedNoise);
    }

    private void AddAudio(string label, string channel) =>
        AddFloat(label, () => AudioMixerVolume.GetPref(channel), value => AudioMixerVolume.SetValue(channel, value), 0f, 1f, 0.05f, Percent);

    private void AddGraphicsRows()
    {
        if (PlayerSettings.graphics == null || PlayerSettings.DetailSettings == null)
        {
            AddRow("Graphics settings aren't loaded yet", () => "", null, null, "");
            return;
        }

        AddToggle("VSync", () => PlayerSettings.graphics.Vsync, value => PlayerSettings.graphics.Vsync = value);
        AddToggle("Cinematic Mode", () => PlayerSettings.cinematicMode, NativeSettingsPanel.SetCinematicMode);
        AddToggle("Debug Visuals", () => PlayerSettings.debugVis, NativeSettingsPanel.SetDebugVisuals);
        AddOptions("Anti-Aliasing", GraphicsHelper.AAOptions, () => PlayerSettings.graphics.AntiAliasing, value => PlayerSettings.graphics.AntiAliasing = value);
        AddOptions("Texture Quality", GraphicsHelper.MipmapLimitOptions, () => PlayerSettings.graphics.MipmapLevel, value => PlayerSettings.graphics.MipmapLevel = value);
        AddOptions("Anisotropic Filtering", GraphicsHelper.AnisotropicOptions, () => PlayerSettings.graphics.AnisotropicFiltering, value => PlayerSettings.graphics.AnisotropicFiltering = value);
        AddOptions("Shadow Quality", GraphicsHelper.ShadowQualityOptions, () => PlayerSettings.graphics.ShadowQuality, value => PlayerSettings.graphics.ShadowQuality = value);
        AddFloat("Shadow Distance", () => PlayerSettings.graphics.ShadowDistance, value => PlayerSettings.graphics.ShadowDistance = Mathf.RoundToInt(value), 500f, 10000f, 500f, value => $"{value:0} m");
        AddToggle("Soft Shadows", () => PlayerSettings.graphics.SoftShadows, value => PlayerSettings.graphics.SoftShadows = value);
        AddFloat("LOD Bias", () => PlayerSettings.graphics.LodBias, value => PlayerSettings.graphics.LodBias = value, 1f, 4f, 0.1f, value => $"{value:0.0}");
        AddFloat("Cloud Detail", () => PlayerSettings.graphics.CloudDetail, value => PlayerSettings.graphics.CloudDetail = value, 0f, 1f, 0.05f, Percent);
        AddToggle("Grass", () => PlayerSettings.DetailSettings.GrassEnabled, value => PlayerSettings.DetailSettings.GrassEnabled = value);
        AddFloat("Tree Distance", () => PlayerSettings.DetailSettings.TreeRangeMultiplier, value => PlayerSettings.DetailSettings.TreeRangeMultiplier = value,
            DetailSettings.TreeRangeMultiplierMin, DetailSettings.TreeRangeMultiplierMax, 0.1f, value => $"{value:0.0}");
        AddAction("Graphics Defaults", "RESET", () =>
        {
            PlayerSettings.graphics.Clear();
            PlayerSettings.DetailSettings.Clear();
            _gameSettingsChanged = true;
        }, "Puts every graphics setting back to the game's default.");
    }

    private void AddGameplayRows()
    {
        AddOptions("Units", new[] { "Metric", "Imperial" }, () => (int)PlayerSettings.unitSystem, NativeSettingsPanel.SetUnitSystem);
        AddFloat("Cockpit Camera Inertia", () => PlayerSettings.cockpitCamInertia, value => NativeSettingsPanel.SetPlayerFloat("CockpitCamInertia", value, static assigned => PlayerSettings.cockpitCamInertia = assigned, apply: true), 0f, 1f, 0.05f, Percent);
        AddFloat("Cockpit FOV", () => PlayerSettings.defaultFoV, value => NativeSettingsPanel.SetPlayerFloat("DefaultFoV", value, static assigned => PlayerSettings.defaultFoV = assigned, apply: true), 30f, 120f, 1f, Degrees);
        AddFloat("External FOV", () => PlayerSettings.defaultExternalFoV, value => NativeSettingsPanel.SetPlayerFloat("DefaultExternalFoV", value, static assigned => PlayerSettings.defaultExternalFoV = assigned, apply: true), 30f, 120f, 1f, Degrees);
        AddToggle("Zoom on Boresight", () => PlayerSettings.zoomOnBoresight, value => NativeSettingsPanel.SetPlayerBool("ZoomOnBoresight", value, static assigned => PlayerSettings.zoomOnBoresight = assigned, apply: true));
        AddToggle("Padlock Target", () => PlayerSettings.padLockTarget, value => NativeSettingsPanel.SetPlayerBool("PadLockTarget", value, static assigned => PlayerSettings.padLockTarget = assigned, apply: true));
        AddToggle("Tac Screen IR", () => PlayerSettings.tacScreenIR, value => NativeSettingsPanel.SetPlayerBool("TacScreenIR", value, static assigned => PlayerSettings.tacScreenIR = assigned, apply: true));
        AddToggle("Camera Auto NVG", () => PlayerSettings.cameraAutoNVG, value => NativeSettingsPanel.SetPlayerBool("CameraAutoNVG", value, static assigned => PlayerSettings.cameraAutoNVG = assigned, apply: true));
        AddToggle("Hit Markers", () => PlayerSettings.showHitMarkers, value => NativeSettingsPanel.SetPlayerBool("ShowHitMarkers", value, static assigned => PlayerSettings.showHitMarkers = assigned, apply: true));
    }

    private void AddControlsRows()
    {
        AddToggle("Virtual Joystick", () => PlayerSettings.virtualJoystickEnabled, value => NativeSettingsPanel.SetPlayerBool("VirtualJoystickEnabled", value, static assigned => PlayerSettings.virtualJoystickEnabled = assigned, apply: true));
        AddToggle("Invert Virtual Pitch", () => PlayerSettings.virtualJoystickInvertPitch, value => NativeSettingsPanel.SetPlayerBool("VirtualJoystickInvertPitch", value, static assigned => PlayerSettings.virtualJoystickInvertPitch = assigned, apply: true));
        AddToggle("Invert View Pitch", () => PlayerSettings.viewInvertPitch, value => NativeSettingsPanel.SetPlayerBool("ViewInvertPitch", value, static assigned => PlayerSettings.viewInvertPitch = assigned, apply: true));
        AddToggle("Throttle Negative Axis", () => PlayerSettings.throttleUseNegative, value => NativeSettingsPanel.SetPlayerBool("ThrottleUseNegative", value, static assigned => PlayerSettings.throttleUseNegative = assigned, apply: true));
        AddToggle("Relative Throttle", () => PlayerSettings.throttleUseRelative, value => NativeSettingsPanel.SetPlayerBool("ThrottleUseRelative", value, static assigned => PlayerSettings.throttleUseRelative = assigned, apply: true));
        AddToggle("Controller Menu Navigation", () => PlayerSettings.controllerMenuNavigation, value => NativeSettingsPanel.SetPlayerBool("ControllerMenuNavigation", value, static assigned => PlayerSettings.controllerMenuNavigation = assigned, apply: true));
        AddToggle("Menu Weapon Safety", () => PlayerSettings.menuWeaponSafety, value => NativeSettingsPanel.SetPlayerBool("MenuWeaponSafety", value, static assigned => PlayerSettings.menuWeaponSafety = assigned, apply: true));
        AddToggle("Invert Collective", () => PlayerSettings.invertCollective, value => NativeSettingsPanel.SetPlayerBool("InvertCollective", value, static assigned => PlayerSettings.invertCollective = assigned, apply: true));
        AddToggle("TrackIR", () => PlayerSettings.useTrackIR, value => NativeSettingsPanel.SetPlayerBool("UseTrackIR", value, static assigned => PlayerSettings.useTrackIR = assigned, apply: true));
        AddFloat("Virtual Stick Sensitivity", () => PlayerSettings.virtualJoystickSensitivity, value => NativeSettingsPanel.SetPlayerFloat("VirtualJoystickSensitivity", value, static assigned => PlayerSettings.virtualJoystickSensitivity = assigned, apply: true), 0f, 1f, 0.05f, Fraction);
        AddFloat("Virtual Stick Centering", () => PlayerSettings.virtualJoystickCentering, value => NativeSettingsPanel.SetPlayerFloat("VirtualJoystickCentering", value, static assigned => PlayerSettings.virtualJoystickCentering = assigned, apply: true), 0f, 1f, 0.05f, Fraction);
        AddFloat("View Sensitivity", () => PlayerSettings.viewSensitivity, value => NativeSettingsPanel.SetPlayerFloat("ViewSensitivity", value, static assigned => PlayerSettings.viewSensitivity = assigned, apply: true), 0f, 1f, 0.05f, Fraction);
        AddFloat("View Smoothing", () => PlayerSettings.viewSmoothing, value => NativeSettingsPanel.SetPlayerFloat("ViewSmoothing", value, static assigned => PlayerSettings.viewSmoothing = assigned, apply: true), 0f, 1f, 0.05f, Fraction);
        AddFloat("Button Click Delay", () => PlayerSettings.clickDelay, value => NativeSettingsPanel.SetPlayerFloat("ClickDelay", value, static assigned => PlayerSettings.clickDelay = assigned, apply: true), 0f, 1f, 0.05f, Seconds);
        AddFloat("Button Hold Delay", () => PlayerSettings.pressDelay, value => NativeSettingsPanel.SetPlayerFloat("PressDelay", value, static assigned => PlayerSettings.pressDelay = assigned, apply: true), 0f, 1f, 0.05f, Seconds);
    }

    private void AddHudRows()
    {
        var hmdWidthMax = Mathf.Max(1080f, 1080f * Screen.width / Mathf.Max(1f, Screen.height));
        var hmdSideDistMax = Mathf.Max(100f, PlayerSettings.hmdWidth * 0.5f);
        var hmdTopHeightMax = Mathf.Max(100f, PlayerSettings.hmdHeight * 0.5f);

        AddToggle("Lag PIP", () => PlayerSettings.lagPip, value => NativeSettingsPanel.SetHudBool("LagPip", value, static assigned => PlayerSettings.lagPip = assigned));
        AddToggle("Range Circle", () => PlayerSettings.rangeCircle, value => NativeSettingsPanel.SetHudBool("RangeCircle", value, static assigned => PlayerSettings.rangeCircle = assigned));
        AddToggle("Gauges", () => PlayerSettings.gauges, value => NativeSettingsPanel.SetHudBool("Gauges", value, static assigned => PlayerSettings.gauges = assigned));
        AddToggle("HUD Weapons", () => PlayerSettings.hudWeapons, value => NativeSettingsPanel.SetHudBool("HUDWeapons", value, static assigned => PlayerSettings.hudWeapons = assigned));
        AddFloat("HMD Width", () => PlayerSettings.hmdWidth, value => NativeSettingsPanel.SetHudFloat("HMDWidth", value, static assigned => PlayerSettings.hmdWidth = assigned), 500f, hmdWidthMax, 10f, Pixels);
        AddFloat("HMD Height", () => PlayerSettings.hmdHeight, value => NativeSettingsPanel.SetHudFloat("HMDHeight", value, static assigned => PlayerSettings.hmdHeight = assigned), 500f, 1080f, 10f, Pixels);
        AddFloat("HMD Side Distance", () => PlayerSettings.hmdSideDist, value => NativeSettingsPanel.SetHudFloat("HMDSideDist", value, static assigned => PlayerSettings.hmdSideDist = assigned), 100f, hmdSideDistMax, 5f, Pixels);
        AddFloat("HMD Side Angle", () => PlayerSettings.hmdSideAngle, value => NativeSettingsPanel.SetHudFloat("HMDSideAngle", value, static assigned => PlayerSettings.hmdSideAngle = assigned), 0f, 90f, 1f, Degrees);
        AddFloat("HMD Top Height", () => PlayerSettings.hmdTopHeight, value => NativeSettingsPanel.SetHudFloat("HMDTopHeight", value, static assigned => PlayerSettings.hmdTopHeight = assigned), -hmdTopHeightMax, hmdTopHeightMax, 5f, Pixels);
        AddFloat("HMD Hide Distance", () => PlayerSettings.hmdHideDist, value => NativeSettingsPanel.SetHudFloat("HMDHideDist", value, static assigned => PlayerSettings.hmdHideDist = assigned), 0.2f, 1f, 0.05f, Percent);
        AddFloat("HMD Icon Size", () => PlayerSettings.hmdIconSize, value => NativeSettingsPanel.SetHudFloat("HMDIconSize", value, static assigned => PlayerSettings.hmdIconSize = assigned), 10f, 80f, 5f, Whole);
        AddFloat("HUD Text Size", () => PlayerSettings.hudTextSize, value => NativeSettingsPanel.SetHudFloat("HUDTextSize", value, static assigned => PlayerSettings.hudTextSize = assigned), 20f, 80f, 2f, Whole);
        AddFloat("HMD Text Size", () => PlayerSettings.hmdTextSize, value => NativeSettingsPanel.SetHudFloat("HMDTextSize", value, static assigned => PlayerSettings.hmdTextSize = assigned), 20f, 80f, 2f, Whole);
        AddFloat("Overlay Text Size", () => PlayerSettings.overlayTextSize, value => NativeSettingsPanel.SetHudFloat("OverlayTextSize", value, static assigned => PlayerSettings.overlayTextSize = assigned), 16f, 80f, 2f, Whole);
    }

    private void AddChatRows()
    {
        AddToggle("Chat", () => PlayerSettings.chatEnabled, value => NativeSettingsPanel.SetPlayerBool("ChatEnabled", value, static assigned => PlayerSettings.chatEnabled = assigned));
        AddToggle("Chat Filter", () => PlayerSettings.chatFilter, value => NativeSettingsPanel.SetPlayerBool("ChatFilter", value, static assigned => PlayerSettings.chatFilter = assigned));
        AddToggle("Text To Speech", () => PlayerSettings.chatTts, value => NativeSettingsPanel.SetPlayerBool("ChatTts", value, static assigned => PlayerSettings.chatTts = assigned));
        AddInt("TTS Speed", () => PlayerSettings.chatTtsSpeed, value => NativeSettingsPanel.SetPlayerInt("ChatTtsSpeed", value, static assigned => PlayerSettings.chatTtsSpeed = assigned), -10, 10, 1, value => $"{value}");
        AddInt("TTS Volume", () => PlayerSettings.chatTtsVolume, value => NativeSettingsPanel.SetPlayerInt("ChatTtsVolume", value, static assigned => PlayerSettings.chatTtsVolume = assigned), 0, 100, 5, value => $"{value}%");
    }

    private void AddVrViewRows()
    {
        var config = ModConfiguration.Instance;
        AddConfig(config.NativeMenuScale, "Menu Size", 0.75f, 2f, 0.05f, Times);
        AddConfig(config.NativeMenuDistance, "Menu Distance", 1.5f, 6f, 0.25f, Meters);
        AddConfig(config.NativeMenuHeightOffset, "Menu Height", -0.25f, 1f, 0.05f, Meters);
        AddConfig(config.MenuHangar, "Hangar");
        AddAction("Recenter View", "IN 2 S", _recenter, "Face where you want straight ahead to be; two seconds later the view and the menus turn to match.");
        AddConfig(config.InGameUiScale, "In-Game Menu Size", 0.5f, 2f, 0.05f, Times);
        AddConfig(config.InGameUiDistance, "In-Game Menu Distance", 1f, 6f, 0.25f, Meters);
        AddConfig(config.HudScale, "HUD Size", 0.5f, 2f, 0.05f, Times);
        AddConfig(config.MinimapZoomOut, "Minimap Zoom Out", 0.5f, 8f, 0.25f, Times);
        AddConfig(config.EnableNativeMenuUi, "NOVR Menus");
    }

    private void AddThreatRows()
    {
        var config = ModConfiguration.Instance;
        AddConfig(config.ThreatRing, "Threat Ring");
        AddConfig(config.RingDegrees, "Ring Radius", 15f, 45f, 1f, Degrees);
        AddConfig(config.AircraftRangeKm, "Enemy Aircraft Range", 0f, 40f, 1f, Kilometres);
        AddConfig(config.FriendlyRangeKm, "Friendly Aircraft Range", 0f, 20f, 1f, Kilometres);
        AddConfig(config.NotchMarks, "Notch Marks");
        AddConfig(config.NotchToleranceDegrees, "Notch Tolerance", 4f, 30f, 1f, Degrees);
        AddConfig(config.MissileTones, "Missile Tones");
        AddConfig(config.MissileToneRangeKm, "Missile Tone Range", 0f, 30f, 0.5f, Kilometres);
        AddConfig(config.MissileToneVolume, "Missile Tone Volume", 0f, 1f, 0.05f, Percent);
        AddConfig(config.MissileBanner, "Missile Banner");
        AddConfig(config.CenterMissileWarning, "Center Missile Warning");
        AddConfig(config.CenterWarningRangeKm, "Center Warning Range", 0f, 20f, 0.5f, Kilometres);
    }

    private void AddEyeTrackingRows()
    {
        var config = ModConfiguration.Instance;
        AddConfig(config.EyeTrackingEnabled, "Eye Tracking");
        AddOptions("Menu Cursor", Enum.GetNames(typeof(GazeCursorMode)), () => (int)config.GazeMenuCursor.Value,
            value => config.GazeMenuCursor.Value = (GazeCursorMode)value, Describe(config.GazeMenuCursor));
        AddConfig(config.SelectClicksMenus, "Select Clicks Menus");
        AddConfig(config.MenuSnapDegrees, "Menu Snap", 0f, 5f, 0.25f, Degrees);
        AddConfig(config.GazeSteadiness, "Gaze Steadiness", 0f, 1f, 0.05f, Percent);
        AddConfig(config.GazeDesignate, "Look To Designate");
        AddConfig(config.ShowGazeReticle, "Gaze Reticle");
        AddConfig(config.ShowGazeDot, "Gaze Dot");
        AddConfig(config.GazeContactInfo, "Gaze Contact Info");
        AddConfig(config.GazeContactDwellSeconds, "Contact Info Dwell", 0f, 1.5f, 0.05f, Seconds);
    }

    private void AddConfig(ConfigEntry<bool> entry, string label) =>
        AddToggle(label, () => entry.Value, value => entry.Value = value, Describe(entry), game: false);

    private void AddConfig(ConfigEntry<float> entry, string label, float min, float max, float step, Func<float, string> format) =>
        AddFloat(label, () => entry.Value, value => entry.Value = value, min, max, step, format, Describe(entry), game: false);

    private static string Describe(ConfigEntryBase entry) => entry.Description?.Description ?? "";

    private void AddToggle(string label, Func<bool> get, Action<bool> set, string description = "", bool game = true) =>
        AddRow(label, () => get() ? $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>ON</color>" : "OFF",
            _ => Change(() => set(!get()), game), null, description);

    private void AddOptions(string label, IList<string> options, Func<int> get, Action<int> set, string description = "", bool game = true)
    {
        if (options.Count == 0) return;

        AddRow(label, () => options[Mathf.Clamp(get(), 0, options.Count - 1)],
            direction => Change(() => set(Mathf.Clamp(get() + direction, 0, options.Count - 1)), game),
            () => Change(() => set((Mathf.Clamp(get(), 0, options.Count - 1) + 1) % options.Count), game), description);
    }

    private void AddFloat(string label, Func<float> get, Action<float> set, float min, float max, float step, Func<float, string> format,
        string description = "", bool game = true) =>
        AddRow(label, () => format(get()),
            direction => Change(() => set(NativeSettingsPanel.AdjustFloat(get(), direction * step, min, max, step)), game), null, description);

    private void AddInt(string label, Func<int> get, Action<int> set, int min, int max, int step, Func<int, string> format) =>
        AddRow(label, () => format(get()), direction => Change(() => set(Mathf.Clamp(get() + direction * step, min, max)), true), null, "");

    private void AddAction(string label, string value, Action action, string description) =>
        AddRow(label, () => value, null, action, description);

    private void Change(Action change, bool game)
    {
        change();
        if (!game) return;

        _gameSettingsChanged = true;
        PlayerPrefs.Save();
    }

    // adjust(direction) changes the value one step; activate is what a click or Select does, by default a step up.
    private void AddRow(string label, Func<string> read, Action<int>? adjust, Action? activate, string description)
    {
        var index = _rows.Count;
        if (index >= RowsPerColumn * ColumnX.Length) return;

        var position = new Vector2(ColumnX[index / RowsPerColumn], FirstRowY - index % RowsPerColumn * RowStep);
        var button = MenuKit.Button(_content, label, label, position, RowSize, () =>
        {
            if (activate != null) activate();
            else adjust?.Invoke(1);
            Refresh();
        }, MenuButton.Look.Secondary, MenuStyle.BodySize);
        if (button.Label != null) button.Label.margin = new Vector4(28f, 0f, ValueWidth + ArrowWidth * 2f + 16f, 0f);

        var valueX = RowSize.x / 2f - ArrowWidth - 8f - ValueWidth / 2f;
        var value = MenuKit.Text(button.transform, "Value", "", MenuStyle.BodySize, MenuStyle.Text, TextAlignmentOptions.Center,
            new Vector2(valueX, 0f), new Vector2(ValueWidth, RowSize.y));
        if (adjust != null)
        {
            // For the pointer; the controls use Left and Right on the row.
            AddArrow(button.transform, "<", valueX - ValueWidth / 2f - ArrowWidth / 2f, () => adjust(-1));
            AddArrow(button.transform, ">", valueX + ValueWidth / 2f + ArrowWidth / 2f, () => adjust(1));
            button.Adjusted += direction =>
            {
                adjust(direction);
                Refresh();
            };
        }

        Add(button);
        _order.Add(button);
        var row = new Row { Button = button, Value = value, Read = read, Description = description };
        _rows.Add(row);
        value.text = read();
    }

    private void AddArrow(Transform parent, string label, float x, Action adjust)
    {
        MenuKit.Button(parent, label, label, new Vector2(x, 0f), new Vector2(ArrowWidth, RowSize.y - 12f), () =>
        {
            adjust();
            Refresh();
        }, MenuButton.Look.Cell, MenuStyle.BodySize);
    }

    // Every row, as one setting can change what another shows.
    private void Refresh()
    {
        foreach (var row in _rows)
        {
            try
            {
                var text = row.Read();
                if (row.Value.text != text) row.Value.text = text;
            }
            catch (Exception)
            {
                row.Value.text = "-";
            }
        }
    }

    private static string Percent(float value) => $"{value * 100f:0}%";
    private static string Fraction(float value) => $"{value:0.00}";
    private static string Seconds(float value) => $"{value:0.00} s";
    private static string Degrees(float value) => $"{value:0.#} deg";
    private static string Pixels(float value) => $"{value:0} px";
    private static string Whole(float value) => $"{value:0}";
    private static string Times(float value) => $"{value:0.00}x";
    private static string Meters(float value) => $"{value:0.00} m";
    private static string Kilometres(float value) => $"{value:0.#} km";
}
