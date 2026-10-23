using System;
using System.Text;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.Menu;

// The look of the game's own menus, read from the running game so NOVR's menus are part of it: the font, text colour and
// button colours of its main menu buttons, the heading font from its largest text, and the accent and alert colours of
// its theme. Changed only where a headset needs it: text a little off white (pure white glares in the lenses), button
// backings kept solid enough to read against the hangar behind them, and the sizes below, which are for reading at a few
// meters rather than on a monitor.
internal static class MenuStyle
{
    // In units of the native menu canvas, which is 2000 x 1125 and about 66 degrees across at the default scale.
    public const float TitleSize = 60f;
    public const float HeadingSize = 42f;
    public const float ButtonSize = 34f;
    public const float BodySize = 28f;
    public const float SmallSize = 23f;

    private const string MainMenuButtonsPath = "MenuButtonsPanel";
    private const float MaxTextBrightness = 0.93f;
    private const float MinButtonAlpha = 0.6f;

    public static TMP_FontAsset? Font { get; private set; }
    public static TMP_FontAsset? HeadingFont { get; private set; }
    public static Color Text { get; private set; } = new(0.92f, 0.93f, 0.93f, 1f);
    public static Color TextDim { get; private set; } = new(0.62f, 0.66f, 0.68f, 1f);
    public static Color Panel { get; private set; } = new(0.03f, 0.04f, 0.05f, 0.88f);
    public static Color Button { get; private set; } = new(0.12f, 0.15f, 0.17f, 0.86f);
    public static Color ButtonFocus { get; private set; } = new(0.21f, 0.26f, 0.29f, 0.96f);
    public static Color Accent { get; private set; } = new(0.93f, 0.68f, 0.24f, 1f);
    public static Color Danger { get; private set; } = new(0.82f, 0.25f, 0.2f, 1f);

    private static bool _captured;

    public static string Hex(Color color) => ColorUtility.ToHtmlStringRGB(color);

    // Reads the game's main menu under its MainCanvas; false until its buttons are there to read.
    public static bool Capture(GameObject? mainCanvas)
    {
        if (_captured) return true;
        if (mainCanvas == null) return false;

        try
        {
            var button = FindMainMenuButton(mainCanvas);
            if (button == null) return false;

            _captured = true;
            ReadButton(button);
            HeadingFont = LargestTextFont(mainCanvas) ?? Font;
            var labels = ReadTheme();
            NOVRPlugin.Log.LogInfo($"[NOVR] Menu style: font '{Name(Font)}', headings '{Name(HeadingFont)}', text #{Hex(Text)}, " +
                                   $"button #{Hex(Button)}/{Button.a:0.00}, focus #{Hex(ButtonFocus)}, accent #{Hex(Accent)}; theme labels: {labels}");
        }
        catch (Exception exception)
        {
            _captured = true;
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not read the game's menu style, using NOVR's own: {exception.Message}");
        }

        return true;
    }

    private static Button? FindMainMenuButton(GameObject mainCanvas)
    {
        foreach (var button in mainCanvas.GetComponentsInChildren<Button>(true))
        {
            var parent = button.transform.parent;
            if (parent == null || parent.name != MainMenuButtonsPath) continue;
            if (button.GetComponentInChildren<TMP_Text>(true) != null) return button;
        }

        return null;
    }

    private static void ReadButton(Button button)
    {
        var label = button.GetComponentInChildren<TMP_Text>(true);
        if (label.font != null) Font = label.font;

        // Kept only when it is light text, which is what the dark backings here are made for.
        var text = label.color;
        if (Luminance(text) > 0.6f) Text = Softened(text);

        var graphic = button.targetGraphic;
        if (graphic == null) return;

        // What Unity draws is the graphic's colour times the state's tint. Text-only buttons (nearly clear) keep the
        // backings here, as clear ones are hard to read over the hangar.
        var colors = button.colors;
        var normal = graphic.color * colors.normalColor;
        var focus = graphic.color * colors.highlightedColor;
        if (normal.a < 0.3f || Luminance(normal) > 0.45f) return;

        Button = WithMinAlpha(normal, MinButtonAlpha);
        ButtonFocus = WithMinAlpha(focus, Mathf.Max(Button.a, 0.9f));
    }

    private static TMP_FontAsset? LargestTextFont(GameObject mainCanvas)
    {
        TMP_FontAsset? font = null;
        var largest = 0f;
        foreach (var text in mainCanvas.GetComponentsInChildren<TMP_Text>(false))
        {
            if (text.font == null || text.fontSize <= largest) continue;

            largest = text.fontSize;
            font = text.font;
        }

        return font;
    }

    // The theme's button colour is what the game lights its buttons with on hover (ButtonStyleApplier), so it is the accent.
    private static string ReadTheme()
    {
        var group = ThemeManager.Active;
        if (group == null) return "none";

        var colors = group.ColorTheme;
        if (colors != null && colors.Alert.maxColorComponent > 0.3f) Danger = Opaque(colors.Alert);

        var theme = group.MenuTheme;
        if (theme == null) return "none";

        var labels = new StringBuilder();
        var accentFound = false;
        if (theme.ButtonStyles != null)
        {
            foreach (var item in theme.ButtonStyles)
            {
                if (item?.Style == null) continue;

                Append(labels, item.labelName);
                var color = item.Style.Color;
                if (accentFound || color.maxColorComponent < 0.35f) continue;

                Accent = Opaque(color);
                accentFound = true;
            }
        }

        if (theme.TextStyles != null)
        {
            foreach (var item in theme.TextStyles)
            {
                if (item?.Style != null) Append(labels, $"{item.labelName}:{Name(item.Style.Font)}");
            }
        }

        return labels.Length > 0 ? labels.ToString() : "none";
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (builder.Length > 0) builder.Append(", ");
        builder.Append(value ?? "?");
    }

    private static string Name(TMP_FontAsset? font) => font != null ? font.name : "default";

    private static float Luminance(Color color) => 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;

    private static Color Opaque(Color color) => new(color.r, color.g, color.b, 1f);

    private static Color WithMinAlpha(Color color, float alpha) => new(color.r, color.g, color.b, Mathf.Max(color.a, alpha));

    private static Color Softened(Color color)
    {
        var max = color.maxColorComponent;
        var scale = max > MaxTextBrightness ? MaxTextBrightness / max : 1f;
        return new Color(color.r * scale, color.g * scale, color.b * scale, 1f);
    }
}
