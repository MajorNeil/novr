using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NOVR.VrUi;

// The cursor moved from a stick (Menu.MenuNavigation), in menus that aren't NOVR's own screens: each step puts it on the
// next button that way, as seen from the seat, and it stays there whatever the mouse or the eyes point at, until the
// mouse is really moved or the eyes rest well away from it for a moment. Items scrolled out of their list are scrolled
// in; a slider takes the steps along it itself.
public partial class VrUiCursor
{
    // Sideways counts double: down from a button is the one below it, not a nearer one off to the side.
    private const float NavLateralWeight = 2f;
    private const float NavMinimumStepDegrees = 0.3f;
    private const int MaxNavReachChecks = 24;
    private const float NavGazeReleaseDegrees = 8f;
    private const float NavGazeReleaseSeconds = 1f;
    // Of the list's height, kept showing past the item scrolled to, so the next one shows too.
    private const float NavScrollMargin = 0.1f;
    private const float NavPageFraction = 0.8f;
    private const float NavSliderStep = 0.05f;

    // In the order Back prefers them: a popup's Cancel before the screen's Done behind it.
    private static readonly string[] BackWords = { "CANCEL", "BACK", "CLOSE", "RETURN", "DONE", "X", "✕", "×" };
    // Never pressed for Back: these leave the game or lose something.
    private static readonly string[] NotBackWords = { "EXIT", "QUIT", "DELETE", "REMOVE", "LEAVE", "DISCONNECT" };

    private Component? _navTarget;
    private bool _navPinned;
    private Vector3 _navDirection;
    private float _navMouseTravel;
    private float _navGazeAwaySince = float.PositiveInfinity;
    private bool _navClickHeld;
    private bool _navClickPressedThisFrame;
    private readonly List<(Component Target, float Cost)> _navCandidates = new();
    private readonly Vector3[] _navCorners = new Vector3[4];

    // The cursor held on what Navigate put it on; the virtual mouse's left button is down while held.
    public void SetNavigationClick(bool held)
    {
        if (held && !_navClickHeld) _navClickPressedThisFrame = true;
        _navClickHeld = held;
    }

    public void ReleaseNavigation()
    {
        _navPinned = false;
        _navTarget = null;
        _navMouseTravel = 0f;
        _navGazeAwaySince = float.PositiveInfinity;
    }

    // One step (up, down, left or right): onto the nearest button that way that a click would reach. The first step,
    // with the cursor on nothing, goes to the button nearest it. Returns whether the cursor went anywhere.
    public bool Navigate(Vector2 step)
    {
        var camera = UiCamera;
        if (camera == null || !IsActive) return false;

        var origin = camera.transform.position;
        var reference = GetProjectionReferenceRotation();
        var current = _navPinned && _navTarget != null ? _navTarget : GetTargetUnderCursor(camera);
        if (current != null && !IsSnappable(current)) current = null;

        if (current is Slider slider && AdjustSlider(slider, step))
        {
            Pin(slider, origin);
            return true;
        }

        var from = ToNavAngles(reference, _navPinned ? _navDirection : _cursorDirection);
        CollectNavCandidates(origin, reference, from, current, step);
        var checks = 0;
        foreach (var candidate in _navCandidates)
        {
            if (checks++ >= MaxNavReachChecks) break;
            if (!TryReach(camera, candidate.Target)) continue;

            _navCandidates.Clear();
            Pin(candidate.Target, origin);
            return true;
        }

        _navCandidates.Clear();
        // Nothing further that way: it stays, now held where it is.
        if (current != null) Pin(current, origin);
        return false;
    }

    // Back, for a menu that isn't NOVR's own: presses its Cancel, Back, Close or Done button, whichever a click there
    // would reach.
    public bool NavigateBack()
    {
        var camera = UiCamera;
        if (camera == null || !IsActive || EventSystem.current == null) return false;

        var origin = camera.transform.position;
        Button? best = null;
        var bestRank = int.MaxValue;
        var count = FillSelectableBuffer();
        for (var i = 0; i < count; i++)
        {
            if (_selectableBuffer[i] is not Button button || !IsSnappable(button) || IsBackdrop(button, origin)) continue;

            var rank = BackRank(button);
            if (rank < 0 || rank >= bestRank) continue;
            if (!TryGetNavDirection(button, origin, out var direction) || !IsTopmostAt(camera, button, direction)) continue;

            best = button;
            bestRank = rank;
        }

        System.Array.Clear(_selectableBuffer, 0, count);
        if (best == null) return false;

        var target = best.gameObject;
        if (!ExecuteEvents.Execute(target, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler))
        {
            ExecuteEvents.Execute(target, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
        }

        // What it was on is likely gone with the screen; the cursor stays where it is until the next step.
        if (_navPinned) _navTarget = null;
        return true;
    }

    // Previous/Next Tab: a page up or down the list or text under the cursor.
    public bool Page(int direction)
    {
        var camera = UiCamera;
        if (camera == null || !IsActive) return false;

        ScrollRect? scroll = null;
        if (_navPinned && _navTarget != null) scroll = _navTarget.GetComponentInParent<ScrollRect>();
        if (scroll == null && TryGetTopRaycastResult(GetScreenPoint(), out var hit)) scroll = hit.gameObject.GetComponentInParent<ScrollRect>();
        if (scroll == null || !scroll.isActiveAndEnabled || scroll.content == null) return false;

        var viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
        scroll.StopMovement();
        if (scroll.vertical)
        {
            var hidden = scroll.content.rect.height - viewport.rect.height;
            if (hidden <= 0f) return false;
            scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition - direction * NavPageFraction * viewport.rect.height / hidden);
        }
        else if (scroll.horizontal)
        {
            var hidden = scroll.content.rect.width - viewport.rect.width;
            if (hidden <= 0f) return false;
            scroll.horizontalNormalizedPosition = Mathf.Clamp01(scroll.horizontalNormalizedPosition + direction * NavPageFraction * viewport.rect.width / hidden);
        }

        // The next step goes from what is under the cursor now.
        if (_navPinned) _navTarget = null;
        return true;
    }

