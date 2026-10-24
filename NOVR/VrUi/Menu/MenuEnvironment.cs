using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using NOVR.VrUi.Native;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace NOVR.VrUi.Menu;

// Out of flight, the menus are in one of the game's own aircraft shelters instead of over a flat picture. The player sits
// at a desk by the left wall behind the parked aircraft, turned toward the open door, with the main menu on a briefing
// board in front and the aircraft beyond it (the menu is anchored to the seat while this is up, NativeVrUiRoot). The
// shelter's floor, walls, roof and door are found with raycasts against copies of its own colliders, so the seat, desk
// and board stand clear of them; that is done once, so the seat stays put whichever aircraft is parked. Only an aircraft
// the shelter is made for that fits it is parked: between the walls and clear of the desk, under the roof, and inside the
// door once moved forward clear of the seat. The aircraft flying past and parked outside can be any of the game's.
// Only the game's standard units are used: not its disabled ones, its event ones (April fools) or the ones it keeps out
// of its encyclopedia, and not one whose copy doesn't come out whole.
// The shelter, the aircraft, the buildings and vehicles across the apron are copied from the game's prefabs
// (VisualClone); the desk and board, the dusk sky, the hills and the airfield are made here. The door rolls open when the
// menu comes up and shut while a mission loads (LoadingScreenPatch), and now and then the game's aircraft fly past the
// field with their engine sound. Everything sits in the menu camera's tracking space, so it stays put around the player
// across the menu scenes and moves with a recenter. Whatever the game doesn't have (a building, a shader) is left out.
public class MenuEnvironment : NOVRBehaviour
{
    // Seated eye height above the floor.
    private const float EyeHeight = 1.3f;
    // How far the seat stays from the walls.
    private const float WallClearance = 1.3f;
    // The shelter's copied colliders, on a layer no camera draws and nothing else casts against.
    private const int ColliderLayer = (int)LayerHelper.Layers.VrUiCapture;
    private const float OpenDelaySeconds = 1.2f;
    private const float OpenSeconds = 7f;
    private const float CloseSeconds = 3f;
    private const float SkyRadius = 3400f;
    private const float HillsRadius = 2900f;
    private const float FarClip = 8000f;
    private const float RunwayFromDoor = 560f;
    private const float FlybyMinSeconds = 25f;
    private const float FlybyMaxSeconds = 60f;
    private const float FlybyLength = 8000f;
    private const int MaxFrameErrors = 3;

    private static readonly string[] MenuScenes = { "MainMenu", "MultiplayerMenu", "MissionsMenu" };
    private static readonly string[] ShelterWords = { "bunker", "shelter", "hardened", "has" };
    private static readonly string[] EngineWords = { "engine", "jet", "turbine", "exterior", "external", "distant", "far" };
    // Support vehicles first (fuel, logistics and transport trucks, light utility cars), not the armed ones.
    private static readonly string[] VehicleSuffixes = { "-ft", "-l", "-t" };
    private static readonly string[] VehicleWords = { "fuel", "tanker", "lcv", "utility", "supply", "cargo" };

    private static readonly Color SkyZenith = new(0.05f, 0.08f, 0.19f);
    private static readonly Color SkyHigh = new(0.2f, 0.25f, 0.42f);
    private static readonly Color SkyGlow = new(0.98f, 0.56f, 0.3f);
    private static readonly Color SkyHorizon = new(0.48f, 0.4f, 0.48f);
    private static readonly Color SkyBelow = new(0.09f, 0.08f, 0.09f);
    private static readonly Color HillsColor = new(0.08f, 0.09f, 0.13f);
    private static readonly Color SunDiscColor = new(1f, 0.86f, 0.62f);
    private static readonly Color Ambient = new(0.2f, 0.21f, 0.27f);
    private static readonly Color SunColor = new(1f, 0.6f, 0.38f);
    private static readonly Color GroundColor = new(0.07f, 0.08f, 0.06f);
    private static readonly Color ConcreteColor = new(0.28f, 0.28f, 0.27f);
    private static readonly Color AsphaltColor = new(0.06f, 0.06f, 0.065f);
    // Light travels in from beyond the door, low, a little from the right: a sunset seen through the door.
    private static readonly Quaternion SunRotation = Quaternion.Euler(7f, 205f, 0f);

    public static MenuEnvironment? I { get; private set; }
    public static string? FeaturedAircraft { get; private set; }

    private readonly List<Door> _doors = new();
    private readonly List<Collider> _shelterColliders = new();
    // The aircraft the game's hangars can hold, which fly past; and the ones whose copies came out incomplete.
    private readonly List<AircraftDefinition> _flybyAircraft = new();
    private readonly HashSet<UnitDefinition> _incomplete = new();
    private readonly HashSet<UnitDefinition> _doesNotFit = new();
    // The room the shelter has for an aircraft, in the site frame: between the walls at wing height, and under the roof.
    private float _fitLeft = -10f;
    private float _fitRight = 10f;
    private float _ceiling = 8f;
    private Vector3 _seat;
    private float _seatYaw;
    private float _doorInside;
    private readonly List<Flyby> _flybys = new();
    // Kept after their pass and flown again, so each aircraft type is copied at most once per wingman.
    private readonly Dictionary<AircraftDefinition, List<GameObject>> _flybyClones = new();
    private GameObject? _root;
    private Transform? _site;
    private Transform? _shelter;
    private Transform? _spawn;
    private Bounds _shelterBounds;
    private float _insideHalfWidth = 10f;
    // The floor under the spawn point, in the site frame (the spawn point is at 0).
    private float _floor;
    private bool _loggedLayout;
    private AircraftDefinition[] _hangarAircraft = Array.Empty<AircraftDefinition>();
    private GameObject? _featured;
    private Transform? _board;
    private Transform? _boardLegs;
    private bool _loggedBoard;
    private float _logBoardAt;
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
    private bool _buildFailed;
    private bool _visible;
    private bool _loading;
    private int _frameErrors;
    private int _pausedScene = -1;
    private float _openAt;
    private float _door;
    private bool _doorMovingNow;
    private float _doorFrom;
    private float _doorTo;
    private float _doorStartTime;
    private float _doorSeconds;
    private float _nextFlyby;
    private float _doorZ;
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

