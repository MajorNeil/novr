using System.Collections.Generic;
using NOVR.EyeTracking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace NOVR.VrUi;

[DefaultExecutionOrder(-1000)]
public class VrUiCursor: NOVRBehaviour
{
    public static VrUiCursor? Instance { get; private set; }
    public static VrUiCursor? I => Instance;

    public bool IsActive => _cursor != null && _cursor.activeSelf;
    // Set while NOVR's native menu is showing and owns the cursor's forward direction.
    public bool HasProjectionReferenceOverride => _hasProjectionReferenceOverride;
    public Vector3 CursorPosition => _cursor != null ? _cursor.transform.position : Vector3.zero;
    // Set while the game's Select button clicks the menus (see UpdateSelectButton), so it doesn't also designate.
    public bool SelectButtonClicksMenus { get; private set; }

    protected override void Awake()
    {
        base.Awake();
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        if (_virtualMouse != null)
        {
            try
            {
                InputSystem.RemoveDevice(_virtualMouse);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[{nameof(VrUiCursor)}] Failed to remove VirtualMouse during OnDestroy: {ex}");
            }
            _virtualMouse = null;
        }
    }

    private Texture2D? _texture;
    private const float MaxYawDegrees = 65f;
    private const float MaxPitchDegrees = 45f;
    private const float DefaultProjectionDistance = 5;
    private const float CursorCanvasScale = 0.001f;
    private const int CursorTextureSize = 64;
    private const float CursorRingRadius = 12f;
    private const float CursorRingThickness = 4f;
    private const float CursorIdlePulseScale = 0.035f;
    private const float CursorIdlePulseSpeed = 5.5f;
    private const float CursorHoverScale = 1.18f;
    private const float CursorPressedScale = 0.84f;
    private const float CursorClickPulseScale = 0.22f;
    private const float CursorClickPulseDuration = 0.18f;
    private const float CursorAnimationLerpSpeed = 24f;
    private static readonly Color CursorNormalColor = new Color32(100, 200, 100, 255);
    private static readonly Color CursorHoverColor = new Color32(155, 255, 175, 255);
    private static readonly Color CursorPressedColor = new Color32(255, 224, 92, 255);
    private const float HybridMouseHoldSeconds = 1.5f;
    private const float HybridTakeoverPixels = 20f;
    private const float HybridTakeoverWindowSeconds = 0.3f;
    private const float MaxSnapDegrees = 5f;
    private const float SnapHysteresis = 1.5f;
    private const float SnapSwitchMarginDegrees = 0.5f;
    private const float SnapInset = 0.2f;
    private const int MaxSnapOcclusionChecks = 3;
    private GameObject? _cursor;
    private RectTransform? _cursorRectTransform;
    private Canvas? _cursorCanvas;
    private RawImage? _cursorImage;
    private bool _cursorOverInteractive;
    private float _lastCursorClickTime = -100f;
    private bool _hasProjectionReferenceOverride;
    private Quaternion _projectionReferenceRotation = Quaternion.identity;
    private bool _gazeDriving;
    private bool _gazeHeldForClick;
    private float _lastMouseMoveTime = float.NegativeInfinity;
    private float _mouseTravelStartTime = float.NegativeInfinity;
    private float _mouseTravel;
    private Vector3 _cursorDirection = Vector3.forward;
    private Selectable? _snapTarget;
    private Selectable[] _selectableBuffer = new Selectable[64];
    private readonly List<(Selectable Selectable, float OutsideDegrees, Vector3 Direction)> _snapCandidates = new();
    private readonly List<RaycastResult> _raycastResults = new();
    private bool _selectHeld;
    private bool _selectPressedThisFrame;

    private static readonly Vector2 OffScreenPoint = new(-1f, -1f);
    private BaseInputModule? _lastSeenUiModule;
    private Mouse? _virtualMouse;
    private Mouse? _realMouse;


    private int ScreenWidth => Screen.width;
    private int ScreenHeight => Screen.height;
    public Camera? UiCamera
    {
        get
        {
            return APIBus.CockpitHudCamera;
        }
    }