    // Called with where the mouse or the eyes put the cursor; while held, where Navigate put it instead.
    private Vector3 HoldNavigation(Mouse mouse, Vector3 pointerDirection)
    {
        if (!_navPinned) return pointerDirection;

        _navMouseTravel += mouse.delta.ReadValue().magnitude;
        if (_navMouseTravel >= HybridTakeoverPixels)
        {
            // The mouse carries on from here rather than from wherever it was left.
            WarpMouseTo(_navDirection);
            var held = _navDirection;
            ReleaseNavigation();
            return held;
        }

        if (_gazeDriving && !_navClickHeld)
        {
            var now = Time.unscaledTime;
            if (Vector3.Angle(pointerDirection, _navDirection) < NavGazeReleaseDegrees) _navGazeAwaySince = float.PositiveInfinity;
            else if (float.IsPositiveInfinity(_navGazeAwaySince)) _navGazeAwaySince = now;
            else if (now - _navGazeAwaySince >= NavGazeReleaseSeconds)
            {
                ReleaseNavigation();
                return pointerDirection;
            }
        }

        var camera = UiCamera;
        if (_navTarget != null && camera != null)
        {
            if (IsSnappable(_navTarget) && TryGetNavDirection(_navTarget, camera.transform.position, out var direction)) _navDirection = direction;
            else _navTarget = null;
        }

        return _navDirection;
    }

    private void Pin(Component target, Vector3 origin)
    {
        _navTarget = target;
        _navPinned = true;
        _navMouseTravel = 0f;
        _navGazeAwaySince = float.PositiveInfinity;
        if (TryGetNavDirection(target, origin, out var direction)) _navDirection = direction;
    }

    private Component? GetTargetUnderCursor(Camera camera)
    {
        if (!TryGetTopRaycastResult(ToEventScreenPoint(camera, camera.transform.position + _cursorDirection * DefaultProjectionDistance), out var hit, includeMapIcons: true)) return null;

        return GetClickTarget(hit.gameObject);
    }

