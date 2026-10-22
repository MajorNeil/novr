using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;

// The radar warning receiver marks where a radar that has detected the aircraft is (when it has no HUD marker), and
// where a jammer is, with icons it places at the flat screen pixel for that direction. In the VR HUD space those land
// nowhere near it, so after the game has placed, faded and timed them out, they are moved onto the HUD sphere in that
// direction, facing the player, and hidden when behind the head like the game hides them behind the camera.
// Patched on its own instead of through CreateAndPatchAll, so a renamed game type only turns this off.
internal static class RadarWarningVrPatch
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const float WarningDirectionDistance = 10000f;

    private static FieldInfo? _warningImage;
    private static FieldInfo? _warningDirection;
    private static FieldInfo? _jammingImage;
    private static FieldInfo? _jammingText;
    private static FieldInfo? _jammingUnit;

    public static void Apply()
    {
        try
        {
            var warningIcon = AccessTools.Inner(typeof(RadarWarning), "RadarWarningIcon");
            var jammingIcon = AccessTools.Inner(typeof(RadarWarning), "JammingIcon");
            var warningPosition = warningIcon?.GetMethod("Position", AnyInstance);
            var jammingPosition = jammingIcon?.GetMethod("Position", AnyInstance);
            _warningImage = warningIcon?.GetField("image", AnyInstance);
            _warningDirection = warningIcon?.GetField("direction", AnyInstance);
            _jammingImage = jammingIcon?.GetField("image", AnyInstance);
            _jammingText = jammingIcon?.GetField("text", AnyInstance);
            _jammingUnit = jammingIcon?.GetField("unit", AnyInstance);
            if (warningPosition == null || jammingPosition == null || _warningImage == null || _warningDirection == null ||
                _jammingImage == null || _jammingUnit == null)
            {
                NOVRPlugin.Log.LogWarning("[NOVR] Could not find the game's radar warning icons; they won't show in VR.");
                return;
            }

            var harmony = new Harmony("novr.vrui.radarwarning");
            harmony.Patch(warningPosition, postfix: new HarmonyMethod(typeof(RadarWarningVrPatch), nameof(WarningPostfix)));
            harmony.Patch(jammingPosition, postfix: new HarmonyMethod(typeof(RadarWarningVrPatch), nameof(JammingPostfix)));
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not patch the radar warning icons for VR: {exception.Message}");
        }
    }

    // __result is false once the icon has timed out and is being destroyed.
    private static void WarningPostfix(object __instance, bool __result)
    {
        if (!__result || _warningImage!.GetValue(__instance) is not Image image || image == null) return;

        var mainCamera = APIBus.MainCamera;
        if (mainCamera == null) return;

        var direction = (Vector3)_warningDirection!.GetValue(__instance);
        var worldPosition = mainCamera.transform.position + direction * WarningDirectionDistance;
        image.enabled = Place(image.transform, worldPosition, mainCamera);
    }

    private static void JammingPostfix(object __instance, bool __result)
    {
        if (!__result || _jammingImage!.GetValue(__instance) is not Image image || image == null) return;
        if (_jammingUnit!.GetValue(__instance) is not Unit unit || unit == null) return;

        var mainCamera = APIBus.MainCamera;
        if (mainCamera == null) return;

        image.enabled = Place(image.transform, unit.transform.position, mainCamera);
        if (_jammingText?.GetValue(__instance) is Text text && text != null) text.enabled = image.enabled;
    }

    // Whether it is in front of the head, where it is then drawn.
    private static bool Place(Transform icon, Vector3 worldPosition, Camera mainCamera)
    {
        var cockpitHudCamera = APIBus.CockpitHudCamera;
        if (cockpitHudCamera == null) return false;
        if (Vector3.Dot(worldPosition - mainCamera.transform.position, mainCamera.transform.forward) <= 0f) return false;
        if (!VrHudProjection.TryProjectDirectionToCockpitHud(worldPosition, out var hudPosition)) return false;

        icon.SetPositionAndRotation(hudPosition, cockpitHudCamera.transform.rotation);
        return true;
    }
}
