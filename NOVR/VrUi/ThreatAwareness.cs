using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

// Near situational awareness in the periphery. A ring sits Ring Radius Degrees out from the centre of the view, moving with
// the head; each incoming missile, enemy aircraft within Aircraft Range and radar that has found the aircraft gets a
// chevron on it, round the ring at the side it is on and pointing at it, with a short label. A double chevron means it
// is behind. Missiles are coloured by seeker (IR orange, radar red, anything else yellow), flash faster as they close
// and, once inside the ring, are boxed where they are instead; the nearest is also named in a banner at the top of the
// ring. Radars are cyan, LOCK when one is guiding at the aircraft. Enemy aircraft and radars inside the ring are left to
// the HUD's own markers. Just above the middle of the view, a center warning says what kind of missile it is (see
// UpdateCenterWarning). The game's own warnings (threat list, alarms, notch indicator) are unchanged.
public class ThreatAwareness : NOVRBehaviour
{
    // The ring is drawn on a plane this far ahead of the eyes, the HUD's distance, so it is in focus with the HUD.
    private const float PlaneDistance = 1000f;
    private const float CanvasUnitMeters = 0.25f;
    private const float MinRingDegrees = 15f;
    private const float MaxRingDegrees = 45f;
    private const float MissileChevronDegrees = 2.6f;
    private const float ContactChevronDegrees = 1.8f;
    private const float MissileBoxDegrees = 3.2f;
    private const float LabelDegrees = 1.0f;
    private const float LabelInsetDegrees = 2.8f;
    private const float BannerDegrees = 1.6f;
    private const float BannerInsetDegrees = 4.5f;
    private const float CenterWarningDegrees = 2.2f;
    private const float CenterWarningHeightDegrees = 5f;
    private const float CenterRangeTextScale = 0.6f;
    private const float WarningHoldSeconds = 1.5f;
    private const float WarningFadeSeconds = 1.5f;
    private const float RadarMemorySeconds = 3f;
    private const int MaxAircraft = 6;
    private const int TextureSize = 64;
    private const int SortingOrder = short.MaxValue - 3;

    private static readonly Color IrColor = new(1f, 0.55f, 0.05f);
    private static readonly Color RadarMissileColor = new(1f, 0.15f, 0.15f);
    private static readonly Color OtherMissileColor = new(1f, 0.9f, 0.1f);
    private static readonly Color RadarColor = new(0.2f, 0.85f, 1f);
    private static readonly Color FallbackHostileColor = new(1f, 0.35f, 0.35f);
    private static readonly FieldInfo? MarkersField = AccessTools.Field(typeof(CombatHUD), "markers");

    private enum Shape
    {
        Chevron,
        DoubleChevron,
        Box,
    }

    private readonly List<Indicator> _indicators = new();
    private readonly List<(Aircraft Aircraft, Vector3 Position, float Distance)> _nearbyAircraft = new();
    private readonly Dictionary<Unit, (float Time, bool Lock)> _radarContacts = new();
    private readonly List<Unit> _expiredRadars = new();
    private readonly Dictionary<Shape, Texture2D> _textures = new();
    private readonly List<(Missile Missile, float Distance, Color Color, string Seeker)> _incoming = new();
    private readonly HashSet<Missile> _seenMissiles = new();
    private Missile? _lastDetected;
    private float _lastDetectedTime = float.NegativeInfinity;
    private GameObject? _root;
    private Text? _banner;
    private Text? _centerWarning;
    private Font? _font;
    private Aircraft? _listeningTo;
    private int _used;
    private bool _loggedFailure;

    private void OnDestroy()
    {
        Listen(null);
        if (_root != null) Destroy(_root);
    }

