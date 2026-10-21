using System.Collections.Generic;
using NOVR.VrUi;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;

namespace NOVR.EyeTracking;

// Where the player is looking, from the OpenXR eye gaze device that EyeGazeFeatureBootstrap asks for. The gaze comes
// in tracking space, the same space as the headset pose, so it is turned into the VR UI space the way the UI camera
// is (its parent's rotation and the recenter offset). Held through blinks, and smoothed two ways:
// - for designating, a One Euro filter, which stays steady while the eyes rest on something and keeps up when they jump;
// - for the menu cursor, which is on screen all the time, a steadier one that sits still while the eyes rest on
//   something and jumps when they move on (see UpdateMenuDirection).
// Runs before VrUiCursor, which reads it every frame.
[DefaultExecutionOrder(-1100)]
public class NOVREyeGaze : NOVRBehaviour
{
    public static NOVREyeGaze? I { get; private set; }

    private const float BlinkHoldSeconds = 0.4f;
    private const float DeviceSearchIntervalSeconds = 1f;
    private const float NoDataWarningSeconds = 20f;
    private const float FilterMinCutoff = 1.5f;
    private const float FilterBeta = 0.6f;
    private const float FilterDerivativeCutoff = 1f;
    private const float MenuJumpDegrees = 2f;
    private const float MenuSlowestCutoff = 0.5f;
    private const float MenuFastestCutoff = 4f;
    private const float DotDistance = 10f;
    private const float DotDegrees = 0.5f;
    private const int DotTextureSize = 32;

    private static readonly UnityEngine.XR.InputFeatureUsage<Quaternion> GazeRotationUsage = new("gazeRotation");

    private readonly List<XRInputDevice> _xrDevices = new();
    private XRInputDevice _xrDevice;
    private InputDevice? _inputSystemDevice;
    private InputControl<Quaternion>? _inputSystemRotation;
    private InputControl<float>? _inputSystemIsTracked;
    private InputControl<int>? _inputSystemTrackingState;
    private float _nextDeviceSearchTime;

    private OneEuroVector3 _filter;
    private Vector3 _trackingDirection = Vector3.forward;
    private Vector3 _menuTrackingDirection = Vector3.forward;
    private Vector3? _menuJumpCandidate;
    private float _lastGazeTime = float.NegativeInfinity;
    private float _startTime;
    private bool _loggedGaze;
    private bool _loggedNoGaze;

    private GameObject? _dot;

    private bool HasGaze => Time.unscaledTime - _lastGazeTime <= BlinkHoldSeconds;

    // Gaze direction in the VR UI space (menus, HUD, cursor), from the UI camera's position.
    public static bool TryGetUiDirection(out Vector3 direction) => TryGetUiDirection(false, out direction);

    // The same, steadied for the menu cursor.
    public static bool TryGetMenuDirection(out Vector3 direction) => TryGetUiDirection(true, out direction);

    private static bool TryGetUiDirection(bool menu, out Vector3 direction)
    {
        direction = Vector3.forward;
        var gaze = I;
        if (gaze == null || !gaze.isActiveAndEnabled || !gaze.HasGaze || NOUIManager.I == null) return false;

        var uiCameraParent = APIBus.CockpitHudCamera.transform.parent;
        var trackingToUi = (uiCameraParent != null ? uiCameraParent.rotation : Quaternion.identity) *
                           NOVRHeadsetData.RotationCalibrationOffset;
        direction = trackingToUi * (menu ? gaze._menuTrackingDirection : gaze._trackingDirection);
        return true;
    }

    protected override void Awake()
    {
        base.Awake();
        I = this;
        _startTime = Time.unscaledTime;
    }

    private void OnDestroy()
    {
        if (I == this) I = null;
        if (_dot != null) Destroy(_dot);
    }

    private void Update()
    {
        if (TryReadGazeRotation(out var rotation, out var source))
        {
            var direction = rotation * Vector3.forward;
            // Back from a blink or a lost track: start from the new gaze instead of sliding over from the old one.
            var restart = !HasGaze;
            if (restart) _filter.Reset();
            _trackingDirection = _filter.Filter(direction, Time.unscaledDeltaTime, FilterMinCutoff, FilterBeta, FilterDerivativeCutoff).normalized;
            UpdateMenuDirection(direction, restart);
            _lastGazeTime = Time.unscaledTime;
            if (!_loggedGaze)
            {
                _loggedGaze = true;
                NOVRPlugin.Log.LogInfo($"[NOVR] Eye tracking: receiving gaze from '{source}'.");
            }
        }
        else
        {
            WarnIfNoGaze();
        }

        UpdateDot();
    }

