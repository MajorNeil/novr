using System;
using System.Collections.Generic;
using System.Reflection;
using NOVR.VrCamera;
using NOVR.VrTogglers;
using NOVR.VrUi;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace NOVR;

public class Core : MonoBehaviour
{
    
    private float _originalFixedDeltaTime;

    private NOVRHeadsetData? _headsetData;
    private NOUIManager? _vrUi;
    private PropertyInfo? _refreshRateProperty;
    private VrTogglerManager? _vrTogglerManager;
    
    private Aircraft _aircraft;
    private Aircraft _oldAircraft;
    private bool? _lastXrDisplayRunning;
    private static readonly List<XRDisplaySubsystem> XrDisplaySubsystems = new();

    public static void Create()
    {
        new GameObject("NOVR").AddComponent<Core>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<VrCameraManager>();
        gameObject.AddComponent<APIBus>();
    }

    private void OnDestroy()
    {
        Debug.Log("NOVR has been destroyed. This shouldn't have happened. Recreating...");
        
        Create();
    }

    private void Start()
    {
        
        
        var xrDeviceType = Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.XRModule") ??
                           Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine");

        _refreshRateProperty = xrDeviceType?.GetProperty("refreshRate");
        
        _headsetData = NOVRBehaviour.Create<NOVRHeadsetData>(transform);
        _vrUi = NOVRBehaviour.Create<NOUIManager>(transform);
        
        _vrTogglerManager = new VrTogglerManager();
        
    }



    private void Update()
    {
        UpdatePhysicsRate();
        LogXrStateChanges();
    }

    public static bool IsXrDisplayRunning()
    {
        SubsystemManager.GetSubsystems(XrDisplaySubsystems);
        foreach (var displaySubsystem in XrDisplaySubsystems)
        {
            if (displaySubsystem.running) return true;
        }

        return false;
    }

    // If XR stops, the game falls back to rendering flat on the monitor; log it next to the UI/settings log lines.
    private void LogXrStateChanges()
    {
        var xrDisplayRunning = IsXrDisplayRunning();
        if (_lastXrDisplayRunning == xrDisplayRunning) return;

        var message = $"XR display running: {_lastXrDisplayRunning?.ToString() ?? "unknown"} -> {xrDisplayRunning} " +
                      $"(frame {Time.frameCount}, control mapper open: {GameManager.controlMapper != null && GameManager.controlMapper.isOpen})";
        if (_lastXrDisplayRunning == true && !xrDisplayRunning)
        {
            NOVRPlugin.Log.LogWarning(message);
        }
        else
        {
            NOVRPlugin.Log.LogInfo(message);
        }

        _lastXrDisplayRunning = xrDisplayRunning;
    }

    // Keyboard and mouse only reach the game while its window has focus (e.g. not the SteamVR window).
    private void OnApplicationFocus(bool hasFocus)
    {
        NOVRPlugin.Log.LogInfo($"Game window focus: {hasFocus}");
    }

    private void UpdatePhysicsRate()
    {
        if (_originalFixedDeltaTime == 0)
        {
            _originalFixedDeltaTime = Time.fixedDeltaTime;
        }

        if (_refreshRateProperty == null) return;

        var headsetRefreshRate = (float)_refreshRateProperty.GetValue(null, null);
        if (headsetRefreshRate <= 0) return;


        Time.fixedDeltaTime = _originalFixedDeltaTime;
        
    }
    private void FixedUpdate()
    {
        _oldAircraft = _aircraft;
        GameManager.GetLocalAircraft(out _aircraft);
        if (_aircraft != _oldAircraft) NOVRHeadsetData.CalibrateTranslation();
        CameraStateManager.enableMouseLook = false;
    }

}