    public Vector2 GetScreenPoint()
    {
        var camera = UiCamera;
        if (_cursor != null && camera != null)
        {
            return ToEventScreenPoint(camera, _cursor.transform.position);
        }
        return Vector2.zero;
    }

    // GraphicRaycaster and RectTransformUtility turn pointer positions back into rays using the event camera's own
    // pixel rect, which under XR is not the desktop window (Screen.width/height), so project with that same camera.
    private static Vector2 ToEventScreenPoint(Camera camera, Vector3 worldPosition)
    {
        var screenPoint = camera.WorldToScreenPoint(worldPosition, Camera.MonoOrStereoscopicEye.Mono);
        // Behind the camera the projection mirrors back onto the screen; keep it off-screen so it can't hit anything.
        return screenPoint.z > 0f ? (Vector2)screenPoint : OffScreenPoint;
    }

    public void SetProjectionReferenceRotation(Quaternion referenceRotation)
    {
        _projectionReferenceRotation = referenceRotation;
        _hasProjectionReferenceOverride = true;
    }

    public void ClearProjectionReferenceRotation()
    {
        _hasProjectionReferenceOverride = false;
    }
    
    
    private void Start()
    {
        _texture = CreateCursorTexture();
    }

    private void Update()
    {
        SelectButtonClicksMenus = false;
        if (!Application.isFocused)
        {
            if (_cursor != null && _cursor.activeSelf)
            {
                _cursor.SetActive(false);
            }
            return;
        }

        if (!IsRealCursorVisible()) // This means we don't have to manually show and hide it every game update
        {
            if (_cursor != null)
            {
                _cursor.SetActive(false);
            }
            return;
        }
        
        if (_virtualMouse == null)
        {
            _realMouse = Mouse.current ?? throw new System.InvalidOperationException(
                $"[{nameof(VrUiCursor)}] Unity InputSystem could not find an active hardware Mouse device during initialization.");
            _virtualMouse = InputSystem.AddDevice<Mouse>("VirtualMouse");
            Debug.Log($"[NOVR] Added VirtualMouse device: name='{_virtualMouse.name}', path='{_virtualMouse.path}', displayName='{_virtualMouse.displayName}'");
        }

        EnsureUIModuleRestrictedToVirtualMouse();
        if (_texture == null) return;

        if (Keyboard.current?.homeKey.wasPressedThisFrame == true)
        {
            RecenterCursor();
        }

        UpdateCursorAngles();
        
        var realMouse = _realMouse;
        if (realMouse == null || _virtualMouse == null) return;

        UpdateSelectButton();
        UpdateCursorAnimation(realMouse);

        var screenPoint = GetScreenPoint();

        ushort buttons = 0;
        if (realMouse.leftButton.isPressed || _selectHeld) buttons |= 1;
        if (realMouse.rightButton.isPressed) buttons |= 2;
        if (realMouse.middleButton.isPressed) buttons |= 4;

        InputState.Change(_virtualMouse, new MouseState
        {
            position = screenPoint,
            delta = realMouse.delta.ReadValue(),
            scroll = realMouse.scroll.ReadValue(),
            buttons = buttons
        });

        if (realMouse.leftButton.wasPressedThisFrame)
        {
            LogRaycastAtCursor();
        }
    }
    

    private void UpdateCursorAngles()
    {
        var camera = UiCamera;
        if (camera == null) return;

        EnsureCursorCanvas(camera);

        if (_cursor == null || _cursorRectTransform == null)
        {
            return;
        }

        if (!_cursor.activeSelf)
        {
            _cursor.SetActive(true);
        }
        
        var mouse = _realMouse;
        if (mouse == null) return;

        Vector3 worldDirection = GetPointerDirection(mouse);
        if (!_gazeDriving)
        {
            _snapTarget = null;
        }
        else if (!_gazeHeldForClick)
        {
            worldDirection = AimAtSelectable(camera, worldDirection);
        }

        Vector2 inScreenSpace = ToEventScreenPoint(camera, camera.transform.position + worldDirection * DefaultProjectionDistance);
        float cursorDistance = GetDistanceUnderCursor(inScreenSpace);
        _cursorDirection = worldDirection;
        Vector3 pos = camera.transform.position + worldDirection * cursorDistance;
        _cursor.transform.position = pos;
        _cursor.transform.rotation = Quaternion.LookRotation(worldDirection, camera.transform.up);
    }

