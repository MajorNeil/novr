using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

// Rewired's ControlMapper (the key binding screen opened from the options menu) keeps a screen-space overlay canvas,
// which XR only draws to the desktop mirror and which gets hit-tested in desktop pixels. This moves it into the VR UI
// space like MenuCanvas, slightly in front of it at the same angular size so it covers the menu it was opened from.
public class NOVRControlMapperBehavior : UIRenderedCanvasBehavior
{
    private const float Distance = 2.9f;
    private const float Scale = 0.003f * Distance / 3f;
    private const float LayerRefreshSeconds = 0.25f;
    private const int MinimumSortingOrder = 4000;

    private float _nextLayerRefreshTime;

    // As an overlay it was drawn and hit-tested above every other menu; in world space only the sorting order does
    // that, so lift it over the game's menu canvases (NOVR's native menu at 5000 is hidden while this is open).
    public override void Awake()
    {
        base.Awake();
        var canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            canvas.sortingOrder = Mathf.Max(canvas.sortingOrder, MinimumSortingOrder);
        }
    }

    // LateUpdate so this wins over PositionZeroBehavior, which the name-based scene patch puts on every "Canvas".
    private void LateUpdate()
    {
        transform.localScale = Vector3.one * Scale;
        transform.SetPositionAndRotation(new Vector3(0f, 0f, Distance), Quaternion.identity);

        // Rewired instantiates its popups ("press a key", conflict dialogs) under this canvas while it is open, with
        // the prefab's layer, which the VR UI camera doesn't render.
        if (Time.unscaledTime < _nextLayerRefreshTime) return;
        _nextLayerRefreshTime = Time.unscaledTime + LayerRefreshSeconds;
        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
    }
}
