using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;

// The game shows multiplayer errors (e.g. why joining a server failed) through FlashErrorMessageSingleton on its own
// screen-space overlay canvas, which XR never draws to the headset, so in VR a failed join gave no readable reason.
// When an error is shown, its canvas is moved into the VR UI space and the message is logged.
// Ported from InfernoSuperNova/novr's fix/vr-popup-rendering (PickleRicci). Found by reflection and patched on its own
// instead of through CreateAndPatchAll, so a renamed game method only turns this off.
internal static class GameErrorPopupPatch
{
    private const string PopupTypeName = "FlashErrorMessageSingleton";
    private const string ShowMethodName = "ShowErrorInternal";
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static FieldInfo? _canvasField;
    private static bool _loggedFailure;

    public static void Apply()
    {
        try
        {
            var popupType = FindGameType(PopupTypeName);
            var showMethods = popupType?.GetMethods(AnyInstance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == ShowMethodName).ToList();
            if (popupType == null || showMethods == null || showMethods.Count == 0)
            {
                NOVRPlugin.Log.LogWarning($"[NOVR] Could not find the game's {PopupTypeName}.{ShowMethodName}; multiplayer error messages won't show in VR.");
                return;
            }

            _canvasField = popupType.GetField("_canvas", AnyInstance);
            var harmony = new Harmony("novr.vrui.errorpopup");
            var postfix = new HarmonyMethod(typeof(GameErrorPopupPatch), nameof(Postfix));
            foreach (var method in showMethods) harmony.Patch(method, postfix: postfix);
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not patch the game's error popup for VR: {exception.Message}");
        }
    }

    private static void Postfix(object __instance, object[] __args)
    {
        try
        {
            if (__instance is not Component popup || popup == null) return;

            var canvas = _canvasField?.GetValue(popup) as Canvas;
            if (canvas == null) canvas = popup.GetComponentInChildren<Canvas>(true);
            NOVRPlugin.Log.LogWarning($"[NOVR] Game error message: {DescribeMessage(__args, canvas)}");
            if (canvas == null) return;

            if (!canvas.TryGetComponent<NOVRPopupCanvasBehavior>(out var behavior))
            {
                behavior = canvas.gameObject.AddComponent<NOVRPopupCanvasBehavior>();
            }

            behavior.Configure();
        }
        catch (Exception exception)
        {
            if (_loggedFailure) return;
            _loggedFailure = true;
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not move the game's error popup into VR: {exception.Message}");
        }
    }

    // The text passed in when there is any, otherwise what the popup shows.
    private static string DescribeMessage(object[] args, Canvas? canvas)
    {
        var texts = args.OfType<string>().Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        if (texts.Count == 0 && canvas != null)
        {
            texts = canvas.GetComponentsInChildren<Graphic>(false)
                .Select(graphic => graphic is Text uiText ? uiText.text : graphic is TMPro.TMP_Text tmpText ? tmpText.text : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)
                .ToList();
        }

        return texts.Count > 0 ? string.Join(" | ", texts.Select(text => text.Replace("\n", " ").Trim())) : "(no text)";
    }

    private static Type? FindGameType(string name)
    {
        IEnumerable<Type?> types;
        try
        {
            types = typeof(GameManager).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types;
        }

        return types.FirstOrDefault(type => type != null && type.Name == name);
    }
}
