using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

// Near situational awareness, for VR, where looking around the cockpit is how the player keeps track of what is close.
// - A ring Ring Radius Degrees out from the centre of the view, moving with the head. Each incoming missile, enemy
//   aircraft within Aircraft Range, friendly within Friendly Range and radar that has found the aircraft gets a chevron
//   on it at the side it is on, pointing at it, with a short label; a double chevron means it is behind. Missiles are
//   coloured by seeker (IR orange, radar red, optical and others yellow), flash faster as they close, show their time to
//   impact, and are boxed where they are once inside the ring. Radars are cyan, LOCK when one guides at the aircraft.
//   Aircraft and radars inside the ring are left to the HUD's own markers.
// - For the nearest radar missile, ticks at its two notch headings (see TryGetNotch).
// - The nearest missile named in a banner at the top of the ring, and a warning just above the middle of the view (see
//   UpdateCenterWarning).
// - Tones from the direction of the missiles that need a reaction now (MissileTones).
// The game's own warnings (threat list, alarms, notch indicator) are unchanged.
public class ThreatAwareness : NOVRBehaviour
{
    public static ThreatAwareness? I { get; private set; }

    private const float MinRingDegrees = 15f;
    private const float MaxRingDegrees = 45f;
    private const float MissileChevronDegrees = 2.6f;
    private const float ContactChevronDegrees = 1.8f;
    private const float FriendlyChevronDegrees = 1.4f;
    private const float FriendlyAlpha = 0.6f;
    private const float MissileBoxDegrees = 3.2f;
    private const float NotchTickDegrees = 2.4f;
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
    private const float ClosingSpeedSmoothingSeconds = 0.3f;
    // Below this the missile isn't really closing (overshot, turning, still to pick up speed): no time to impact.
    private const float MinClosingSpeed = 30f;
    private const float MaxShownTimeToImpact = 99f;
    // Too slow to say which way the aircraft is flying relative to a radar.
    private const float MinNotchSpeed = 30f;
    private const int MaxEnemyAircraft = 6;
    private const int MaxFriendlyAircraft = 4;
    private const int TextureSize = 64;
    private const int SortingOrder = short.MaxValue - 3;

    private static readonly Color IrColor = new(1f, 0.55f, 0.05f);
    private static readonly Color RadarMissileColor = new(1f, 0.15f, 0.15f);
    private static readonly Color OtherMissileColor = new(1f, 0.9f, 0.1f);
    private static readonly Color RadarColor = new(0.2f, 0.85f, 1f);
    private static readonly Color NotchColor = new(0.35f, 1f, 0.45f);
    private static readonly Color FallbackHostileColor = new(1f, 0.35f, 0.35f);
    private static readonly Color FallbackFriendlyColor = new(0.4f, 0.7f, 1f);
    private static readonly FieldInfo? MarkersField = AccessTools.Field(typeof(CombatHUD), "markers");

    internal enum MissileKind
    {
        Infrared,
        ActiveRadar,
        SemiActiveRadar,
        Optical,
        Other,
    }

    internal readonly struct IncomingMissile
    {
        public readonly Missile Missile;
        public readonly Vector3 Position;
        public readonly float Distance;
        public readonly float ClosingSpeed;
        public readonly MissileKind Kind;
        public readonly string Seeker;

        public IncomingMissile(Missile missile, Vector3 position, float distance, float closingSpeed, MissileKind kind, string seeker)
        {
            Missile = missile;
            Position = position;
            Distance = distance;
            ClosingSpeed = closingSpeed;
            Kind = kind;
            Seeker = seeker;
        }

        public bool IsRadar => Kind is MissileKind.ActiveRadar or MissileKind.SemiActiveRadar;

        public float? TimeToImpact => ClosingSpeed >= MinClosingSpeed ? Distance / ClosingSpeed : null;
    }

    private enum Shape
    {
        Chevron,
        DoubleChevron,
        Box,
        Tick,
    }

    private sealed class RangeTrack
    {
        public float Distance;
        public float ClosingSpeed;
        public bool HasSpeed;
        public int Frame;
    }

