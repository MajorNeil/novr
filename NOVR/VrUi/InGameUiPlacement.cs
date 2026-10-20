using UnityEngine;

namespace NOVR.VrUi;

// Where the game's own in-flight canvases (menus, map, messages, key bindings) sit in the VR UI space, and how big the
// flight HUD is drawn. Adjustable in game from InGameVrUiBar and saved in the "In-Game UI" config section.
public static class InGameUiPlacement
{
    public const float BaseCanvasScale = 0.003f;
    public const float MinScale = 0.5f;
    public const float MaxScale = 2.0f;
    public const float ScaleStep = 0.05f;
    public const float MinDistance = 1.0f;
    public const float MaxDistance = 6.0f;
    public const float DistanceStep = 0.25f;
    private const float DefaultScale = 1.0f;
    private const float DefaultDistance = 3.0f;

    public static float Scale => Mathf.Clamp(ModConfiguration.Instance?.InGameUiScale.Value ?? DefaultScale, MinScale, MaxScale);
    public static float Distance => Mathf.Clamp(ModConfiguration.Instance?.InGameUiDistance.Value ?? DefaultDistance, MinDistance, MaxDistance);
    public static float CanvasScale => BaseCanvasScale * Scale;
    public static float HudScale => Mathf.Clamp(ModConfiguration.Instance?.HudScale.Value ?? DefaultScale, MinScale, MaxScale);

    public static void AdjustScale(float delta)
    {
        var config = ModConfiguration.Instance;
        config.InGameUiScale.Value = RoundToStep(Mathf.Clamp(Scale + delta, MinScale, MaxScale), ScaleStep);
        config.Config.Save();
    }

    public static void AdjustDistance(float delta)
    {
        var config = ModConfiguration.Instance;
        config.InGameUiDistance.Value = RoundToStep(Mathf.Clamp(Distance + delta, MinDistance, MaxDistance), DistanceStep);
        config.Config.Save();
    }

    public static void AdjustHudScale(float delta)
    {
        var config = ModConfiguration.Instance;
        config.HudScale.Value = RoundToStep(Mathf.Clamp(HudScale + delta, MinScale, MaxScale), ScaleStep);
        config.Config.Save();
    }

    private static float RoundToStep(float value, float step)
    {
        return Mathf.Round(value / step) * step;
    }
}
