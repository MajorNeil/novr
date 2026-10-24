using System.Collections.Generic;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// One of NOVR's menu screens on the native menu canvas. It owns the focus among its buttons: pointing at a button moves the
// focus there, the direction bindings move it to the nearest button that way, Select activates it and Back goes back.
// The focus stays where it is when the pointer leaves, so switching between the cursor and the controls never loses it.
// A list of settings can be given an order, which Up and Down follow (down one column and on into the next), while Left
// and Right change the focused setting's value. A screen can have several (a list and a column of settings); stepping
// within one can scroll its list (OnOrderStep), and past either end the screen decides (OnOrderEnd), else the focus goes
// on to the nearest button that way. Left and Right off a button that isn't a setting can be sent somewhere set
// (OnSideways), so they always lead the same way: from the panel into the screen's content and back, say.
// While a text field is being typed into, the keyboard is left to it. The joystick stops the typing: Select and Back
// just stop it, the directions stop it and move on.
internal abstract class MenuScreen
{
    private const float FadeSeconds = 0.12f;
    // Moving the focus prefers a button straight that way over a nearer one off to the side.
    private const float SidewaysPenalty = 2.5f;

    private readonly List<MenuButton> _buttons = new();
    private readonly CanvasGroup _group;
    private MenuButton? _focused;
    private readonly List<List<MenuButton>> _orders = new();
    private ICollection<MenuButton>? _modal;
    private bool _active;

    protected MenuScreen(RectTransform canvas, string name)
    {
        Root = MenuKit.Rect(canvas, name, Vector2.zero, canvas.sizeDelta);
        _group = Root.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.interactable = false;
        _group.blocksRaycasts = false;
        Root.gameObject.SetActive(false);
    }

    public RectTransform Root { get; }
    public bool Visible { get; private set; }

    public void SetVisible(bool visible)
    {
        if (Visible == visible) return;

        Visible = visible;
        _group.interactable = visible;
        _group.blocksRaycasts = visible;
        if (visible)
        {
            _active = true;
            Root.gameObject.SetActive(true);
            OnShown();
            if (_focused == null || !Usable(_focused)) Focus(FirstFocus(), sound: false);
        }
        else
        {
            OnHidden();
        }
    }

    // Every frame, from the native menu root.
    public void Tick()
    {
        if (_active) Fade();
        if (!Visible) return;

        OnTick();
        if (MenuInput.IsRebinding) return;
        if (MenuField.Typing)
        {
            if (MenuInput.PressedOn(MenuCommand.Select, MenuDevice.Joystick) || MenuInput.PressedOn(MenuCommand.Back, MenuDevice.Joystick))
            {
                MenuField.StopTyping();
                return;
            }

            if (!JoystickMoved()) return;

            MenuField.StopTyping();
        }

        if (MenuInput.Pressed(MenuCommand.Up)) Step(Vector2.up);
        if (MenuInput.Pressed(MenuCommand.Down)) Step(Vector2.down);
        if (MenuInput.Pressed(MenuCommand.Left)) Step(Vector2.left);
        if (MenuInput.Pressed(MenuCommand.Right)) Step(Vector2.right);
        if (MenuInput.Pressed(MenuCommand.PreviousTab)) OnTab(-1);
        if (MenuInput.Pressed(MenuCommand.NextTab)) OnTab(1);
        if (MenuInput.Pressed(MenuCommand.Select) && _focused != null && Usable(_focused)) _focused.Activate();
        if (MenuInput.Pressed(MenuCommand.Back) && OnBack()) MenuSounds.Back();
    }

    protected MenuButton Add(MenuButton button)
    {
        _buttons.Add(button);
        button.Hovered += hovered => Focus(hovered, sound: false);
        return button;
    }

    protected void Remove(MenuButton button)
    {
        _buttons.Remove(button);
        if (_focused == button) _focused = null;
    }

    protected void SetOrder(List<MenuButton>? order)
    {
        _orders.Clear();
        if (order != null) _orders.Add(order);
    }

    protected void SetOrders(params List<MenuButton>[] orders)
    {
        _orders.Clear();
        _orders.AddRange(orders);
    }

    protected MenuButton? Focused => _focused;

    protected bool CanFocus(MenuButton? button) => button != null && Usable(button);

    // The first of these that can take the focus; false when none can.
    protected bool FocusFirst(params MenuButton?[] buttons)
    {
        foreach (var button in buttons)
        {
            if (!CanFocus(button)) continue;

            Focus(button, sound: true);
            return true;
        }

        return false;
    }