    private void LateUpdate()
    {
        var config = ModConfiguration.Instance;
        var showRing = config.ThreatRing.Value;
        var showBanner = config.MissileBanner.Value;
        var showCenter = config.CenterMissileWarning.Value;
        if ((!showRing && !showBanner && !showCenter) || !TryGetFlying(out var aircraft, out var mainCamera))
        {
            Listen(null);
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            return;
        }

        Listen(aircraft);
        try
        {
            EnsureRoot();
            if (!_root!.activeSelf) _root.SetActive(true);
            _used = 0;
            var ringDegrees = Mathf.Clamp(config.RingDegrees.Value, MinRingDegrees, MaxRingDegrees);
            ShowMissiles(aircraft, mainCamera, ringDegrees, showRing);
            if (showRing)
            {
                ShowAircraft(aircraft, mainCamera, ringDegrees, config.AircraftRangeKm.Value * 1000f);
                ShowRadars(aircraft, mainCamera, ringDegrees);
            }

            for (var i = _used; i < _indicators.Count; i++) _indicators[i].SetVisible(false);
            UpdateBanner(showBanner, ringDegrees);
            UpdateCenterWarning(showCenter, Mathf.Clamp(config.CenterWarningRangeKm.Value, 0f, 20f) * 1000f);
        }
        catch (Exception exception)
        {
            if (_loggedFailure) return;
            _loggedFailure = true;
            NOVRPlugin.Log.LogWarning($"[NOVR] Threat ring stopped: {exception.Message}");
        }
    }

    // In the cockpit of a live aircraft, with the HUD up (the full map hides it).
    private static bool TryGetFlying(out Aircraft aircraft, out Camera mainCamera)
    {
        aircraft = null!;
        mainCamera = APIBus.MainCamera;
        var hud = SceneSingleton<CombatHUD>.i;
        if (hud == null || hud.aircraft == null || hud.aircraft.disabled || mainCamera == null || NOUIManager.I == null) return false;
        if (DynamicMap.mapMaximized || CameraStateManager.cameraMode != CameraMode.cockpit) return false;

        aircraft = hud.aircraft;
        return true;
    }

    // Missiles the warning system knows are coming at the aircraft, noting each the first time it shows up.
    private void ShowMissiles(Aircraft aircraft, Camera mainCamera, float ringDegrees, bool showRing)
    {
        _incoming.Clear();
        var warning = aircraft.GetMissileWarningSystem();
        var missiles = warning != null ? warning.knownMissiles : null;
        if (missiles != null)
        {
            foreach (var missile in missiles)
            {
                if (missile == null || missile.disabled) continue;

                if (_seenMissiles.Add(missile))
                {
                    _lastDetected = missile;
                    _lastDetectedTime = Time.unscaledTime;
                }

                var position = missile.transform.position;
                var distance = Vector3.Distance(aircraft.transform.position, position);
                var (color, seeker) = DescribeMissile(missile);
                _incoming.Add((missile, distance, color, seeker));
                if (!showRing) continue;

                var flashing = color;
                flashing.a = MissileAlpha(missile, distance);
                Show(mainCamera, position, ringDegrees, flashing, $"{seeker} {UnitConverter.DistanceReading(distance)}", MissileChevronDegrees, boxInside: true);
            }
        }

        // Forgotten once none are left, so the set doesn't grow over a long flight.
        if (_incoming.Count == 0) _seenMissiles.Clear();
    }

    // The nearest incoming missile within range, if any.
    private (Missile Missile, float Distance, Color Color, string Seeker)? NearestIncoming(float range)
    {
        (Missile Missile, float Distance, Color Color, string Seeker)? nearest = null;
        foreach (var incoming in _incoming)
        {
            if (incoming.Distance <= range && (nearest == null || incoming.Distance < nearest.Value.Distance)) nearest = incoming;
        }

        return nearest;
    }

    private static (Color Color, string Seeker) DescribeMissile(Missile missile)
    {
        var seeker = missile.GetSeekerType();
        return seeker switch
        {
            "IR" => (IrColor, "IR"),
            "ARH" or "SARH" => (RadarMissileColor, seeker),
            _ => (OtherMissileColor, string.IsNullOrEmpty(seeker) ? "MSL" : seeker),
        };
    }

