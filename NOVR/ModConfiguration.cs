using System.Collections.Generic;
using System.ComponentModel;
using BepInEx.Configuration;
using NOVR.VrUi.Menu;
using UnityEngine;

namespace NOVR;

public class ModConfiguration
{
    public static ModConfiguration Instance;
    // 0.4.18's "Ring Degrees" default, which put the ring further out than wanted.
    private const float OldRingDegreesDefault = 30f;
    

    public readonly ConfigFile Config;
    public readonly ConfigEntry<bool> EnableNativeMenuUi;
    public readonly ConfigEntry<float> NativeMenuScale;
    public readonly ConfigEntry<float> NativeMenuDistance;
    public readonly ConfigEntry<float> NativeMenuHeightOffset;
    public readonly ConfigEntry<float> InGameUiScale;
    public readonly ConfigEntry<float> InGameUiDistance;
    public readonly ConfigEntry<float> HudScale;
    public readonly ConfigEntry<float> MinimapZoomOut;
    public readonly ConfigEntry<bool> ThreatRing;
    public readonly ConfigEntry<float> RingDegrees;
    public readonly ConfigEntry<float> AircraftRangeKm;
    public readonly ConfigEntry<float> FriendlyRangeKm;
    public readonly ConfigEntry<bool> NotchMarks;
    public readonly ConfigEntry<float> NotchToleranceDegrees;
    public readonly ConfigEntry<bool> MissileTones;
    public readonly ConfigEntry<float> MissileToneRangeKm;
    public readonly ConfigEntry<float> MissileToneVolume;
    public readonly ConfigEntry<bool> MissileBanner;
    public readonly ConfigEntry<bool> CenterMissileWarning;
    public readonly ConfigEntry<float> CenterWarningRangeKm;
    public readonly ConfigEntry<bool> EyeTrackingEnabled;
    public readonly ConfigEntry<GazeCursorMode> GazeMenuCursor;
    public readonly ConfigEntry<bool> SelectClicksMenus;
    public readonly ConfigEntry<float> MenuSnapDegrees;
    public readonly ConfigEntry<float> GazeSteadiness;
    public readonly ConfigEntry<bool> GazeDesignate;
    public readonly ConfigEntry<bool> ShowGazeReticle;
    public readonly ConfigEntry<bool> ShowGazeDot;
    public readonly ConfigEntry<bool> GazeContactInfo;
    public readonly ConfigEntry<float> GazeContactDwellSeconds;
    public readonly ConfigEntry<bool> MenuHangar;
    public readonly Dictionary<MenuCommand, ConfigEntry<string>> MenuBindings = new();

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;
        EnableNativeMenuUi = config.Bind(
            "Experimental",
            "Enable Native Menu UI",
            true,
            "Use NOVR's native VR menu UI for non-flight menus. Disable to fall back to the existing patched game UI.");

        NativeMenuScale = config.Bind(
            "Experimental",
            "Native Menu Scale",
            1.25f,
            "Size multiplier for NOVR's native VR menu UI. Values from 0.75 to 2.0 are supported.");

        NativeMenuDistance = config.Bind(
            "Experimental",
            "Native Menu Distance",
            3.0f,
            "Distance in meters from the headset when NOVR's native VR menu UI is opened or recentered. Values from 1.5 to 6.0 are supported.");

        NativeMenuHeightOffset = config.Bind(
            "Experimental",
            "Native Menu Height Offset",
            0.0f,
            "Vertical offset in meters applied when NOVR's native VR menu UI is opened or recentered. Values from -0.25 to 1.0 are supported.");

        InGameUiScale = config.Bind(
            "In-Game UI",
            "Scale",
            1.0f,
            "Size multiplier for the in-game menus, map and key binding screen. Values from 0.5 to 2.0 are supported. Also adjustable in game from the VR UI button under the menus.");

        InGameUiDistance = config.Bind(
            "In-Game UI",
            "Distance",
            3.0f,
            "Distance in meters of the in-game menus, map and key binding screen. Values from 1.0 to 6.0 are supported. Also adjustable in game from the VR UI button under the menus.");

