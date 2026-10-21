using System;
using HarmonyLib;
using NOVR.VrUi;
using NOVR.VrUi.HarmonyPatches;
using UnityEngine;

namespace NOVR.EyeTracking;

// The game designates with CombatHUD.TargetSelect, which picks the HUD markers within 100 units of the designator circle
// and ranks them by how close they are to it. NOVR keeps that circle where the head points, on the same 1000 unit
// sphere as the markers, so for the length of the call it is moved to where the eyes point instead, then put back.
// Also skips designating when the Select press clicked a menu (VrUiCursor.SelectButtonClicksMenus).
// Patched on its own instead of through CreateAndPatchAll, so a renamed game method only turns this off.
internal static class GazeDesignatePatch
{
    public static void Apply()
    {
        if (!ModConfiguration.Instance.EyeTrackingEnabled.Value) return;

        try
        {
            var targetSelect = AccessTools.Method(typeof(CombatHUD), "TargetSelect", new[] { typeof(bool) });
            if (targetSelect == null)
            {
                NOVRPlugin.Log.LogWarning("[NOVR] Eye tracking: could not find the game's CombatHUD.TargetSelect; look to designate is off.");
                return;
            }

            new Harmony("novr.eyetracking.designate").Patch(
                targetSelect,
                prefix: new HarmonyMethod(typeof(GazeDesignatePatch), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(GazeDesignatePatch), nameof(Postfix)));
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: could not patch target designation: {exception.Message}");
        }
    }

    private static bool Prefix(CombatHUD __instance, out Vector3? __state)
    {
        __state = null;
        var cursor = VrUiCursor.I;
        if (cursor != null && cursor.SelectButtonClicksMenus) return false;

        var designator = __instance.targetDesignator;
        if (!ModConfiguration.Instance.GazeDesignate.Value || designator == null ||
            !NOVREyeGaze.TryGetUiDirection(out var gazeDirection))
        {
            return true;
        }

        var designatorTransform = designator.transform;
        __state = designatorTransform.position;
        designatorTransform.position = gazeDirection * VrHudProjection.HudDistance;
        return true;
    }

    private static void Postfix(CombatHUD __instance, Vector3? __state)
    {
        var designator = __instance.targetDesignator;
        if (__state is { } position && designator != null) designator.transform.position = position;
    }
}