    private readonly List<Indicator> _indicators = new();
    private readonly List<(Aircraft Aircraft, Vector3 Position, float Distance)> _enemyAircraft = new();
    private readonly List<(Aircraft Aircraft, Vector3 Position, float Distance)> _friendlyAircraft = new();
    private readonly Dictionary<Unit, (float Time, bool Lock)> _radarContacts = new();
    private readonly List<Unit> _expiredRadars = new();
    private readonly Dictionary<Shape, Texture2D> _textures = new();
    private readonly List<IncomingMissile> _incoming = new();
    private readonly HashSet<Missile> _seenMissiles = new();
    private readonly Dictionary<Missile, RangeTrack> _rangeTracks = new();
    private readonly List<Missile> _staleTracks = new();
    private readonly MissileTones _tones = new();
    private Missile? _lastDetected;
    private float _lastDetectedTime = float.NegativeInfinity;
    private int _frame;
    private GameObject? _root;
    private Text? _banner;
    private Text? _centerWarning;
    private Aircraft? _listeningTo;
    private int _used;
    private bool _loggedFailure;

    // Whether a radar on this unit found the aircraft in the last few seconds, and whether it is guiding at it.
    internal bool TryGetRadarContact(Unit unit, out bool locked)
    {
        locked = false;
        if (!_radarContacts.TryGetValue(unit, out var contact) || Time.unscaledTime - contact.Time > RadarMemorySeconds) return false;

        locked = contact.Lock;
        return true;
    }

    // Whether a missile this unit fired is coming at the aircraft.
    internal bool HasMissileFrom(Unit unit)
    {
        foreach (var incoming in _incoming)
        {
            if (incoming.Missile.owner == unit) return true;
        }

        return false;
    }

    protected override void Awake()
    {
        base.Awake();
        I = this;
    }

    private void OnDestroy()
    {
        if (I == this) I = null;
        Listen(null);
        _tones.Dispose();
        if (_root != null) Destroy(_root);
    }

    private void LateUpdate()
    {
        if (!TryGetFlying(out var aircraft, out var mainCamera))
        {
            Listen(null);
            _incoming.Clear();
            _tones.Stop();
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            return;
        }

        Listen(aircraft);
        try
        {
            var config = ModConfiguration.Instance;
            TrackMissiles(aircraft);
            UpdateTones(aircraft, config);

            var showRing = config.ThreatRing.Value;
            var showBanner = config.MissileBanner.Value;
            var showCenter = config.CenterMissileWarning.Value;
            if (!showRing && !showBanner && !showCenter)
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }

            EnsureRoot();
            if (!_root!.activeSelf) _root.SetActive(true);
            _used = 0;
            var ringDegrees = Mathf.Clamp(config.RingDegrees.Value, MinRingDegrees, MaxRingDegrees);
            if (showRing)
            {
                ShowMissiles(mainCamera, ringDegrees);
                if (config.NotchMarks.Value) ShowNotch(aircraft, mainCamera, ringDegrees, config.NotchToleranceDegrees.Value);
                ShowAircraft(aircraft, mainCamera, ringDegrees, config.AircraftRangeKm.Value * 1000f, config.FriendlyRangeKm.Value * 1000f);
                ShowRadars(mainCamera, ringDegrees);
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
    private void TrackMissiles(Aircraft aircraft)
    {
        _incoming.Clear();
        _frame++;
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
                var seeker = missile.GetSeekerType();
                if (string.IsNullOrEmpty(seeker)) seeker = "MSL";
                _incoming.Add(new IncomingMissile(missile, position, distance, TrackClosingSpeed(missile, distance), KindOf(seeker), seeker));
            }
        }

        _staleTracks.Clear();
        foreach (var track in _rangeTracks)
        {
            if (track.Value.Frame != _frame) _staleTracks.Add(track.Key);
        }

        foreach (var missile in _staleTracks) _rangeTracks.Remove(missile);
        // Forgotten once none are left, so the set doesn't grow over a long flight.
        if (_incoming.Count == 0) _seenMissiles.Clear();
    }

    // How fast the range is closing, from how it changed since last frame, smoothed. Taken from the range rather than from
    // velocities: other players' missiles are moved by the network, not by physics, and a range doesn't jump when the
    // game moves its floating origin.
    private float TrackClosingSpeed(Missile missile, float distance)
    {
        if (!_rangeTracks.TryGetValue(missile, out var track))
        {
            _rangeTracks[missile] = new RangeTrack { Distance = distance, Frame = _frame };
            return 0f;
        }

        track.Frame = _frame;
        var deltaTime = Time.deltaTime;
        if (deltaTime <= 0f) return track.ClosingSpeed;

        var measured = (track.Distance - distance) / deltaTime;
        track.Distance = distance;
        track.ClosingSpeed = track.HasSpeed
            ? Mathf.Lerp(track.ClosingSpeed, measured, deltaTime / (deltaTime + ClosingSpeedSmoothingSeconds))
            : measured;
        track.HasSpeed = true;
        return track.ClosingSpeed;
    }

