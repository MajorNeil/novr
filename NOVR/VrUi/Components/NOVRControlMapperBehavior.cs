using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
    private const string UndrawnTextShaderMarker = "Additive";
    private const string FallbackTextShaderName = "TextMeshPro/Mobile/Distance Field";

    private static bool _loggedShaderSwitch;
    private readonly HashSet<Canvas> _sortedCanvases = new();
    private Shader? _drawableTextShader;
    private bool _searchedDrawableTextShader;
    private int _sortingOffset;
    private float _nextLayerRefreshTime;

    // As an overlay it was drawn and hit-tested above every other menu; in world space only the sorting order does that.
    public override void Awake()
    {
        base.Awake();
        var canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            _sortingOffset = Mathf.Max(0, MinimumSortingOrder - canvas.sortingOrder);
        }
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

        // Rewired instantiates its popups ("press a key", conflict dialogs) under this canvas while it is open, with
        // the prefab's layer, which the VR UI camera doesn't render.
        if (Time.unscaledTime < _nextLayerRefreshTime) return;
        _nextLayerRefreshTime = Time.unscaledTime + LayerRefreshSeconds;
        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
        ShiftSortingOrders();
        ReplaceUndrawnTextShaders();
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

    // The action names in the binding grid use a "TextMeshPro/Mobile/Distance Field Additive" material. Drawn as a
    // desktop overlay it shows, but drawn by the camera from a world-space canvas it doesn't, while the key texts next to
    // them, on "TextMeshPro/Mobile/Distance Field", do. Give those materials the shader the key texts use. Rewired
    // recreates the masked copies when the grid is rebuilt, so this keeps checking while the screen is open.
    private void ReplaceUndrawnTextShaders()
    {
        var texts = GetComponentsInChildren<TMPro.TMP_Text>(false);
        foreach (var text in texts)
        {
            var material = text.materialForRendering;
            if (material == null || material.shader == null || !IsUndrawnTextShader(material.shader)) continue;

            var replacement = FindDrawableTextShader(texts);
            if (replacement == null) return;

            var original = material.shader.name;
            material.shader = replacement;
            text.SetMaterialDirty();
            if (!_loggedShaderSwitch)
            {
                _loggedShaderSwitch = true;
                NOVRPlugin.Log.LogInfo($"[NOVR] Key binding screen: switched text shader '{original}' to '{replacement.name}' so the action names show in VR.");
            }
        }
    }

    private static bool IsUndrawnTextShader(Shader shader) =>
        shader.name.StartsWith("TextMeshPro/") && shader.name.Contains(UndrawnTextShaderMarker);

    private Shader? FindDrawableTextShader(IEnumerable<TMPro.TMP_Text> texts)
    {
        if (_searchedDrawableTextShader) return _drawableTextShader;

        _searchedDrawableTextShader = true;
        _drawableTextShader = texts
            .Select(text => text.materialForRendering)
            .Where(material => material != null && material.shader != null)
            .Select(material => material.shader)
            .FirstOrDefault(shader => shader.name.StartsWith("TextMeshPro/") && !IsUndrawnTextShader(shader));
        if (_drawableTextShader == null) _drawableTextShader = Shader.Find(FallbackTextShaderName);
        if (_drawableTextShader == null) NOVRPlugin.Log.LogWarning("[NOVR] Key binding screen: no TextMeshPro shader found to draw the action names in VR.");
        return _drawableTextShader;
    }
}