    // Steady while a radar missile is still searching; otherwise flashing, faster the closer it is.
    private static float MissileAlpha(Missile missile, float distance)
    {
        if (missile.seekerMode == Missile.SeekerMode.activeSearch) return 0.7f;

        var hertz = Mathf.Lerp(6f, 1.5f, Mathf.InverseLerp(1500f, 8000f, distance));
        return Mathf.Sin(Time.unscaledTime * hertz * 2f * Mathf.PI) > -0.3f ? 1f : 0.3f;
    }

    // Enemy aircraft the HUD has markers for, nearest first, where the faction knows them to be.
    private void ShowAircraft(Aircraft aircraft, Camera mainCamera, float ringDegrees, float range)
    {
        _nearbyAircraft.Clear();
        if (range <= 0f || MarkersField?.GetValue(SceneSingleton<CombatHUD>.i) is not List<HUDUnitMarker> markers) return;

        var hq = aircraft.NetworkHQ;
        foreach (var marker in markers)
        {
            if (marker?.unit is not Aircraft other || other == null || other == aircraft || other.disabled) continue;
            if (DynamicMap.GetFactionMode(other.NetworkHQ) != FactionMode.Enemy) continue;
            if (hq == null || !hq.TryGetKnownPosition(other, out var known)) continue;

            var position = known.ToLocalPosition();
            var distance = Vector3.Distance(aircraft.transform.position, position);
            if (distance <= range) _nearbyAircraft.Add((other, position, distance));
        }

        _nearbyAircraft.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var hostile = HostileColor();
        for (var i = 0; i < _nearbyAircraft.Count && i < MaxAircraft; i++)
        {
            var (other, position, distance) = _nearbyAircraft[i];
            var painting = _radarContacts.ContainsKey(other);
            var label = painting ? $"RDR {UnitConverter.DistanceReading(distance)}" : UnitConverter.DistanceReading(distance);
            Show(mainCamera, position, ringDegrees, painting ? RadarColor : hostile, label, ContactChevronDegrees, boxInside: false);
        }
    }

    // Radars that have found the aircraft in the last few seconds, other than the aircraft already shown.
    private void ShowRadars(Aircraft aircraft, Camera mainCamera, float ringDegrees)
    {
        var now = Time.unscaledTime;
        _expiredRadars.Clear();
        foreach (var contact in _radarContacts)
        {
            var emitter = contact.Key;
            if (emitter == null || emitter.disabled || now - contact.Value.Time > RadarMemorySeconds)
            {
                _expiredRadars.Add(emitter!);
                continue;
            }

            if (emitter is Aircraft emitterAircraft && IsShownAircraft(emitterAircraft)) continue;

            var color = RadarColor;
            if (contact.Value.Lock) color.a = Mathf.Sin(now * 8f * Mathf.PI) > 0f ? 1f : 0.35f;
            Show(mainCamera, emitter.transform.position, ringDegrees, color, contact.Value.Lock ? "LOCK" : "RDR", ContactChevronDegrees, boxInside: false);
        }

        foreach (var emitter in _expiredRadars) _radarContacts.Remove(emitter);
    }

    private bool IsShownAircraft(Aircraft aircraft)
    {
        for (var i = 0; i < _nearbyAircraft.Count && i < MaxAircraft; i++)
        {
            if (_nearbyAircraft[i].Aircraft == aircraft) return true;
        }

        return false;
    }

    private void Listen(Aircraft? aircraft)
    {
        if (ReferenceEquals(aircraft, _listeningTo)) return;

        if (_listeningTo is { } previous)
        {
            try
            {
                previous.onRadarWarning -= OnRadarWarning;
            }
            catch (Exception)
            {
                // Already torn down with its aircraft.
            }
        }

        _radarContacts.Clear();
        _seenMissiles.Clear();
        _lastDetected = null;
        _listeningTo = aircraft;
        if (aircraft != null) aircraft.onRadarWarning += OnRadarWarning;
    }

