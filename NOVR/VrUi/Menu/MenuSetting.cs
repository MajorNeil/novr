using System;
using TMPro;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// One setting as one row button, as in the settings screen: its name on the left and its value on the right, between
// arrows for the pointer (the controls use Left and Right on the row). Select or a click runs activate, else steps the
// value on. changed runs after either, for the screen to refresh what depends on it.
internal sealed class MenuSetting
{
    private const float ArrowWidth = 40f;

    private readonly Func<string> _read;

    public MenuSetting(Transform parent, string label, Vector2 position, Vector2 size, float valueWidth, Func<string> read,
        Action<int>? adjust, Action? activate, Action changed)
    {
        _read = read;
        Button = MenuKit.Button(parent, label, label, position, size, () =>
        {
            if (activate != null) activate();
            else adjust?.Invoke(1);
            changed();
        }, MenuButton.Look.Secondary, MenuStyle.BodySize);
        var arrows = adjust != null ? ArrowWidth * 2f : 0f;
        if (Button.Label != null) Button.Label.margin = new Vector4(28f, 0f, valueWidth + arrows + 16f, 0f);

        var valueX = size.x / 2f - arrows / 2f - 8f - valueWidth / 2f;
        Value = MenuKit.Text(Button.transform, "Value", "", MenuStyle.BodySize, MenuStyle.Text, TextAlignmentOptions.Center,
            new Vector2(valueX, 0f), new Vector2(valueWidth, size.y));
        if (adjust != null)
        {
            Arrow("<", valueX - valueWidth / 2f - ArrowWidth / 2f, size.y, () => adjust(-1), changed);
            Arrow(">", valueX + valueWidth / 2f + ArrowWidth / 2f, size.y, () => adjust(1), changed);
            Button.Adjusted += direction =>
            {
                adjust(direction);
                changed();
            };
        }

        Refresh();
    }

    public MenuButton Button { get; }
    public TextMeshProUGUI Value { get; }

    public static string OnOff(bool on) => on ? $"<color=#{MenuStyle.Hex(MenuStyle.Accent)}>ON</color>" : "OFF";

    public void Refresh()
    {
        string text;
        try
        {
            text = _read();
        }
        catch (Exception)
        {
            text = "-";
        }

        if (Value.text != text) Value.text = text;
    }

    private void Arrow(string label, float x, float height, Action adjust, Action changed)
    {
        MenuKit.Button(Button.transform, label, label, new Vector2(x, 0f), new Vector2(ArrowWidth, height - 12f), () =>
        {
            adjust();
            changed();
        }, MenuButton.Look.Cell, MenuStyle.BodySize);
    }
}