    // The eyes rest on something, then jump to the next: a fixation filter rather than a plain low-pass. Within
    // MenuJumpDegrees the cursor is smoothed heavily (Menu Gaze Steadiness), so the tracker's jitter doesn't show; past
    // it the cursor goes straight to the new spot instead of sliding over everything in between. One sample that far
    // out is as likely to be noise, so it takes two in a row that agree, and the jump lands on their average.
    private void UpdateMenuDirection(Vector3 direction, bool restart)
    {
        if (restart)
        {
            _menuTrackingDirection = direction;
            _menuJumpCandidate = null;
            return;
        }

        if (Vector3.Angle(_menuTrackingDirection, direction) > MenuJumpDegrees)
        {
            if (_menuJumpCandidate is { } candidate && Vector3.Angle(candidate, direction) <= MenuJumpDegrees)
            {
                _menuTrackingDirection = (candidate + direction).normalized;
                _menuJumpCandidate = null;
            }
            else
            {
                _menuJumpCandidate = direction;
            }

            return;
        }

        _menuJumpCandidate = null;
        var steadiness = Mathf.Clamp01(ModConfiguration.Instance.MenuGazeSteadiness.Value);
        var cutoff = MenuFastestCutoff * Mathf.Pow(MenuSlowestCutoff / MenuFastestCutoff, steadiness);
        var timeConstant = 1f / (2f * Mathf.PI * cutoff);
        var deltaTime = Time.unscaledDeltaTime;
        _menuTrackingDirection = Vector3.Slerp(_menuTrackingDirection, direction, deltaTime / (deltaTime + timeConstant)).normalized;
    }

    private bool TryReadGazeRotation(out Quaternion rotation, out string source)
    {
        rotation = Quaternion.identity;
        source = "";
        if (!_xrDevice.isValid && (_inputSystemDevice == null || !_inputSystemDevice.added))
        {
            _inputSystemDevice = null;
            if (Time.unscaledTime < _nextDeviceSearchTime) return false;
            _nextDeviceSearchTime = Time.unscaledTime + DeviceSearchIntervalSeconds;
            FindDevice();
        }

        if (_xrDevice.isValid)
        {
            source = _xrDevice.name;
            return TryReadXrDevice(out rotation);
        }

        if (_inputSystemDevice != null)
        {
            source = _inputSystemDevice.displayName ?? _inputSystemDevice.name;
            return TryReadInputSystemDevice(out rotation);
        }

        return false;
    }

    // Unity's XR device list sees the OpenXR gaze device whatever the Input System was built with; the Input System's
    // "EyeGaze" device is the fallback.
    private void FindDevice()
    {
        XRInputDevices.GetDevicesWithCharacteristics(UnityEngine.XR.InputDeviceCharacteristics.EyeTracking, _xrDevices);
        foreach (var device in _xrDevices)
        {
            if (!device.isValid) continue;
            _xrDevice = device;
            return;
        }

        foreach (var device in InputSystem.devices)
        {
            if (device.layout != "EyeGaze" && device.description.product != "Eye Tracking OpenXR") continue;
            if (device.TryGetChildControl("pose/rotation") is not InputControl<Quaternion> rotation) continue;

            _inputSystemDevice = device;
            _inputSystemRotation = rotation;
            _inputSystemIsTracked = device.TryGetChildControl("pose/isTracked") as InputControl<float>;
            _inputSystemTrackingState = device.TryGetChildControl("pose/trackingState") as InputControl<int>;
            return;
        }
    }

    private bool TryReadXrDevice(out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (_xrDevice.TryGetFeatureValue(XRCommonUsages.isTracked, out var isTracked) && !isTracked) return false;
        if (_xrDevice.TryGetFeatureValue(XRCommonUsages.trackingState, out var trackingState) &&
            (trackingState & UnityEngine.XR.InputTrackingState.Rotation) == 0) return false;

        var read = _xrDevice.TryGetFeatureValue(GazeRotationUsage, out rotation) ||
                   _xrDevice.TryGetFeatureValue(XRCommonUsages.deviceRotation, out rotation);
        return read && IsValidRotation(rotation);
    }