    private void OnRadarWarning(Aircraft.OnRadarWarning warning)
    {
        if (!warning.detected || warning.emitter == null) return;

        var locked = warning.isTarget || (_radarContacts.TryGetValue(warning.emitter, out var previous) && previous.Lock &&
                                          Time.unscaledTime - previous.Time < RadarMemorySeconds);
        _radarContacts[warning.emitter] = (Time.unscaledTime, locked);
    }

    // Places one indicator for something at worldPosition: on the ring at its side when it is outside it (or behind),
    // or, for missiles, boxed where it is when it is inside.
    private void Show(Camera mainCamera, Vector3 worldPosition, float ringDegrees, Color color, string label, float chevronDegrees, bool boxInside)
    {
        var offset = worldPosition - mainCamera.transform.position;
        if (offset.sqrMagnitude < 1f) return;

        // The UI camera, which this hangs from, mirrors the main camera, so main camera space is the ring's space.
        var local = mainCamera.transform.InverseTransformDirection(offset).normalized;
        var offAxis = Vector3.Angle(Vector3.forward, local);
        var inside = local.z > 0f && offAxis <= ringDegrees;
        if (inside && !boxInside) return;

        var round = Mathf.Atan2(local.y, local.x);
        var outward = new Vector2(Mathf.Cos(round), Mathf.Sin(round));
        var hudScale = InGameUiPlacement.HudScale;
        var indicator = NextIndicator();
        if (inside)
        {
            var position = new Vector2(local.x, local.y) * (PlaneDistance / local.z);
            var boxSize = AngleToPlane(MissileBoxDegrees) * hudScale;
            indicator.Set(_textures[Shape.Box], position, 0f, boxSize, color, label, position + Vector2.down * (boxSize * 0.5f + AngleToPlane(LabelDegrees)), hudScale);
            return;
        }

        var ringRadius = PlaneDistance * Mathf.Tan(ringDegrees * Mathf.Deg2Rad);
        var labelRadius = PlaneDistance * Mathf.Tan((ringDegrees - LabelInsetDegrees * hudScale) * Mathf.Deg2Rad);
        var shape = local.z < 0f ? Shape.DoubleChevron : Shape.Chevron;
        indicator.Set(_textures[shape], outward * ringRadius, round * Mathf.Rad2Deg - 90f, AngleToPlane(chevronDegrees) * hudScale, color, label, outward * labelRadius, hudScale);
    }

    private void UpdateBanner(bool show, float ringDegrees)
    {
        if (_banner == null) return;
        if (!show || NearestIncoming(float.MaxValue) is not { } missile)
        {
            if (_banner.enabled) _banner.enabled = false;
            return;
        }

        var hudScale = InGameUiPlacement.HudScale;
        var more = _incoming.Count > 1 ? $"  +{_incoming.Count - 1}" : "";
        _banner.text = $"MISSILE  {missile.Seeker}  {UnitConverter.DistanceReading(missile.Distance)}{more}";
        var color = missile.Color;
        color.a = MissileAlpha(missile.Missile, missile.Distance);
        _banner.color = color;
        _banner.fontSize = Mathf.RoundToInt(AngleToPlane(BannerDegrees) * hudScale / CanvasUnitMeters);
        var height = PlaneDistance * Mathf.Tan((ringDegrees - BannerInsetDegrees * hudScale) * Mathf.Deg2Rad);
        _banner.rectTransform.localPosition = new Vector3(0f, height / CanvasUnitMeters, 0f);
        if (!_banner.enabled) _banner.enabled = true;
    }