    // The mouse maps across the projection reference like a flat screen; eye gaze points straight at what is looked at.
    private Vector3 GetPointerDirection(Mouse mouse)
    {
        var config = ModConfiguration.Instance;
        var mode = config.EyeTrackingEnabled.Value ? config.GazeMenuCursor.Value : GazeCursorMode.Mouse;
        var wasGazeDriving = _gazeDriving;
        _gazeDriving = false;
        _gazeHeldForClick = false;
        if (mode == GazeCursorMode.Mouse || !NOVREyeGaze.TryGetMenuDirection(out var gazeDirection))
        {
            return GetMouseDirection(mouse.position.ReadValue());
        }

        if (mode == GazeCursorMode.Hybrid && IsMouseInUse(mouse))
        {
            // Carry on from where the gaze cursor was instead of jumping to wherever the mouse was left.
            if (wasGazeDriving)
            {
                WarpMouseTo(_cursorDirection);
                return _cursorDirection;
            }

            return GetMouseDirection(mouse.position.ReadValue());
        }

        _gazeDriving = true;
        // Held still while clicking, so the eyes moving on before the button is released don't drag a list instead.
        if (wasGazeDriving && (_selectHeld || mouse.leftButton.isPressed))
        {
            _gazeHeldForClick = true;
            return _cursorDirection;
        }

        return gazeDirection;
    }

    // In Hybrid the mouse takes over once it has really been moved, HybridTakeoverPixels within
    // HybridTakeoverWindowSeconds (a bumped desk or a drifting sensor doesn't count), and keeps the cursor until it has
    // been still for a moment.
    private bool IsMouseInUse(Mouse mouse)
    {
        var now = Time.unscaledTime;
        var motion = mouse.delta.ReadValue().magnitude;
        if (motion > 0f)
        {
            if (now - _mouseTravelStartTime > HybridTakeoverWindowSeconds)
            {
                _mouseTravel = 0f;
                _mouseTravelStartTime = now;
            }

            _mouseTravel += motion;
            var inUse = now - _lastMouseMoveTime < HybridMouseHoldSeconds;
            if (inUse || _mouseTravel >= HybridTakeoverPixels) _lastMouseMoveTime = now;
        }

        return now - _lastMouseMoveTime < HybridMouseHoldSeconds;
    }

    private Vector3 GetMouseDirection(Vector2 mousePosition)
    {
        float cursorPitch = ProjectPitchAngle(mousePosition.y);
        float cursorYaw = ProjectYawAngle(mousePosition.x);
        Vector3 localDirection = Quaternion.Euler(-cursorPitch, cursorYaw, 0f) * Vector3.forward;
        return GetProjectionReferenceRotation() * localDirection;
    }

    // The inverse of GetMouseDirection, held to the area the mouse covers.
    private void WarpMouseTo(Vector3 worldDirection)
    {
        var localDirection = Quaternion.Inverse(GetProjectionReferenceRotation()) * worldDirection;
        var yaw = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
        var pitch = Mathf.Asin(Mathf.Clamp(localDirection.y, -1f, 1f)) * Mathf.Rad2Deg;
        _realMouse?.WarpCursorPosition(new Vector2(
            Mathf.InverseLerp(-MaxYawDegrees, MaxYawDegrees, yaw) * ScreenWidth,
            Mathf.InverseLerp(-MaxPitchDegrees, MaxPitchDegrees, pitch) * ScreenHeight));
    }