    private bool TryReadInputSystemDevice(out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (_inputSystemRotation == null) return false;
        if (_inputSystemIsTracked != null && _inputSystemIsTracked.ReadValue() < 0.5f) return false;
        if (_inputSystemTrackingState != null &&
            ((UnityEngine.XR.InputTrackingState)_inputSystemTrackingState.ReadValue() & UnityEngine.XR.InputTrackingState.Rotation) == 0) return false;

        rotation = _inputSystemRotation.ReadValue();
        return IsValidRotation(rotation);
    }

    // An untracked pose reads as all zeros.
    private static bool IsValidRotation(Quaternion rotation) =>
        rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > 0.5f;

    private void WarnIfNoGaze()
    {
        if (_loggedGaze || _loggedNoGaze || Time.unscaledTime - _startTime < NoDataWarningSeconds) return;
        _loggedNoGaze = true;
        if (!OpenXRRuntime.IsExtensionEnabled(EyeGazeFeatureBootstrap.ExtensionName)) return;

        var found = _xrDevice.isValid || _inputSystemDevice != null ? "the gaze device is there but has not tracked" : "no gaze device showed up";
        NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: the runtime offers eye gaze but {found}; check that eye tracking is turned on and calibrated for the headset.");
    }

    private void UpdateDot()
    {
        if (!ModConfiguration.Instance.ShowGazeDot.Value || !TryGetUiDirection(out var direction))
        {
            if (_dot != null && _dot.activeSelf) _dot.SetActive(false);
            return;
        }

        var dot = _dot != null ? _dot : _dot = CreateDot();
        if (!dot.activeSelf) dot.SetActive(true);

        var camera = APIBus.CockpitHudCamera;
        dot.transform.SetPositionAndRotation(
            camera.transform.position + direction * DotDistance,
            Quaternion.LookRotation(direction, camera.transform.up));
    }

    private static GameObject CreateDot()
    {
        var dot = new GameObject("NOVRGazeDot");
        DontDestroyOnLoad(dot);
        var size = 2f * DotDistance * Mathf.Tan(DotDegrees * 0.5f * Mathf.Deg2Rad);
        dot.transform.localScale = Vector3.one * (size / DotTextureSize);

        var canvas = dot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = short.MaxValue - 1;
        dot.GetComponent<RectTransform>().sizeDelta = new Vector2(DotTextureSize, DotTextureSize);

        var image = dot.AddComponent<RawImage>();
        image.raycastTarget = false;
        image.texture = CreateDotTexture();
        image.color = new Color(0.3f, 0.85f, 1f, 0.8f);
        LayerHelper.SetLayerRecursive(dot.transform, LayerHelper.GetVrUiLayer());
        return dot;
    }

    private static Texture2D CreateDotTexture()
    {
        var texture = new Texture2D(DotTextureSize, DotTextureSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var colors = new Color32[DotTextureSize * DotTextureSize];
        var center = (DotTextureSize - 1) * 0.5f;
        var radius = DotTextureSize * 0.5f - 1f;
        for (var y = 0; y < DotTextureSize; y++)
        {
            for (var x = 0; x < DotTextureSize; x++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                var alpha = (byte)(Mathf.Clamp01(radius - distance) * 255f);
                colors[y * DotTextureSize + x] = new Color32(255, 255, 255, alpha);
            }
        }

        texture.SetPixels32(colors);
        texture.Apply();
        return texture;
    }

    private struct OneEuroVector3
    {
        private Vector3 _value;
        private Vector3 _derivative;
        private bool _initialized;

        public Vector3 Filter(Vector3 input, float deltaTime, float minCutoff, float beta, float derivativeCutoff)
        {
            if (!_initialized || deltaTime <= 0f)
            {
                if (!_initialized)
                {
                    _value = input;
                    _derivative = Vector3.zero;
                    _initialized = true;
                }

                return _value;
            }

            _derivative = Vector3.Lerp(_derivative, (input - _value) / deltaTime, Alpha(derivativeCutoff, deltaTime));
            _value = Vector3.Lerp(_value, input, Alpha(minCutoff + beta * _derivative.magnitude, deltaTime));
            return _value;
        }

        public void Reset() => _initialized = false;

        private static float Alpha(float cutoff, float deltaTime)
        {
            var timeConstant = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + timeConstant / deltaTime);
        }
    }
}
