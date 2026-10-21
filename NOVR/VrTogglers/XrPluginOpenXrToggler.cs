
using NOVR.EyeTracking;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace NOVR.VrTogglers;

public class XrPluginOpenXrToggler : XrPluginToggler
{
    protected override bool SetUp()
    {
        // OpenXR reads its features once, when the loader initializes.
        if (ModConfiguration.Instance.EyeTrackingEnabled.Value) EyeGazeFeatureBootstrap.Inject();

        var setUp = base.SetUp();
        EyeGazeFeatureBootstrap.LogRuntimeSupport();
        return setUp;
    }

    protected override XRLoader CreateLoader()
    {
        var xrLoader = ScriptableObject.CreateInstance<OpenXRLoader>();
        return xrLoader;
    }
}
