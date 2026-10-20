using NOVR.VrUi.Native;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

// While an in-game menu has the cursor up, a small "VR UI SETTINGS" button sits below the menus; it opens a panel to
// recenter and to size and place the in-game menus and the flight HUD (InGameUiPlacement). It stays put in front of the
// player rather than following the head, below where the menus are, so it doesn't cover them until it's opened.
// NOVR's own main menu has these in its VR UI settings instead.
public class InGameVrUiBar : NOVRBehaviour
{
    private const float PitchBelowForwardDegrees = 37f;
    private const float DistanceInFrontOfMenus = 0.15f;
    // Meters per canvas unit per meter of distance, so it looks the same size at any menu distance.
    private const float CanvasScalePerMeter = 0.00075f;
    private const float RecenterDelaySeconds = 2.0f;
    private const int SortingOrder = 6000;
    private const float Gap = 10f;
    private const float Padding = 14f;
    private const float RowHeight = 46f;
    private const int RowCount = 3;
    private static readonly float[] RowWidths = { 110f, 100f, 110f, 100f, 110f, 100f, 110f };
    private static readonly Color BackgroundColor = new(0.025f, 0.035f, 0.045f, 0.9f);
    private static readonly Color ButtonColor = new(0.24f, 0.29f, 0.31f, 0.96f);
    private static readonly Color AccentButtonColor = new(0.18f, 0.23f, 0.26f, 0.96f);

    private GameObject? _root;
    private GameObject? _collapsed;
    private GameObject? _expanded;
    private Canvas? _canvas;
    private Font? _font;
    private Text? _centerText;
    private Text? _menuScaleText;
    private Text? _menuDistanceText;
    private Text? _hudScaleText;
    private float _recenterTime = -1f;
    private bool _snapReferenceNextFrame;

    private void Update()
    {
        if (_snapReferenceNextFrame)
        {
            // The recenter turned the camera last frame; skip the smoothing so head-locked UI doesn't swing across.
            _snapReferenceNextFrame = false;
            NOUIManager.I.SnapSmoothedReference();
        }

        var visible = ShouldShow();
        if (visible) EnsureRoot();
        if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
        if (!visible)
        {
            // Closed menus put it back to the small button for next time.
            _recenterTime = -1f;
            SetExpanded(false);
            return;
        }

        Place();
        UpdatePendingRecenter();
        RefreshTexts();
    }

    private static bool ShouldShow()
    {
        var cursor = VrUiCursor.I;
        return cursor != null && cursor.IsActive && !cursor.HasProjectionReferenceOverride && NOVRGameplayUIBehaviour.ActiveCount > 0;
    }

    // Like the in-game menus, fixed in the VR UI space ahead of the recentered forward, just in front of them.
    private void Place()
    {
        if (_root == null) return;

        var distance = Mathf.Max(0.5f, InGameUiPlacement.Distance - DistanceInFrontOfMenus);
        var direction = Quaternion.Euler(PitchBelowForwardDegrees, 0f, 0f) * Vector3.forward;
        _root.transform.SetPositionAndRotation(direction * distance, Quaternion.LookRotation(direction, Vector3.up));
        _root.transform.localScale = Vector3.one * (CanvasScalePerMeter * distance);
        if (_canvas != null) _canvas.worldCamera = APIBus.CockpitHudCamera;
    }

    private void UpdatePendingRecenter()
    {
        if (_recenterTime < 0f || Time.unscaledTime < _recenterTime) return;

        _recenterTime = -1f;
        VrUiCursor.I?.RecenterCursor();
        _snapReferenceNextFrame = true;
    }

    private void RefreshTexts()
    {
        SetText(_centerText, _recenterTime < 0f ? "VR CENTER" : $"CENTER {Mathf.Max(0f, _recenterTime - Time.unscaledTime):0.0}");
        SetText(_menuScaleText, $"{InGameUiPlacement.Scale:0.00}x");
        SetText(_menuDistanceText, $"{InGameUiPlacement.Distance:0.00} m");
        SetText(_hudScaleText, $"{InGameUiPlacement.HudScale:0.00}x");
    }

    private static void SetText(Text? text, string value)
    {
        if (text != null && text.text != value) text.text = value;
    }

    private void SetExpanded(bool expanded)
    {
        if (_collapsed != null && _collapsed.activeSelf == expanded) _collapsed.SetActive(!expanded);
        if (_expanded != null && _expanded.activeSelf != expanded) _expanded.SetActive(expanded);
    }

