using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOVR.GameplayPatches;

// Nuclear Option's DetailRenderer draws trees and grass and culls them on the GPU against a camera it caches from
// Camera.main when it starts. That is usually before NOVR has swapped in its tracked camera, so it keeps culling with
// the game's flat-screen camera, which doesn't follow the headset: trees and grass get cut and pop in at the edges of
// the view and when looking around. Point it at the tracked camera instead.
// Ported from InfernoSuperNova/novr#36 (glinkot). Reflection only: the renderer and its camera field are private.
internal static class DetailRendererVrCameraFix
{
    private static readonly Type? DetailRendererType = AccessTools.TypeByName("NuclearOption.Effects.DetailRenderer");
    private static readonly FieldInfo? CameraField = DetailRendererType != null ? AccessTools.Field(DetailRendererType, "camera") : null;
    private static readonly FieldInfo? InstanceField = DetailRendererType != null ? AccessTools.Field(DetailRendererType, "i") : null;
    private static readonly PropertyInfo? InstanceProperty = DetailRendererType != null ? AccessTools.Property(DetailRendererType, "i") : null;

    private static bool _loggedBinding;
    private static bool _loggedFailure;

    internal static void BindToTrackedCamera(Camera trackedCamera)
    {
        if (trackedCamera == null) return;

        try
        {
            if (DetailRendererType == null || CameraField == null)
            {
                LogFailureOnce("the game's DetailRenderer camera field was not found");
                return;
            }

            // Not there yet: when it starts later it takes Camera.main, which is then the tracked camera.
            var detailRenderer = FindDetailRenderer();
            if (detailRenderer == null || ReferenceEquals(CameraField.GetValue(detailRenderer), trackedCamera)) return;

            CameraField.SetValue(detailRenderer, trackedCamera);
            if (_loggedBinding) return;
            _loggedBinding = true;
            NOVRPlugin.Log.LogInfo("[NOVR] Trees and grass now cull against the VR camera.");
        }
        catch (Exception exception)
        {
            LogFailureOnce(exception.Message);
        }
    }

    private static UnityEngine.Object? FindDetailRenderer()
    {
        var instance = InstanceField != null ? InstanceField.GetValue(null) : InstanceProperty?.GetValue(null);
        if (instance is UnityEngine.Object detailRenderer && detailRenderer != null) return detailRenderer;

        return UnityEngine.Object.FindObjectOfType(DetailRendererType);
    }

    private static void LogFailureOnce(string reason)
    {
        if (_loggedFailure) return;
        _loggedFailure = true;
        NOVRPlugin.Log.LogWarning($"[NOVR] Could not point trees and grass culling at the VR camera ({reason}); they may pop in at the edges of the view.");
    }
}
