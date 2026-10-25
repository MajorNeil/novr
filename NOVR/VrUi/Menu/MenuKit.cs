using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.Menu;

// Builds the parts NOVR's menus are made of, in the game's menu style (MenuStyle). Positions and sizes are in canvas units
// from the parent's centre unless stated otherwise.
internal static class MenuKit
{
    private static int Layer => (int)LayerHelper.GetVrUiLayer();

    public static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
    {
        var gameObject = new GameObject(name, typeof(RectTransform)) { layer = Layer };
        var rect = (RectTransform)gameObject.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        return rect;
    }

    // Covers its parent, less the inset on every side.
    public static void Stretch(RectTransform rect, float inset = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    public static Image Image(Transform parent, string name, Color color, Vector2 position, Vector2 size)
    {
        var image = Rect(parent, name, position, size).gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    // A backing for a group of controls, with the thin accent rule along its top that the game's panels have.
    public static Image Panel(Transform parent, string name, Vector2 position, Vector2 size)
    {
        var panel = Image(parent, name, MenuStyle.Panel, position, size);
        var rule = Image(panel.transform, "Rule", MenuStyle.Accent.WithAlpha(0.8f), Vector2.zero, Vector2.zero);
        rule.rectTransform.anchorMin = new Vector2(0f, 1f);
        rule.rectTransform.anchorMax = new Vector2(1f, 1f);
        rule.rectTransform.pivot = new Vector2(0.5f, 1f);
        rule.rectTransform.sizeDelta = new Vector2(0f, 3f);
        rule.rectTransform.anchoredPosition = Vector2.zero;
        return panel;
    }

    public static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Color color,
        TextAlignmentOptions alignment, Vector2 position, Vector2 box, bool heading = false, bool wrap = false)
    {
        var label = Rect(parent, name, position, box).gameObject.AddComponent<TextMeshProUGUI>();
        var font = heading ? MenuStyle.HeadingFont ?? MenuStyle.Font : MenuStyle.Font;
        if (font != null) label.font = font;
        label.fontSize = size;
        label.color = color;
        label.alignment = alignment;
        label.enableWordWrapping = wrap;
        label.overflowMode = wrap ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;
        // A line too long for its box (the game's font is a wide one) shrinks a little before it is cut short.
        if (!wrap)
        {
            label.enableAutoSizing = true;
            label.fontSizeMin = size * 0.6f;
            label.fontSizeMax = size;
        }

        label.richText = true;
        label.raycastTarget = false;
        label.text = text;
        return label;
    }

    public static MenuButton Button(Transform parent, string name, string label, Vector2 position, Vector2 size,
        Action onActivate, MenuButton.Look look = MenuButton.Look.Primary, float? fontSize = null)
    {
        var rect = Rect(parent, name, position, size);
        var background = rect.gameObject.AddComponent<Image>();
        background.color = MenuStyle.Button;

        var textSize = fontSize ?? (look == MenuButton.Look.Primary || look == MenuButton.Look.Danger ? MenuStyle.ButtonSize : MenuStyle.BodySize);
        var alignment = look == MenuButton.Look.Cell ? TextAlignmentOptions.Center : TextAlignmentOptions.Left;
        var text = Text(rect, "Label", label, textSize, MenuStyle.Text, alignment, Vector2.zero, size);
        Stretch(text.rectTransform);
        text.margin = look == MenuButton.Look.Cell ? new Vector4(12f, 0f, 12f, 0f) : new Vector4(32f, 0f, 16f, 0f);

        var button = rect.gameObject.AddComponent<MenuButton>();
        button.Setup(background, text, look);
        button.Activated += onActivate;
        return button;
    }
}