    // Eye trackers are good to about a degree, which is a lot on a small button. The gaze cursor goes onto the button
    // the gaze is on, or the nearest within Menu Snap Degrees, and then only moves inside it, a little in from its
    // edges, so it never sits on an edge flickering on and off. It stays on that button until the gaze is clearly
    // past its edge (by SnapSwitchMarginDegrees more than the next one) or well beyond the snap distance.
    private Vector3 AimAtSelectable(Camera camera, Vector3 gazeDirection)
    {
        var snapDegrees = Mathf.Clamp(ModConfiguration.Instance.MenuSnapDegrees.Value, 0f, MaxSnapDegrees);
        var origin = camera.transform.position;
        Selectable? underGaze = null;
        if (TryGetTopRaycastResult(ToEventScreenPoint(camera, origin + gazeDirection * DefaultProjectionDistance), out var hit))
        {
            var selectable = hit.gameObject.GetComponentInParent<Selectable>();
            if (selectable != null && IsSnappable(selectable))
            {
                underGaze = selectable;
            }
            else if (IsInteractiveRaycastTarget(hit.gameObject))
            {
                // A scrollbar, a drag area or the like: it takes the gaze as it is.
                _snapTarget = null;
                return gazeDirection;
            }
        }

        if (snapDegrees <= 0f)
        {
            _snapTarget = null;
            return gazeDirection;
        }

        CollectSnapCandidates(origin, gazeDirection, snapDegrees * SnapHysteresis);

        // A label or icon can stick out of its button's rect; hitting it still clicks the button, right where it is.
        (Selectable Selectable, float OutsideDegrees, Vector3 Direction)? underGazeCandidate =
            underGaze == null ? null : FindCandidate(underGaze) ?? (underGaze, 0f, gazeDirection);
        var current = _snapTarget == underGaze ? underGazeCandidate : FindCandidate(_snapTarget);
        var keepCurrent = current is { } kept &&
                          kept.OutsideDegrees <= snapDegrees * SnapHysteresis &&
                          (_snapTarget == underGaze || IsTopmostAt(camera, kept.Selectable, kept.Direction));

        // The button under the gaze, else the nearest one in reach that a click would actually hit.
        var next = underGazeCandidate;
        if (next == null)
        {
            var checks = 0;
            foreach (var candidate in _snapCandidates)
            {
                if (candidate.OutsideDegrees > snapDegrees || checks >= MaxSnapOcclusionChecks) break;
                if (candidate.Selectable == _snapTarget) continue;
                if (keepCurrent && candidate.OutsideDegrees + SnapSwitchMarginDegrees >= current!.Value.OutsideDegrees) break;

                checks++;
                if (!IsTopmostAt(camera, candidate.Selectable, candidate.Direction)) continue;
                next = candidate;
                break;
            }
        }

        var switchTo = next is { } other && other.Selectable != _snapTarget &&
                       (!keepCurrent || other.OutsideDegrees + SnapSwitchMarginDegrees < current!.Value.OutsideDegrees);
        if (switchTo)
        {
            _snapTarget = next!.Value.Selectable;
            gazeDirection = next.Value.Direction;
        }
        else if (keepCurrent)
        {
            gazeDirection = current!.Value.Direction;
        }
        else
        {
            _snapTarget = null;
        }

        _snapCandidates.Clear();
        return gazeDirection;
    }

    // Every snappable button within reach of the gaze, nearest first.
    private void CollectSnapCandidates(Vector3 origin, Vector3 gazeDirection, float maxDegrees)
    {
        _snapCandidates.Clear();
        if (_selectableBuffer.Length < Selectable.allSelectableCount)
        {
            _selectableBuffer = new Selectable[Mathf.NextPowerOfTwo(Selectable.allSelectableCount)];
        }

        var count = Selectable.AllSelectablesNoAlloc(_selectableBuffer);
        for (var i = 0; i < count; i++)
        {
            var selectable = _selectableBuffer[i];
            if (IsSnappable(selectable) &&
                TryGetSnapDirection(selectable, origin, gazeDirection, out var direction, out var outsideDegrees) &&
                outsideDegrees <= maxDegrees)
            {
                _snapCandidates.Add((selectable, outsideDegrees, direction));
            }
        }

        System.Array.Clear(_selectableBuffer, 0, count);
        _snapCandidates.Sort((a, b) => a.OutsideDegrees.CompareTo(b.OutsideDegrees));
    }

