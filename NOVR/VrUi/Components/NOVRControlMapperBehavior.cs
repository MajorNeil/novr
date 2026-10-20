using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

// Rewired's ControlMapper (the key binding screen opened from the options menu) keeps a screen-space overlay canvas,
// which XR only draws to the desktop mirror and which gets hit-tested in desktop pixels. This moves it into the VR UI
// space just in front of the in-game menus it is opened from.
public class NOVRControlMapperBehavior : UIRenderedCanvasBehavior
{
    private const float DistanceInFrontOfMenus = 0.1f;
    // About 70 degrees wide whatever the canvas size. Scaled like the other menus, the overlay canvas is as many units
    // wide as the window has pixels, so at 4K its action-name column ended up outside the headset's view.
    private const float WidthPerMeterOfDistance = 1.4f;
    private const float LayerRefreshSeconds = 0.25f;
    private const int MinimumSortingOrder = 4000;

    private const float ContentLogDelaySeconds = 1f;
    private const int ContentLogLimit = 30;

    private readonly HashSet<Canvas> _sortedCanvases = new();
    private int _sortingOffset;
    private float _nextLayerRefreshTime;
    private float _contentLogTime = -1f;

    // As an overlay it was drawn and hit-tested above every other menu; in world space only the sorting order does that.
    public override void Awake()
    {
        base.Awake();
        var canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            _sortingOffset = Mathf.Max(0, MinimumSortingOrder - canvas.sortingOrder);
        }

        LogLayout();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        _contentLogTime = Time.unscaledTime + ContentLogDelaySeconds;
    }

    // LateUpdate so this wins over PositionZeroBehavior, which the name-based scene patch puts on every "Canvas".
    private void LateUpdate()
    {
        var distance = InGameUiPlacement.Distance - DistanceInFrontOfMenus;
        var width = ((RectTransform)transform).rect.width;
        var scale = width > 0f
            ? WidthPerMeterOfDistance * distance * InGameUiPlacement.Scale / width
            : InGameUiPlacement.CanvasScale;
        transform.localScale = Vector3.one * scale;
        transform.SetPositionAndRotation(new Vector3(0f, 0f, distance), Quaternion.identity);

        if (_contentLogTime >= 0f && Time.unscaledTime >= _contentLogTime)
        {
            _contentLogTime = -1f;
            LogContent();
        }

        // Rewired instantiates its popups ("press a key", conflict dialogs) under this canvas while it is open, with
        // the prefab's layer, which the VR UI camera doesn't render.
        if (Time.unscaledTime < _nextLayerRefreshTime) return;
        _nextLayerRefreshTime = Time.unscaledTime + LayerRefreshSeconds;
        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
        ShiftSortingOrders();
    }

    // Nested canvases that sort themselves use absolute orders, so lifting only the root would hide them behind it.
    private void ShiftSortingOrders()
    {
        foreach (var canvas in GetComponentsInChildren<Canvas>(true))
        {
            if (!_sortedCanvases.Add(canvas)) continue;
            if (canvas.transform == transform || canvas.overrideSorting)
            {
                canvas.sortingOrder += _sortingOffset;
            }
        }
    }

    private void LogLayout()
    {
        var rect = ((RectTransform)transform).rect;
        var scaler = GetComponent<CanvasScaler>();
        var nested = GetComponentsInChildren<Canvas>(true)
            .Where(canvas => canvas.transform != transform)
            .Select(canvas => $"{canvas.name}(override={canvas.overrideSorting}, order={canvas.sortingOrder})");
        NOVRPlugin.Log.LogInfo(
            $"[NOVR] ControlMapper canvas: size {rect.width:0}x{rect.height:0}, scaler {(scaler != null ? scaler.uiScaleMode.ToString() : "none")}, " +
            $"sorting offset {_sortingOffset}, nested canvases: [{string.Join(", ", nested)}]");
    }

    // Once per opening, after Rewired has built the binding grid: what text it shows and whether each would render.
    private void LogContent()
    {
        var vrUiLayer = (int)LayerHelper.GetVrUiLayer();
        var texts = GetComponentsInChildren<Graphic>(true)
            .Select(graphic => (graphic, text: graphic is Text uiText ? uiText.text : graphic is TMPro.TMP_Text tmpText ? tmpText.text : null))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.text))
            .ToList();
        var hidden = texts.Count(entry => !entry.graphic.gameObject.activeInHierarchy);
        var wrongLayer = texts.Count(entry => entry.graphic.gameObject.layer != vrUiLayer);
        NOVRPlugin.Log.LogInfo($"[NOVR] ControlMapper content: {texts.Count} texts ({hidden} inactive, {wrongLayer} not on the VR UI layer), canvas scale {transform.localScale.x:0.00000}");
        foreach (var (graphic, text) in texts.Take(ContentLogLimit))
        {
            var position = transform.InverseTransformPoint(graphic.transform.position);
            NOVRPlugin.Log.LogInfo(
                $"[NOVR]   '{text!.Replace("\n", " ").Trim()}' on '{graphic.name}' ({graphic.GetType().Name}): active={graphic.gameObject.activeInHierarchy}, " +
                $"layer={graphic.gameObject.layer}, alpha={graphic.color.a:0.00}, canvas='{(graphic.canvas != null ? graphic.canvas.name : "none")}' " +
                $"order={(graphic.canvas != null ? graphic.canvas.sortingOrder : 0)}, at ({position.x:0}, {position.y:0}) in canvas units");
        }
    }
}
