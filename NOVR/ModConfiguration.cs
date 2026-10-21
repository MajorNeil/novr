using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

public class ModConfiguration
{
    public static ModConfiguration Instance;
    

    public readonly ConfigFile Config;
    public readonly ConfigEntry<bool> EnableNativeMenuUi;
    public readonly ConfigEntry<float> NativeMenuScale;
    public readonly ConfigEntry<float> NativeMenuDistance;
    public readonly ConfigEntry<float> NativeMenuHeightOffset;
    public readonly ConfigEntry<float> InGameUiScale;
    public readonly ConfigEntry<float> InGameUiDistance;
    public readonly ConfigEntry<float> HudScale;
    public readonly ConfigEntry<bool> EyeTrackingEnabled;
    public readonly ConfigEntry<GazeCursorMode> GazeMenuCursor;
    public readonly ConfigEntry<bool> SelectClicksMenus;
    public readonly ConfigEntry<float> MenuSnapDegrees;
    public readonly ConfigEntry<float> MenuGazeSteadiness;
    public readonly ConfigEntry<bool> GazeDesignate;
    public readonly ConfigEntry<bool> ShowGazeDot;

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

        MenuGazeSteadiness = config.Bind(
            "Eye Tracking",
            "Menu Gaze Steadiness",
            0.6f,
            "How still the gaze menu cursor holds while you look at one spot, from 0 (follows every flicker of the tracker) to 1 (steadiest, slowest to settle on small eye movements). Looking further away always jumps straight there. Doesn't affect designating.");

        GazeDesignate = config.Bind(
            "Eye Tracking",
            "Look To Designate",
            true,
            "Select (target designate) picks the target you are looking at instead of the one nearest the head-aimed designator circle.");

        ShowGazeDot = config.Bind(
            "Eye Tracking",
            "Show Gaze Dot",
            false,
            "Draw a small dot where the headset says you are looking, to check the eye tracking calibration.");
    }
}

public enum GazeCursorMode
{
    Gaze,
    Mouse,
    Hybrid,
}