    private (Selectable Selectable, float OutsideDegrees, Vector3 Direction)? FindCandidate(Selectable? selectable)
    {
        if (selectable == null) return null;
        foreach (var candidate in _snapCandidates)
        {
            if (candidate.Selectable == selectable) return candidate;
        }

        return null;
    }

    private static bool IsSnappable(Selectable selectable) =>
        selectable != null && selectable.isActiveAndEnabled && selectable.IsInteractable() &&
        selectable is not Scrollbar && selectable.gameObject.layer == (int)LayerHelper.GetVrUiLayer();

    // How far outside the selectable's rect the gaze is (0 when on it), and the direction to put the cursor: the point
    // of the rect nearest the gaze, kept a little inside its edges so a click lands on it.
    private static bool TryGetSnapDirection(Selectable selectable, Vector3 origin, Vector3 gazeDirection, out Vector3 direction, out float outsideDegrees)
    {
        direction = gazeDirection;
        outsideDegrees = float.MaxValue;
        if (selectable.transform is not RectTransform rectTransform) return false;

        var rect = rectTransform.rect;
        if (rect.width <= 0f || rect.height <= 0f) return false;

        var plane = new Plane(rectTransform.forward, rectTransform.position);
        if (!plane.Raycast(new Ray(origin, gazeDirection), out var hitDistance)) return false;

        var gazePoint = (Vector2)rectTransform.InverseTransformPoint(origin + gazeDirection * hitDistance);
        var edgePoint = new Vector2(
            Mathf.Clamp(gazePoint.x, rect.xMin, rect.xMax),
            Mathf.Clamp(gazePoint.y, rect.yMin, rect.yMax));
        var inset = rect.size * SnapInset;
        var cursorPoint = new Vector2(
            Mathf.Clamp(gazePoint.x, rect.xMin + inset.x, rect.xMax - inset.x),
            Mathf.Clamp(gazePoint.y, rect.yMin + inset.y, rect.yMax - inset.y));

        var toEdge = rectTransform.TransformPoint(edgePoint) - origin;
        var toCursor = rectTransform.TransformPoint(cursorPoint) - origin;
        if (toEdge.sqrMagnitude <= Mathf.Epsilon || toCursor.sqrMagnitude <= Mathf.Epsilon) return false;

        outsideDegrees = gazePoint == edgePoint ? 0f : Vector3.Angle(gazeDirection, toEdge);
        direction = toCursor.normalized;
        return true;
    }

    // Behind another menu, masked out of a scroll view or in a group that blocks clicks: only snap to what a click
    // there would actually hit.
    private bool IsTopmostAt(Camera camera, Selectable selectable, Vector3 direction)
    {
        var screenPoint = ToEventScreenPoint(camera, camera.transform.position + direction * DefaultProjectionDistance);
        return TryGetTopRaycastResult(screenPoint, out var result) &&
               result.gameObject.GetComponentInParent<Selectable>() == selectable;
    }

    // With eye tracking, the game's Select (designate) button clicks the menus too, so they work from the stick.
    private void UpdateSelectButton()
    {
        var config = ModConfiguration.Instance;
        SelectButtonClicksMenus = config.EyeTrackingEnabled.Value && config.SelectClicksMenus.Value && !GameUsesSelectHere();
        var held = SelectButtonClicksMenus && IsSelectButtonHeld();
        _selectPressedThisFrame = held && !_selectHeld;
        _selectHeld = held;
    }

    // The map picks units with Select and dialogue boxes answer to it themselves.
    private static bool GameUsesSelectHere() =>
        global::DynamicMap.mapMaximized ||
        global::CursorManager.GetFlag(global::CursorFlags.Map) ||
        global::CursorManager.GetFlag(global::CursorFlags.Dialogue);

