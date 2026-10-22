using System;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

// The corner minimap is the game's map shrunk into the HUD, at a zoom the game picks when it is minimized. Each frame
// while minimized, DynamicMap.CenterMinimizedMap places the map image around the aircraft using that image's scale, so
// scaling the image down just before it runs zooms the minimap out (Minimap Zoom Out) with everything still lined up;
// the icons keep their size, as the game sizes them by the inverse of that scale. The game's own scale is put back
// before the map is maximized, so the full map opens at its usual zoom.
// Patched on its own instead of through CreateAndPatchAll, so a renamed game method only turns this off.
internal static class MinimapZoomPatch
{
    private static Vector3 _gameScale = Vector3.one;
    private static Vector3 _appliedScale = Vector3.zero;

    public static void Apply()
    {
        try
        {
            var center = AccessTools.Method(typeof(DynamicMap), "CenterMinimizedMap");
            var maximize = AccessTools.Method(typeof(DynamicMap), nameof(DynamicMap.Maximize));
            if (center == null || maximize == null)
            {
                NOVRPlugin.Log.LogWarning("[NOVR] Could not find the game's minimap methods; Minimap Zoom Out is off.");
                return;
            }

            var harmony = new Harmony("novr.vrui.minimapzoom");
            harmony.Patch(center, prefix: new HarmonyMethod(typeof(MinimapZoomPatch), nameof(CenterPrefix)));
            harmony.Patch(maximize, prefix: new HarmonyMethod(typeof(MinimapZoomPatch), nameof(MaximizePrefix)));
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not patch the minimap zoom: {exception.Message}");
        }
    }

    private static void CenterPrefix(DynamicMap __instance)
    {
        // Maximize calls it too, once the map is already full size.
        if (DynamicMap.mapMaximized || __instance.mapImage == null) return;

        var image = __instance.mapImage.transform;
        // Anything but the scale set here last frame is the game's own (a fresh minimize, a new map).
        if (image.localScale != _appliedScale) _gameScale = image.localScale;
        _appliedScale = _gameScale / InGameUiPlacement.MinimapZoomOut;
        image.localScale = _appliedScale;
    }

    private static void MaximizePrefix(DynamicMap __instance)
    {
        if (DynamicMap.mapMaximized || __instance.mapImage == null) return;

        var image = __instance.mapImage.transform;
        if (image.localScale == _appliedScale) image.localScale = _gameScale;
        _appliedScale = Vector3.zero;
    }
}