    private void EnsureRoot()
    {
        if (_root != null) return;

        _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        _root = new GameObject("NOVR In-Game VR UI Bar", typeof(RectTransform));
        // Under NOVR's UI manager, which survives scene loads, at the VR UI space origin like the menus' positions.
        _root.transform.SetParent(transform, false);
        var rootRect = (RectTransform)_root.transform;
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = SortingOrder;
        _root.AddComponent<GraphicRaycaster>();

        var rowWidth = Gap * (RowWidths.Length - 1);
        foreach (var width in RowWidths) rowWidth += width;
        var panelSize = new Vector2(rowWidth + 2f * Padding, RowCount * RowHeight + (RowCount - 1) * Gap + 2f * Padding);
        // Pivot at the bottom: the button and the panel it opens both rest on the same point, the panel growing up.
        rootRect.sizeDelta = panelSize;
        rootRect.pivot = new Vector2(0.5f, 0f);

        _collapsed = CreateBottomAnchored("Collapsed", rootRect, new Vector2(190f, RowHeight));
        CreateButton("VR UI SETTINGS", _collapsed.GetComponent<RectTransform>(), Vector2.zero, 190f, AccentButtonColor, () => SetExpanded(true));

        _expanded = CreateBottomAnchored("Expanded", rootRect, panelSize);
        var panel = _expanded.GetComponent<RectTransform>();
        CreateImage("Background", panel, BackgroundColor, Vector2.zero, panelSize);

        var rowStep = RowHeight + Gap;
        var menuRow = new Row(this, panel, rowStep, rowWidth);
        menuRow.Label("MENUS");
        menuRow.Button("SIZE -", () => InGameUiPlacement.AdjustScale(-InGameUiPlacement.ScaleStep));
        _menuScaleText = menuRow.Value();
        menuRow.Button("SIZE +", () => InGameUiPlacement.AdjustScale(InGameUiPlacement.ScaleStep));
        menuRow.Button("CLOSER", () => InGameUiPlacement.AdjustDistance(-InGameUiPlacement.DistanceStep));
        _menuDistanceText = menuRow.Value();
        menuRow.Button("FARTHER", () => InGameUiPlacement.AdjustDistance(InGameUiPlacement.DistanceStep));

        var hudRow = new Row(this, panel, 0f, rowWidth);
        hudRow.Label("HUD");
        hudRow.Button("SIZE -", () => InGameUiPlacement.AdjustHudScale(-InGameUiPlacement.ScaleStep));
        _hudScaleText = hudRow.Value();
        hudRow.Button("SIZE +", () => InGameUiPlacement.AdjustHudScale(InGameUiPlacement.ScaleStep));

        _centerText = CreateButton("VR CENTER", panel, new Vector2(-85f, -rowStep), 150f, AccentButtonColor, OnCenterClicked);
        CreateButton("CLOSE", panel, new Vector2(85f, -rowStep), 150f, ButtonColor, () => SetExpanded(false));

        LayerHelper.SetLayerRecursive(_root.transform, LayerHelper.GetVrUiLayer());
        SetExpanded(false);
        _root.SetActive(false);
        NOVRPlugin.Log.LogInfo("[NOVR] In-game VR UI button created.");
    }

    private void OnCenterClicked()
    {
        // Gives the player time to look where "forward" should be, like the native menu's VR CENTER.
        _recenterTime = Time.unscaledTime + RecenterDelaySeconds;
    }

    // One row of the panel, filled left to right in the RowWidths columns.
    private sealed class Row
    {
        private readonly InGameVrUiBar _bar;
        private readonly RectTransform _parent;
        private readonly float _y;
        private float _x;
        private int _column;

        public Row(InGameVrUiBar bar, RectTransform parent, float y, float rowWidth)
        {
            _bar = bar;
            _parent = parent;
            _y = y;
            _x = -rowWidth / 2f;
        }

        public void Label(string text) => _bar.CreateText($"{text} Label", _parent, text, NextCenter(out var width), new Vector2(width, RowHeight));

        public Text Value() => _bar.CreateText("Value", _parent, "", NextCenter(out var width), new Vector2(width, RowHeight));

        public void Button(string label, UnityEngine.Events.UnityAction onClick) =>
            _bar.CreateButton(label, _parent, NextCenter(out var width), width, ButtonColor, onClick);

        private Vector2 NextCenter(out float width)
        {
            width = RowWidths[_column++];
            var center = _x + width / 2f;
            _x += width + Gap;
            return new Vector2(center, _y);
        }
    }

    private static GameObject CreateBottomAnchored(string name, RectTransform parent, Vector2 size)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        var rectTransform = gameObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(0.5f, 0f);
        rectTransform.pivot = new Vector2(0.5f, 0f);
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = Vector2.zero;
        return gameObject;
    }

    private Text CreateButton(string label, RectTransform parent, Vector2 position, float width, Color color, UnityEngine.Events.UnityAction onClick)
    {
        var rectTransform = CreateImage(label, parent, color, position, new Vector2(width, RowHeight));
        var button = rectTransform.gameObject.AddComponent<Button>();
        button.targetGraphic = rectTransform.GetComponent<Image>();
        button.onClick.AddListener(onClick);
        NativeButtonFeedback.Configure(button, color);
        return CreateText($"{label} Text", rectTransform, label, Vector2.zero, new Vector2(width, RowHeight));
    }

    private static RectTransform CreateImage(string name, RectTransform parent, Color color, Vector2 anchoredPosition, Vector2 size)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        var rectTransform = gameObject.AddComponent<RectTransform>();
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = anchoredPosition;
        gameObject.AddComponent<Image>().color = color;
        return rectTransform;
    }

    private Text CreateText(string name, RectTransform parent, string text, Vector2 anchoredPosition, Vector2 size)
    {
        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        var rectTransform = gameObject.AddComponent<RectTransform>();
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = anchoredPosition;

        var textComponent = gameObject.AddComponent<Text>();
        textComponent.text = text;
        textComponent.font = _font;
        textComponent.fontSize = 16;
        textComponent.alignment = TextAnchor.MiddleCenter;
        textComponent.color = Color.white;
        textComponent.raycastTarget = false;
        return textComponent;
    }
}