    private static bool IsSelectButtonHeld()
    {
        var player = global::GameManager.playerInput;
        return Rewired.ReInput.isReady && player != null && player.GetButton("Select");
    }

    private Quaternion GetProjectionReferenceRotation()
    {
        if (_hasProjectionReferenceOverride)
        {
            return _projectionReferenceRotation;
        }

        return transform.parent != null ? transform.parent.rotation : Quaternion.identity;
    }

    // Home puts the OS mouse back in the middle of the game window, which maps to the projection reference forward.
    // The native menu recenters the headset itself on Home; everywhere else the headset is recentered here so that
    // reference forward (where the in-game menus sit) is where the player is looking.
    public void RecenterCursor()
    {
        _realMouse?.WarpCursorPosition(new Vector2(ScreenWidth * 0.5f, ScreenHeight * 0.5f));
        if (!_hasProjectionReferenceOverride)
        {
            NOVRHeadsetData.CalibrateTranslation();
            NOVRHeadsetData.CalibrateRotation();
        }
    }

    private void EnsureCursorCanvas(Camera uiCaptureCamera)
    {
        if (_cursor != null)
        {
            if (_cursorImage != null)
            {
                _cursorImage.texture = _texture;
            }
            return;
        }

        _cursor = new GameObject("VrUiCursorCanvas");
        _cursor.transform.localScale = Vector3.one * CursorCanvasScale;
        _cursorCanvas = _cursor.AddComponent<Canvas>();
        _cursorCanvas.renderMode = RenderMode.WorldSpace;
        _cursorCanvas.planeDistance = Mathf.Max(uiCaptureCamera.nearClipPlane + 0.01f, 0.11f);
        _cursorCanvas.overrideSorting = true;
        _cursorCanvas.sortingOrder = short.MaxValue;
        _cursorCanvas.pixelPerfect = true;

        _cursorRectTransform = _cursor.GetComponent<RectTransform>();
        _cursorRectTransform.sizeDelta = new Vector2(CursorTextureSize, CursorTextureSize);
        _cursorImage = _cursor.AddComponent<RawImage>();
        _cursorImage.raycastTarget = false;
        _cursorImage.texture = _texture;
        _cursorImage.color = CursorNormalColor;
        LayerHelper.SetLayerRecursive(_cursor.transform, LayerHelper.GetVrUiLayer());
        
        
        
        
    }


    private float GetDistanceUnderCursor(Vector2 screenPos)
    {
        _cursorOverInteractive = false;
        if (TryGetUiDistanceUnderCursor(screenPos, out var uiDistance, out var overInteractive))
        {
            _cursorOverInteractive = overInteractive;
            return uiDistance;
        }

        return DefaultProjectionDistance;
    }

    private bool TryGetUiDistanceUnderCursor(Vector2 screenPos, out float distance, out bool overInteractive)
    {
        distance = default;
        overInteractive = false;

        if (!TryGetTopRaycastResult(screenPos, out var result))
        {
            return false;
        }

        var camera = UiCamera;
        Vector3 cameraPos = camera != null ? camera.transform.position : Vector3.zero;

        overInteractive = IsInteractiveRaycastTarget(result.gameObject);
        distance = result.worldPosition == Vector3.zero
            ? result.distance
            : Vector3.Distance(cameraPos, result.worldPosition);

        return distance > 0f;
    }

    private bool TryGetTopRaycastResult(Vector2 screenPos, out RaycastResult topResult)
    {
        topResult = default;
        if (EventSystem.current == null)
        {
            return false;
        }

        var pointerEventData = new PointerEventData(EventSystem.current)
        {
            position = screenPos
        };

        _raycastResults.Clear();
        EventSystem.current.RaycastAll(pointerEventData, _raycastResults);

        foreach (var result in _raycastResults)
        {
            if (result.gameObject == _cursor || 
                result.distance < 0f ||
                result.gameObject.GetComponentInParent<global::MapIcon>() != null)
            {
                continue;
            }

            topResult = result;
            return true;
        }

        return false;
    }

