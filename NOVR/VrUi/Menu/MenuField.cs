using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Keyboard = UnityEngine.InputSystem.Keyboard;

namespace NOVR.VrUi.Menu;

// Text typed on the keyboard (a search, a lobby name, a password): one row button with its name on the left and the text
// in a box on the right. Select on the row, or a click on the box, starts typing; Enter, Escape or a click elsewhere stops
// it, and so do Select and Back on the joystick (MenuScreen). The box is Unity's plain input field, in its built-in font,
// as the game's font asset can't be typed into without parts of TextMeshPro the game itself never uses. Enter is told
// apart by watching the field (MenuFieldWatcher): the event the field raises for it changed type between Unity versions,
// so a build referring to it fails on one of them.
internal sealed class MenuField
{
    private const float BoxInset = 8f;
    private static int _typingUntilFrame = -1;
    private static Font? _font;
    private static bool _lookedForFont;

    public MenuField(Transform parent, string name, string label, string placeholder, Vector2 position, Vector2 size,
        float labelWidth, bool password = false, int characterLimit = 0)
    {
        Button = MenuKit.Button(parent, name, label, position, size, Begin, MenuButton.Look.Secondary, MenuStyle.BodySize);
        if (Button.Label != null) Button.Label.margin = new Vector4(28f, 0f, size.x - labelWidth, 0f);

        var boxWidth = size.x - labelWidth - BoxInset;
        var box = MenuKit.Image(Button.transform, "Box", MenuStyle.Panel.WithAlpha(0.95f),
            new Vector2(size.x / 2f - BoxInset - boxWidth / 2f, 0f), new Vector2(boxWidth, size.y - 2f * BoxInset));
        box.raycastTarget = true;

        Input = box.gameObject.AddComponent<InputField>();
        Input.targetGraphic = box;
        Input.transition = Selectable.Transition.None;
        Input.navigation = new Navigation { mode = Navigation.Mode.None };
        Input.lineType = InputField.LineType.SingleLine;
        Input.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
        Input.characterLimit = characterLimit;
        Input.customCaretColor = true;
        Input.caretColor = MenuStyle.Accent;
        Input.caretWidth = 3;
        Input.selectionColor = MenuStyle.Accent.WithAlpha(0.35f);

        var textSize = Mathf.RoundToInt(MenuStyle.SmallSize);
        Input.textComponent = BoxText(box.transform, "Text", "", textSize, MenuStyle.Text);
        var hint = BoxText(box.transform, "Placeholder", placeholder, textSize, MenuStyle.TextDim.WithAlpha(0.7f));
        hint.fontStyle = FontStyle.Italic;
        Input.placeholder = hint;
        Input.onValueChanged.AddListener(text => Changed?.Invoke(text));
        box.gameObject.AddComponent<MenuFieldWatcher>().Field = this;
    }

    public MenuButton Button { get; }
    public InputField Input { get; }
    public event Action<string>? Changed;

    // Typing ended with Enter, with what was typed.
    public Action<string>? Submitted { get; set; }

    public string Value
    {
        get => Input.text;
        set => Input.text = value ?? "";
    }

    // Typing now, or a moment ago: the Enter or Escape that ended it mustn't also act on the menu.
    public static bool Typing
    {
        get
        {
            var eventSystem = EventSystem.current;
            var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected != null && selected.TryGetComponent<InputField>(out var input) && input.isFocused) _typingUntilFrame = Time.frameCount + 2;
            return Time.frameCount <= _typingUntilFrame;
        }
    }

    public static void StopTyping()
    {
        var eventSystem = EventSystem.current;
        var selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
        if (selected != null && selected.TryGetComponent<InputField>(out var input)) input.DeactivateInputField();
        if (eventSystem != null) eventSystem.SetSelectedGameObject(null);
        _typingUntilFrame = Time.frameCount + 2;
    }

    public void Begin()
    {
        if (!Input.isActiveAndEnabled || !Input.interactable) return;

        Input.Select();
        Input.ActivateInputField();
    }

    private static Text BoxText(Transform box, string name, string text, int size, Color color)
    {
        var rect = MenuKit.Rect(box, name, Vector2.zero, Vector2.zero);
        MenuKit.Stretch(rect);
        rect.offsetMin = new Vector2(14f, 4f);
        rect.offsetMax = new Vector2(-14f, -4f);
        var label = rect.gameObject.AddComponent<Text>();
        label.font = LegacyFont();
        label.fontSize = size;
        label.color = color;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.supportRichText = false;
        label.raycastTarget = false;
        label.text = text;
        return label;
    }

    // Unity's built-in font, under its name since 2022 or before.
    private static Font? LegacyFont()
    {
        if (_lookedForFont) return _font;

        _lookedForFont = true;
        foreach (var name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try
            {
                _font = Resources.GetBuiltinResource<Font>(name);
            }
            catch (Exception)
            {
                // Not under this name.
            }

            if (_font != null) break;
        }

        return _font;
    }
}

// Tells when typing in a MenuField ends with Enter: the field stops being focused within a frame of Enter going down.
internal sealed class MenuFieldWatcher : MonoBehaviour
{
    private bool _wasFocused;
    private int _enterFrame = -10;

    public MenuField? Field { get; set; }

    private void Update()
    {
        var field = Field;
        if (field == null) return;

        var keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) _enterFrame = Time.frameCount;

        var focused = field.Input.isFocused;
        if (_wasFocused && !focused && Time.frameCount - _enterFrame <= 1) field.Submitted?.Invoke(field.Value);
        _wasFocused = focused;
    }
}
