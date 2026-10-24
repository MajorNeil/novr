using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NOVR.EyeTracking;
using NOVR.VrCamera;
using NOVR.VrUi;
using NOVR.VrUi.HarmonyPatches;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

#if CPP
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
#endif

namespace NOVR;

[BepInPlugin(
    "deltawing.novr",
    "NOVR",
    "0.4.29")]
public class NOVRPlugin : BaseUnityPlugin
{
    
    private static NOVRPlugin _instance;
    public static string ModFolderPath { get; private set; }
    internal static ManualLogSource Log { get; private set; } = null!;

    public NOVRPlugin()
    {
        Log = Logger;

        InputTracking.trackingAcquired += TrackingAcquired;
        _instance = this;
        ModFolderPath = Path.GetDirectoryName(Assembly.GetAssembly(typeof(NOVRPlugin)).Location);
        
        new ModConfiguration(Config);
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        GameErrorPopupPatch.Apply();
        GazeDesignatePatch.Apply();
        MinimapZoomPatch.Apply();
        RadarWarningVrPatch.Apply();
        LoadingScreenPatch.Apply();
        Core.Create();
    }

    private void TrackingAcquired(XRNodeState obj)
    {
        NOVRHeadsetData.CalibrateTranslation();
    }
     
    private void Awake()
    {

    }
    
    
}
