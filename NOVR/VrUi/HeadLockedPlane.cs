using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

// A canvas hung from the UI camera, so it moves with the head exactly, on a plane Distance ahead of the eyes: the HUD's
// distance, so what is drawn on it is in focus with the HUD. Positions and sizes on it are worked out in meters; the
// canvas is scaled so one of its units is UnitMeters, which keeps text sharp.
internal static class HeadLockedPlane
{
    public const float Distance = 1000f;
    public const float UnitMeters = 0.25f;

    private static Font? _font;

    public static Font? Font => _font != null ? _font : _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

    // Size on the plane, in meters, of something this many degrees across.
    public static float AngleToPlane(float degrees) => 2f * Distance * Mathf.Tan(degrees * 0.5f * Mathf.Deg2Rad);

    // How far from the plane's centre, in meters, a point this many degrees off the line of sight is.
    public static float RadiusAt(float degrees) => Distance * Mathf.Tan(degrees * Mathf.Deg2Rad);

    // Font size for text this many degrees tall, scaled with the HUD.
    public static int FontSize(float degrees) => Mathf.RoundToInt(AngleToPlane(degrees) * InGameUiPlacement.HudScale / UnitMeters);

    // Where a direction in the UI camera's own space crosses the plane, in meters; false when it points behind.
    public static bool TryProject(Vector3 local, out Vector2 position)
    {
        position = Vector2.zero;
        if (local.z <= 0.01f) return false;

        position = new Vector2(local.x, local.y) * (Distance / local.z);
        return true;
    }

    public static void Place(Transform element, Vector2 position) => element.localPosition = position / UnitMeters;

    public static GameObject CreateRoot(string name, int sortingOrder)
    {
        var root = new GameObject(name, typeof(RectTransform));
        root.transform.SetParent(APIBus.CockpitHudCamera.transform, false);
        root.transform.localPosition = new Vector3(0f, 0f, Distance);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one * UnitMeters;
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;
        LayerHelper.SetLayerRecursive(root.transform, LayerHelper.GetVrUiLayer());
        return root;
    }

    public static Text CreateText(string name, Transform parent, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        textObject.layer = (int)LayerHelper.GetVrUiLayer();
        var text = textObject.AddComponent<Text>();
        text.font = Font;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        // Readable against bright sky and dark ground alike.
        var outline = textObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
        outline.effectDistance = new Vector2(3f, -3f);
        return text;
    }
}
