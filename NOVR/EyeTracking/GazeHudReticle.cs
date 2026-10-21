using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NOVR.VrUi;
using NOVR.VrUi.HarmonyPatches;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.EyeTracking;

// With Look To Designate the head-aimed designator circle no longer shows what Select picks, so in flight this draws a
// small ring on the HUD where the eyes point, and brackets the target Select would designate from there: the unselected
// marker within CombatHUD.TargetSelect's reach that it ranks first. Same gaze as GazeDesignatePatch, so what is
// bracketed is what gets designated. Runs after CombatHUD has placed its markers for the frame.
[DefaultExecutionOrder(1000)]
public class GazeHudReticle : NOVRBehaviour
{
    // CombatHUD.TargetSelect only considers markers this close to the designator, in HUD units.
    private const float DesignateReach = 100f;
    private const float ReticleDegrees = 1.6f;
    private const float BracketDegrees = 2.8f;
    private const float ReticleAlpha = 0.7f;
    private const int TextureSize = 64;
    private const int SortingOrder = short.MaxValue - 2;

    private static readonly FieldInfo? MarkersField = AccessTools.Field(typeof(CombatHUD), "markers");

    private RawImage? _reticle;
    private RawImage? _bracket;
    // Stands in for the designator when ranking markers the way TargetSelect does.
    private GameObject? _probe;
    private bool _loggedFailure;

    private void OnDestroy()
    {
        if (_reticle != null) Destroy(_reticle.gameObject);
        if (_bracket != null) Destroy(_bracket.gameObject);
        if (_probe != null) Destroy(_probe);
    }

    private void LateUpdate()
    {
        var hud = GetFlyingHud();
        if (hud == null || !NOVREyeGaze.TryGetSteadyUiDirection(out var direction))
        {
            SetVisible(_reticle, false);
            SetVisible(_bracket, false);
            return;
        }

        var camera = APIBus.CockpitHudCamera;
        var color = GetHudColor();
        var gazePoint = direction * VrHudProjection.HudDistance;
        Place(_reticle != null ? _reticle : _reticle = CreateElement("NOVRGazeReticle", CreateRingTexture()), gazePoint, camera, ReticleDegrees, color);

        var target = FindDesignationTarget(hud, gazePoint);
        if (target == null)
        {
            SetVisible(_bracket, false);
            return;
        }

        Place(_bracket != null ? _bracket : _bracket = CreateElement("NOVRGazeTargetBracket", CreateBracketTexture()), target.image.transform.position, camera, BracketDegrees, color);
    }

    private static CombatHUD? GetFlyingHud()
    {
        var config = ModConfiguration.Instance;
        if (!config.GazeDesignate.Value || !config.ShowGazeReticle.Value) return null;

        // A menu or the map is up: the menu cursor shows the gaze there.
        var cursor = VrUiCursor.I;
        if (cursor != null && cursor.IsActive) return null;

        var hud = SceneSingleton<CombatHUD>.i;
        return hud != null && hud.aircraft != null ? hud : null;
    }

    // CombatHUD.TargetSelect(paint: false), without selecting: markers within reach of the gaze that are showing, not
    // already selected and not filtered out, ranked by HUDUnitMarker.AssessPriority.
    private HUDUnitMarker? FindDesignationTarget(CombatHUD hud, Vector3 gazePoint)
    {
        try
        {
            if (MarkersField?.GetValue(hud) is not List<HUDUnitMarker> markers) return null;

            var probe = _probe != null ? _probe : _probe = CreateProbe();
            probe.transform.position = gazePoint;
            var selector = SceneSingleton<TargetListSelector>.i;
            var station = hud.GetWeaponStation();
            HUDUnitMarker? best = null;
            var bestPriority = float.NegativeInfinity;
            foreach (var marker in markers)
            {
                if (marker == null || marker.selected || marker.image == null || !marker.image.enabled) continue;
                if ((marker.image.transform.position - gazePoint).sqrMagnitude >= DesignateReach * DesignateReach) continue;
                if (selector != null && selector.CheckExclusions(marker.unit)) continue;

                var priority = marker.AssessPriority(hud.aircraft, probe, station);
                if (best != null && priority <= bestPriority) continue;
                best = marker;
                bestPriority = priority;
            }

            return best;
        }
        catch (Exception exception)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                NOVRPlugin.Log.LogWarning($"[NOVR] Eye tracking: could not work out the target under the gaze reticle: {exception.Message}");
            }

            return null;
        }
    }

    private static Color GetHudColor()
    {
        Color color;
        try
        {
            color = ThemeManager.Active.ColorTheme.AllClear;
        }
        catch (Exception)
        {
            color = Color.green;
        }

        color.a = ReticleAlpha;
        return color;
    }

    // Sized by angle, so it looks the same at any distance, and by the HUD scale like the rest of the HUD.
    private static void Place(RawImage element, Vector3 position, Camera camera, float degrees, Color color)
    {
        var toElement = position - camera.transform.position;
        var size = 2f * toElement.magnitude * Mathf.Tan(degrees * 0.5f * Mathf.Deg2Rad) * InGameUiPlacement.HudScale;
        var elementTransform = element.transform;
        elementTransform.SetPositionAndRotation(position, Quaternion.LookRotation(toElement, camera.transform.up));
        elementTransform.localScale = Vector3.one * (size / TextureSize);
        element.color = color;
        SetVisible(element, true);
    }

    private static void SetVisible(RawImage? element, bool visible)
    {
        if (element != null && element.gameObject.activeSelf != visible) element.gameObject.SetActive(visible);
    }

    private static RawImage CreateElement(string name, Texture2D texture)
    {
        var element = new GameObject(name);
        DontDestroyOnLoad(element);
        var canvas = element.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = SortingOrder;
        element.GetComponent<RectTransform>().sizeDelta = new Vector2(TextureSize, TextureSize);

        var image = element.AddComponent<RawImage>();
        image.raycastTarget = false;
        image.texture = texture;
        LayerHelper.SetLayerRecursive(element.transform, LayerHelper.GetVrUiLayer());
        return image;
    }

    private static GameObject CreateProbe()
    {
        var probe = new GameObject("NOVRGazeDesignatorProbe");
        DontDestroyOnLoad(probe);
        return probe;
    }

    private static Texture2D CreateRingTexture()
    {
        const float radius = TextureSize * 0.5f - 3f;
        const float thickness = 3f;
        return CreateTexture((x, y) => Mathf.Clamp01(thickness * 0.5f - Mathf.Abs(new Vector2(x, y).magnitude - radius) + 0.5f));
    }

    // Four corners of a square, like the game's own target boxes.
    private static Texture2D CreateBracketTexture()
    {
        const float half = TextureSize * 0.5f - 2f;
        const float arm = TextureSize * 0.22f;
        const float thickness = 3f;
        return CreateTexture((x, y) =>
        {
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
            var onVerticalEdge = ax >= half - thickness && ax <= half && ay >= half - arm && ay <= half;
            var onHorizontalEdge = ay >= half - thickness && ay <= half && ax >= half - arm && ax <= half;
            return onVerticalEdge || onHorizontalEdge ? 1f : 0f;
        });
    }

    // alpha(x, y) with x and y measured from the texture's centre.
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
}
