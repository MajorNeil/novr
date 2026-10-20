using NOVR.VrUi.Native;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

// A small bar under the player's view while an in-game menu has the cursor up: recenter, and the size and distance
// of the in-game menus (InGameUiPlacement). NOVR's own main menu has these in its VR UI settings instead.
public class InGameVrUiBar : NOVRBehaviour
{
    private const float DistanceMeters = 1.35f;
    private const float VerticalOffsetMeters = -0.42f;
    private const float CanvasScale = 0.0012f;
    private const float RecenterDelaySeconds = 2.0f;
    private const int SortingOrder = 6000;
    private const float Gap = 12f;
    private const float Height = 46f;
    private static readonly Color BackgroundColor = new(0.025f, 0.035f, 0.045f, 0.85f);
    private static readonly Color ButtonColor = new(0.24f, 0.29f, 0.31f, 0.96f);
    private static readonly Color CenterButtonColor = new(0.18f, 0.23f, 0.26f, 0.96f);

    private GameObject? _root;
    private Canvas? _canvas;
    private Font? _font;
    private Text? _centerText;
    private Text? _scaleText;
    private Text? _distanceText;
    private float _recenterTime = -1f;
    private bool _snapReferenceNextFrame;

    private void Update()
    {
        if (_snapReferenceNextFrame)
        {
            // The recenter turned the camera last frame; skip the smoothing so the bar doesn't swing across the view.
            _snapReferenceNextFrame = false;
            NOUIManager.I.SnapSmoothedReference();
        }

        var visible = ShouldShow();
        if (visible) EnsureRoot();
        if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
        if (!visible)
        {
            _recenterTime = -1f;
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

    private void Place()
    {
        if (_root == null) return;

        var reference = APIBus.CockpitHudReference.transform;
        if (_root.transform.parent != reference) _root.transform.SetParent(reference, false);
        _root.transform.localPosition = new Vector3(0f, VerticalOffsetMeters, DistanceMeters);
        _root.transform.localRotation = Quaternion.identity;
        _root.transform.localScale = Vector3.one * CanvasScale;
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
        SetText(_scaleText, $"SIZE {InGameUiPlacement.Scale:0.00}x");
        SetText(_distanceText, $"{InGameUiPlacement.Distance:0.00} m");
    }

    private static void SetText(Text? text, string value)
    {
        if (text != null && text.text != value) text.text = value;
    }

    private void EnsureRoot()
    {
        if (_root != null) return;

        _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        _root = new GameObject("NOVR In-Game VR UI Bar");
        var rootRect = _root.AddComponent<RectTransform>();
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = SortingOrder;
        _root.AddComponent<GraphicRaycaster>();

        // Left to right: VR CENTER | SIZE - | SIZE 1.00x | SIZE + | CLOSER | 3.00 m | FARTHER
        float[] widths = { 150f, 100f, 130f, 100f, 110f, 100f, 110f };
        var total = 0f;
        foreach (var width in widths) total += width;
        total += Gap * (widths.Length - 1);
        rootRect.sizeDelta = new Vector2(total + 2f * Gap, Height + 2f * Gap);
        CreateImage("Background", rootRect, BackgroundColor, Vector2.zero, rootRect.sizeDelta);

        var x = -total / 2f;
        float Next(int index)
        {
            var center = x + widths[index] / 2f;
            x += widths[index] + Gap;
            return center;
        }

        _centerText = CreateButton("VR CENTER", rootRect, Next(0), widths[0], CenterButtonColor, OnCenterClicked);
        CreateButton("SIZE -", rootRect, Next(1), widths[1], ButtonColor, () => InGameUiPlacement.AdjustScale(-InGameUiPlacement.ScaleStep));
        _scaleText = CreateText("Size Value", rootRect, "", new Vector2(Next(2), 0f), new Vector2(widths[2], Height));
        CreateButton("SIZE +", rootRect, Next(3), widths[3], ButtonColor, () => InGameUiPlacement.AdjustScale(InGameUiPlacement.ScaleStep));
        CreateButton("CLOSER", rootRect, Next(4), widths[4], ButtonColor, () => InGameUiPlacement.AdjustDistance(-InGameUiPlacement.DistanceStep));
        _distanceText = CreateText("Distance Value", rootRect, "", new Vector2(Next(5), 0f), new Vector2(widths[5], Height));
        CreateButton("FARTHER", rootRect, Next(6), widths[6], ButtonColor, () => InGameUiPlacement.AdjustDistance(InGameUiPlacement.DistanceStep));

        LayerHelper.SetLayerRecursive(_root.transform, LayerHelper.GetVrUiLayer());
        _root.SetActive(false);
        NOVRPlugin.Log.LogInfo("[NOVR] In-game VR UI bar created.");
    }

    private void OnCenterClicked()
    {
        // Gives the player time to look where "forward" should be, like the native menu's VR CENTER.
        _recenterTime = Time.unscaledTime + RecenterDelaySeconds;
    }

    private Text CreateButton(string label, RectTransform parent, float x, float width, Color color, UnityEngine.Events.UnityAction onClick)
    {
        var rectTransform = CreateImage(label, parent, color, new Vector2(x, 0f), new Vector2(width, Height));
        var button = rectTransform.gameObject.AddComponent<Button>();
        button.targetGraphic = rectTransform.GetComponent<Image>();
        button.onClick.AddListener(onClick);
        NativeButtonFeedback.Configure(button, color);
        return CreateText($"{label} Text", rectTransform, label, Vector2.zero, new Vector2(width, Height));
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
        textComponent.fontSize = 15;
        textComponent.alignment = TextAnchor.MiddleCenter;
        textComponent.color = Color.white;
        textComponent.raycastTarget = false;
        return textComponent;
    }
}