    // Every button the step could go to, cheapest first: along the step plus twice the way off to the side, in degrees
    // as seen from the seat (the menus being at different distances).
    private void CollectNavCandidates(Vector3 origin, Quaternion reference, Vector2 from, Component? current, Vector2 step)
    {
        _navCandidates.Clear();
        var count = FillSelectableBuffer();
        for (var i = 0; i < count; i++)
        {
            AddNavCandidate(_selectableBuffer[i], origin, reference, from, current, step);
        }

        System.Array.Clear(_selectableBuffer, 0, count);
        foreach (var icon in GetClickableAirbaseIcons())
        {
            AddNavCandidate(icon, origin, reference, from, current, step);
        }

        _navCandidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));
    }

    private void AddNavCandidate(Component target, Vector3 origin, Quaternion reference, Vector2 from, Component? current, Vector2 step)
    {
        if (target == current || !IsSnappable(target) || IsBackdrop(target, origin)) return;
        if (!TryGetNavDirection(target, origin, out var direction)) return;

        var offset = ToNavAngles(reference, direction) - from;
        float cost;
        if (current == null)
        {
            cost = offset.magnitude;
        }
        else
        {
            var along = Vector2.Dot(offset, step);
            if (along < NavMinimumStepDegrees) return;
            cost = along + NavLateralWeight * Mathf.Abs(offset.x * step.y - offset.y * step.x);
        }

        _navCandidates.Add((target, cost));
    }

    // A click would hit it where it is, or it is in a list that isn't covered, scrolled out of view (scrolled in now).
    private bool TryReach(Camera camera, Component target)
    {
        var origin = camera.transform.position;
        if (!TryGetNavDirection(target, origin, out var direction)) return false;
        if (IsTopmostAt(camera, target, direction)) return true;

        var scroll = target.GetComponentInParent<ScrollRect>();
        if (target.transform is not RectTransform item || scroll == null || !scroll.isActiveAndEnabled || scroll.content == null || !target.transform.IsChildOf(scroll.content)) return false;

        var viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
        if (!TryGetNavDirection(viewport, origin, out var viewDirection) ||
            !TryGetTopRaycastResult(ToEventScreenPoint(camera, origin + viewDirection * DefaultProjectionDistance), out var hit, includeMapIcons: true) ||
            !hit.gameObject.transform.IsChildOf(scroll.transform))
        {
            return false;
        }

        ScrollIntoView(scroll, viewport, item);
        return true;
    }

    private void ScrollIntoView(ScrollRect scroll, RectTransform viewport, RectTransform item)
    {
        item.GetWorldCorners(_navCorners);
        var min = (Vector2)viewport.InverseTransformPoint(_navCorners[0]);
        var max = min;
        for (var i = 1; i < 4; i++)
        {
            var corner = (Vector2)viewport.InverseTransformPoint(_navCorners[i]);
            min = Vector2.Min(min, corner);
            max = Vector2.Max(max, corner);
        }

        var view = viewport.rect;
        var shift = Vector2.zero;
        if (scroll.vertical) shift.y = ShiftIntoView(min.y, max.y, view.yMin, view.yMax);
        if (scroll.horizontal) shift.x = ShiftIntoView(min.x, max.x, view.xMin, view.xMax);
        if (shift == Vector2.zero) return;

        scroll.StopMovement();
        scroll.content.position += viewport.TransformVector(shift);
    }

    // How far to move the content for the item to show, with a margin past it where there is room.
    private static float ShiftIntoView(float itemMin, float itemMax, float viewMin, float viewMax)
    {
        var margin = Mathf.Max(0f, Mathf.Min((viewMax - viewMin) * NavScrollMargin, ((viewMax - viewMin) - (itemMax - itemMin)) * 0.5f));
        if (itemMax > viewMax - margin && itemMin > viewMin + margin) return viewMax - margin - itemMax;
        if (itemMin < viewMin + margin && itemMax < viewMax - margin) return viewMin + margin - itemMin;
        return 0f;
    }

    // Left and right along a horizontal slider (up and down a vertical one) move it a twentieth, or one whole number.
    private static bool AdjustSlider(Slider slider, Vector2 step)
    {
        var horizontal = slider.direction is Slider.Direction.LeftToRight or Slider.Direction.RightToLeft;
        var amount = horizontal ? step.x : step.y;
        if (Mathf.Approximately(amount, 0f)) return false;

        if (slider.direction is Slider.Direction.RightToLeft or Slider.Direction.TopToBottom) amount = -amount;
        slider.value += amount * (slider.wholeNumbers ? 1f : (slider.maxValue - slider.minValue) * NavSliderStep);
        return true;
    }

    // How much it looks like the way back: lower is better, -1 not at all. Its label first, then its name and what it
    // calls when clicked (the encyclopedia's "BackToMenu").
    private static int BackRank(Button button)
    {
        var label = GetLabel(button);
        var name = button.name.ToUpperInvariant();
        var calls = "";
        for (var i = 0; i < button.onClick.GetPersistentEventCount(); i++) calls += " " + button.onClick.GetPersistentMethodName(i).ToUpperInvariant();

        foreach (var word in NotBackWords)
        {
            if (label.Contains(word) || name.Contains(word) || calls.Contains(word)) return -1;
        }

        for (var i = 0; i < BackWords.Length; i++)
        {
            if (label == BackWords[i]) return i;
        }

        // Single letters only count as the whole label.
        for (var i = 0; i < BackWords.Length; i++)
        {
            if (BackWords[i].Length > 1 && label.Contains(BackWords[i])) return BackWords.Length + i;
        }

        for (var i = 0; i < BackWords.Length; i++)
        {
            if (BackWords[i].Length > 1 && (name.Contains(BackWords[i]) || calls.Contains(BackWords[i]))) return 2 * BackWords.Length + i;
        }

        return -1;
    }

    private static string GetLabel(Component target)
    {
        var tmp = target.GetComponentInChildren<TMP_Text>();
        if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text.Trim().ToUpperInvariant();

        var text = target.GetComponentInChildren<Text>();
        return text != null && !string.IsNullOrEmpty(text.text) ? text.text.Trim().ToUpperInvariant() : "";
    }

    private static bool TryGetNavDirection(Component target, Vector3 origin, out Vector3 direction)
    {
        direction = Vector3.forward;
        var rectTransform = GetSnapRect(target);
        if (rectTransform == null) return false;

        var rect = rectTransform.rect;
        if (rect.width <= 0f || rect.height <= 0f) return false;

        var toCenter = rectTransform.TransformPoint(rect.center) - origin;
        if (toCenter.sqrMagnitude <= Mathf.Epsilon) return false;

        direction = toCenter.normalized;
        return true;
    }

    // Yaw right and pitch up, in degrees, from the direction the menus face.
    private static Vector2 ToNavAngles(Quaternion reference, Vector3 direction)
    {
        var local = Quaternion.Inverse(reference) * direction;
        return new Vector2(
            Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg,
            Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg);
    }
}
