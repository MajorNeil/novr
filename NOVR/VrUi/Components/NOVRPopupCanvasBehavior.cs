using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

// A popup canvas the game keeps in screen space (see GameErrorPopupPatch), which XR never draws to the headset. It is
// moved just in front of the in-game menus in the VR UI space and above NOVR's native menu (5000), below the small VR
// widgets (6000), so it can be read and dismissed with the VR cursor.
public class NOVRPopupCanvasBehavior : UIRenderedCanvasBehavior
{
    private const float DistanceInFrontOfMenus = 0.2f;
    private const int SortingOrder = 5500;

    public void Configure()
    {
        var canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = APIBus.CockpitHudCamera;
            canvas.sortingOrder = SortingOrder;
            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();
        }

        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
    }

    // The message contents are instantiated when shown, with the prefab's layer.
    private void OnTransformChildrenChanged()
    {
        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
    }

    // LateUpdate so this wins over PositionZeroBehavior, which the name-based scene patch puts on every "Canvas".
    private void LateUpdate()
    {
        var distance = Mathf.Max(0.5f, InGameUiPlacement.Distance - DistanceInFrontOfMenus);
        transform.localScale = Vector3.one * InGameUiPlacement.CanvasScale;
        transform.SetPositionAndRotation(new Vector3(0f, 0f, distance), Quaternion.identity);
    }
}
