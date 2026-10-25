using System;
using HarmonyLib;
using NOVR.VrUi.Menu;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

// Tells the menu hangar when the game's loading screen comes up and goes, so its door shuts while a mission loads and
// opens again if the load is given up. The screen counts nested calls, so whether it is showing is read from it after each.
// Patched on its own instead of through CreateAndPatchAll, so a renamed game type only turns this off.
internal static class LoadingScreenPatch
{
    public static void Apply()
    {
        try
        {
            var type = AccessTools.TypeByName("LoadingScreen");
            var show = type != null ? AccessTools.Method(type, "ShowLoadingScreen") : null;
            var hide = type != null ? AccessTools.Method(type, "HideLoadingScreen") : null;
            if (show == null || hide == null)
            {
                NOVRPlugin.Log.LogWarning("[NOVR] Could not find the game's loading screen; the menu hangar door won't close for loads.");
                return;
            }

            var harmony = new Harmony("novr.vrui.loadingscreen");
            var postfix = new HarmonyMethod(typeof(LoadingScreenPatch), nameof(Postfix));
            harmony.Patch(show, postfix: postfix);
            harmony.Patch(hide, postfix: postfix);
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not patch the loading screen for the menu hangar: {exception.Message}");
        }
    }

    private static void Postfix(Component __instance)
    {
        if (__instance != null) MenuEnvironment.I?.SetLoading(__instance.gameObject.activeSelf);
    }
}