    private static MissileKind KindOf(string seeker) => seeker switch
    {
        "IR" => MissileKind.Infrared,
        "ARH" => MissileKind.ActiveRadar,
        "SARH" => MissileKind.SemiActiveRadar,
        "Optical" or "INS / Opt." => MissileKind.Optical,
        _ => MissileKind.Other,
    };

    private static Color ColorOf(MissileKind kind) => kind switch
    {
        MissileKind.Infrared => IrColor,
        MissileKind.ActiveRadar or MissileKind.SemiActiveRadar => RadarMissileColor,
        _ => OtherMissileColor,
    };

    private static string WarningName(IncomingMissile missile) => missile.Kind switch
    {
        MissileKind.Infrared => "IR MISSILE",
        MissileKind.ActiveRadar or MissileKind.SemiActiveRadar => "RADAR MISSILE",
        MissileKind.Optical => "OPTICAL MISSILE",
        _ when missile.Seeker == "MSL" => "MISSILE",
        _ => $"{missile.Seeker.ToUpperInvariant()} MISSILE",
    };

    // "6s" while it is closing, otherwise the range.
    private static string TimeOrRange(IncomingMissile missile) => missile.TimeToImpact is { } seconds && seconds <= MaxShownTimeToImpact
        ? $"{Mathf.CeilToInt(seconds)}s"
        : UnitConverter.DistanceReading(missile.Distance);

    // "6s · 3.2km" while it is closing, otherwise the range.
    private static string TimeAndRange(IncomingMissile missile) => missile.TimeToImpact is { } seconds && seconds <= MaxShownTimeToImpact
        ? $"{Mathf.CeilToInt(seconds)}s  ·  {UnitConverter.DistanceReading(missile.Distance)}"
        : UnitConverter.DistanceReading(missile.Distance);

    // The nearest incoming missile within range that the filter accepts, if any.
    private IncomingMissile? NearestIncoming(float range, Func<IncomingMissile, bool>? filter = null)
    {
        IncomingMissile? nearest = null;
        foreach (var incoming in _incoming)
        {
            if (incoming.Distance > range || (filter != null && !filter(incoming))) continue;
            if (nearest == null || incoming.Distance < nearest.Value.Distance) nearest = incoming;
        }

        return nearest;
    }

    // Steady while a radar missile is still searching; otherwise flashing, faster the closer it is.
    private static float MissileAlpha(IncomingMissile missile)
    {
        if (missile.Missile.seekerMode == Missile.SeekerMode.activeSearch) return 0.7f;

        var hertz = Mathf.Lerp(6f, 1.5f, Mathf.InverseLerp(1500f, 8000f, missile.Distance));
        return Mathf.Sin(Time.unscaledTime * hertz * 2f * Mathf.PI) > -0.3f ? 1f : 0.3f;
    }

    private void ShowMissiles(Camera mainCamera, float ringDegrees)
    {
        foreach (var missile in _incoming)
        {
            var color = ColorOf(missile.Kind);
            color.a = MissileAlpha(missile);
            Show(mainCamera, missile.Position, ringDegrees, color, $"{missile.Seeker} {TimeOrRange(missile)}", MissileChevronDegrees, boxInside: true);
        }
    }

    // A radar loses a target flying across its line of sight: the game boosts a radar return by the target's speed along
    // that line (RadarParams.GetSignalStrength), so with none there is no boost to hold the lock. The line is from where
    // the guiding radar is (Missile.GetEvasionPoint: the missile itself for ARH, the launcher's radar for SARH). The
    // heading returned is the level one across the line on the side needing the smaller turn; inNotch is whether the
    // aircraft is already flying within toleranceDegrees of across it.
    internal static bool TryGetNotch(Aircraft aircraft, Missile missile, float toleranceDegrees, out Vector3 heading, out bool inNotch)
    {
        heading = Vector3.zero;
        inNotch = false;
        var lineOfSight = aircraft.GlobalPosition() - missile.GetEvasionPoint();
        if (lineOfSight.sqrMagnitude < 1f) return false;

        heading = Vector3.Cross(lineOfSight, Vector3.up);
        if (heading.sqrMagnitude < 1e-6f) heading = Vector3.Cross(lineOfSight, Vector3.right);
        heading.Normalize();

        var velocity = aircraft.rb != null ? aircraft.rb.velocity : aircraft.transform.forward;
        if (Vector3.Dot(heading, velocity) < 0f) heading = -heading;
        if (velocity.sqrMagnitude < MinNotchSpeed * MinNotchSpeed) return true;

        inNotch = Mathf.Abs(90f - Vector3.Angle(velocity, lineOfSight)) <= toleranceDegrees;
        return true;
    }