    // Only these take the focus while set: a popup over the screen (which blocks the pointer itself).
    protected void SetModal(ICollection<MenuButton>? buttons, MenuButton? focus)
    {
        _modal = buttons;
        if (focus != null) Focus(focus, sound: false);
        else if (_focused != null && !Usable(_focused)) Focus(FirstFocus(), sound: false);
    }

    protected void Focus(MenuButton? button, bool sound)
    {
        if (button == _focused) return;

        if (_focused != null) _focused.SetFocused(false);
        _focused = button;
        if (_focused != null) _focused.SetFocused(true);
        if (sound && button != null) MenuSounds.Move();
        OnFocused(button);
    }

    protected virtual MenuButton? FirstFocus()
    {
        foreach (var button in _buttons)
        {
            if (Usable(button)) return button;
        }

        return null;
    }

    protected virtual void OnShown()
    {
    }

    protected virtual void OnHidden()
    {
    }

    protected virtual void OnTick()
    {
    }

    protected virtual void OnFocused(MenuButton? button)
    {
    }

    // True when it went somewhere, for the sound.
    protected virtual bool OnBack() => false;

    // Up or Down (step -1 or 1) past the end of an order; true when the screen took it (scrolled its list, or keeps the
    // focus where it is), false to go on to the nearest button that way.
    protected virtual bool OnOrderEnd(List<MenuButton> order, int step) => false;

    // Up or Down within an order, before the focus moves on; true when the screen took it (scrolled its list instead).
    protected virtual bool OnOrderStep(List<MenuButton> order, MenuButton focused, int step) => false;

    // Left (-1) or Right (1) off a button that isn't a setting; true when the screen moved the focus (or keeps it).
    protected virtual bool OnSideways(MenuButton focused, int direction) => false;

    protected virtual void OnTab(int direction)
    {
    }

    private void Fade()
    {
        var target = Visible ? 1f : 0f;
        _group.alpha = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime / FadeSeconds);
        if (Visible || _group.alpha > 0f) return;

        _active = false;
        Root.gameObject.SetActive(false);
    }

    private void Step(Vector2 direction)
    {
        if (_focused != null && Usable(_focused))
        {
            if (direction.x != 0f && _focused.Adjustable)
            {
                _focused.Adjust(direction.x > 0f ? 1 : -1);
                return;
            }

            if (direction.x != 0f && OnSideways(_focused, direction.x > 0f ? 1 : -1)) return;

            var order = direction.y != 0f ? _orders.Find(candidate => candidate.Contains(_focused)) : null;
            if (order != null)
            {
                var step = direction.y > 0f ? -1 : 1;
                if (OnOrderStep(order, _focused, step)) return;

                for (var next = order.IndexOf(_focused) + step; next >= 0 && next < order.Count; next += step)
                {
                    if (!Usable(order[next])) continue;

                    Focus(order[next], sound: true);
                    return;
                }

                if (OnOrderEnd(order, step)) return;
            }
        }

        Move(direction);
    }

    private void Move(Vector2 direction)
    {
        if (_focused == null || !Usable(_focused))
        {
            Focus(FirstFocus(), sound: true);
            return;
        }

        var from = Center(_focused);
        var sideways = new Vector2(-direction.y, direction.x);
        MenuButton? best = null;
        var bestScore = float.MaxValue;
        foreach (var button in _buttons)
        {
            if (button == _focused || !Usable(button)) continue;

            var offset = Center(button) - from;
            var along = Vector2.Dot(offset, direction);
            if (along <= 1f) continue;

            var score = along + Mathf.Abs(Vector2.Dot(offset, sideways)) * SidewaysPenalty;
            if (score >= bestScore) continue;

            best = button;
            bestScore = score;
        }

        if (best != null) Focus(best, sound: true);
    }

    private static bool JoystickMoved() =>
        MenuInput.PressedOn(MenuCommand.Up, MenuDevice.Joystick) || MenuInput.PressedOn(MenuCommand.Down, MenuDevice.Joystick) ||
        MenuInput.PressedOn(MenuCommand.Left, MenuDevice.Joystick) || MenuInput.PressedOn(MenuCommand.Right, MenuDevice.Joystick) ||
        MenuInput.PressedOn(MenuCommand.PreviousTab, MenuDevice.Joystick) || MenuInput.PressedOn(MenuCommand.NextTab, MenuDevice.Joystick);

    private Vector2 Center(MenuButton button) =>
        Root.InverseTransformPoint(button.Rect.TransformPoint(button.Rect.rect.center));

    private bool Usable(MenuButton button) =>
        button != null && button.gameObject.activeInHierarchy && button.interactable && (_modal == null || _modal.Contains(button));
}
