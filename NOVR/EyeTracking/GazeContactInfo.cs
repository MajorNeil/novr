using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NOVR.VrUi;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.EyeTracking;

// Rest the gaze on a HUD marker for Gaze Contact Dwell Seconds and a small card beside it says what it is, its range and,
// for aircraft and missiles, altitude, and flags RDR / LOCK if its radar has found the aircraft (ThreatAwareness) and MSL
// if a missile it fired is coming. It follows the marker, and goes a moment after the gaze leaves it. Same steady gaze as
// the reticle; the card sits to the right of the marker, clear of the reticle's target bracket.
[DefaultExecutionOrder(1000)]
public class GazeContactInfo : NOVRBehaviour
{
    // How close to a marker the gaze has to be to count as on it.
    private const float OnMarkerDegrees = 1.5f;
    private const float LingerSeconds = 1f;
    private const float CardOffsetDegrees = 2f;
    private const float CardTextDegrees = 0.9f;
    private const int SortingOrder = short.MaxValue - 4;

    private static readonly FieldInfo? MarkersField = AccessTools.Field(typeof(CombatHUD), "markers");

    private readonly StringBuilder _text = new();
    private GameObject? _root;
    private Text? _card;
    private HUDUnitMarker? _candidate;
    private float _candidateSince;
    private HUDUnitMarker? _shown;
    private float _lastOnShown;
    private bool _loggedFailure;

    private void OnDestroy()
    {
        if (_root != null) Destroy(_root);
    }

    private void LateUpdate()
    {
        try
        {
            if (!TryGetMarkers(out var hud, out var markers) || !NOVREyeGaze.TryGetSteadyUiDirection(out var gaze))
            {
                Hide();
                return;
            }

            var now = Time.unscaledTime;
            var camera = APIBus.CockpitHudCamera;
            var onMarker = MarkerUnderGaze(markers, camera, gaze);
            if (onMarker != _candidate)
            {
                _candidate = onMarker;
                _candidateSince = now;
            }

            var dwell = Mathf.Max(0f, ModConfiguration.Instance.GazeContactDwellSeconds.Value);
            if (_candidate != null && now - _candidateSince >= dwell) _shown = _candidate;
            if (_shown != null && _shown == _candidate) _lastOnShown = now;
            if (_shown == null || now - _lastOnShown > LingerSeconds || !IsShowing(_shown))
            {
                _shown = null;
                Hide();
                return;
            }

            ShowCard(hud, _shown, camera);
        }
        catch (Exception exception)
        {
            Hide();
            if (_loggedFailure) return;
            _loggedFailure = true;
            NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: contact info stopped: {exception.Message}");
        }
    }

    private static bool TryGetMarkers(out CombatHUD hud, out List<HUDUnitMarker> markers)
    {
        hud = SceneSingleton<CombatHUD>.i;
        markers = null!;
        if (!ModConfiguration.Instance.GazeContactInfo.Value || hud == null || hud.aircraft == null || NOUIManager.I == null) return false;

        // A menu or the map is up: the menu cursor has the gaze there.
        var cursor = VrUiCursor.I;
        if (cursor != null && cursor.IsActive) return false;

        if (MarkersField?.GetValue(hud) is not List<HUDUnitMarker> list) return false;
        markers = list;
        return true;
    }

    private static bool IsShowing(HUDUnitMarker marker) =>
        marker.unit != null && !marker.unit.disabled && marker.image != null && marker.image.enabled;

    // The showing marker nearest the gaze, if one is within OnMarkerDegrees of it.
    private static HUDUnitMarker? MarkerUnderGaze(List<HUDUnitMarker> markers, Camera camera, Vector3 gaze)
    {
        HUDUnitMarker? best = null;
        var bestDegrees = OnMarkerDegrees;
        foreach (var marker in markers)
        {
            if (marker == null || !IsShowing(marker)) continue;

            var degrees = Vector3.Angle(gaze, marker.image.transform.position - camera.transform.position);
            if (degrees > bestDegrees) continue;

            best = marker;
            bestDegrees = degrees;
        }

        return best;
    }

    private void ShowCard(CombatHUD hud, HUDUnitMarker marker, Camera camera)
    {
        var local = camera.transform.InverseTransformDirection(marker.image.transform.position - camera.transform.position);
        if (!HeadLockedPlane.TryProject(local, out var position))
        {
            Hide();
            return;
        }

        EnsureRoot();
        var unit = marker.unit;
        var aircraft = hud.aircraft;
        var hq = aircraft.NetworkHQ;
        var unitPosition = hq != null && hq.TryGetKnownPosition(unit, out var known) ? known : unit.GlobalPosition();

        _text.Clear();
        var definition = unit.definition;
        var name = definition == null ? unit.name : !string.IsNullOrEmpty(definition.code) ? definition.code : definition.unitName;
        _text.Append(name);
        _text.Append('\n').Append(UnitConverter.DistanceReading(Vector3.Distance(aircraft.transform.position, unitPosition.ToLocalPosition())));
        if (unit is Aircraft or Missile) _text.Append("  ").Append(UnitConverter.AltitudeReading(unitPosition.y));

        var flags = "";
        var threats = ThreatAwareness.I;
        if (threats != null && threats.TryGetRadarContact(unit, out var locked)) flags = locked ? "LOCK" : "RDR";
        if (threats != null && threats.HasMissileFrom(unit)) flags = flags.Length > 0 ? $"{flags}  MSL" : "MSL";
        if (flags.Length > 0) _text.Append('\n').Append(flags);

        _card!.text = _text.ToString();
        _card.fontSize = HeadLockedPlane.FontSize(CardTextDegrees);
        _card.color = ColorFor(unit);
        HeadLockedPlane.Place(_card.transform, position + Vector2.right * HeadLockedPlane.AngleToPlane(CardOffsetDegrees * InGameUiPlacement.HudScale));
        if (!_root!.activeSelf) _root.SetActive(true);
    }

    private static Color ColorFor(Unit unit)
    {
        try
        {
            var theme = ThemeManager.Active.ColorTheme;
            return DynamicMap.GetFactionMode(unit.NetworkHQ) switch
            {
                FactionMode.Enemy => theme.HudUnitHostile,
                FactionMode.Friendly => theme.HudUnitFriendly,
                _ => Color.white,
            };
        }
        catch (Exception)
        {
            return Color.white;
        }
    }

    private void Hide()
    {
        if (_root != null && _root.activeSelf) _root.SetActive(false);
    }

    private void EnsureRoot()
    {
        if (_root != null) return;

        _root = HeadLockedPlane.CreateRoot("NOVR Gaze Contact Info", SortingOrder);
        _card = HeadLockedPlane.CreateText("Contact Card", _root.transform, TextAnchor.MiddleLeft);
        // Grows to the right of where it is placed.
        _card.rectTransform.pivot = new Vector2(0f, 0.5f);
    }
}