    // Up and around the player; NativeVrUiRoot then anchors the menu to the seat.
    public bool IsShowing => _visible;

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
        var scene = SceneManager.GetActiveScene();
        try
        {
            var wanted = !_buildFailed && scene.handle != _pausedScene && ModConfiguration.Instance.MenuHangar.Value &&
                         Array.IndexOf(MenuScenes, scene.name) >= 0 && MainMenu.State == MainMenu.LoadingState.Loaded;
            var camera = wanted ? APIBus.MainCamera : null;
            if (camera == null)
            {
                Hide();
                return;
            }

            if (!_built) Build();
            if (_root == null) return;

            if (!_visible) Arrive(scene, camera);
            Follow(camera);
            if (scene.handle != _lightingScene) ApplyLighting(scene);

            if (!_loading && _openAt > 0f && Time.unscaledTime >= _openAt)
            {
                _openAt = 0f;
                MoveDoor(1f, OpenSeconds);
            }

            UpdateDoor();
            UpdateFlybys();
            UpdateBoard();
        }
        catch (Exception exception)
        {
            // Given up on for this scene; the next menu scene tries again.
            _pausedScene = scene.handle;
            Hide();
            if (++_frameErrors <= MaxFrameErrors) NOVRPlugin.Log.LogWarning($"[NOVR] Menu hangar stopped in {scene.name}: {exception}");
        }
    }

    private void Build()
    {
        _built = true;
        try
        {
            _root = new GameObject("NOVR Menu Hangar");
            DontDestroyOnLoad(_root);
            _root.SetActive(false);
            _site = new GameObject("Site").transform;
            _site.SetParent(_root.transform, false);

            BuildShelter();
            CollectFlybyAircraft();
            _root.SetActive(true);
            _floor = MeasureFloor();
            _seat = PlaceSeat(out _seatYaw);
            _root.SetActive(false);
            _lit = LitTemplate();
            _unlit = UnlitTemplate();
            BuildSun();
            BuildSky();
            BuildOutside();
            BuildDesk();
            BuildBoard();
            var hidden = HideUndrawable(_root);
            NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: surfaces from {Describe(_lit)}, sky from {Describe(_unlit)}; " +
                                   $"{VisualClone.SkippedParts} parts of the game's models left out ({string.Join(", ", VisualClone.SkippedReasons)})" +
                                   (hidden > 0 ? $", {hidden} undrawable parts hidden." : "."));
        }
        catch (Exception exception)
        {
            _buildFailed = true;
            if (_root != null) Destroy(_root);
            _root = null;
            NOVRPlugin.Log.LogWarning($"[NOVR] Could not build the menu hangar: {exception}");
        }
    }

    // Each time the menus come back: a new aircraft in the shelter, and the door shut, to open once the menu is up.
    private void Arrive(Scene scene, Camera camera)
    {
        _visible = true;
        _root!.SetActive(true);
        // Shut first, so the seat is measured against the closed door.
        _doorMovingNow = false;
        ApplyDoor(0f);
        PlaceFeaturedAircraft();
        _openAt = Time.unscaledTime + OpenDelaySeconds;
        _nextFlyby = Time.unscaledTime + Random.Range(8f, 20f);
        _logBoardAt = Time.unscaledTime + 3f;
        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: shown in {scene.name} from '{camera.name}', which draws {CountSceneMeshes(scene)} meshes of its own; " +
                               $"{FeaturedAircraft ?? "no aircraft"} parked.");
    }

    private void Hide()
    {
        if (!_visible) return;

        _visible = false;
        RestoreCamera();
        foreach (var flyby in _flybys)
        {
            if (flyby.Clone != null) flyby.Clone.SetActive(false);
        }

        _flybys.Clear();
        if (_doorLoop != null) _doorLoop.Stop();
        if (_root != null) _root.SetActive(false);
    }

    // The menu camera's parent is the tracking space: the headset moves the camera within it. Its origin is the seat.
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

    // The shelter goes in the site frame: the game's aircraft spawn point at its origin, facing +Z out of the door.
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
            if (!Standard(definition)) continue;

            if (candidates.Length > 0) candidates.Append(", ");
            candidates.Append(definition.unitPrefab.name);
            var hangar = definition.unitPrefab.GetComponentInChildren<Hangar>(true);
            if (hangar == null) continue;

            var doors = GetField<HangarDoor[]>(hangar, "doors")?.Length ?? 0;
            var name = $"{definition.unitName} {definition.unitPrefab.name}".ToLowerInvariant();
            var score = doors > 0 ? 10 : 0;
            foreach (var word in ShelterWords)
            {
                if (name.Contains(word)) score += 20;
            }

            if (score <= bestScore) continue;

            best = definition;
            bestHangar = hangar;
            bestScore = score;
        }

        if (best == null || bestHangar == null)
        {
            NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: no hangar among the game's buildings ({candidates}); outside only.");
            return;
        }

        var map = new Dictionary<Transform, Transform>();
        _shelter = VisualClone.Create(best.unitPrefab, _site!, map, _shelterColliders, ColliderLayer).transform;
        _hangarAircraft = Array.FindAll(GetField<AircraftDefinition[]>(bestHangar, "availableAircraft") ?? Array.Empty<AircraftDefinition>(), Standard);

        var spawn = GetField<Transform>(bestHangar, "spawnTransform");
        if (spawn == null || !map.TryGetValue(spawn, out _spawn)) map.TryGetValue(bestHangar.transform, out _spawn);
        _spawn ??= _shelter;

        var spawnRotation = Quaternion.Inverse(_shelter.rotation) * _spawn.rotation;
        var turn = Quaternion.Euler(0f, -spawnRotation.eulerAngles.y, 0f);
        var spawnInShelter = _shelter.InverseTransformPoint(_spawn.position);
        _shelter.localRotation = turn;
        _shelter.localPosition = -(turn * spawnInShelter);

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

        // The inside, roughly: the whole extent less walls and banks.
        if (TryGetBounds(_shelter.gameObject, _site!, out _shelterBounds))
        {
            _insideHalfWidth = Mathf.Min(-_shelterBounds.min.x, _shelterBounds.max.x) * 0.7f;
            _doorZ = _shelterBounds.max.z;
        }

        BuildDoorSound(bestHangar);
        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: using {best.unitPrefab.name} ({_doors.Count} doors, {_hangarAircraft.Length} aircraft types, " +
                               $"{_shelterBounds.size.x:0}x{_shelterBounds.size.z:0} m); buildings: {candidates}.");
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

    // A new aircraft each time, at the game's spawn point; the site is then moved so the seat is beside it.
    private void PlaceFeaturedAircraft()
    {
        if (_featured != null) Destroy(_featured);
        _featured = null;
        FeaturedAircraft = null;
        if (_site == null) return;

        var candidates = new List<AircraftDefinition>();
        foreach (var aircraft in _hangarAircraft)
        {
            if (!_incomplete.Contains(aircraft) && !_doesNotFit.Contains(aircraft)) candidates.Add(aircraft);
        }

        // In random order until one is whole and fits; one that doesn't isn't tried again.
        while (_featured == null && _spawn != null && candidates.Count > 0)
        {
            var pick = Random.Range(0, candidates.Count);
            var aircraft = candidates[pick];
            candidates.RemoveAt(pick);

            var clone = VisualClone.Create(aircraft.unitPrefab, _spawn);
            if (!LooksWhole(clone, aircraft))
            {
                Destroy(clone);
                continue;
            }

            // On the floor found under the spawn point, which the game's spawn drops it onto.
            clone.transform.localPosition = Vector3.up * (aircraft.spawnOffset.y + _floor) + Vector3.forward * aircraft.spawnOffset.z;
            clone.transform.localRotation = Quaternion.Euler(aircraft.restRotation);

            // Its tail clear of the desk in front of the seat; then it has to fit.
            if (!TryGetBounds(clone, _site, out var bounds))
            {
                Destroy(clone);
                continue;
            }

            var forward = Mathf.Max(0f, _seat.z + 1.6f - bounds.min.z);
            if (!Fits(bounds, forward, out var why))
            {
                Destroy(clone);
                if (_doesNotFit.Add(aircraft)) NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: {aircraft.unitName} doesn't fit: {why}.");
                continue;
            }

            clone.transform.localPosition += Vector3.forward * forward;
            _featured = clone;
            FeaturedAircraft = aircraft.unitName;
        }

        var turn = Quaternion.Euler(0f, -_seatYaw, 0f);
        _site.localRotation = turn;
        _site.localPosition = -(turn * _seat);
    }

    // Bounds in the site frame, before being moved forward by forward.
    private bool Fits(Bounds bounds, float forward, out string why)
    {
        why = bounds.min.x < _fitLeft + 0.3f || bounds.max.x > _fitRight - 0.3f ? $"{bounds.size.x:0.0} m span, the walls are {_fitRight - _fitLeft:0.0} m apart"
            : bounds.min.x < _seat.x + 0.8f ? "its wing would reach the desk"
            : bounds.max.y > _ceiling - 0.3f ? $"{bounds.max.y - _floor:0.0} m tall, the roof is {_ceiling - _floor:0.0} m up"
            : bounds.max.z + forward > _doorInside - 0.4f ? $"{bounds.size.z:0.0} m long, too long between the desk and the door"
            : "";
        return why.Length == 0;
    }

    // The game's own units only: not disabled, not event content (April fools), not kept out of its encyclopedia.
    private static bool Standard(UnitDefinition? definition) =>
        definition != null && definition.unitPrefab != null && definition.IsAllowed(false) && !definition.dontAutomaticallyAddToEncyclopedia;

    // A copy much smaller than the size the game gives the unit is missing its body (a unit that builds it at run time);
    // it is left out, and not tried again.
    private bool LooksWhole(GameObject clone, UnitDefinition definition)
    {
        var expected = Mathf.Max(definition.length, definition.width);
        if (expected <= 0f) return true;

        var size = TryGetBounds(clone, clone.transform, out var bounds) ? Mathf.Max(bounds.size.x, bounds.size.z) : 0f;
        if (size >= expected * 0.5f) return true;

        if (_incomplete.Add(definition))
            NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: left out {definition.unitName} ({definition.unitPrefab.name}), whose copy is {size:0.0} m across of {expected:0.0} m.");
        return false;
    }

    // Any of the game's standard aircraft, to fly past and park outside.
    private void CollectFlybyAircraft()
    {
        foreach (var aircraft in Encyclopedia.i.aircraft)
        {
            if (Standard(aircraft) && !_flybyAircraft.Contains(aircraft)) _flybyAircraft.Add(aircraft);
        }
    }

    // In the back left corner, by the left wall as far back as the back wall allows, turned toward the door; then turned
    // further right while the board or desk would reach the wall.
    private Vector3 PlaceSeat(out float yaw)
    {
        SetCollidersEnabled(true);
        try
        {
            var eye = _floor + EyeHeight;
            var back = -CastOr(new Vector3(-4f, eye, 0f), Vector3.back, -_shelterBounds.min.z * 0.8f);
            var door = CastOr(new Vector3(0f, eye, 0f), Vector3.forward, _doorZ);
            _doorInside = door;
            var z = Mathf.Clamp(back + WallClearance, back + 0.5f, door - 3f);

            // The room for an aircraft at the spawn point: the narrower of the walls low and at wing height, and the roof.
            _fitLeft = -Mathf.Min(CastOr(new Vector3(0f, _floor + 1f, 0f), Vector3.left, _insideHalfWidth),
                CastOr(new Vector3(0f, _floor + 2.5f, 0f), Vector3.left, _insideHalfWidth));
            _fitRight = Mathf.Min(CastOr(new Vector3(0f, _floor + 1f, 0f), Vector3.right, _insideHalfWidth),
                CastOr(new Vector3(0f, _floor + 2.5f, 0f), Vector3.right, _insideHalfWidth));
            _ceiling = _floor + 1f + CastOr(new Vector3(0f, _floor + 1f, 0f), Vector3.up, Mathf.Max(3f, _shelterBounds.max.y * 0.8f - _floor - 1f));

            var wall = Mathf.Min(CastOr(new Vector3(0f, eye, z), Vector3.left, _insideHalfWidth),
                CastOr(new Vector3(0f, _floor + 0.75f, z), Vector3.left, _insideHalfWidth));
            var x = -wall + WallClearance;
            var seat = new Vector3(x, eye, z);
            yaw = Mathf.Clamp(Mathf.Atan2(-x, door - z) * Mathf.Rad2Deg, 0f, 45f);
            for (var step = 0; step < 8 && !Clear(seat, yaw); step++) yaw += 5f;

            if (!_loggedLayout)
            {
                _loggedLayout = true;
                NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: {_shelterColliders.Count} colliders; floor {_floor:0.00} m from the spawn point, back wall {back:0.0} m, " +
                                       $"left wall {-wall:0.0} m, door {door:0.0} m; room for {_fitRight - _fitLeft:0.0} m span, {_ceiling - _floor:0.0} m high; " +
                                       $"seat at {x:0.0}, {z:0.0} m, turned {yaw:0} deg; " +
                                       $"{_hangarAircraft.Length} aircraft types park here, {_flybyAircraft.Count} fly past.");
            }

            return seat;
        }
        finally
        {
            SetCollidersEnabled(false);
        }
    }

    // Whether the board's far edge and the desk's corner, seen from the seat turned by yaw, are this side of any wall.
    private bool Clear(Vector3 seat, float yaw)
    {
        var turn = Quaternion.Euler(0f, yaw, 0f);
        var unit = NativeVrUiRoot.CanvasUnitMeters;
        var boardEdge = new Vector3((MainMenuScreen.RailCenter.x - MainMenuScreen.RailSize.x / 2f - 60f) * unit, NativeVrUiRoot.MenuHeightOffset,
            NativeVrUiRoot.MenuDistance + 0.1f);
        var points = new[] { boardEdge, boardEdge + Vector3.down * 0.9f, new Vector3(-0.75f, -EyeHeight + 0.75f, 1.15f) };
        foreach (var point in points)
        {
            var direction = turn * point;
            if (Cast(seat, direction.normalized, direction.magnitude, out _)) return false;
        }

        return true;
    }

    private float MeasureFloor()
    {
        SetCollidersEnabled(true);
        try
        {
            return Cast(new Vector3(0f, 4f, 0f), Vector3.down, 12f, out var hit) ? 4f - hit : 0f;
        }
        finally
        {
            SetCollidersEnabled(false);
        }
    }

    private void SetCollidersEnabled(bool enabled)
    {
        foreach (var collider in _shelterColliders)
        {
            if (collider != null) collider.enabled = enabled;
        }

        if (enabled) Physics.SyncTransforms();
    }

    // A ray in the site frame against the shelter's colliders.
    private bool Cast(Vector3 origin, Vector3 direction, float distance, out float hit)
    {
        hit = distance;
        if (_site == null || _shelterColliders.Count == 0) return false;
        if (!Physics.Raycast(_site.TransformPoint(origin), _site.TransformDirection(direction), out var info, distance, 1 << ColliderLayer,
                QueryTriggerInteraction.Ignore)) return false;

        hit = info.distance;
        return true;
    }

    private float CastOr(Vector3 origin, Vector3 direction, float fallback) => Cast(origin, direction, 80f, out var hit) ? hit : fallback;

    private AircraftDefinition? PickAircraft(IList<AircraftDefinition> preferred)
    {
        var choices = new List<AircraftDefinition>();
        foreach (var definition in preferred.Count > 0 ? preferred : _flybyAircraft)
        {
            if (Standard(definition) && !_incomplete.Contains(definition)) choices.Add(definition);
        }

        return choices.Count > 0 ? choices[Random.Range(0, choices.Count)] : null;
    }

    // Bounds of everything drawn under root, in space's frame.
    private static bool TryGetBounds(GameObject root, Transform space, out Bounds bounds)
    {
        bounds = new Bounds();
        var any = false;
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            if (!renderer.enabled) continue;

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

    private void BuildSun()
    {
        var sun = new GameObject("Sun");
        sun.transform.SetParent(_site!, false);
        sun.transform.localRotation = SunRotation;
        _sun = sun.AddComponent<Light>();
        _sun.type = LightType.Directional;
        _sun.color = SunColor;
        _sun.intensity = 1.6f;
        _sun.shadows = LightShadows.Soft;

        // Work lights over the aircraft, as the shelter's own may be few.
        AddPointLight(_site!, "Work Light", new Vector3(0f, 7f, 0f), new Color(1f, 0.92f, 0.8f), 1.3f, 26f);
    }

    // A dome shaded from the sunset glow to night overhead, the sun, and a ring of dark hills, all unlit and drawn
    // behind everything else.
    private void BuildSky()
    {
        if (_unlit == null) return;

        var toSun = -(SunRotation * Vector3.forward);
        AddMesh(_site!, "Sky", SkyMesh(toSun), UnlitMaterial(Color.white, 2980), Vector3.zero, SkyRadius);

        var disc = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(disc.GetComponent<Collider>());
        disc.name = "Sun Disc";
        disc.transform.SetParent(_site!, false);
        disc.transform.localPosition = toSun * (SkyRadius - 200f);
        disc.transform.localScale = Vector3.one * 90f;
        SetupRenderer(disc.GetComponent<MeshRenderer>(), UnlitMaterial(SunDiscColor, 2981));

        AddMesh(_site!, "Hills", HillsMesh(toSun), UnlitMaterial(Color.white, 2990), Vector3.zero, 1f);
    }

    private static Mesh SkyMesh(Vector3 toSun)
    {
        const int segments = 48;
        const int rings = 24;
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var triangles = new List<int>();
        for (var ring = 0; ring <= rings; ring++)
        {
            var elevation = Mathf.Lerp(-90f, 90f, ring / (float)rings) * Mathf.Deg2Rad;
            for (var segment = 0; segment <= segments; segment++)
            {
                var azimuth = segment / (float)segments * Mathf.PI * 2f;
                var direction = new Vector3(Mathf.Cos(elevation) * Mathf.Sin(azimuth), Mathf.Sin(elevation), Mathf.Cos(elevation) * Mathf.Cos(azimuth));
                vertices.Add(direction);
                colors.Add(Linear(SkyColor(direction, toSun)));
            }
        }

        for (var ring = 0; ring < rings; ring++)
        {
            for (var segment = 0; segment < segments; segment++)
            {
                var a = ring * (segments + 1) + segment;
                var b = a + segments + 1;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        }

        return CreateMesh("NOVR Sky", vertices, colors, triangles);
    }

    private static Color SkyColor(Vector3 direction, Vector3 toSun)
    {
        var height = direction.y;
        if (height < 0f) return Color.Lerp(SkyHorizon * 0.5f, SkyBelow, Mathf.Clamp01(-height * 8f));

        // Toward the sun the horizon glows; away from it, it is the dusky violet of the far side.
        var flat = new Vector3(direction.x, 0f, direction.z).normalized;
        var facing = Mathf.Clamp01((Vector3.Dot(flat, new Vector3(toSun.x, 0f, toSun.z).normalized) + 1f) * 0.5f);
        var horizon = Color.Lerp(SkyHorizon, SkyGlow, Mathf.Pow(facing, 3f));
        var low = Color.Lerp(horizon, SkyHigh, Mathf.Clamp01(height / Mathf.Lerp(0.12f, 0.3f, Mathf.Pow(facing, 4f))));
        var color = Color.Lerp(low, SkyZenith, Mathf.Clamp01((height - 0.2f) / 0.8f));
        // A halo close around the sun.
        var halo = Mathf.Pow(Mathf.Clamp01(Vector3.Dot(direction, toSun)), 24f);
        return Color.Lerp(color, SkyGlow * 1.15f, halo * 0.8f);
    }

    private static Mesh HillsMesh(Vector3 toSun)
    {
        const int segments = 160;
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var triangles = new List<int>();
        var flatSun = new Vector3(toSun.x, 0f, toSun.z).normalized;
        for (var segment = 0; segment <= segments; segment++)
        {
            var azimuth = segment / (float)segments * Mathf.PI * 2f;
            var direction = new Vector3(Mathf.Sin(azimuth), 0f, Mathf.Cos(azimuth));
            // Ridges of a few overlapping sizes, lower toward the sun so the sunset shows over them.
            var ridge = 0.5f + 0.25f * Mathf.Sin(azimuth * 3f + 1.3f) + 0.15f * Mathf.Sin(azimuth * 7f + 0.4f) + 0.1f * Mathf.Sin(azimuth * 17f + 2.1f);
            var towardSun = Mathf.Clamp01(Vector3.Dot(direction, flatSun));
            var height = Mathf.Lerp(60f, 300f, ridge) * (1f - 0.6f * towardSun * towardSun);
            vertices.Add(direction * HillsRadius + Vector3.down * 80f);
            vertices.Add(direction * HillsRadius + Vector3.up * height);
            var shade = Linear(Color.Lerp(HillsColor, SkyHorizon * 0.45f, 0.35f * towardSun));
            colors.Add(Linear(HillsColor * 0.7f));
            colors.Add(shade);
        }

        for (var segment = 0; segment < segments; segment++)
        {
            var a = segment * 2;
            triangles.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
        }

        return CreateMesh("NOVR Hills", vertices, colors, triangles);
    }

    // The ground, the apron in front of the door, a runway across the view with its edge lights, blue taxiway lights out to
    // it, and some of the game's other buildings and vehicles about the field.
    private void BuildOutside()
    {
        var outside = new GameObject("Outside").transform;
        outside.SetParent(_site!, false);
        outside.localPosition = Vector3.up * _floor;

        // Slabs whose tops are far enough apart that the distant ones don't flicker against each other: the ground 30 cm
        // under the shelter floor, the apron and taxiway just under it, the runway just over the ground.
        if (_lit != null)
        {
            AddBox(outside, "Ground", new Vector3(0f, -0.8f, 0f), new Vector3(SkyRadius * 2f, 1f, SkyRadius * 2f), GroundColor, 0.1f);
            // From the front of the shelter out, so none of it comes up through the shelter floor.
            AddBox(outside, "Apron", new Vector3(0f, -0.54f, _doorZ + 60f), new Vector3(80f, 1f, 120f), ConcreteColor, 0.2f);
            var taxiwayLength = RunwayFromDoor - 120f;
            AddBox(outside, "Taxiway", new Vector3(0f, -0.56f, _doorZ + 120f + taxiwayLength / 2f), new Vector3(24f, 1f, taxiwayLength), AsphaltColor, 0.25f);
            AddBox(outside, "Runway", new Vector3(0f, -0.48f, _doorZ + RunwayFromDoor + 30f), new Vector3(5000f, 1f, 50f), AsphaltColor, 0.25f);
        }

        if (_unlit != null)
        {
            var runwayLight = UnlitMaterial(new Color(1f, 0.88f, 0.66f), 3000);
            var taxiwayLight = UnlitMaterial(new Color(0.3f, 0.45f, 1f), 3000);
            for (var x = -2400f; x <= 2400f; x += 60f)
            {
                AddLightBulb(outside, runwayLight, new Vector3(x, 0.2f, _doorZ + RunwayFromDoor + 4f), 1.6f);
                AddLightBulb(outside, runwayLight, new Vector3(x, 0.2f, _doorZ + RunwayFromDoor + 56f), 1.6f);
            }

            for (var z = _doorZ + 120f; z < _doorZ + RunwayFromDoor - 10f; z += 30f)
            {
                AddLightBulb(outside, taxiwayLight, new Vector3(-13f, 0.2f, z), 0.8f);
                AddLightBulb(outside, taxiwayLight, new Vector3(13f, 0.2f, z), 0.8f);
            }
        }

        // Across the apron, facing back toward the shelter, on the ground.
        PlaceBuilding(outside, new[] { "hangar_med", "hangar" }, new Vector3(-75f, -0.3f, _doorZ + 170f), 165f, park: false);
        PlaceBuilding(outside, new[] { "revetment" }, new Vector3(55f, -0.3f, _doorZ + 125f), 195f, park: true);
        PlaceBuilding(outside, new[] { "shelter" }, new Vector3(150f, -0.3f, _doorZ + 215f), 185f, park: false);
        PlaceBuilding(outside, new[] { "helipad" }, new Vector3(-160f, -0.3f, _doorZ + 70f), 150f, park: false);
        PlaceVehicles(outside);
    }

    private void PlaceBuilding(Transform parent, string[] words, Vector3 position, float yaw, bool park)
    {
        var buildings = Encyclopedia.i.buildings;
        if (buildings == null) return;

        foreach (var word in words)
        {
            foreach (var definition in buildings)
            {
                if (!Standard(definition) || !definition.unitPrefab.name.ToLowerInvariant().Contains(word)) continue;

                var building = VisualClone.Create(definition.unitPrefab, parent);
                building.transform.localPosition = position;
                building.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                if (park && PickAircraft(_flybyAircraft) is { } aircraft)
                {
                    var parked = VisualClone.Create(aircraft.unitPrefab, building.transform);
                    parked.transform.localPosition = Vector3.up * aircraft.spawnOffset.y;
                    parked.transform.localRotation = Quaternion.Euler(aircraft.restRotation);
                    if (!LooksWhole(parked, aircraft)) Destroy(parked);
                }

                return;
            }
        }
    }

    private void PlaceVehicles(Transform parent)
    {
        var vehicles = Encyclopedia.i.vehicles;
        if (vehicles == null) return;

        var names = new StringBuilder();
        var picked = new List<VehicleDefinition>();
        foreach (var definition in vehicles)
        {
            if (!Standard(definition)) continue;

            if (names.Length > 0) names.Append(", ");
            names.Append(definition.unitPrefab.name);
            var name = definition.unitPrefab.name.ToLowerInvariant();
            if (definition.length > 14f || picked.Count >= 2) continue;
            if (Array.Exists(VehicleSuffixes, name.EndsWith) || Array.Exists(VehicleWords, name.Contains)) picked.Add(definition);
        }

        var spots = new[] { (new Vector3(16f, 0f, _doorZ + 22f), 250f), (new Vector3(-38f, 0f, _doorZ + 100f), 170f) };
        for (var index = 0; index < picked.Count; index++)
        {
            var vehicle = VisualClone.Create(picked[index].unitPrefab, parent);
            vehicle.transform.localPosition = spots[index].Item1 + Vector3.up * picked[index].spawnOffset.y;
            vehicle.transform.localRotation = Quaternion.Euler(0f, spots[index].Item2, 0f);
            if (!LooksWhole(vehicle, picked[index])) Destroy(vehicle);
        }

        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: {picked.Count} vehicles out on the apron, of {names}.");
    }

    // A desk in front of the seat, with a lamp, papers, a clipboard and a mug. The seat is the root's origin and the
    // floor EyeHeight below it.
    private void BuildDesk()
    {
        if (_lit == null) return;

        var desk = new GameObject("Desk").transform;
        desk.SetParent(_root!.transform, false);
        desk.localPosition = new Vector3(0f, -EyeHeight, 0.78f);

        var wood = new Color(0.24f, 0.16f, 0.1f);
        var steel = new Color(0.3f, 0.31f, 0.33f);
        const float top = 0.74f;
        AddBox(desk, "Top", new Vector3(0f, top - 0.02f, 0f), new Vector3(1.3f, 0.04f, 0.62f), wood, 0.35f);
        foreach (var x in new[] { -0.6f, 0.6f })
        {
            foreach (var z in new[] { -0.26f, 0.26f })
            {
                AddBox(desk, "Leg", new Vector3(x, (top - 0.04f) / 2f, z), new Vector3(0.05f, top - 0.04f, 0.05f), steel, 0.5f);
            }
        }

        AddBox(desk, "Back Panel", new Vector3(0f, 0.42f, 0.28f), new Vector3(1.2f, 0.5f, 0.02f), steel * 0.8f, 0.4f);
        AddBox(desk, "Paper", new Vector3(-0.18f, top + 0.001f, -0.02f), new Vector3(0.21f, 0.002f, 0.297f), new Color(0.85f, 0.84f, 0.78f), 0.1f, 8f);
        AddBox(desk, "Paper", new Vector3(-0.02f, top + 0.003f, 0.06f), new Vector3(0.21f, 0.002f, 0.297f), new Color(0.82f, 0.82f, 0.77f), 0.1f, -5f);
        AddBox(desk, "Clipboard", new Vector3(-0.45f, top + 0.005f, -0.05f), new Vector3(0.23f, 0.01f, 0.32f), new Color(0.35f, 0.24f, 0.14f), 0.3f, 12f);
        AddCylinder(desk, "Mug", new Vector3(0.22f, top + 0.05f, -0.12f), 0.085f, 0.1f, new Color(0.55f, 0.12f, 0.1f));

        // A low green-shaded lamp on the right-hand corner, below where the menus reach, its warm light on the desk.
        AddCylinder(desk, "Lamp Base", new Vector3(0.52f, top + 0.01f, 0.16f), 0.14f, 0.02f, steel);
        AddCylinder(desk, "Lamp Stem", new Vector3(0.52f, top + 0.12f, 0.16f), 0.02f, 0.22f, steel);
        AddBox(desk, "Lamp Shade", new Vector3(0.52f, top + 0.24f, 0.12f), new Vector3(0.24f, 0.06f, 0.11f), new Color(0.08f, 0.24f, 0.14f), 0.6f);
        AddPointLight(desk, "Lamp Light", new Vector3(0.52f, top + 0.18f, 0.1f), new Color(1f, 0.82f, 0.6f), 1.2f, 2.2f);
    }

    // A briefing board behind the main menu's panel; UpdateBoard keeps it there as the menu's size and distance change.
    private void BuildBoard()
    {
        if (_lit == null) return;

        _board = AddBox(_root!.transform, "Board", Vector3.zero, Vector3.one, new Color(0.07f, 0.08f, 0.09f), 0.3f).transform;
        _boardLegs = new GameObject("Board Legs").transform;
        _boardLegs.SetParent(_root.transform, false);
        var steel = new Color(0.3f, 0.31f, 0.33f);
        foreach (var side in new[] { -0.5f, 0.5f })
        {
            AddBox(_boardLegs, "Leg", new Vector3(side, 0.5f, 0f), new Vector3(0.04f, 1f, 0.04f), steel, 0.5f);
            AddBox(_boardLegs, "Foot", new Vector3(side, 0.01f, 0f), new Vector3(0.06f, 0.02f, 0.6f), steel, 0.5f);
        }
    }

    private void UpdateBoard()
    {
        if (_board == null || _boardLegs == null) return;

        var unit = NativeVrUiRoot.CanvasUnitMeters;
        var center = new Vector3(MainMenuScreen.RailCenter.x * unit, NativeVrUiRoot.MenuHeightOffset + MainMenuScreen.RailCenter.y * unit,
            NativeVrUiRoot.MenuDistance + 0.06f);
        var size = (MainMenuScreen.RailSize + new Vector2(90f, 90f)) * unit;
        _board.localPosition = center;
        _board.localScale = new Vector3(size.x, size.y, 0.04f);

        // Legs from the floor to the board's lower edge, inset from its sides.
        var bottom = center.y - size.y / 2f;
        var legHeight = bottom + EyeHeight;
        _boardLegs.gameObject.SetActive(legHeight > 0.02f);
        _boardLegs.localPosition = new Vector3(center.x, -EyeHeight, center.z + 0.03f);
        _boardLegs.localScale = new Vector3(size.x - 0.2f, Mathf.Max(0.02f, legHeight), 1f);
        // Once, after the menu has had time to settle on the seat.
        if (_loggedBoard || Time.unscaledTime < _logBoardAt) return;

        _loggedBoard = true;
        NOVRPlugin.Log.LogInfo($"[NOVR] Menu hangar: board {size.x:0.00}x{size.y:0.00} m at {center.x:0.00}, {center.y:0.00}, {center.z:0.00} m from the seat; " +
                               $"menu anchored at the seat: {NativeVrUiRoot.MenuAtSeat}.");
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
        var definition = PickAircraft(_flybyAircraft);
        if (definition == null) return;

        var side = Random.value < 0.5f ? -1f : 1f;
        var speed = Random.Range(140f, 230f);
        var start = new Vector3(-side * FlybyLength / 2f, Random.Range(90f, 240f), _doorZ + RunwayFromDoor + Random.Range(-120f, 300f));
        var velocity = new Vector3(side * speed, 0f, 0f);
        // Sometimes a pair, the wingman back and out to the side.
        if (Launch(definition, start, velocity, FlybyLength / speed) && Random.value < 0.35f)
            Launch(definition, start + new Vector3(-side * 60f, -10f, 40f), velocity, FlybyLength / speed);
    }

    private bool Launch(AircraftDefinition definition, Vector3 start, Vector3 velocity, float seconds)
    {
        if (!_flybyClones.TryGetValue(definition, out var clones)) _flybyClones[definition] = clones = new List<GameObject>();
        var clone = clones.Find(cached => cached != null && !cached.activeSelf);
        if (clone == null)
        {
            clone = VisualClone.Create(definition.unitPrefab, _site!);
            if (!LooksWhole(clone, definition))
            {
                Destroy(clone);
                return false;
            }

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
        return true;
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

    private GameObject AddBox(Transform parent, string name, Vector3 position, Vector3 size, Color color, float smoothness, float yaw = 0f)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(box.GetComponent<Collider>());
        box.name = name;
        box.transform.SetParent(parent, false);
        box.transform.localPosition = position;
        box.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        box.transform.localScale = size;
        box.GetComponent<MeshRenderer>().sharedMaterial = LitMaterial(color, smoothness);
        return box;
    }

    private void AddCylinder(Transform parent, string name, Vector3 position, float diameter, float height, Color color)
    {
        var cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(cylinder.GetComponent<Collider>());
        cylinder.name = name;
        cylinder.transform.SetParent(parent, false);
        cylinder.transform.localPosition = position;
        // Unity's cylinder is two units tall and one across.
        cylinder.transform.localScale = new Vector3(diameter, height / 2f, diameter);
        cylinder.GetComponent<MeshRenderer>().sharedMaterial = LitMaterial(color, 0.4f);
    }

    private static void AddPointLight(Transform parent, string name, Vector3 position, Color color, float intensity, float range)
    {
        var owner = new GameObject(name);
        owner.transform.SetParent(parent, false);
        owner.transform.localPosition = position;
        var light = owner.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.shadows = LightShadows.None;
    }

    private static void AddLightBulb(Transform parent, Material material, Vector3 position, float size)
    {
        var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(bulb.GetComponent<Collider>());
        bulb.transform.SetParent(parent, false);
        bulb.transform.localPosition = position;
        bulb.transform.localScale = Vector3.one * size;
        SetupRenderer(bulb.GetComponent<MeshRenderer>(), material);
    }

    private static void AddMesh(Transform parent, string name, Mesh mesh, Material material, Vector3 position, float scale)
    {
        var owner = new GameObject(name);
        owner.transform.SetParent(parent, false);
        owner.transform.localPosition = position;
        owner.transform.localScale = Vector3.one * scale;
        owner.AddComponent<MeshFilter>().sharedMesh = mesh;
        SetupRenderer(owner.AddComponent<MeshRenderer>(), material);
    }

    private static void SetupRenderer(MeshRenderer renderer, Material material)
    {
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private static Mesh CreateMesh(string name, List<Vector3> vertices, List<Color> colors, List<int> triangles)
    {
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        // Seen from anywhere inside, so never culled for being off to one side.
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * SkyRadius * 4f);
        return mesh;
    }

    // The surfaces made here start from a material the shelter itself draws with (else any opaque one the game has loaded),
    // recoloured with its textures taken off: one the game uses is sure to have its shader variant in the build whatever
    // the game's lighting state, where a material made from the bare URP Lit shader drew pink.
    private Material? LitTemplate()
    {
        var source = _shelter != null ? BestMaterial(ShelterMaterials(), lit: true) : null;
        source ??= BestMaterial(Resources.FindObjectsOfTypeAll<Material>(), lit: true);
        if (source != null) return new Material(source);

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        return shader != null ? new Material(shader) : null;
    }

    // Sprites/Default is built in and has no optional variants; failing that, a copy of one of the game's unlit materials.
    private static Material? UnlitTemplate()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader != null && shader.isSupported) return new Material(shader);

        var source = BestMaterial(Resources.FindObjectsOfTypeAll<Material>(), lit: false);
        return source != null ? new Material(source) : null;
    }

    private IEnumerable<Material> ShelterMaterials()
    {
        foreach (var renderer in _shelter!.GetComponentsInChildren<MeshRenderer>(true))
        {
            foreach (var material in renderer.sharedMaterials) yield return material;
        }
    }

    // The plainest drawable material of the kind: fewest shader keywords, URP's own shader first, opaque first.
    private static Material? BestMaterial(IEnumerable<Material> materials, bool lit)
    {
        Material? best = null;
        var bestScore = int.MaxValue;
        foreach (var material in materials)
        {
            if (!VisualClone.Drawable(material) || !material.HasProperty("_BaseColor")) continue;

            var shader = material.shader.name;
            var unlit = shader.Contains("Unlit");
            if (lit == unlit || (lit && (shader.Contains("Particles") || material.renderQueue >= 2450))) continue;

            var score = material.shaderKeywords.Length;
            if (!shader.StartsWith("Universal Render Pipeline/")) score += 20;
            if (material.renderQueue >= 2450) score += 10;
            if (score >= bestScore) continue;

            best = material;
            bestScore = score;
        }

        return best;
    }

    private Material LitMaterial(Color color, float smoothness)
    {
        var material = new Material(_lit!);
        SetTexture(material, "_BaseMap", Texture2D.whiteTexture);
        SetTexture(material, "_MainTex", Texture2D.whiteTexture);
        foreach (var name in new[] { "_BumpMap", "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask", "_ParallaxMap", "_EmissionMap" })
        {
            SetTexture(material, name, null);
        }

        SetTexture(material, "_MetallicGlossMap", Texture2D.blackTexture);
        SetTexture(material, "_SpecGlossMap", Texture2D.blackTexture);
        SetTexture(material, "_OcclusionMap", Texture2D.whiteTexture);
        SetColor(material, "_BaseColor", color);
        SetColor(material, "_Color", color);
        SetColor(material, "_EmissionColor", Color.black);
        SetColor(material, "_SpecColor", new Color(0.2f, 0.2f, 0.2f));
        SetFloat(material, "_Metallic", 0f);
        SetFloat(material, "_Smoothness", smoothness);
        SetFloat(material, "_GlossMapScale", smoothness);
        return material;
    }

    // Unlit and vertex coloured, drawn in this order after the opaque scene, from both sides.
    private Material UnlitMaterial(Color color, int queue)
    {
        var material = new Material(_unlit!) { renderQueue = queue };
        SetTexture(material, "_MainTex", Texture2D.whiteTexture);
        SetTexture(material, "_BaseMap", Texture2D.whiteTexture);
        SetColor(material, "_Color", color);
        SetColor(material, "_BaseColor", color);
        SetFloat(material, "_Cull", 0f);
        return material;
    }

    private static void SetTexture(Material material, string name, Texture? texture)
    {
        if (!material.HasProperty(name)) return;

        material.SetTexture(name, texture);
        material.SetTextureScale(name, Vector2.one);
        material.SetTextureOffset(name, Vector2.zero);
    }

    private static void SetColor(Material material, string name, Color color)
    {
        if (material.HasProperty(name)) material.SetColor(name, color);
    }

    private static void SetFloat(Material material, string name, float value)
    {
        if (material.HasProperty(name)) material.SetFloat(name, value);
    }

    // Last line against pink: anything whose shader isn't usable is turned off.
    private static int HideUndrawable(GameObject root)
    {
        var hidden = 0;
        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (VisualClone.Drawable(material)) continue;

                renderer.enabled = false;
                hidden++;
                break;
            }
        }

        return hidden;
    }

    private static string Describe(Material? material) =>
        material == null ? "nothing (left out)" : $"'{material.name}' ({material.shader.name}, {material.shaderKeywords.Length} keywords)";

    // Vertex colours go to the shader as they are, so in a linear-colour game they are converted here.
    private static Color Linear(Color color) => QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;

    // Whether the menu scene has a 3D scene of its own, which the shelter would then overlap.
    private static int CountSceneMeshes(Scene scene)
    {
        var count = 0;
        foreach (var renderer in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (renderer.gameObject.scene == scene) count++;
        }

        return count;
    }

    private static T? GetField<T>(object instance, string name) where T : class =>
        AccessTools.Field(instance.GetType(), name)?.GetValue(instance) as T;
}