    private void ShowNotch(Aircraft aircraft, Camera mainCamera, float ringDegrees, float toleranceDegrees)
    {
        if (NearestIncoming(float.MaxValue, missile => missile.IsRadar) is not { } radarMissile) return;
        if (!TryGetNotch(aircraft, radarMissile.Missile, toleranceDegrees, out var heading, out var inNotch)) return;

        var preferred = NotchColor;
        if (!inNotch) preferred.a = 0.85f;
        var other = NotchColor;
        other.a = 0.3f;
        ShowTick(mainCamera, heading, ringDegrees, preferred, inNotch ? "NOTCH" : "");
        ShowTick(mainCamera, -heading, ringDegrees, other, "");
    }

    // A radial tick on the ring at the side a heading is on, or where it is when it is inside the ring.
    private void ShowTick(Camera mainCamera, Vector3 heading, float ringDegrees, Color color, string label)
    {
        var local = mainCamera.transform.InverseTransformDirection(heading);
        var round = Mathf.Atan2(local.y, local.x);
        var outward = new Vector2(Mathf.Cos(round), Mathf.Sin(round));
        var inside = local.z > 0f && Vector3.Angle(Vector3.forward, local) <= ringDegrees;
        var position = inside && HeadLockedPlane.TryProject(local, out var projected) ? projected : outward * HeadLockedPlane.RadiusAt(ringDegrees);
        var hudScale = InGameUiPlacement.HudScale;
        var labelPosition = position - outward * HeadLockedPlane.AngleToPlane(LabelInsetDegrees * hudScale);
        NextIndicator().Set(_textures[Shape.Tick], position, round * Mathf.Rad2Deg - 90f, HeadLockedPlane.AngleToPlane(NotchTickDegrees) * hudScale, color, label, labelPosition);
    }

    // Aircraft the HUD has markers for, where their faction knows them to be: enemies within enemyRange and friendlies
    // within friendlyRange, nearest first.
    private void ShowAircraft(Aircraft aircraft, Camera mainCamera, float ringDegrees, float enemyRange, float friendlyRange)
    {
        _enemyAircraft.Clear();
        _friendlyAircraft.Clear();
        if ((enemyRange <= 0f && friendlyRange <= 0f) || MarkersField?.GetValue(SceneSingleton<CombatHUD>.i) is not List<HUDUnitMarker> markers) return;

        var hq = aircraft.NetworkHQ;
        foreach (var marker in markers)
        {
            if (marker?.unit is not Aircraft other || other == null || other == aircraft || other.disabled) continue;
            if (hq == null || !hq.TryGetKnownPosition(other, out var known)) continue;

            var faction = DynamicMap.GetFactionMode(other.NetworkHQ);
            var position = known.ToLocalPosition();
            var distance = Vector3.Distance(aircraft.transform.position, position);
            if (faction == FactionMode.Enemy && distance <= enemyRange) _enemyAircraft.Add((other, position, distance));
            else if (faction == FactionMode.Friendly && distance <= friendlyRange) _friendlyAircraft.Add((other, position, distance));
        }

        _enemyAircraft.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        _friendlyAircraft.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var friendly = ThemeColor(theme => theme.HudUnitFriendly, FallbackFriendlyColor);
        friendly.a = FriendlyAlpha;
        for (var i = 0; i < _friendlyAircraft.Count && i < MaxFriendlyAircraft; i++)
        {
            var (_, position, distance) = _friendlyAircraft[i];
            Show(mainCamera, position, ringDegrees, friendly, UnitConverter.DistanceReading(distance), FriendlyChevronDegrees, boxInside: false);
        }

        var hostile = ThemeColor(theme => theme.HudUnitHostile, FallbackHostileColor);
        for (var i = 0; i < _enemyAircraft.Count && i < MaxEnemyAircraft; i++)
        {
            var (other, position, distance) = _enemyAircraft[i];
            var painting = _radarContacts.ContainsKey(other);
            var label = painting ? $"RDR {UnitConverter.DistanceReading(distance)}" : UnitConverter.DistanceReading(distance);
            Show(mainCamera, position, ringDegrees, painting ? RadarColor : hostile, label, ContactChevronDegrees, boxInside: false);
        }
    }

