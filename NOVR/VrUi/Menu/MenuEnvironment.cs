using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace NOVR.VrUi.Menu;

// Out of flight, the menus float inside one of the game's own aircraft shelters instead of over a flat picture: the shelter
// and an aircraft it can hold, copied from the game's prefabs (VisualClone), at dusk, with its door rolling open when the
// menu comes up and shut while a mission loads (LoadingScreenPatch). Outside, runway lights, and now and then the game's
// aircraft flying past the field with their own engine sound. The player stands behind the parked aircraft, a little to
// its left, facing the door, with the menu panel on the left clear of the aircraft and the walls.
// Everything sits in the menu camera's tracking space, so it stays put around the player across the menu scenes and moves
// with a recenter. Anything the game doesn't have (no shelter found, a shader missing) is left out, not faked.
public class MenuEnvironment : NOVRBehaviour
{
    private const float EyeHeight = 1.3f;
    private const float BehindTail = 3.5f;
    private const float OpenDelaySeconds = 1.2f;
    private const float OpenSeconds = 7f;
    private const float CloseSeconds = 3f;
    private const float SkyRadius = 3500f;
    private const float FarClip = 8000f;
    private const float RunwayDistance = 620f;
    private const float FlybyMinSeconds = 25f;
    private const float FlybyMaxSeconds = 60f;
    private const float FlybyLength = 8000f;

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] MenuScenes = { "MainMenu", "MultiplayerMenu", "MissionsMenu" };
    // The game's empty loading scene, shown between a menu and a mission: the shelter stays, door shut.
    private const string LoadingScene = "empty";
    private static readonly string[] ShelterWords = { "bunker", "shelter", "hardened", "has" };
    private static readonly string[] EngineWords = { "engine", "jet", "turbine", "exterior", "external", "distant", "far" };

    private static readonly Color SkyZenith = new(0.05f, 0.08f, 0.19f);
    private static readonly Color SkyHigh = new(0.2f, 0.25f, 0.42f);
    private static readonly Color SkyHorizon = new(0.95f, 0.55f, 0.32f);
    private static readonly Color SkyBelow = new(0.11f, 0.09f, 0.09f);
    private static readonly Color Ambient = new(0.2f, 0.21f, 0.27f);
    private static readonly Color SunColor = new(1f, 0.6f, 0.38f);
    private static readonly Color GroundColor = new(0.08f, 0.09f, 0.08f);

    public static MenuEnvironment? I { get; private set; }
    public static string? FeaturedAircraft { get; private set; }

    private readonly List<Door> _doors = new();
    private readonly List<Flyby> _flybys = new();
    // Kept after their pass and flown again, so each aircraft type is copied at most once per wingman.
    private readonly Dictionary<AircraftDefinition, List<GameObject>> _flybyClones = new();
    private GameObject? _root;
    private Transform? _shelter;
    private Transform? _spawn;
    private AircraftDefinition[] _hangarAircraft = Array.Empty<AircraftDefinition>();
    private GameObject? _featured;
    private Light? _sun;
    private AudioSource? _doorLoop;
    private AudioSource? _doorOneShot;
    private AudioClip? _doorMoving;
    private AudioClip? _doorStart;
    private AudioClip? _doorEnd;
    private float _doorVolume = 1f;
    private Material? _unlit;
    private Material? _lit;
    private bool _built;
    private bool _failed;
    private bool _visible;
    private bool _loading;
    private float _openAt;
    private float _door;
    private bool _doorMovingNow;
    private float _doorFrom;
    private float _doorTo;
    private float _doorStartTime;
    private float _doorSeconds;
    private float _nextFlyby;
    private Camera? _camera;
    private CameraState _cameraState;
    private int _lightingScene = -1;

    private struct Door
    {
        public Transform Transform;
        public Vector3 BaseAngles;
        public Vector3 OpenAngle;
        public Vector3 BasePosition;
        public Vector3 OpenPosition;
    }

    private sealed class Flyby
    {
        public GameObject Clone = null!;
        public Vector3 Velocity;
        public float EndTime;
    }

    private struct CameraState
    {
        public CameraClearFlags ClearFlags;
        public Color Background;
        public int CullingMask;
        public float FarClip;
    }

    protected override void Awake()
    {
        base.Awake();
        I = this;
    }

    private void OnDestroy()
    {
        RestoreCamera();
        if (_root != null) Destroy(_root);
        if (I == this) I = null;
    }

    // From the loading screen: shut the door while it is up, open it again if it goes without leaving the menus.
    public void SetLoading(bool loading)
    {
        if (_loading == loading) return;

        _loading = loading;
        if (!_visible) return;

        if (loading) MoveDoor(0f, CloseSeconds);
        else _openAt = Time.unscaledTime + OpenDelaySeconds;
    }

    private void LateUpdate()
    {
        try
        {
            var scene = SceneManager.GetActiveScene();
            var inMenu = Array.IndexOf(MenuScenes, scene.name) >= 0;
            var wanted = !_failed && ModConfiguration.Instance.MenuHangar.Value && (inMenu || scene.name == LoadingScene) &&
                         MainMenu.State == MainMenu.LoadingState.Loaded;
            var camera = wanted ? APIBus.MainCamera : null;
            if (camera == null)
            {
                Hide();
                return;
            }

            if (!_built) Build();
            if (_root == null) return;

            if (!_visible) Arrive();
            Follow(camera);
            if (scene.handle != _lightingScene) ApplyLighting(scene);

            if (inMenu && !_loading && _openAt > 0f && Time.unscaledTime >= _openAt)
            {
                _openAt = 0f;
                MoveDoor(1f, OpenSeconds);
            }

            UpdateDoor();
            UpdateFlybys();
        }
        catch (Exception exception)
        {
            _failed = true;
            Hide();
            NOVRPlugin.Log.LogWarning($"[NOVR] Menu hangar stopped: {exception}");
        }
    }

    private void Build()
    {
        _built = true;
        _root = new GameObject("NOVR Menu Hangar");
        DontDestroyOnLoad(_root);
        _root.SetActive(false);

        _unlit = CreateMaterial("Universal Render Pipeline/Unlit");
        _lit = CreateMaterial("Universal Render Pipeline/Lit");
        BuildShelter();
        BuildSky();
        BuildGround();
        BuildRunwayLights();
        BuildSun();
        LogSceneContentOnce();
    }

    // Each time the menus come back: a new aircraft in the shelter, and the door shut, to open once the menu is up.
    private void Arrive()
    {
        _visible = true;
        _root!.SetActive(true);
        PlaceFeaturedAircraft();
        _doorMovingNow = false;
        ApplyDoor(0f);
        _openAt = Time.unscaledTime + OpenDelaySeconds;
        _nextFlyby = Time.unscaledTime + Random.Range(8f, 20f);
    }

    private void Hide()
    {
        if (!_visible) return;

        _visible = false;
        RestoreCamera();
        foreach (var flyby in _flybys) flyby.Clone.SetActive(false);
        _flybys.Clear();
        if (_doorLoop != null) _doorLoop.Stop();
        if (_root != null) _root.SetActive(false);
    }

    // The menu camera's parent is the tracking space: the headset moves the camera within it.
    private void Follow(Camera camera)
    {
        var origin = camera.transform.parent;
        var position = origin != null ? origin.position : Vector3.zero;
        var forward = origin != null ? Vector3.ProjectOnPlane(origin.forward, Vector3.up) : Vector3.forward;
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        _root!.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));

        if (camera == _camera) return;

        RestoreCamera();
        _camera = camera;
        _cameraState = new CameraState
        {
            ClearFlags = camera.clearFlags,
            Background = camera.backgroundColor,
            CullingMask = camera.cullingMask,
            FarClip = camera.farClipPlane
        };
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = SkyHigh;
        camera.cullingMask |= 1 << (int)LayerHelper.Layers.Default;
        camera.farClipPlane = Mathf.Max(camera.farClipPlane, FarClip);
    }

    private void RestoreCamera()
    {
        if (_camera != null)
        {
            _camera.clearFlags = _cameraState.ClearFlags;
            _camera.backgroundColor = _cameraState.Background;
            _camera.cullingMask = _cameraState.CullingMask;
            _camera.farClipPlane = _cameraState.FarClip;
        }

        _camera = null;
    }

    // Each scene has its own lighting settings, so they are set again in each.
    private void ApplyLighting(Scene scene)
    {
        _lightingScene = scene.handle;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Ambient;
        var probe = new SphericalHarmonicsL2();
        probe.AddAmbientLight(Ambient);
        RenderSettings.ambientProbe = probe;
        RenderSettings.fog = false;
        if (_sun != null) RenderSettings.sun = _sun;
    }

    private void BuildShelter()
    {
        var encyclopedia = Encyclopedia.i;
        if (encyclopedia == null || encyclopedia.buildings == null) return;

        BuildingDefinition? best = null;
        Hangar? bestHangar = null;
        var bestScore = int.MinValue;
        var candidates = new StringBuilder();
        foreach (var definition in encyclopedia.buildings)
        {
            if (definition == null || definition.unitPrefab == null) continue;

            var hangar = definition.unitPrefab.GetComponentInChildren<Hangar>(true);
            if (hangar == null) continue;

            var doors = GetField<HangarDoor[]>(hangar, "doors")?.Length ?? 0;
            var name = $"{definition.unitName} {definition.unitPrefab.name}".ToLowerInvariant();
            var score = doors > 0 ? 10 : 0;
            foreach (var word in ShelterWords)
            {
                if (name.Contains(word)) score += 20;
            }

            if (candidates.Length > 0) candidates.Append(", ");
            candidates.Append($"{definition.unitPrefab.name} ({doors} doors)");
            if (score <= bestScore) continue;

            best = definition;
            bestHangar = hangar;
            bestScore = score;
        }

        if (best == null || bestHangar == null)
        {
            NOVRPlugin.Log.LogInfo("[NOVR] Menu hangar: the game has no hangar building to use; sky and flybys only.");
            return;
        }

        var map = new Dictionary<Transform, Transform>();
        _shelter = VisualClone.Create(best.unitPrefab, _root!.transform, map).transform;
        _hangarAircraft = GetField<AircraftDefinition[]>(bestHangar, "availableAircraft") ?? Array.Empty<AircraftDefinition>();

        var spawn = GetField<Transform>(bestHangar, "spawnTransform");
        if (spawn == null || !map.TryGetValue(spawn, out _spawn)) map.TryGetValue(bestHangar.transform, out _spawn);
        _spawn ??= _shelter;

        foreach (var door in GetField<HangarDoor[]>(bestHangar, "doors") ?? Array.Empty<HangarDoor>())
        {
            if (door?.transform == null || !map.TryGetValue(door.transform, out var copy)) continue;

            _doors.Add(new Door
            {
                Transform = copy,
                BaseAngles = copy.localEulerAngles,
                OpenAngle = door.openAngle,
                BasePosition = copy.localPosition,
                OpenPosition = door.openPos
            });
        }

        // Its lights are the ones the game only turns on at night; it is dusk.
        foreach (var light in GetField<HangarLighting[]>(bestHangar, "lights") ?? Array.Empty<HangarLighting>())
        {
            if (light == null) continue;
            if (light.light != null && map.TryGetValue(light.light.transform, out var lightCopy) && lightCopy.TryGetComponent<Light>(out var copiedLight))
                copiedLight.enabled = true;
            if (light.lightRenderer != null && map.TryGetValue(light.lightRenderer.transform, out var rendererCopy) &&
                rendererCopy.TryGetComponent<MeshRenderer>(out var copiedRenderer))
                copiedRenderer.enabled = true;
        }

        BuildDoorSound(bestHangar);
        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: using {best.unitPrefab.name} ({_doors.Count} doors, {_hangarAircraft.Length} aircraft types) of {candidates}.");
    }

    private void BuildDoorSound(Hangar hangar)
    {
        _doorMoving = GetField<AudioClip>(hangar, "movingSound");
        _doorStart = GetField<AudioClip>(hangar, "openSound");
        _doorEnd = GetField<AudioClip>(hangar, "closedSound");
        var volume = AccessTools.Field(hangar.GetType(), "movingVolume")?.GetValue(hangar);
        if (volume is float value && value > 0f) _doorVolume = value;
        if (_doors.Count == 0 || (_doorMoving == null && _doorStart == null && _doorEnd == null)) return;

        var owner = new GameObject("Door Sound");
        owner.transform.SetParent(_doors[0].Transform, false);
        _doorLoop = AddSource(owner, loop: true, minDistance: 8f, maxDistance: 150f);
        _doorLoop.clip = _doorMoving;
        _doorOneShot = AddSource(owner, loop: false, minDistance: 8f, maxDistance: 150f);
    }

    // The aircraft stands where the game spawns one in this hangar, and the shelter is placed around the player from it.
    private void PlaceFeaturedAircraft()
    {
        if (_featured != null) Destroy(_featured);
        _featured = null;
        FeaturedAircraft = null;
        if (_shelter == null || _spawn == null) return;

        var choices = new List<AircraftDefinition>();
        foreach (var definition in _hangarAircraft.Length > 0 ? _hangarAircraft : Encyclopedia.i.aircraft.ToArray())
        {
            if (definition != null && definition.unitPrefab != null) choices.Add(definition);
        }

        // Laid out with the shelter at rest, then turned and moved around the player.
        _shelter.localPosition = Vector3.zero;
        _shelter.localRotation = Quaternion.identity;
        var halfSpan = 6f;
        var tail = 8f;
        if (choices.Count > 0)
        {
            var aircraft = choices[Random.Range(0, choices.Count)];
            _featured = VisualClone.Create(aircraft.unitPrefab, _spawn);
            _featured.transform.localPosition = Vector3.up * aircraft.spawnOffset.y + Vector3.forward * aircraft.spawnOffset.z;
            _featured.transform.localRotation = Quaternion.Euler(aircraft.restRotation);
            FeaturedAircraft = aircraft.unitName;
            if (TryGetBounds(_featured, _spawn, out var bounds))
            {
                halfSpan = Mathf.Max(-bounds.min.x, bounds.max.x);
                tail = -bounds.min.z;
            }
        }

        // The shelter's inside, roughly: its whole extent less walls and banks. The player stands BehindTail behind the
        // aircraft, unless the back wall is nearer than that.
        var haveShelter = TryGetBounds(_shelter.gameObject, _spawn, out var shelter);
        var insideHalfWidth = haveShelter ? Mathf.Max(halfSpan + 2f, Mathf.Min(-shelter.min.x, shelter.max.x) * 0.7f) : halfSpan + 4f;
        var back = haveShelter ? shelter.min.z * 0.85f + 1.5f : float.NegativeInfinity;
        var playerZ = -(tail + BehindTail);
        if (playerZ < back) playerZ = Mathf.Min(back, -tail - 1f);
        var player = new Vector3(-Mathf.Clamp(halfSpan * 0.6f, 2f, Mathf.Max(2f, insideHalfWidth - 2.5f)), 0f, playerZ);

        var spawnYaw = Quaternion.Inverse(_shelter.rotation) * _spawn.rotation;
        var turn = Quaternion.Euler(0f, -spawnYaw.eulerAngles.y, 0f);
        var spawnInShelter = _shelter.InverseTransformPoint(_spawn.position);
        var spawnFromPlayer = new Vector3(-player.x, -EyeHeight, -player.z);
        _shelter.localRotation = turn;
        _shelter.localPosition = spawnFromPlayer - turn * spawnInShelter;

        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: {FeaturedAircraft ?? "no aircraft"} parked; half span {halfSpan:0.0} m, tail {tail:0.0} m, " +
                               $"shelter {shelter.size.x:0}x{shelter.size.z:0}x{shelter.size.y:0} m, player at {player.x:0.0}, {player.z:0.0} m from the spawn point.");
    }

    // Bounds of everything drawn under root, in space's frame.
    private static bool TryGetBounds(GameObject root, Transform space, out Bounds bounds)
    {
        bounds = new Bounds();
        var any = false;
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            var world = renderer.bounds;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = space.InverseTransformPoint(world.center + Vector3.Scale(world.extents,
                    new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)));
                if (!any) bounds = new Bounds(point, Vector3.zero);
                else bounds.Encapsulate(point);
                any = true;
            }
        }

        return any;
    }

    private void MoveDoor(float target, float seconds)
    {
        if (_doors.Count == 0) return;
        if (_doorMovingNow ? Mathf.Approximately(_doorTo, target) : Mathf.Approximately(_door, target)) return;

        _doorMovingNow = true;
        _doorFrom = _door;
        _doorTo = target;
        _doorSeconds = seconds * Mathf.Abs(target - _door);
        _doorStartTime = Time.unscaledTime;
        if (_doorOneShot != null && _doorStart != null) _doorOneShot.PlayOneShot(_doorStart, _doorVolume);
        if (_doorLoop != null && _doorMoving != null)
        {
            _doorLoop.volume = _doorVolume;
            _doorLoop.Play();
        }
    }

    private void UpdateDoor()
    {
        if (!_doorMovingNow) return;

        var t = _doorSeconds > 0f ? Mathf.Clamp01((Time.unscaledTime - _doorStartTime) / _doorSeconds) : 1f;
        ApplyDoor(Mathf.Lerp(_doorFrom, _doorTo, Mathf.SmoothStep(0f, 1f, t)));
        if (t < 1f) return;

        _doorMovingNow = false;
        if (_doorLoop != null) _doorLoop.Stop();
        if (_doorOneShot != null && _doorEnd != null) _doorOneShot.PlayOneShot(_doorEnd, _doorVolume);
    }

    // As the game's HangarDoor.Move does it.
    private void ApplyDoor(float open)
    {
        _door = open;
        foreach (var door in _doors)
        {
            door.Transform.localEulerAngles = Vector3.Lerp(door.BaseAngles, door.BaseAngles + door.OpenAngle, open);
            door.Transform.localPosition = Vector3.Lerp(door.BasePosition, door.BasePosition + door.OpenPosition, open);
        }
    }

    private void BuildSky()
    {
        if (_unlit == null) return;

        var sky = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(sky.GetComponent<Collider>());
        sky.name = "Sky";
        sky.transform.SetParent(_root!.transform, false);
        sky.transform.localScale = Vector3.one * SkyRadius * 2f;
        var renderer = sky.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        // A sphere's texture runs from its bottom (0) to its top (1); its middle is the horizon.
        var texture = new Texture2D(1, 256, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (var y = 0; y < 256; y++)
        {
            var height = (y / 255f - 0.5f) * 2f;
            var color = height < 0f
                ? Color.Lerp(SkyHorizon * 0.6f, SkyBelow, Mathf.Clamp01(-height * 12f))
                : height < 0.12f
                    ? Color.Lerp(SkyHorizon, SkyHigh, height / 0.12f)
                    : Color.Lerp(SkyHigh, SkyZenith, (height - 0.12f) / 0.88f);
            texture.SetPixel(0, y, color);
        }

        texture.Apply();
        var material = new Material(_unlit);
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        // Seen from inside.
        material.SetFloat("_Cull", 0f);
        renderer.sharedMaterial = material;
    }

    private void BuildGround()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        Destroy(ground.GetComponent<Collider>());
        ground.name = "Ground";
        ground.transform.SetParent(_root!.transform, false);
        // Just under the shelter floor, so the two don't flicker where they meet.
        ground.transform.localPosition = new Vector3(0f, -EyeHeight - 0.03f, 0f);
        ground.transform.localScale = new Vector3(SkyRadius / 5f, 1f, SkyRadius / 5f);
        var material = _lit != null ? new Material(_lit) : _unlit != null ? new Material(_unlit) : null;
        if (material == null)
        {
            Destroy(ground);
            return;
        }

        material.SetColor("_BaseColor", _lit != null ? GroundColor : GroundColor * 0.6f);
        if (_lit != null) material.SetFloat("_Smoothness", 0.15f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    // Edge lights along a runway crossing in front of the door, and blue taxiway lights out to it.
    private void BuildRunwayLights()
    {
        if (_unlit == null) return;

        var runway = new Material(_unlit);
        runway.SetColor("_BaseColor", new Color(1f, 0.88f, 0.66f));
        var taxiway = new Material(_unlit);
        taxiway.SetColor("_BaseColor", new Color(0.3f, 0.45f, 1f));
        var lights = new GameObject("Field Lights").transform;
        lights.SetParent(_root!.transform, false);

        var y = -EyeHeight + 0.4f;
        for (var x = -2400f; x <= 2400f; x += 60f)
        {
            AddLight(lights, runway, new Vector3(x, y, RunwayDistance - 25f), 1.6f);
            AddLight(lights, runway, new Vector3(x, y, RunwayDistance + 25f), 1.6f);
        }

        for (var z = 40f; z < RunwayDistance - 40f; z += 30f)
        {
            AddLight(lights, taxiway, new Vector3(-12f, y, z), 0.8f);
            AddLight(lights, taxiway, new Vector3(12f, y, z), 0.8f);
        }
    }

    private static void AddLight(Transform parent, Material material, Vector3 position, float size)
    {
        var light = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(light.GetComponent<Collider>());
        light.transform.SetParent(parent, false);
        light.transform.localPosition = position;
        light.transform.localScale = Vector3.one * size;
        var renderer = light.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
    }

    // Low and out beyond the door, so it reaches in across the floor.
    private void BuildSun()
    {
        var sun = new GameObject("Sun");
        sun.transform.SetParent(_root!.transform, false);
        sun.transform.localRotation = Quaternion.Euler(7f, 205f, 0f);
        _sun = sun.AddComponent<Light>();
        _sun.type = LightType.Directional;
        _sun.color = SunColor;
        _sun.intensity = 1.6f;
        _sun.shadows = LightShadows.Soft;

        var fill = new GameObject("Shelter Fill");
        fill.transform.SetParent(_root.transform, false);
        fill.transform.localPosition = new Vector3(3f, 4f, 6f);
        var light = fill.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.9f, 0.76f);
        light.intensity = 1.5f;
        light.range = 30f;
        light.shadows = LightShadows.None;
    }

    private void UpdateFlybys()
    {
        var now = Time.unscaledTime;
        for (var index = _flybys.Count - 1; index >= 0; index--)
        {
            var flyby = _flybys[index];
            if (now >= flyby.EndTime || flyby.Clone == null)
            {
                if (flyby.Clone != null) flyby.Clone.SetActive(false);
                _flybys.RemoveAt(index);
                continue;
            }

            flyby.Clone.transform.localPosition += flyby.Velocity * Time.unscaledDeltaTime;
        }

        if (now < _nextFlyby || _flybys.Count > 0) return;

        _nextFlyby = now + Random.Range(FlybyMinSeconds, FlybyMaxSeconds);
        var aircraft = Encyclopedia.i?.aircraft;
        if (aircraft == null || aircraft.Count == 0) return;

        var definition = aircraft[Random.Range(0, aircraft.Count)];
        if (definition == null || definition.unitPrefab == null) return;

        var side = Random.value < 0.5f ? -1f : 1f;
        var speed = Random.Range(140f, 230f);
        var start = new Vector3(-side * FlybyLength / 2f, Random.Range(90f, 240f), RunwayDistance + Random.Range(-120f, 300f));
        var velocity = new Vector3(side * speed, 0f, 0f);
        Launch(definition, start, velocity, FlybyLength / speed);
        // Sometimes a pair, the wingman back and out to the side.
        if (Random.value < 0.35f) Launch(definition, start + new Vector3(-side * 60f, -10f, 40f), velocity, FlybyLength / speed);
    }

    private void Launch(AircraftDefinition definition, Vector3 start, Vector3 velocity, float seconds)
    {
        if (!_flybyClones.TryGetValue(definition, out var clones)) _flybyClones[definition] = clones = new List<GameObject>();
        var clone = clones.Find(cached => cached != null && !cached.activeSelf);
        if (clone == null)
        {
            clone = VisualClone.Create(definition.unitPrefab, _root!.transform);
            clone.name = $"Flyby {definition.unitName}";
            AddEngineSound(clone, definition.unitPrefab);
            clones.Add(clone);
        }

        clone.transform.localPosition = start;
        clone.transform.localRotation = Quaternion.LookRotation(velocity, Vector3.up);
        clone.SetActive(true);
        var engine = clone.GetComponent<AudioSource>();
        if (engine != null) engine.Play();
        _flybys.Add(new Flyby { Clone = clone, Velocity = velocity, EndTime = Time.unscaledTime + seconds });
    }

    // The aircraft's own looping engine sound, preferring one named for the engine or the outside.
    private void AddEngineSound(GameObject clone, GameObject prefab)
    {
        AudioClip? best = null;
        var bestScore = -1;
        foreach (var source in prefab.GetComponentsInChildren<AudioSource>(true))
        {
            if (source.clip == null || !source.loop) continue;

            var name = $"{source.name} {source.clip.name}".ToLowerInvariant();
            var score = 0;
            foreach (var word in EngineWords)
            {
                if (name.Contains(word)) score++;
            }

            if (score <= bestScore) continue;

            best = source.clip;
            bestScore = score;
        }

        if (best == null) return;

        var engine = AddSource(clone, loop: true, minDistance: 80f, maxDistance: 5000f);
        engine.clip = best;
        engine.dopplerLevel = 1f;
        engine.rolloffMode = AudioRolloffMode.Logarithmic;
        engine.volume = 1f;
    }

    private AudioSource AddSource(GameObject owner, bool loop, float minDistance, float maxDistance)
    {
        var source = owner.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        var mixer = EffectsMixer();
        if (mixer != null) source.outputAudioMixerGroup = mixer;
        return source;
    }

    private static AudioMixerGroup? EffectsMixer()
    {
        var sound = SoundManager.i;
        return sound != null ? sound.EffectsMixer : null;
    }

    private static Material? CreateMaterial(string shaderName)
    {
        var shader = Shader.Find(shaderName);
        if (shader != null) return new Material(shader);

        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: no '{shaderName}' shader in the game; what needs it is left out.");
        return null;
    }

    // Whether the menu scene has a 3D scene of its own around the camera, which the shelter would then overlap.
    private static void LogSceneContentOnce()
    {
        var count = 0;
        foreach (var renderer in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (renderer.gameObject.scene == SceneManager.GetActiveScene()) count++;
        }

        var camera = APIBus.MainCamera;
        var origin = camera != null && camera.transform.parent != null ? camera.transform.parent.name : "none";
        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: the menu scene draws {count} meshes of its own; camera space '{origin}'.");
    }

    private static T? GetField<T>(object instance, string name) where T : class =>
        AccessTools.Field(instance.GetType(), name)?.GetValue(instance) as T;
}
