using System.Collections.Generic;
using UnityEngine;

namespace NOVR.VrUi.Menu;

// Copies what a game prefab looks like and nothing else: its meshes, materials and lights in a hierarchy of plain objects,
// without the game's scripts, colliders or networking, so a unit can stand in the menus where the game isn't running.
// Only the most detailed level of a LOD group is kept, as the copy is seen up close. Inactive parts are left out;
// disabled renderers and lights are copied disabled, so the caller can turn on what it wants (a hangar's night lights).
// Parts the game never draws to its world camera (helper volumes on its UI, HUD, exclusion zone, preview, editor and grass
// blocker layers) and parts whose shader isn't in the build (they would draw pink) are left out too.
internal static class VisualClone
{
    private static readonly HashSet<int> HiddenLayers = new()
    {
        (int)LayerHelper.Layers.UI,
        (int)LayerHelper.Layers.HUD,
        (int)LayerHelper.Layers.ExclusionZones,
        (int)LayerHelper.Layers.PreviewRender,
        (int)LayerHelper.Layers.EditorSelectOnly,
        (int)LayerHelper.Layers.GrassBlockerProxy,
        (int)LayerHelper.Layers.VrUi,
        (int)LayerHelper.Layers.VrUiCapture
    };

    // What was left out, for one log line from the caller.
    public static int SkippedParts { get; private set; }
    public static readonly HashSet<string> SkippedReasons = new();

    // map, when given, gets each prefab transform's copy, to find the copies of parts the prefab's scripts name.
    public static GameObject Create(GameObject prefab, Transform parent, Dictionary<Transform, Transform>? map = null)
    {
        var skipped = new HashSet<Renderer>();
        foreach (var lodGroup in prefab.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lodGroup.GetLODs();
            if (lods.Length < 2) continue;

            var kept = new HashSet<Renderer>(lods[0].renderers);
            for (var index = 1; index < lods.Length; index++)
            {
                foreach (var renderer in lods[index].renderers)
                {
                    if (renderer != null && !kept.Contains(renderer)) skipped.Add(renderer);
                }
            }
        }

        var root = Copy(prefab.transform, parent, skipped, map);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        return root;
    }

    private static GameObject Copy(Transform source, Transform parent, HashSet<Renderer> skipped, Dictionary<Transform, Transform>? map)
    {
        var copy = new GameObject(source.name);
        var transform = copy.transform;
        transform.SetParent(parent, false);
        transform.localPosition = source.localPosition;
        transform.localRotation = source.localRotation;
        transform.localScale = source.localScale;
        if (map != null) map[source] = transform;

        CopyMesh(source, copy, skipped);
        if (source.TryGetComponent<Light>(out var light)) CopyLight(light, copy);

        for (var index = 0; index < source.childCount; index++)
        {
            var child = source.GetChild(index);
            if (child.gameObject.activeSelf) Copy(child, transform, skipped, map);
        }

        return copy;
    }

    private static void CopyMesh(Transform source, GameObject copy, HashSet<Renderer> skipped)
    {
        Mesh? mesh = null;
        Renderer? renderer = null;
        if (source.TryGetComponent<MeshRenderer>(out var meshRenderer) && source.TryGetComponent<MeshFilter>(out var filter))
        {
            mesh = filter.sharedMesh;
            renderer = meshRenderer;
        }
        else if (source.TryGetComponent<SkinnedMeshRenderer>(out var skinned))
        {
            // Drawn in its bind pose, which for the game's units is how they stand.
            mesh = skinned.sharedMesh;
            renderer = skinned;
        }

        if (mesh == null || renderer == null || skipped.Contains(renderer)) return;
        if (HiddenLayers.Contains(source.gameObject.layer))
        {
            Skip($"layer {LayerMask.LayerToName(source.gameObject.layer)}");
            return;
        }

        foreach (var material in renderer.sharedMaterials)
        {
            if (Drawable(material)) continue;

            Skip(material == null ? "no material" : $"shader {(material.shader != null ? material.shader.name : "none")}");
            return;
        }

        copy.AddComponent<MeshFilter>().sharedMesh = mesh;
        var copied = copy.AddComponent<MeshRenderer>();
        copied.sharedMaterials = renderer.sharedMaterials;
        copied.shadowCastingMode = renderer.shadowCastingMode;
        copied.receiveShadows = renderer.receiveShadows;
        copied.enabled = renderer.enabled;
    }

    public static bool Drawable(Material? material) =>
        material != null && material.shader != null && material.shader.isSupported && material.shader.name != "Hidden/InternalErrorShader";

    private static void Skip(string reason)
    {
        SkippedParts++;
        SkippedReasons.Add(reason);
    }

    private static void CopyLight(Light source, GameObject copy)
    {
        var light = copy.AddComponent<Light>();
        light.type = source.type;
        light.color = source.color;
        light.intensity = source.intensity;
        light.range = source.range;
        light.spotAngle = source.spotAngle;
        light.innerSpotAngle = source.innerSpotAngle;
        light.cookie = source.cookie;
        light.renderMode = source.renderMode;
        // Point and spot shadows cost a lot for a backdrop; the sun's are the ones that show.
        light.shadows = LightShadows.None;
        light.enabled = source.enabled;
    }
}