    // Radars that have found the aircraft in the last few seconds, other than the enemy aircraft already shown.
    private void ShowRadars(Camera mainCamera, float ringDegrees)
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

            if (emitter is Aircraft emitterAircraft && IsShownEnemy(emitterAircraft)) continue;

            var color = RadarColor;
            if (contact.Value.Lock) color.a = Mathf.Sin(now * 8f * Mathf.PI) > 0f ? 1f : 0.35f;
            Show(mainCamera, emitter.transform.position, ringDegrees, color, contact.Value.Lock ? "LOCK" : "RDR", ContactChevronDegrees, boxInside: false);
        }

        foreach (var emitter in _expiredRadars) _radarContacts.Remove(emitter);
    }

    private bool IsShownEnemy(Aircraft aircraft)
    {
        for (var i = 0; i < _enemyAircraft.Count && i < MaxEnemyAircraft; i++)
        {
            if (_enemyAircraft[i].Aircraft == aircraft) return true;
        }

        return false;
    }

    // An active radar missile within Missile Tone Range gets the continuous tone, which changes while the aircraft is in
    // its notch; the nearest other missile in range gets beeps, unless it is a semi-active radar one: those, and active
    // radar ones further out, are routine and left to the game's alarm.
    private void UpdateTones(Aircraft aircraft, ModConfiguration config)
    {
        if (!config.MissileTones.Value)
        {
            _tones.Stop();
            return;
        }

        var range = Mathf.Clamp(config.MissileToneRangeKm.Value, 0f, 30f) * 1000f;
        var activeRadar = NearestIncoming(range, missile => missile.Kind == MissileKind.ActiveRadar);
        var inNotch = false;
        if (activeRadar is { } radarMissile) TryGetNotch(aircraft, radarMissile.Missile, config.NotchToleranceDegrees.Value, out _, out inNotch);
        var other = NearestIncoming(range, missile => !missile.IsRadar);
        _tones.Update(activeRadar?.Position, inNotch, other?.Position, other?.Distance ?? 0f, Mathf.Clamp01(config.MissileToneVolume.Value));
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
        _rangeTracks.Clear();
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

        // The UI camera, which the ring hangs from, mirrors the main camera, so main camera space is the ring's space.
        var local = mainCamera.transform.InverseTransformDirection(offset).normalized;
        var inside = local.z > 0f && Vector3.Angle(Vector3.forward, local) <= ringDegrees;
        if (inside && !boxInside) return;

        var hudScale = InGameUiPlacement.HudScale;
        if (inside && HeadLockedPlane.TryProject(local, out var position))
        {
            var boxSize = HeadLockedPlane.AngleToPlane(MissileBoxDegrees) * hudScale;
            var below = position + Vector2.down * (boxSize * 0.5f + HeadLockedPlane.AngleToPlane(LabelDegrees * hudScale));
            NextIndicator().Set(_textures[Shape.Box], position, 0f, boxSize, color, label, below);
            return;
        }

        var round = Mathf.Atan2(local.y, local.x);
        var outward = new Vector2(Mathf.Cos(round), Mathf.Sin(round));
        var shape = local.z < 0f ? Shape.DoubleChevron : Shape.Chevron;
        var labelPosition = outward * HeadLockedPlane.RadiusAt(ringDegrees - LabelInsetDegrees * hudScale);
        NextIndicator().Set(_textures[shape], outward * HeadLockedPlane.RadiusAt(ringDegrees), round * Mathf.Rad2Deg - 90f,
            HeadLockedPlane.AngleToPlane(chevronDegrees) * hudScale, color, label, labelPosition);
    }

    private void UpdateBanner(bool show, float ringDegrees)
    {
        if (_banner == null) return;
        if (!show || NearestIncoming(float.MaxValue) is not { } missile)
        {
            if (_banner.enabled) _banner.enabled = false;
            return;
        }

        var more = _incoming.Count > 1 ? $"  +{_incoming.Count - 1}" : "";
        _banner.text = $"MISSILE  {missile.Seeker}  {TimeAndRange(missile)}{more}";
        var color = ColorOf(missile.Kind);
        color.a = MissileAlpha(missile);
        _banner.color = color;
        _banner.fontSize = HeadLockedPlane.FontSize(BannerDegrees);
        var height = HeadLockedPlane.RadiusAt(ringDegrees - BannerInsetDegrees * InGameUiPlacement.HudScale);
        HeadLockedPlane.Place(_banner.transform, new Vector2(0f, height));
        if (!_banner.enabled) _banner.enabled = true;
    }

    // What kind of missile is coming, in the middle of the view. It shows the moment the warning system detects one, then
    // fades, so a missile fired from far off doesn't sit in front of the player all the way in; while one is within
    // stickRange it stays, pulsing, with its time to impact and range, for the nearest such.
    private void UpdateCenterWarning(bool show, float stickRange)
    {
        if (_centerWarning == null) return;

        IncomingMissile? shown = null;
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

        var fontSize = HeadLockedPlane.FontSize(CenterWarningDegrees);
        var name = WarningName(missile);
        _centerWarning.text = sticking
            ? $"{name}\n<size={Mathf.RoundToInt(fontSize * CenterRangeTextScale)}>{TimeAndRange(missile)}</size>"
            : name;
        _centerWarning.fontSize = fontSize;
        var color = ColorOf(missile.Kind);
        color.a = alpha;
        _centerWarning.color = color;
        HeadLockedPlane.Place(_centerWarning.transform, new Vector2(0f, HeadLockedPlane.RadiusAt(CenterWarningHeightDegrees)));
        if (!_centerWarning.enabled) _centerWarning.enabled = true;
    }

    private static Color ThemeColor(Func<ColorTheme, Color> pick, Color fallback)
    {
        try
        {
            return pick(ThemeManager.Active.ColorTheme);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private Indicator NextIndicator()
    {
        if (_used == _indicators.Count) _indicators.Add(new Indicator(_root!.transform));
        return _indicators[_used++];
    }

    private void EnsureRoot()
    {
        if (_root != null) return;

        _textures[Shape.Chevron] = CreateTexture(Chevron);
        _textures[Shape.DoubleChevron] = CreateTexture((x, y) => Mathf.Max(Chevron(x, y - 12f), Chevron(x, y + 12f)));
        _textures[Shape.Box] = CreateTexture(Box);
        _textures[Shape.Tick] = CreateTexture(Tick);

        _root = HeadLockedPlane.CreateRoot("NOVR Threat Ring", SortingOrder);
        _banner = HeadLockedPlane.CreateText("Missile Banner", _root.transform);
        _banner.enabled = false;
        _centerWarning = HeadLockedPlane.CreateText("Center Missile Warning", _root.transform);
        _centerWarning.GetComponent<Outline>().effectDistance = new Vector2(6f, -6f);
        _centerWarning.enabled = false;
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

    // A bar along the texture's up axis, so it lies along the ring's radius.
    private static float Tick(float x, float y) => Mathf.Abs(x) <= 4f && Mathf.Abs(y) <= TextureSize * 0.45f ? 1f : 0f;

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

    // One chevron, box or tick and its label, reused from frame to frame.
    private sealed class Indicator
    {
        private readonly RawImage _icon;
        private readonly Text _label;

        public Indicator(Transform parent)
        {
            var iconObject = new GameObject("Threat", typeof(RectTransform));
            iconObject.transform.SetParent(parent, false);
            iconObject.layer = (int)LayerHelper.GetVrUiLayer();
            _icon = iconObject.AddComponent<RawImage>();
            _icon.raycastTarget = false;
            _label = HeadLockedPlane.CreateText("Threat Label", parent);
        }

        // Positions and sizes in meters on the plane.
        public void Set(Texture texture, Vector2 position, float rollDegrees, float size, Color color, string label, Vector2 labelPosition)
        {
            var iconTransform = _icon.rectTransform;
            HeadLockedPlane.Place(iconTransform, position);
            iconTransform.localRotation = Quaternion.Euler(0f, 0f, rollDegrees);
            iconTransform.sizeDelta = Vector2.one * (size / HeadLockedPlane.UnitMeters);
            _icon.texture = texture;
            _icon.color = color;
            if (!_icon.enabled) _icon.enabled = true;

            var showLabel = label.Length > 0;
            if (_label.enabled != showLabel) _label.enabled = showLabel;
            if (!showLabel) return;

            HeadLockedPlane.Place(_label.transform, labelPosition);
            _label.fontSize = HeadLockedPlane.FontSize(LabelDegrees);
            if (_label.text != label) _label.text = label;
            _label.color = color;
        }

        public void SetVisible(bool visible)
        {
            if (_icon.enabled != visible) _icon.enabled = visible;
            if (!visible && _label.enabled) _label.enabled = false;
        }
    }
}