        HudScale = config.Bind(
            "In-Game UI",
            "HUD Scale",
            1.0f,
            "Size multiplier for the flight HUD: speed, altitude, heading and artificial horizon readouts, pitch ladder, weapon panels and target markers. Markers stay on what they point at. Values from 0.5 to 2.0 are supported. Also adjustable in game from the VR UI button under the menus.");

        MinimapZoomOut = config.Bind(
            "In-Game UI",
            "Minimap Zoom Out",
            1.0f,
            "How far the corner minimap is zoomed out compared with the game's own zoom: 2 shows twice as much ground across, 0.5 zooms in. Values from 0.5 to 8.0 are supported. The full map is unaffected. Also adjustable in game from the VR UI button under the menus.");

        ThreatRing = config.Bind(
            "Threat Awareness",
            "Threat Ring",
            true,
            "In flight, a ring of chevrons near the edge of your view, moving with your head, pointing at incoming missiles (IR orange, radar red, other yellow; flashing faster as they close), enemy aircraft within Aircraft Range and radars that have found you (cyan, LOCK when guiding at you). A double chevron means it is behind you. Missiles inside the ring are boxed where they are.");

        // The setting used to be "Ring Degrees". Its line comes out of the file, and a value other than its old default
        // carries over. Saving is held off meanwhile, so the old line isn't written back on every launch.
        var oldRingDegrees = new ConfigDefinition("Threat Awareness", "Ring Degrees");
        var saveOnConfigSet = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
        var keptRingDegrees = config.Bind(oldRingDegrees, OldRingDegreesDefault).Value;
        config.Remove(oldRingDegrees);
        config.SaveOnConfigSet = saveOnConfigSet;
        RingDegrees = config.Bind(
            "Threat Awareness",
            "Ring Radius Degrees",
            24f,
            "How far out from the centre of your view the threat ring sits, in degrees. Values from 15 to 45 are supported; lower is easier to notice, higher keeps it out of the way.");
        if (!Mathf.Approximately(keptRingDegrees, OldRingDegreesDefault)) RingDegrees.Value = keptRingDegrees;

        AircraftRangeKm = config.Bind(
            "Threat Awareness",
            "Aircraft Range km",
            10f,
            "Enemy aircraft within this many kilometres get a chevron on the threat ring (the nearest six). 0 shows none.");

        FriendlyRangeKm = config.Bind(
            "Threat Awareness",
            "Friendly Range km",
            5f,
            "Friendly aircraft within this many kilometres get a smaller, dimmer chevron on the threat ring in the HUD's friendly colour (the nearest four). 0 shows none.");

        NotchMarks = config.Bind(
            "Threat Awareness",
            "Notch Marks",
            true,
            "While a radar missile is coming, two green ticks on the threat ring at the headings that put you across the line from the radar guiding it (the notch, where the game's radar loses the boost it gets from your speed towards it). The brighter one is the smaller turn; it reads NOTCH once you are in it.");

        NotchToleranceDegrees = config.Bind(
            "Threat Awareness",
            "Notch Tolerance Degrees",
            12f,
            "How close to straight across the radar's line of sight your flight path has to be to count as in the notch, for the NOTCH mark and the notch tone.");

        MissileTones = config.Bind(
            "Threat Awareness",
            "Missile Tones",
            true,
            "Sounds from the direction of the missiles that need a reaction, within Missile Tone Range km: a continuous tone for an active radar (ARH) missile, which turns into a lower pulsing tone while you are in its notch, and beeps for IR, optical and other missiles, quicker as they close (pitched lower when behind you). Semi-active radar missiles, and ARH ones further out, are left to the game's alarm. Plays through the game's missile alert volume.");

        MissileToneRangeKm = config.Bind(
            "Threat Awareness",
            "Missile Tone Range km",
            8f,
            "How close a missile has to be for Missile Tones. Values from 0 to 30 are supported.");

        MissileToneVolume = config.Bind(
            "Threat Awareness",
            "Missile Tone Volume",
            0.5f,
            "Volume of the Missile Tones, from 0 to 1, on top of the game's missile alert volume.");

        MissileBanner = config.Bind(
            "Threat Awareness",
            "Missile Banner",
            true,
            "While a missile is coming at you, name the nearest at the top of the threat ring: its seeker (IR, ARH, SARH...) and range, and how many more there are, in the seeker's colour.");