    private static bool IsInteractiveRaycastTarget(GameObject gameObject)
    {
        var selectable = gameObject.GetComponentInParent<Selectable>();
        if (selectable != null)
        {
            return selectable.IsInteractable();
        }

        return ExecuteEvents.GetEventHandler<IPointerClickHandler>(gameObject) != null ||
               ExecuteEvents.GetEventHandler<IPointerDownHandler>(gameObject) != null ||
               ExecuteEvents.GetEventHandler<ISubmitHandler>(gameObject) != null ||
               ExecuteEvents.GetEventHandler<IDragHandler>(gameObject) != null;
    }

    private void UpdateCursorAnimation(Mouse realMouse)
    {
        if (_cursor == null || _cursorImage == null) return;

        if (realMouse.leftButton.wasPressedThisFrame || _selectPressedThisFrame)
        {
            _lastCursorClickTime = Time.unscaledTime;
        }

        var isPressed = realMouse.leftButton.isPressed || _selectHeld;
        var idlePulse = Mathf.Sin(Time.unscaledTime * CursorIdlePulseSpeed) * CursorIdlePulseScale;
        var clickProgress = Mathf.Clamp01((Time.unscaledTime - _lastCursorClickTime) / CursorClickPulseDuration);
        var clickPulse = clickProgress < 1f
            ? Mathf.Sin((1f - clickProgress) * Mathf.PI) * CursorClickPulseScale
            : 0f;

        var targetVisualScale = 1f + idlePulse + clickPulse;
        if (_cursorOverInteractive)
        {
            targetVisualScale *= CursorHoverScale;
        }
        if (isPressed)
        {
            targetVisualScale *= CursorPressedScale;
        }

        var targetScale = Vector3.one * (CursorCanvasScale * targetVisualScale);
        _cursor.transform.localScale = Vector3.Lerp(_cursor.transform.localScale, targetScale, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);

        var targetColor = CursorNormalColor;
        if (_cursorOverInteractive)
        {
            targetColor = CursorHoverColor;
        }
        if (isPressed)
        {
            targetColor = CursorPressedColor;
        }

        _cursorImage.color = Color.Lerp(_cursorImage.color, targetColor, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);
    }


    private float ProjectPitchAngle(float y)
    {
        int height = ScreenHeight;
        if (height <= 0) throw new System.InvalidOperationException($"[{nameof(VrUiCursor)}] Screen height is invalid ({height}).");
        return Mathf.Lerp(-MaxPitchDegrees, MaxPitchDegrees, y / height);
    }
    private float ProjectYawAngle(float x)
    {
        int width = ScreenWidth;
        if (width <= 0) throw new System.InvalidOperationException($"[{nameof(VrUiCursor)}] Screen width is invalid ({width}).");
        return Mathf.Lerp(-MaxYawDegrees, MaxYawDegrees, x / width);
    }
    private static bool IsRealCursorVisible() => Cursor.visible && Cursor.lockState != CursorLockMode.Locked;

    private static Texture2D CreateCursorTexture()
    {
        var texture = new Texture2D(CursorTextureSize, CursorTextureSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var colors = new Color32[CursorTextureSize * CursorTextureSize];
        var center = new Vector2((CursorTextureSize - 1) * 0.5f, (CursorTextureSize - 1) * 0.5f);
        var innerRadius = CursorRingRadius - CursorRingThickness * 0.5f;
        var outerRadius = CursorRingRadius + CursorRingThickness * 0.5f;
        var transparent = new Color32(0, 0, 0, 0);

        for (var y = 0; y < CursorTextureSize; y++)
        {
            for (var x = 0; x < CursorTextureSize; x++)
            {
                var distanceFromCenter = Vector2.Distance(new Vector2(x, y), center);
                var isRing = distanceFromCenter >= innerRadius && distanceFromCenter <= outerRadius;
                colors[y * CursorTextureSize + x] = isRing ? Color.white : transparent;
            }
        }

        texture.SetPixels32(colors);
        texture.Apply();
        return texture;
    }
    
    private void LogRaycastAtCursor()
    {
        if (EventSystem.current == null) return;
        
        var screenPos = GetScreenPoint();
        var pointerEventData = new PointerEventData(EventSystem.current)
        {
            position = screenPos
        };

        var results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerEventData, results);
        
        Debug.Log($"[VrUiCursor] Click Raycast at screenPos={screenPos}: found {results.Count} results");
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.gameObject == _cursor) continue;
            
