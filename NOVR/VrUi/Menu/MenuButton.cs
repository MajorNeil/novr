using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NOVR.VrUi.Menu;

// A menu button that is used the same way with every control: pointing at it (mouse, gaze) focuses it, the menu keys
// move the focus to it, and a click, the cursor's select button or the Select binding activates it. Unity's own
// selection and navigation are off: the screen owns the focus, so a mouse click and a key press can't disagree about
// which button is current. Two activations within a moment are one, as a joystick button can be both a cursor click
// and the menu's Select.
public class MenuButton : Button
{
    public enum Look
    {
        Primary,
        Secondary,
        Danger,
        Cell
    }

    private const float AnimateSeconds = 0.08f;
    private const float RepeatGuardSeconds = 0.3f;
    private const float FocusBarWidth = 6f;

    private Image? _background;
    private Image? _bar;
    private TextMeshProUGUI? _label;
    private Look _look;
    private float _focusAmount;
    private float _lastActivated = -10f;
    private bool _highlight;

    public event Action? Activated;
    public event Action<MenuButton>? Hovered;
    // Left and right change its value (a setting) instead of moving the focus.
    public event Action<int>? Adjusted;

    public bool Focused { get; private set; }
    public bool Adjustable => Adjusted != null;
    public TextMeshProUGUI? Label => _label;
    public RectTransform Rect => (RectTransform)transform;

    // Highlighted without focus: the binding being listened for, or a selected entry.
    public bool Highlight
    {
        get => _highlight;
        set => _highlight = value;
    }

    public void Setup(Image background, TextMeshProUGUI label, Look look)
    {
        _background = background;
        _label = label;
        _look = look;
        targetGraphic = background;
        transition = Transition.None;
        navigation = new Navigation { mode = Navigation.Mode.None };

        _bar = MenuKit.Image(transform, "Focus Bar", MenuStyle.Accent, Vector2.zero, Vector2.zero);
        var barRect = _bar.rectTransform;
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(0f, 1f);
        barRect.pivot = new Vector2(0f, 0.5f);
        barRect.sizeDelta = new Vector2(FocusBarWidth, 0f);
        barRect.anchoredPosition = Vector2.zero;
        if (look == Look.Danger) _bar.color = MenuStyle.Danger;

        Apply(instant: true);
    }

    public void SetLabel(string text)
    {
        if (_label != null) _label.text = text;
    }

    public void SetFocused(bool focused)
    {
        Focused = focused;
    }

    public void Activate()
    {
        if (!IsActive() || !IsInteractable()) return;

        var now = Time.unscaledTime;
        if (now - _lastActivated < RepeatGuardSeconds) return;

        _lastActivated = now;
        MenuSounds.Select();
        Activated?.Invoke();
    }

    public void Adjust(int direction)
    {
        if (!IsActive() || !IsInteractable() || Adjusted == null) return;

        MenuSounds.Move();
        Adjusted(direction);
    }

    public override void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;

        Activate();
    }

    public override void OnSubmit(BaseEventData eventData)
    {
        Activate();
    }

    public override void OnPointerEnter(PointerEventData eventData)
    {
        base.OnPointerEnter(eventData);
        if (IsInteractable()) Hovered?.Invoke(this);
    }

    private void Update()
    {
        Apply(instant: false);
    }

    private void Apply(bool instant)
    {
        if (_background == null || _label == null || _bar == null) return;

        var target = Focused || _highlight ? 1f : 0f;
        _focusAmount = instant ? target : Mathf.MoveTowards(_focusAmount, target, Time.unscaledDeltaTime / AnimateSeconds);
        var pressed = IsPressed() ? 0.75f : 1f;
        var enabled = IsInteractable() ? 1f : 0.4f;

        var normal = _look == Look.Secondary ? MenuStyle.Button.WithAlpha(MenuStyle.Button.a * 0.6f) : MenuStyle.Button;
        var focus = _look == Look.Danger ? Color.Lerp(MenuStyle.ButtonFocus, MenuStyle.Danger, 0.45f) : MenuStyle.ButtonFocus;
        var background = Color.Lerp(normal, focus, _focusAmount);
        background.r *= pressed;
        background.g *= pressed;
        background.b *= pressed;
        background.a *= enabled;
        _background.color = background;

        var text = Color.Lerp(_look == Look.Secondary ? MenuStyle.TextDim : MenuStyle.Text, MenuStyle.Text, _focusAmount);
        if (_highlight && _look == Look.Cell) text = MenuStyle.Accent;
        text.a *= enabled;
        _label.color = text;

        var bar = _bar.color;
        bar.a = Focused ? _focusAmount : 0f;
        _bar.color = bar;
    }
}

internal static class ColorExtensions
{
    public static Color WithAlpha(this Color color, float alpha) => new(color.r, color.g, color.b, alpha);
}
