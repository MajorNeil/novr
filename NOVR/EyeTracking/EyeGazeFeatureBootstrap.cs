using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace NOVR.EyeTracking;

// The game ships no OpenXR settings asset with eye tracking in it, so the runtime is never asked for eye gaze. Before
// the OpenXR loader starts, this adds Unity's own Eye Gaze Interaction feature to the settings it reads, which requests
// XR_EXT_eye_gaze_interaction and registers the gaze device. A runtime without the extension just leaves it disabled.
// Found by name and filled in through reflection: the fields the build normally fills are internal, and the installed
// Unity.XR.OpenXR may not be the one NOVR was built against.
internal static class EyeGazeFeatureBootstrap
{
    internal const string ExtensionName = "XR_EXT_eye_gaze_interaction";
    private const string FeatureTypeName = "UnityEngine.XR.OpenXR.Features.Interactions.EyeGazeInteraction";
    private const string FeatureId = "com.unity.openxr.feature.input.eyetracking";
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static bool Requested { get; private set; }

    internal static void Inject()
    {
        try
        {
            var featureType = typeof(OpenXRFeature).Assembly.GetType(FeatureTypeName);
            var featuresField = typeof(OpenXRSettings).GetField("features", AnyInstance);
            var settings = OpenXRSettings.Instance;
            if (featureType == null || featuresField == null || settings == null)
            {
                NOVRPlugin.Log.LogWarning("[NOVR] Eye tracking: this OpenXR version has no eye gaze feature to enable.");
                return;
            }

            var features = (featuresField.GetValue(settings) as OpenXRFeature[] ?? Array.Empty<OpenXRFeature>())
                .Where(feature => feature != null).ToList();
            var eyeGaze = features.FirstOrDefault(feature => feature.GetType() == featureType);
            if (eyeGaze == null)
            {
                eyeGaze = (OpenXRFeature)ScriptableObject.CreateInstance(featureType);
                SetField(eyeGaze, "nameUi", "Eye Gaze Interaction Profile");
                SetField(eyeGaze, "version", "0.0.1");
                SetField(eyeGaze, "featureIdInternal", FeatureId);
                SetField(eyeGaze, "openxrExtensionStrings", ExtensionName);
                SetField(eyeGaze, "company", "Unity");
                features.Add(eyeGaze);
                featuresField.SetValue(settings, features.ToArray());
            }

            eyeGaze.enabled = true;
            Requested = true;
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: could not request eye gaze from OpenXR: {exception.Message}");
        }
    }

    // Once the loader has started, whether the runtime (SteamVR) actually turned the extension on.
    internal static void LogRuntimeSupport()
    {
        if (!Requested) return;

        try
        {
            if (OpenXRRuntime.IsExtensionEnabled(ExtensionName))
            {
                NOVRPlugin.Log.LogInfo($"[NOVR] Eye tracking: {OpenXRRuntime.name} enabled {ExtensionName}.");
            }
            else
            {
                NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: {OpenXRRuntime.name} does not offer {ExtensionName}, so there is no eye gaze; menus and target designation stay on the mouse and head.");
            }
        }
        catch (Exception exception)
        {
            NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: could not check the runtime's eye gaze support: {exception.Message}");
        }
    }

    private static void SetField(object target, string name, object value)
    {
        typeof(OpenXRFeature).GetField(name, AnyInstance)?.SetValue(target, value);
    }
}