    // What kind of missile is coming, in the middle of the view. It shows the moment the warning system detects one, then
    // fades, so a missile fired from far off doesn't sit in front of the player all the way in; while one is within
    // stickRange it stays, pulsing, with the range, for the nearest such.
    private void UpdateCenterWarning(bool show, float stickRange)
    {
        if (_centerWarning == null) return;

        (Missile Missile, float Distance, Color Color, string Seeker)? shown = null;
        var alpha = 0f;
        var sticking = false;
        if (show && NearestIncoming(stickRange) is { } near)
        {
            shown = near;
            sticking = true;
            var hertz = Mathf.Lerp(3f, 1f, Mathf.InverseLerp(2000f, Mathf.Max(stickRange, 2001f), near.Distance));
            alpha = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * hertz * 2f * Mathf.PI);
        }
        else if (show && _lastDetected != null)
        {
            var age = Time.unscaledTime - _lastDetectedTime;
            foreach (var incoming in _incoming)
            {
                if (incoming.Missile != _lastDetected || age >= WarningHoldSeconds + WarningFadeSeconds) continue;

                shown = incoming;
                alpha = age <= WarningHoldSeconds ? 1f : 1f - (age - WarningHoldSeconds) / WarningFadeSeconds;
            }
        }

        if (shown is not { } missile || alpha <= 0f)
        {
            if (_centerWarning.enabled) _centerWarning.enabled = false;
            return;
        }

