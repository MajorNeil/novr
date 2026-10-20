using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

public class ModConfiguration
{
    public static ModConfiguration Instance;
    

    public readonly ConfigFile Config;
    public readonly ConfigEntry<float> TargetDesignatorOvershoot;
    public readonly ConfigEntry<bool> EnableNativeMenuUi;
    public readonly ConfigEntry<float> NativeMenuScale;
    public readonly ConfigEntry<float> NativeMenuDistance;
    public readonly ConfigEntry<float> NativeMenuHeightOffset;
    public readonly ConfigEntry<float> InGameUiScale;
    public readonly ConfigEntry<float> InGameUiDistance;
    public readonly ConfigEntry<float> HudScale;

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;
        TargetDesignatorOvershoot = config.Bind(
            "General",
            "Target Designator Overshoot",
            1.2f,
            "How much the target designator should multiply rotation to make for easier high off boresight target designation. Set to 1.0 to disable");

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
    }
}