        CenterMissileWarning = config.Bind(
            "Threat Awareness",
            "Center Missile Warning",
            true,
            "Just above the middle of your view, say what kind of missile is coming (IR MISSILE, RADAR MISSILE, OPTICAL MISSILE) the moment one is detected, fading out after a few seconds. While one is within Center Warning Range km it stays, with its range.");

        CenterWarningRangeKm = config.Bind(
            "Threat Awareness",
            "Center Warning Range km",
            8f,
            "Within this many kilometres an incoming missile keeps the center warning up until it is gone. Values from 0 (only the warning on detection) to 20 are supported. IR and optical missiles are only detected within 5 km, so they stay up from the start.");

        EyeTrackingEnabled = config.Bind(
            "Eye Tracking",
            "Enabled",
            true,
            "Ask the headset runtime for eye gaze (OpenXR XR_EXT_eye_gaze_interaction). Without it, or if the runtime doesn't offer it, everything below stays on the mouse and head. Needs a game restart.");

        GazeMenuCursor = config.Bind(
            "Eye Tracking",
            "Menu Cursor",
            GazeCursorMode.Hybrid,
            "What moves the menu cursor. Gaze: where you look. Mouse: the mouse only. Hybrid: where you look, until the mouse moves; it hands back to gaze after the mouse rests for a moment.");

        SelectClicksMenus = config.Bind(
            "Eye Tracking",
            "Select Clicks Menus",
            true,
            "The game's Select (target designate) button clicks whatever the menu cursor is on, so menus work from the stick. Not on the map, which already picks units with Select, or on dialogue boxes.");

        MenuSnapDegrees = config.Bind(
            "Eye Tracking",
            "Menu Snap Degrees",
            1.5f,
            "When the gaze cursor lands just off a button, it snaps onto the nearest one within this many degrees. 0 turns snapping off.");

        GazeSteadiness = config.Bind(
            "Eye Tracking",
            "Gaze Steadiness",
            0.6f,
            "How still the gaze menu cursor and flight reticle hold while you look at one spot, from 0 (follows every flicker of the tracker) to 1 (steadiest, slowest to settle on small eye movements). Looking further away always jumps straight there. Designating uses the same gaze, so it picks what the reticle shows.");

        GazeDesignate = config.Bind(
            "Eye Tracking",
            "Look To Designate",
            true,
            "Select (target designate) picks the target you are looking at instead of the one nearest the head-aimed designator circle.");

        ShowGazeReticle = config.Bind(
            "Eye Tracking",
            "Show Gaze Reticle",
            true,
            "With Look To Designate, draw a small ring on the HUD where you are looking and bracket the target Select would designate from there.");

        ShowGazeDot = config.Bind(
            "Eye Tracking",
            "Show Gaze Dot",
            false,
            "Draw a small dot where the headset says you are looking, to check the eye tracking calibration.");

        GazeContactInfo = config.Bind(
            "Eye Tracking",
            "Gaze Contact Info",
            true,
            "Rest your gaze on a HUD marker for a moment to show beside it what it is, its range and altitude, and RDR / LOCK if its radar has found you or MSL if a missile it fired is coming at you.");

        GazeContactDwellSeconds = config.Bind(
            "Eye Tracking",
            "Gaze Contact Dwell Seconds",
            0.3f,
            "How long your gaze has to rest on a marker before Gaze Contact Info shows.");

        MenuHangar = config.Bind(
            "Menus",
            "Hangar",
            true,
            "Out of flight, the menus float inside one of the game's aircraft shelters, with an aircraft parked in it and others flying past outside. The door closes while a mission loads. Off shows the menus over plain sky.");

        foreach (var command in MenuInput.Commands)
        {
            MenuBindings[command] = config.Bind(
                MenuInput.Section,
                MenuInput.Name(command),
                MenuInput.DefaultBindings(command),
                "Keyboard | gamepad | joystick binding for this in NOVR's menus, as Unity Input System control paths; leave a part empty for none. Easiest to set from Menu Controls in the main menu.");
        }
    }
}

public enum GazeCursorMode
{
    Gaze,
    Mouse,
    Hybrid,
}