            var canvas = result.gameObject.GetComponentInParent<Canvas>();
            var cg = result.gameObject.GetComponentInParent<CanvasGroup>();
            string cgInfo = cg != null ? $", CanvasGroup(alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts})" : "";
            string rectInfo = "";
            var rt = result.gameObject.GetComponent<RectTransform>();
            if (rt != null)
            {
                rectInfo = $", localPos={rt.localPosition}, size={rt.sizeDelta}";
            }
            Debug.Log($"[VrUiCursor]   Hit[{i}]: name='{result.gameObject.name}', path='{GetGameObjectPath(result.gameObject)}', canvas='{(canvas != null ? canvas.name : "None")}'{rectInfo}{cgInfo}");
        }
    }
    
    private static string GetGameObjectPath(GameObject go)
    {
        string path = go.name;
        Transform p = go.transform.parent;
        while (p != null)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }
        return path;
    }

    // A scene change can bring a new EventSystem/UI module whose actions still read the real mouse, i.e. desktop
    // coordinates pushed through the head-tracked UI camera, so re-check whenever the active module changes.
    private void EnsureUIModuleRestrictedToVirtualMouse()
    {
        var eventSystem = EventSystem.current;
        var module = eventSystem != null ? eventSystem.currentInputModule : null;
        if (module == null || module == _lastSeenUiModule) return;

        if (module is UnityEngine.InputSystem.UI.InputSystemUIInputModule uiModule)
        {
            if (!RestrictUIModuleToVirtualMouse(uiModule)) return;
        }
        else
        {
            NOVRPlugin.Log.LogWarning($"[{nameof(VrUiCursor)}] Active UI module is {module.GetType().Name} on '{module.name}'; it reads the real mouse, so UI clicks will not follow the VR cursor.");
        }

        _lastSeenUiModule = module;
    }

    private bool RestrictUIModuleToVirtualMouse(UnityEngine.InputSystem.UI.InputSystemUIInputModule uiModule)
    {
        try
        {
            if (_virtualMouse == null) return false;
            Debug.Log($"[NOVR] Restricting InputSystemUIInputModule actions to VirtualMouse (path: {_virtualMouse.path})");
            RestrictActionToVirtualMouse(uiModule.point?.action, _virtualMouse.path);
            RestrictActionToVirtualMouse(uiModule.leftClick?.action, _virtualMouse.path);
            RestrictActionToVirtualMouse(uiModule.middleClick?.action, _virtualMouse.path);
            RestrictActionToVirtualMouse(uiModule.rightClick?.action, _virtualMouse.path);
            RestrictActionToVirtualMouse(uiModule.scrollWheel?.action, _virtualMouse.path);
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[NOVR] Exception while restricting UI actions to VirtualMouse: {ex}");
            return false;
        }
    }

    private static void RestrictActionToVirtualMouse(InputAction? action, string devicePath)
    {
        if (action == null) return;
        for (int i = 0; i < action.bindings.Count; i++)
        {
            var binding = action.bindings[i];
            // <Pointer> also matches the real mouse, so it has to be narrowed down the same way.
            var genericDevice = binding.path.Contains("<Mouse>") ? "<Mouse>" : binding.path.Contains("<Pointer>") ? "<Pointer>" : null;
            if (genericDevice != null)
            {
                var newPath = binding.path.Replace(genericDevice, devicePath);
                action.ApplyBindingOverride(i, newPath);
                Debug.Log($"[NOVR] Overriding UI binding path: '{binding.path}' -> '{newPath}' for action '{action.name}'");
            }
        }
    }
}
