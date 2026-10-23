using System.Collections.Generic;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// One of NOVR's menu screens on the native menu canvas. It owns the focus among its buttons: pointing at a button moves the
// focus there, the direction bindings move it to the nearest button that way, Select activates it and Back goes back.
// The focus stays where it is when the pointer leaves, so switching between the cursor and the controls never loses it.
internal abstract class MenuScreen
{
    private const float FadeSeconds = 0.12f;
    // Moving the focus prefers a button straight that way over a nearer one off to the side.
    private const float SidewaysPenalty = 2.5f;

    private readonly List<MenuButton> _buttons = new();
    private readonly CanvasGroup _group;
    private MenuButton? _focused;
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

        if (MenuInput.Pressed(MenuCommand.Up)) Move(Vector2.up);
        if (MenuInput.Pressed(MenuCommand.Down)) Move(Vector2.down);
        if (MenuInput.Pressed(MenuCommand.Left)) Move(Vector2.left);
        if (MenuInput.Pressed(MenuCommand.Right)) Move(Vector2.right);
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

    protected void Focus(MenuButton? button, bool sound)
    {
        if (button == _focused) return;

        _focused?.SetFocused(false);
        _focused = button;
        _focused?.SetFocused(true);
        if (sound && button != null) MenuSounds.Move();
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

    // True when it went somewhere, for the sound.
    protected virtual bool OnBack() => false;

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

    private Vector2 Center(MenuButton button) =>
        Root.InverseTransformPoint(button.Rect.TransformPoint(button.Rect.rect.center));

    private static bool Usable(MenuButton button) =>
        button != null && button.gameObject.activeInHierarchy && button.interactable;
}