        var hudScale = InGameUiPlacement.HudScale;
        var fontSize = Mathf.RoundToInt(AngleToPlane(CenterWarningDegrees) * hudScale / CanvasUnitMeters);
        var name = WarningName(missile.Seeker);
        _centerWarning.text = sticking
            ? $"{name}\n<size={Mathf.RoundToInt(fontSize * CenterRangeTextScale)}>{UnitConverter.DistanceReading(missile.Distance)}</size>"
            : name;
        _centerWarning.fontSize = fontSize;
        var color = missile.Color;
        color.a = alpha;
        _centerWarning.color = color;
        var height = PlaneDistance * Mathf.Tan(CenterWarningHeightDegrees * Mathf.Deg2Rad);
        _centerWarning.rectTransform.localPosition = new Vector3(0f, height / CanvasUnitMeters, 0f);
        if (!_centerWarning.enabled) _centerWarning.enabled = true;
    }

    private static string WarningName(string seeker) => seeker switch
    {
        "IR" => "IR MISSILE",
        "ARH" or "SARH" => "RADAR MISSILE",
        "Optical" or "INS / Opt." => "OPTICAL MISSILE",
        "MSL" => "MISSILE",
        _ => $"{seeker.ToUpperInvariant()} MISSILE",
    };

    private static Color HostileColor()
    {
        try
        {
            return ThemeManager.Active.ColorTheme.HudUnitHostile;
        }
        catch (Exception)
        {
            return FallbackHostileColor;
        }
    }

    // Size on the plane, in meters, of something this many degrees across.
    private static float AngleToPlane(float degrees) => 2f * PlaneDistance * Mathf.Tan(degrees * 0.5f * Mathf.Deg2Rad);

    private Indicator NextIndicator()
    {
        if (_used == _indicators.Count) _indicators.Add(new Indicator(_root!.transform, _font));
        return _indicators[_used++];
    }

    // Hangs from the UI camera so it moves with the head exactly, like the cursor ring of a helmet sight.
    private void EnsureRoot()
    {
        if (_root != null) return;

        _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        _textures[Shape.Chevron] = CreateTexture(Chevron);
        _textures[Shape.DoubleChevron] = CreateTexture((x, y) => Mathf.Max(Chevron(x, y - 12f), Chevron(x, y + 12f)));
        _textures[Shape.Box] = CreateTexture(Box);

        _root = new GameObject("NOVR Threat Ring", typeof(RectTransform));
        _root.transform.SetParent(APIBus.CockpitHudCamera.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, PlaneDistance);
        _root.transform.localRotation = Quaternion.identity;
        _root.transform.localScale = Vector3.one * CanvasUnitMeters;
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = SortingOrder;

        _banner = CreateText("Missile Banner", _root.transform, _font);
        _banner.enabled = false;
        _centerWarning = CreateText("Center Missile Warning", _root.transform, _font);
        _centerWarning.GetComponent<Outline>().effectDistance = new Vector2(6f, -6f);
        _centerWarning.enabled = false;
        LayerHelper.SetLayerRecursive(_root.transform, LayerHelper.GetVrUiLayer());
    }

    private static Text CreateText(string name, Transform parent, Font? font)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        textObject.layer = (int)LayerHelper.GetVrUiLayer();
        var text = textObject.AddComponent<Text>();
        text.font = font;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        // Readable against bright sky and dark ground alike.
        var outline = textObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
        outline.effectDistance = new Vector2(3f, -3f);
        return text;
    }

    // A chevron pointing up the texture, x and y from its centre.
    private static float Chevron(float x, float y)
    {
        const float halfWidth = TextureSize * 0.42f;
        const float thickness = 6f;
        var arm = y - (TextureSize * 0.18f - Mathf.Abs(x) * 0.9f);
        return Mathf.Abs(x) <= halfWidth ? Mathf.Clamp01(thickness * 0.5f - Mathf.Abs(arm) + 0.5f) : 0f;
    }

    private static float Box(float x, float y)
    {
        const float half = TextureSize * 0.5f - 2f;
        const float thickness = 4f;
        float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
        var onEdge = (ax >= half - thickness && ax <= half && ay <= half) || (ay >= half - thickness && ay <= half && ax <= half);
        return onEdge ? 1f : 0f;
    }

    private static Texture2D CreateTexture(Func<float, float, float> alpha)
    {
        var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var colors = new Color32[TextureSize * TextureSize];
        var center = (TextureSize - 1) * 0.5f;
        for (var y = 0; y < TextureSize; y++)
        {
            for (var x = 0; x < TextureSize; x++)
            {
                colors[y * TextureSize + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(x - center, y - center)) * 255f));
            }
        }

        texture.SetPixels32(colors);
        texture.Apply();
        return texture;
    }

    // One chevron or box and its label, reused from frame to frame.
    private sealed class Indicator
    {
        private readonly RawImage _icon;
        private readonly Text _label;

        public Indicator(Transform parent, Font? font)
        {
            var iconObject = new GameObject("Threat", typeof(RectTransform));
            iconObject.transform.SetParent(parent, false);
            iconObject.layer = (int)LayerHelper.GetVrUiLayer();
            _icon = iconObject.AddComponent<RawImage>();
            _icon.raycastTarget = false;
            _label = CreateText("Threat Label", parent, font);
        }

        // Positions and sizes in meters on the ring's plane.
        public void Set(Texture texture, Vector2 position, float rollDegrees, float size, Color color, string label, Vector2 labelPosition, float hudScale)
        {
            var iconTransform = _icon.rectTransform;
            iconTransform.localPosition = position / CanvasUnitMeters;
            iconTransform.localRotation = Quaternion.Euler(0f, 0f, rollDegrees);
            iconTransform.sizeDelta = Vector2.one * (size / CanvasUnitMeters);
            _icon.texture = texture;
            _icon.color = color;

            _label.rectTransform.localPosition = labelPosition / CanvasUnitMeters;
            _label.fontSize = Mathf.RoundToInt(AngleToPlane(LabelDegrees) * hudScale / CanvasUnitMeters);
            if (_label.text != label) _label.text = label;
            _label.color = color;
            SetVisible(true);
        }

        public void SetVisible(bool visible)
        {
            if (_icon.enabled != visible) _icon.enabled = visible;
            if (_label.enabled != visible) _label.enabled = visible;
        }
    }
}
