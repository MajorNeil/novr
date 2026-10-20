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
    // About 53 degrees wide whatever the canvas size, so the action-name column on its left edge stays well inside the
    // headset's view. Scaled like the other menus, the overlay canvas is as many units wide as the window has pixels.
    private const float WidthPerMeterOfDistance = 1.0f;
    private const float LayerRefreshSeconds = 0.25f;
    private const int MinimumSortingOrder = 4000;

    private const float ContentLogDelaySeconds = 1f;
    private const int DetailedLogCount = 3;

    private readonly HashSet<Canvas> _sortedCanvases = new();
    private readonly HashSet<Graphic> _rebuiltLabels = new();
    private int _sortingOffset;
    private float _nextLayerRefreshTime;
    private float _contentLogTime = -1f;
    private int _unculledLabels;
    private int _restoredLabelAlphas;
    private bool _repairLogged;

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
        _repairLogged = false;
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
        RepairActionLabels();
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

    // The action names in the binding grid exist, are active and opaque and take clicks, yet don't show in VR while
    // the key fields next to them do. Undo the ways a graphic can be left undrawn after the move: culled by the grid's
    // RectMask2D against stale bounds, faded out through its CanvasRenderer, or a text mesh built for the overlay
    // scale. Culled rows outside the scroll view stay hidden by the mask's rect clipping.
    private void RepairActionLabels()
    {
        foreach (var label in GetComponentsInChildren<Graphic>(false).Where(IsActionLabel))
        {
            var canvasRenderer = label.canvasRenderer;
            if (canvasRenderer.cull)
            {
                canvasRenderer.cull = false;
                _unculledLabels++;
            }

            if (canvasRenderer.GetAlpha() < 0.01f && label.color.a > 0.01f)
            {
                canvasRenderer.SetAlpha(1f);
                _restoredLabelAlphas++;
            }

            if (_rebuiltLabels.Add(label))
            {
                label.SetAllDirty();
            }
        }

        if (_repairLogged || (_unculledLabels == 0 && _restoredLabelAlphas == 0)) return;
        _repairLogged = true;
        NOVRPlugin.Log.LogInfo($"[NOVR] ControlMapper labels repaired: {_unculledLabels} were culled, {_restoredLabelAlphas} were faded out (rebuilt {_rebuiltLabels.Count} meshes)");
    }

    private static bool IsActionLabel(Graphic graphic) =>
        graphic.name.StartsWith("InputGridLabel") || HasAncestorNamed(graphic.transform, "ActionLabelColumn");

    private static bool HasAncestorNamed(Transform transform, string name)
    {
        for (var parent = transform.parent; parent != null; parent = parent.parent)
        {
            if (parent.name == name) return true;
        }

        return false;
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

    // Once per opening, after Rewired has built the binding grid: how the action names are drawn next to the key
    // fields that do show, what masks the grid, and what is drawn on top of the names.
    private void LogContent()
    {
        var graphics = GetComponentsInChildren<Graphic>(true);
        var texts = graphics.Where(graphic => !string.IsNullOrWhiteSpace(GetText(graphic))).ToList();
        var labels = texts.Where(IsActionLabel).ToList();
        var fields = texts.Where(graphic => !IsActionLabel(graphic) && HasAncestorNamed(graphic.transform, "InputGridInnerGroup")).ToList();
        NOVRPlugin.Log.LogInfo(
            $"[NOVR] ControlMapper content: {texts.Count} texts ({texts.Count(text => !text.gameObject.activeInHierarchy)} inactive), " +
            $"{labels.Count} action labels, {fields.Count} key field texts, canvas scale {transform.localScale.x:0.00000}");

        foreach (var label in labels.Where(label => label.gameObject.activeInHierarchy).Take(DetailedLogCount))
        {
            NOVRPlugin.Log.LogInfo($"[NOVR]   label {Describe(label)}");
            var coverers = FindGraphicsDrawnOver(label, graphics).Take(3).Select(graphic => $"'{GetPath(graphic.transform)}' ({DescribeMaterial(graphic)}, alpha {graphic.color.a:0.00})");
            NOVRPlugin.Log.LogInfo($"[NOVR]     drawn over it: [{string.Join(", ", coverers)}]");
        }

        foreach (var field in fields.Where(field => field.gameObject.activeInHierarchy).Take(DetailedLogCount - 1))
        {
            NOVRPlugin.Log.LogInfo($"[NOVR]   field {Describe(field)}");
        }

        var masks = GetComponentsInChildren<RectMask2D>(true).Select(mask => $"RectMask2D '{GetPath(mask.transform)}' enabled={mask.enabled} rect={CanvasRect(mask.rectTransform)}")
            .Concat(GetComponentsInChildren<Mask>(true).Select(mask => $"Mask '{GetPath(mask.transform)}' enabled={mask.enabled} showGraphic={mask.showMaskGraphic} rect={CanvasRect(mask.rectTransform)}"));
        NOVRPlugin.Log.LogInfo($"[NOVR]   masks: [{string.Join("; ", masks)}]");

        var materials = graphics.Select(DescribeMaterial).Distinct();
        NOVRPlugin.Log.LogInfo($"[NOVR]   materials: [{string.Join("; ", materials)}]");
    }

    private string Describe(Graphic graphic)
    {
        var canvasRenderer = graphic.canvasRenderer;
        var groupAlpha = 1f;
        foreach (var group in graphic.GetComponentsInParent<CanvasGroup>())
        {
            groupAlpha *= group.alpha;
            if (group.ignoreParentGroups) break;
        }

        return $"'{GetText(graphic)!.Replace("\n", " ").Trim()}' on '{GetPath(graphic.transform)}' ({graphic.GetType().Name}): " +
               $"enabled={graphic.enabled}, cull={canvasRenderer.cull}, rendererAlpha={canvasRenderer.GetAlpha():0.00}, " +
               $"inheritedAlpha={canvasRenderer.GetInheritedAlpha():0.00}, groupAlpha={groupAlpha:0.00}, color={graphic.color}, " +
               $"depth={canvasRenderer.absoluteDepth}, rectClipping={canvasRenderer.hasRectClipping}, rect={CanvasRect(graphic.rectTransform)}, " +
               $"z={transform.InverseTransformPoint(graphic.transform.position).z:0.0}, {DescribeMaterial(graphic)}";
    }

    // Visible graphics later in the canvas's draw order whose rect contains the middle of the given one.
    private IEnumerable<Graphic> FindGraphicsDrawnOver(Graphic target, IEnumerable<Graphic> graphics)
    {
        var center = CanvasRect(target.rectTransform).center;
        var depth = target.canvasRenderer.absoluteDepth;
        return graphics
            .Where(graphic => graphic != target && graphic.isActiveAndEnabled && !graphic.canvasRenderer.cull &&
                              graphic.canvasRenderer.absoluteDepth > depth && graphic.color.a * graphic.canvasRenderer.GetInheritedAlpha() > 0.05f &&
                              CanvasRect(graphic.rectTransform).Contains(center))
            .OrderByDescending(graphic => graphic.canvasRenderer.absoluteDepth);
    }

    private Rect CanvasRect(RectTransform rectTransform)
    {
        var corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);
        var min = transform.InverseTransformPoint(corners[0]);
        var max = transform.InverseTransformPoint(corners[2]);
        return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
    }

    private static string DescribeMaterial(Graphic graphic)
    {
        var material = graphic.materialForRendering;
        return material != null ? $"material '{material.name}' shader '{(material.shader != null ? material.shader.name : "none")}'" : "no material";
    }

    private static string? GetText(Graphic graphic) =>
        graphic is Text uiText ? uiText.text : graphic is TMPro.TMP_Text tmpText ? tmpText.text : null;

    private string GetPath(Transform target)
    {
        var names = new List<string>();
        for (var current = target; current != null && current != transform; current = current.parent)
        {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }
}
