using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    /// <summary>
    /// Displays recorded data using renderer-only objects. Never loads a game level,
    /// instantiates a game prefab, or invokes a recorded method or network message.
    /// The caller must disconnect from a live session before creating this viewer.
    /// </summary>
    public sealed class ReplayViewer : IDisposable
    {
        private const int ReplayLayer = 31;
        private ReplaySession _session;
        private readonly ReplayRecordingTimeline _recording;
        private int _partIndex;
        private int _readPart = -1;
        private Task<ReplaySession>? _partRead;
        private CancellationTokenSource? _partCancellation;
        private int _requestedPart = -1;
        private double _requestedTime;
        private Exception? _playbackError;
        private readonly Dictionary<Camera, bool> _cameraStates = new Dictionary<Camera, bool>();
        private readonly Dictionary<Canvas, bool> _canvasStates = new Dictionary<Canvas, bool>();
        private readonly Dictionary<Renderer, bool> _rendererStates = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Behaviour, bool> _uiInputStates = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<Type, bool> _uiInputTypes = new Dictionary<Type, bool>();
        private readonly Dictionary<string, EntityVisual> _entities = new Dictionary<string, EntityVisual>();
        private readonly Dictionary<Color32, Material> _materials = new Dictionary<Color32, Material>();
        private readonly List<GameObject> _worldObjects = new List<GameObject>();
        private readonly List<Mesh> _worldMeshes = new List<Mesh>();
        private readonly List<InstancedGeometry> _instancedGeometry = new List<InstancedGeometry>();
        private readonly List<Renderer> _proceduralGrassRenderers = new List<Renderer>();
        private readonly List<ParticleSystem> _worldParticles = new List<ParticleSystem>();
        private readonly Dictionary<string, GeometrySnapshot> _renderGeometrySources = new Dictionary<string, GeometrySnapshot>();
        private readonly List<KeyValuePair<Renderer, string>> _interiorRenderers = new List<KeyValuePair<Renderer, string>>();
        private readonly List<Renderer> _exteriorRenderers = new List<Renderer>();
        private readonly Dictionary<string, List<KeyValuePair<Renderer, GeometrySnapshot>>> _naturalLods =
            new Dictionary<string, List<KeyValuePair<Renderer, GeometrySnapshot>>>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<Light, bool>> _worldLights = new List<KeyValuePair<Light, bool>>();
        private readonly Dictionary<string, Transform> _anchorRoots = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<string, Bounds> _interiorRooms = new Dictionary<string, Bounds>();
        private readonly Dictionary<string, WorldSnapshot> _combinedWorlds = new Dictionary<string, WorldSnapshot>(StringComparer.Ordinal);
        internal float InteriorRenderDistance { get; set; } = 105f;
        private ReplayAppearance? _appearance;
        private ReplayEnvironment? _environment;
        private RenderTexture? _displayTexture;
        private RawImage? _displayImage;
        private float _resolutionScale;
        private float _gamma;
        private readonly Action<float>? _saveResolution;
        private readonly Action<float>? _saveGamma;
        private int _screenWidth, _screenHeight;
        private readonly Dictionary<string, GameObject> _geometryObjects = new Dictionary<string, GameObject>();
        private readonly List<KeyValuePair<Behaviour, LocalFogSnapshot>> _localFogs = new List<KeyValuePair<Behaviour, LocalFogSnapshot>>();
        private readonly List<Texture3D> _worldFogMasks = new List<Texture3D>();
        private readonly Dictionary<string, GeometrySnapshot> _dynamicGeometry = new Dictionary<string, GeometrySnapshot>();
        private readonly HashSet<string> _completeRendererLists = new HashSet<string>();
        private bool _hasRendererPoseCapability;
        private readonly List<EntitySnapshot> _players = new List<EntitySnapshot>();
        private readonly CursorLockMode _savedCursorLock;
        private readonly bool _savedCursorVisible;
        private Scene _scene;
        private GameObject? _root;
        private Camera? _camera;
        private Light? _spectatorLight;
        private Light? _exteriorFill;
        private Mesh? _cube;
        private NativePlaybackHud? _hud;
        private Shader? _shader;
        private Shader? _unlitShader;
        private bool _worldHasLighting;
        private bool _indoor;
        private PropertyInfo? _eventSystemCurrentProperty;
        private Component? _savedEventSystem;
        private bool _eventSystemRemembered;
        private ReplayFrame _frame = new ReplayFrame();
        private int _worldIndex = -2;
        private string _selectedId = "";
        private string _sceneName = "No world geometry recorded";
        private bool _follow;
        private bool _showLabels;
        private bool _showSkeletons;
        private bool _disposed;
        private bool _looking;
        private float _yaw;
        private float _pitch = 12f;
        private float _speed = 1f;
        private float _cameraSpeed = 8f;
        private float _environmentScanElapsed;
        private float _interiorScanElapsed;
        private float _followClearanceElapsed;
        private float _followDistance = 2.5f;

        public bool IsPlaying { get; private set; } = true;
        public double Time { get; private set; }
        public double Duration => _recording.Duration;
        public double LocalTime => _recording.LocalTime(_partIndex, Time);
        public bool IsBuffering => _requestedPart >= 0;
        public int PartIndex => _partIndex;
        public Exception? Error => _playbackError;
        public bool CloseRequested { get; private set; }
        public float Speed
        {
            get => _speed;
            set => _speed = float.IsNaN(value) || float.IsInfinity(value) ? 1f : Mathf.Clamp(value, 0.1f, 8f);
        }

        public ReplayViewer(ReplaySession session, ReplayRecordingTimeline? recording = null, int partIndex = 0,
            float resolutionScale = 1f, float gamma = 1f, Action<float>? saveResolution = null, Action<float>? saveGamma = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _recording = recording ?? new ReplayRecordingTimeline(new[] { new ReplayRecordingPart { FilePath = "single.lcr", Duration = session.Duration } });
            _partIndex = Math.Max(0, Math.Min(_recording.Parts.Count - 1, partIndex));
            _hasRendererPoseCapability = session.Header.Capabilities.Contains("child-renderer-poses");
            _savedCursorLock = Cursor.lockState;
            _savedCursorVisible = Cursor.visible;
            _resolutionScale = Mathf.Clamp(resolutionScale, 0.25f, 1f);
            _gamma = Mathf.Clamp(gamma, 0.5f, 2f);
            _saveResolution = saveResolution;
            _saveGamma = saveGamma;
            try
            {
                _shader = FindShader();
                _unlitShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                if (_shader == null) throw new InvalidOperationException("No supported replay shader is available in this game version.");
                _scene = SceneManager.CreateScene("LCReplay_" + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.None));
                _root = NewObject("LCReplay (render only)", null);
                SceneManager.MoveGameObjectToScene(_root, _scene);
                _cube = MakeCube();
                SuspendOtherViews();
                var cameraObject = NewObject("Replay camera", _root.transform);
                _camera = cameraObject.AddComponent<Camera>();
                _camera.cullingMask = 1 << ReplayLayer;
                _camera.clearFlags = CameraClearFlags.Skybox;
                _camera.backgroundColor = new Color(0.025f, 0.035f, 0.05f);
                _camera.nearClipPlane = 0.05f;
                _camera.farClipPlane = 5000f;
                _camera.fieldOfView = 70f;
                _camera.rect = new Rect(0, 0, 1, 1);
                _camera.targetDisplay = 0;
                _camera.depth = 1000f;
                _camera.allowHDR = true;
                _camera.transform.position = new Vector3(0, 8, -12);
                _camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
                AddRenderPipelineCameraData(cameraObject);
                CreateDisplay();
                _spectatorLight = cameraObject.AddComponent<Light>();
                _spectatorLight.type = LightType.Point;
                _spectatorLight.range = 16f;
                _spectatorLight.shadows = LightShadows.Soft;
                _spectatorLight.shadowResolution = LightShadowResolution.Low;
                _spectatorLight.cullingMask = 1 << ReplayLayer;
                AddRenderPipelineLightData(cameraObject, 60f);
                _spectatorLight.intensity = 60f;
                _spectatorLight.enabled = false;
                var fillObject = NewObject("Replay exterior fill", cameraObject.transform);
                _exteriorFill = fillObject.AddComponent<Light>();
                _exteriorFill.type = LightType.Directional;
                _exteriorFill.shadows = LightShadows.None;
                _exteriorFill.cullingMask = 1 << ReplayLayer;
                AddRenderPipelineLightData(fillObject, 2.5f);
                _exteriorFill.intensity = 2.5f;
                SetLooking(false);
                Seek(_recording.Parts[_partIndex].Offset);
                if (_players.Count != 0) SelectPlayer(_players[0].Id, true);
                else
                {
                    var visible = _frame.Entities.FirstOrDefault(entity => entity.Active && IsVisualKind(entity.Kind));
                    if (visible != null) Focus(visible);
                }
                _hud = new NativePlaybackHud(session, TogglePause, SeekFromHud, CycleSpeed, () => _follow = !_follow,
                    FocusSelected, id => SelectPlayer(id, true), ToggleDiagnostics, () => CloseRequested = true,
                    _resolutionScale, _gamma, SetResolutionScale, SetGamma);
                SceneManager.MoveGameObjectToScene(_hud.Root, _scene);
                PrefetchNextPart();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (_disposed || _camera == null) return;
            if (Screen.width != _screenWidth || Screen.height != _screenHeight) ResizeDisplay();
            CompletePartRead();
            var delta = float.IsNaN(unscaledDeltaTime) || float.IsInfinity(unscaledDeltaTime) ? 0 : Mathf.Max(0f, unscaledDeltaTime);
            // A menu transition can introduce another camera or canvas while replay is open.
            _environmentScanElapsed += delta;
            if (_environmentScanElapsed >= 0.5f)
            {
                _environmentScanElapsed = 0;
                SuspendOtherViews();
            }
            if (IsPlaying && !IsBuffering)
            {
                Seek(Time + delta * Speed);
                if (Time >= Duration) IsPlaying = false;
            }
            if (ReplayInput.WasPressed("Space")) TogglePause();
            if (ReplayInput.WasPressed("F")) FocusSelected();
            if (ReplayInput.WasPressed("Tab") && _players.Count != 0)
            {
                var index = _players.FindIndex(p => p.Id == _selectedId);
                SelectPlayer(_players[(index + 1) % _players.Count].Id, true);
            }
            if (ReplayInput.WasPressed("LeftArrow")) Seek(Time - 5);
            if (ReplayInput.WasPressed("RightArrow")) Seek(Time + 5);
            SetLooking(Application.isFocused && ReplayInput.RightMousePressed);
            if (_looking)
            {
                var mouse = ReplayInput.MouseDelta;
                _yaw += mouse.x * 0.12f;
                _pitch = Mathf.Clamp(_pitch - mouse.y * 0.12f, -89f, 89f);
            }
            _camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var direction = Vector3.zero;
            if (_looking)
            {
                if (ReplayInput.IsPressed("W")) direction += _camera.transform.forward;
                if (ReplayInput.IsPressed("S")) direction -= _camera.transform.forward;
                if (ReplayInput.IsPressed("D")) direction += _camera.transform.right;
                if (ReplayInput.IsPressed("A")) direction -= _camera.transform.right;
                if (ReplayInput.IsPressed("E")) direction += Vector3.up;
                if (ReplayInput.IsPressed("Q")) direction -= Vector3.up;
            }
            if (direction.sqrMagnitude > 0)
            {
                _follow = false;
                var fast = ReplayInput.IsPressed("LeftShift") || ReplayInput.IsPressed("RightShift") ? 4f : 1f;
                _camera.transform.position += direction.normalized * (_cameraSpeed * fast * Mathf.Min(delta, 0.1f));
            }
            if (_follow)
            {
                var player = SelectedPlayer();
                if (player != null)
                {
                    var eye = ToVector(player.Position) + Vector3.up * 1.4f;
                    _followClearanceElapsed += delta;
                    if (_followClearanceElapsed >= 0.2f)
                    {
                        _followClearanceElapsed = 0;
                        _followDistance = Vector3.Distance(eye, ClearCameraPosition(eye, eye - _camera.transform.forward * 2.5f, player.Id));
                    }
                    _camera.transform.position = eye - _camera.transform.forward * _followDistance;
                }
            }
            _interiorScanElapsed += delta;
            if (_interiorScanElapsed >= 0.2f) { _interiorScanElapsed = 0; UpdateInteriorVisibility(); }
            UpdatePlayerBodyOcclusion();
            foreach (var effect in _worldParticles) if (effect) { var main = effect.main; main.simulationSpeed = IsPlaying ? Speed : 0f; }
            _hud?.Update(_session, _frame, _players, _selectedId, _sceneName, Time, IsPlaying, Speed, _follow, _showSkeletons, Duration, LocalTime, IsBuffering);
        }

        private void SeekFromHud(double time) => Seek(double.IsNegativeInfinity(time) ? Time - 5 : double.IsPositiveInfinity(time) ? Time + 5 : time);

        private void CycleSpeed()
        {
            var steps = new[] { 0.25f, 0.5f, 1f, 1.5f, 2f, 4f };
            Speed = steps.FirstOrDefault(value => value > Speed + 0.01f);
            if (Speed < 0.25f) Speed = steps[0];
        }

        private void ToggleDiagnostics()
        {
            _showLabels = _showSkeletons = !_showSkeletons;
            ApplyFrame();
        }

        public void Seek(double time)
        {
            if (_disposed) return;
            if (double.IsNaN(time) || double.IsInfinity(time)) return;
            var target = Math.Max(0, Math.Min(Duration, time));
            var targetPart = _recording.Locate(target);
            if (targetPart != _partIndex)
            {
                _requestedPart = targetPart;
                _requestedTime = target;
                Time = target;
                RequestPartRead(targetPart);
                CompletePartRead();
                return;
            }
            _requestedPart = -1;
            Time = target;
            _frame = ReplayTimeline.Sample(_session, LocalTime);
            ApplyFrame();
            var index = FindWorld(LocalTime);
            if (index != _worldIndex)
            {
                RebuildWorld(index < 0 ? null : AssembleWorld(index));
                _worldIndex = index;
            }
        }

        public void TogglePause()
        {
            if (_disposed) return;
            if (!IsPlaying && Time >= Duration) Seek(0);
            IsPlaying = !IsPlaying;
        }

        private void PrefetchNextPart()
        {
            if (_partIndex + 1 < _recording.Parts.Count) RequestPartRead(_partIndex + 1);
        }

        private void RequestPartRead(int part)
        {
            if (_partRead != null)
            {
                if (_readPart == part) return;
                _partCancellation?.Cancel();
                if (!_partRead.IsCompleted) return;
                _ = _partRead.Exception;
                _partRead = null;
            }
            _partCancellation?.Dispose();
            _partCancellation = new CancellationTokenSource();
            var token = _partCancellation.Token;
            var target = _recording.Parts[part];
            _readPart = part;
            _partRead = Task.Run(() => target.Window == null
                ? ReplayReader.Read(target.FilePath, cancellationToken: token)
                : ReplayReader.ReadWindow(target.Window, token), token);
        }

        private void CompletePartRead()
        {
            if (_partRead == null) { if (_requestedPart >= 0) RequestPartRead(_requestedPart); return; }
            if (!_partRead.IsCompleted) return;
            if (_requestedPart < 0) return; // Keep exactly one prefetched part.
            if (_readPart != _requestedPart) { RequestPartRead(_requestedPart); return; }
            var task = _partRead;
            _partRead = null;
            if (task.IsCanceled) { RequestPartRead(_requestedPart); return; }
            if (task.IsFaulted)
            {
                _playbackError = task.Exception?.GetBaseException();
                _requestedPart = -1;
                IsPlaying = false;
                return;
            }
            var selectedName = SelectedPlayer()?.Name;
            var part = _requestedPart;
            var target = _requestedTime;
            _requestedPart = -1;
            RebuildWorld(null);
            foreach (var entity in _entities.Values)
            {
                if (entity.SkeletonMesh != null) Object.Destroy(entity.SkeletonMesh);
                if (entity.Root != null) { entity.Root.SetActive(false); Object.Destroy(entity.Root); }
            }
            _entities.Clear();
            _session = task.Result;
            _combinedWorlds.Clear();
            _partIndex = part;
            _hasRendererPoseCapability = _session.Header.Capabilities.Contains("child-renderer-poses");
            _worldIndex = -2;
            Seek(target);
            var selected = _players.FirstOrDefault(player => player.Name == selectedName);
            if (selected != null) _selectedId = selected.Id;
            PrefetchNextPart();
        }

        public void DrawGui()
        {
            if (_disposed || _camera == null) return;
            DrawLabels();
        }
        private void DrawLabels()
        {
            if (!_showLabels || _camera == null) return;
            foreach (var entity in _frame.Entities)
            {
                if (!entity.Active || (entity.Kind != "player" && entity.Kind != "enemy")) continue;
                var screen = _camera.WorldToScreenPoint(ToVector(entity.Position) + Vector3.up * 2.1f);
                if (screen.z <= 0 || screen.x < 0 || screen.x > Screen.width || screen.y < 0 || screen.y > Screen.height) continue;
                GUI.color = EntityColor(entity.Kind);
                GUI.Label(new Rect(screen.x - 95, Screen.height - screen.y, 190, 40), SafeText(entity.Name));
            }
            GUI.color = Color.white;
        }

        private void ApplyFrame()
        {
            foreach (var anchor in _frame.Anchors)
                if (_anchorRoots.TryGetValue(anchor.Id, out var root)) SetTransform(root, anchor.Position, anchor.Rotation, anchor.Scale);
            foreach (var visual in _entities.Values) visual.Root.SetActive(false);
            _players.Clear();
            foreach (var entity in _frame.Entities)
            {
                var visual = GetEntity(entity.Id);
                visual.Root.SetActive(entity.Active && IsVisualKind(entity.Kind));
                SetTransform(visual.Root.transform, entity.Position, entity.Rotation, entity.Scale);
                ConfigureProxy(visual, entity.Kind);
                ConfigureSwarm(visual, entity);
                UpdateBones(visual, entity.Bones);
                // The game keeps unused player slots in the scene. Their hidden spawn
                // positions must not become the initial camera target or a selectable player.
                if (entity.Active && entity.Kind.Equals("player", StringComparison.OrdinalIgnoreCase)) _players.Add(entity);
            }
            if (_players.Count != 0 && !_players.Any(player => player.Id == _selectedId)) _selectedId = _players[0].Id;
            ApplyRendererPoses();
            UpdatePlayerBodyOcclusion();
        }

        private void ApplyRendererPoses()
        {
            _completeRendererLists.Clear();
            foreach (var entity in _frame.Entities)
            {
                if ((_hasRendererPoseCapability || entity.Renderers.Count != 0) && !entity.State.ContainsKey("$omittedRenderers"))
                    _completeRendererLists.Add(entity.Id);
            }
            // Start from the world snapshot on every sample. This keeps seeking
            // backwards deterministic and supports older files without renderer poses.
            foreach (var pair in _dynamicGeometry)
            {
                if (!_geometryObjects.TryGetValue(pair.Key, out var obj) || obj == null) continue;
                var baseline = pair.Value;
                SetTransform(obj.transform, baseline.Position, baseline.Rotation, baseline.Scale);
                // Missing IDs in a complete renderer list mean destroyed renderers.
                // Older or capped recordings retain their world-snapshot fallback.
                obj.SetActive(!_completeRendererLists.Contains(baseline.EntityId));
            }
            foreach (var entity in _frame.Entities)
            {
                foreach (var pose in entity.Renderers)
                {
                    if (!_dynamicGeometry.TryGetValue(pose.Id, out var geometry) || geometry.EntityId != entity.Id || !_geometryObjects.TryGetValue(pose.Id, out var obj) || obj == null) continue;
                    SetTransform(obj.transform, pose.Position, pose.Rotation, pose.Scale);
                    // This is local renderer visibility. An inactive/nonvisual owner
                    // remains hidden because its entity root is still inactive.
                    obj.SetActive(pose.Active);
                }
            }
        }

        private EntityVisual GetEntity(string id)
        {
            if (_entities.TryGetValue(id, out var existing)) return existing;
            var root = NewObject("Entity " + id, _root!.transform);
            var proxy = NewObject("Proxy", root.transform);
            AddMesh(proxy, _cube!, GetMaterial(EntityColor("unknown")));
            var result = new EntityVisual(root, proxy);
            _entities.Add(id, result);
            return result;
        }

        private void ConfigureProxy(EntityVisual visual, string kind)
        {
            visual.Proxy.SetActive(_showSkeletons && visual.GeometryCount == 0 && IsVisualKind(kind));
            if (kind == "player") visual.Root.name = "Recorded player body (render only)";
            if (visual.Kind == kind) return;
            visual.Kind = kind;
            visual.Proxy.GetComponent<MeshRenderer>().sharedMaterial = GetMaterial(EntityColor(kind));
            switch (kind.ToLowerInvariant())
            {
                case "player":
                    visual.Proxy.transform.localPosition = new Vector3(0, 0.9f, 0);
                    visual.Proxy.transform.localScale = new Vector3(0.6f, 1.8f, 0.6f);
                    break;
                case "enemy":
                    visual.Proxy.transform.localPosition = new Vector3(0, 0.65f, 0);
                    visual.Proxy.transform.localScale = new Vector3(0.85f, 1.3f, 0.85f);
                    break;
                case "door":
                    visual.Proxy.transform.localPosition = Vector3.up;
                    visual.Proxy.transform.localScale = new Vector3(1.2f, 2f, 0.16f);
                    break;
                default:
                    visual.Proxy.transform.localPosition = Vector3.zero;
                    visual.Proxy.transform.localScale = Vector3.one * 0.35f;
                    break;
            }
        }

        // Circuit bees and docile locusts are VFX Graph particles in the live
        // game, so they have no MeshRenderer that a world scan can archive.
        // Rebuild a bounded visual swarm around their recorded entity pose.
        private void ConfigureSwarm(EntityVisual visual, EntitySnapshot entity)
        {
            var isSwarm = entity.Kind == "enemy" &&
                (entity.Name.IndexOf("bee", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 entity.Name.IndexOf("locust", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!isSwarm) { if (visual.Swarm) visual.Swarm!.SetActive(false); return; }
            if (!visual.Swarm)
            {
                var effect = NewObject("Recorded swarm particles", visual.Root.transform);
                effect.transform.localPosition = Vector3.up * 0.55f;
                var particles = effect.AddComponent<ParticleSystem>();
                var main = particles.main;
                main.loop = true; main.duration = 2f; main.startLifetime = 1.5f;
                main.startSpeed = 0.55f; main.startSize = 0.065f;
                main.maxParticles = 160; main.simulationSpace = ParticleSystemSimulationSpace.Local;
                var emission = particles.emission; emission.rateOverTime = 95f;
                var shape = particles.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 1.05f;
                var noise = particles.noise; noise.enabled = true; noise.strength = 0.35f; noise.frequency = 1.2f;
                var renderer = effect.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.sharedMaterial = GetMaterial(entity.Name.IndexOf("locust", StringComparison.OrdinalIgnoreCase) >= 0 ?
                    new Color(0.68f, 0.24f, 0.13f) : new Color(0.89f, 0.69f, 0.17f));
                visual.Swarm = effect;
                visual.SwarmParticles = particles;
                particles.Play(true);
            }
            var dead = entity.State.TryGetValue("isEnemyDead", out var value) &&
                string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
            visual.Swarm!.SetActive(entity.Active && !dead);
            var playback = visual.SwarmParticles!.main;
            playback.simulationSpeed = IsPlaying ? Speed : 0f;
        }

        private void UpdateBones(EntityVisual visual, List<BonePose> bones)
        {
            if (bones != null)
                foreach (var bone in bones)
                {
                    if (string.IsNullOrEmpty(bone.Path) || bone.Path.Length > 4096 || bone.Path.Count(c => c == '/') > 127) continue;
                    SetTransform(GetBone(visual, bone.Path), bone.Position, bone.Rotation, bone.Scale);
                }
            if (!_showSkeletons || bones == null || bones.Count == 0)
            {
                if (visual.Skeleton != null) visual.Skeleton.SetActive(false);
                return;
            }
            if (visual.Skeleton == null)
            {
                visual.Skeleton = NewObject("Recorded skeleton", visual.Root.transform);
                visual.SkeletonMesh = new Mesh { name = "Replay skeleton" };
                AddMesh(visual.Skeleton, visual.SkeletonMesh, GetMaterial(new Color(1f, 0.93f, 0.45f)));
            }
            visual.Skeleton.SetActive(true);
            var vertices = new List<Vector3>(bones.Count * 2);
            foreach (var bone in bones)
            {
                if (string.IsNullOrEmpty(bone.Path) || !visual.Bones.TryGetValue(bone.Path, out var transform)) continue;
                if (transform.parent == null) continue;
                vertices.Add(visual.Root.transform.InverseTransformPoint(transform.parent.position));
                vertices.Add(visual.Root.transform.InverseTransformPoint(transform.position));
            }
            visual.SkeletonMesh!.Clear();
            visual.SkeletonMesh.SetVertices(vertices);
            var indices = new int[vertices.Count];
            for (var i = 0; i < indices.Length; i++) indices[i] = i;
            visual.SkeletonMesh.SetIndices(indices, MeshTopology.Lines, 0);
            visual.SkeletonMesh.RecalculateBounds();
        }

        private Transform GetBone(EntityVisual visual, string path)
        {
            if (visual.Bones.TryGetValue(path, out var bone)) return bone;
            var parent = visual.Root.transform;
            var prefix = "";
            foreach (var segment in path.Split('/'))
            {
                prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
                if (!visual.Bones.TryGetValue(prefix, out bone))
                {
                    bone = NewObject(segment, parent).transform;
                    visual.Bones[prefix] = bone;
                }
                parent = bone;
            }
            return parent;
        }

        private int FindWorld(double time)
        {
            var lo = 0;
            var hi = _session.Worlds.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_session.Worlds[mid].Time <= time) lo = mid + 1;
                else hi = mid;
            }
            return lo - 1;
        }

        private WorldSnapshot AssembleWorld(int index)
        {
            var selected = _session.Worlds[index].World!;
            if (selected.CaptureSetId.Length == 0) return selected;
            if (_combinedWorlds.TryGetValue(selected.CaptureSetId, out var cached)) return cached;
            var exteriors = new List<WorldSnapshot>();
            WorldSnapshot? interior = null;
            for (var i = 0; i <= index; i++)
            {
                var part = _session.Worlds[i].World;
                if (part == null || part.CaptureSetId != selected.CaptureSetId) continue;
                if (part.Layer == "exterior") exteriors.Add(part);
                else if (part.Layer == "interior") interior = part;
            }
            if (exteriors.Count == 0) return selected;
            if (exteriors.Count == 1 && interior == null) return selected;
            var parts = interior == null ? exteriors : exteriors.Concat(new[] { interior! }).ToList();
            var combined = new WorldSnapshot
            {
                Scene = selected.Scene, CaptureSetId = selected.CaptureSetId,
                Geometry = parts.SelectMany(part => part.Geometry).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Textures = parts.SelectMany(part => part.Textures).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Materials = parts.SelectMany(part => part.Materials).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Lights = parts.SelectMany(part => part.Lights).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                ParticleEmitters = parts.SelectMany(part => part.ParticleEmitters).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                LocalFogs = parts.SelectMany(part => part.LocalFogs).GroupBy(item => item.Id).Select(group => group.Last()).ToList(),
                Rooms = interior?.Rooms ?? new List<RoomSnapshot>(),
                Environment = exteriors.Select(part => part.Environment).FirstOrDefault(environment => environment != null)
            };
            if (interior != null) _combinedWorlds[selected.CaptureSetId] = combined;
            return combined;
        }

        private void CreateDisplay()
        {
            var display = new GameObject("LCReplay.Display", typeof(RectTransform), typeof(Canvas));
            display.hideFlags = HideFlags.DontSave;
            display.layer = 5;
            display.transform.SetParent(_root!.transform, false);
            var canvas = display.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31800;
            var imageObject = new GameObject("Low resolution replay image", typeof(RectTransform), typeof(RawImage));
            imageObject.hideFlags = HideFlags.DontSave;
            imageObject.layer = 5;
            imageObject.transform.SetParent(display.transform, false);
            var rect = (RectTransform)imageObject.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            _displayImage = imageObject.GetComponent<RawImage>();
            _displayImage.color = Color.white;
            _displayImage.raycastTarget = false;
            ResizeDisplay();
        }

        private void ResizeDisplay()
        {
            if (_camera == null || _displayImage == null || Screen.width < 1 || Screen.height < 1) return;
            _screenWidth = Screen.width; _screenHeight = Screen.height;
            var width = Math.Max(64, Mathf.RoundToInt(_screenWidth * _resolutionScale));
            var height = Math.Max(64, Mathf.RoundToInt(_screenHeight * _resolutionScale));
            if (_displayTexture != null && _displayTexture.width == width && _displayTexture.height == height) return;
            var replacement = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "LCReplay scene resolution", filterMode = FilterMode.Point, antiAliasing = 1, useMipMap = false, hideFlags = HideFlags.DontSave };
            replacement.Create();
            _camera.targetTexture = replacement;
            _displayImage.texture = replacement;
            if (_displayTexture != null) { _displayTexture.Release(); Object.Destroy(_displayTexture); }
            _displayTexture = replacement;
        }

        private void SetResolutionScale(float value)
        {
            _resolutionScale = Mathf.Clamp(value, 0.25f, 1f);
            ResizeDisplay();
            _saveResolution?.Invoke(_resolutionScale);
        }

        private void SetGamma(float value)
        {
            _gamma = Mathf.Clamp(value, 0.5f, 2f);
            _environment?.SetGamma(_gamma);
            _saveGamma?.Invoke(_gamma);
        }

        private void RebuildWorld(WorldSnapshot? world)
        {
            _environment?.Dispose(); _environment = null;
            foreach (var obj in _worldObjects)
            {
                if (obj != null) { obj.SetActive(false); Object.Destroy(obj); }
            }
            foreach (var mesh in _worldMeshes) if (mesh != null) Object.Destroy(mesh);
            foreach (var mask in _worldFogMasks) if (mask) Object.Destroy(mask);
            _worldObjects.Clear();
            _worldMeshes.Clear();
            _worldFogMasks.Clear();
            _instancedGeometry.Clear();
            _proceduralGrassRenderers.Clear();
            _worldParticles.Clear();
            _localFogs.Clear();
            _geometryObjects.Clear();
            _dynamicGeometry.Clear();
            _renderGeometrySources.Clear();
            _interiorRenderers.Clear();
            _exteriorRenderers.Clear();
            _naturalLods.Clear();
            _worldLights.Clear();
            _anchorRoots.Clear();
            _interiorRooms.Clear();
            _appearance?.Dispose(); _appearance = null;
            _worldHasLighting = world != null && world.Lights.Count != 0;
            foreach (var visual in _entities.Values) { visual.GeometryCount = 0; visual.Proxy.SetActive(_showSkeletons && IsVisualKind(visual.Kind)); }
            _sceneName = world?.Scene ?? "No world geometry recorded";
            if (world == null)
            {
                if (_spectatorLight) _spectatorLight!.enabled = false;
                if (_exteriorFill) _exteriorFill!.enabled = false;
                return;
            }
            _environment = new ReplayEnvironment(world, _root!.transform, ReplayLayer);
            _environment.SetGamma(_gamma);
            _appearance = new ReplayAppearance(world, world.Lights.Count == 0 && _unlitShader != null && _unlitShader.isSupported ? _unlitShader : _shader!);
            var representedRooms = new HashSet<string>(world.Geometry.Where(geometry => geometry.IsInterior &&
                !geometry.IsBoundsProxy && geometry.RoomId.Length != 0).Select(geometry => geometry.RoomId), StringComparer.Ordinal);
            foreach (var room in world.Rooms)
                if (representedRooms.Contains(room.Id))
                    _interiorRooms[room.Id] = new Bounds(ToVector(room.Center), ToVector(room.Size));
            foreach (var anchor in _frame.Anchors)
            {
                var root = NewObject("Moving environment " + anchor.Id, _root!.transform);
                _worldObjects.Add(root);
                _anchorRoots[anchor.Id] = root.transform;
                SetTransform(root.transform, anchor.Position, anchor.Rotation, anchor.Scale);
            }
            var debugMaterials = new HashSet<string>(world.Materials.Where(material => RenderVisibilityPolicy.IsDebugMaterial(material.Name)).Select(material => material.Id));
            var meshes = new Dictionary<string, Mesh>();
            foreach (var geometry in world.Geometry)
                _renderGeometrySources[geometry.Id] = geometry.MeshSourceId.Length != 0 && _renderGeometrySources.TryGetValue(geometry.MeshSourceId, out var source) ? source : geometry;
            foreach (var geometry in world.Geometry)
            {
                // Bounds describe unavailable geometry; filled bounds would invent opaque walls and fill the camera.
                if (geometry.IsBoundsProxy || RenderVisibilityPolicy.IsDebugGeometry(geometry, debugMaterials)) continue;
                if (geometry.Instances.Length != 0)
                {
                    var instanceMesh = MakeGeometryMesh(geometry);
                    if (instanceMesh == null) continue;
                    _worldMeshes.Add(instanceMesh);
                    var instanceMaterials = _appearance.Resolve(geometry, GetMaterial(GeometryColor(geometry)));
                    foreach (var material in instanceMaterials) material.enableInstancing = true;
                    var matrices = new Matrix4x4[geometry.Instances.Length / 16];
                    for (var index = 0; index < matrices.Length; index++)
                        for (var cell = 0; cell < 16; cell++) matrices[index][cell] = geometry.Instances[index * 16 + cell];
                    _instancedGeometry.Add(new InstancedGeometry(instanceMesh, instanceMaterials, matrices));
                    BuildProceduralGrass(instanceMesh, instanceMaterials, matrices);
                    continue;
                }
                var parent = _root!.transform;
                EntityVisual? owner = null;
                if (!string.IsNullOrEmpty(geometry.EntityId))
                {
                    owner = GetEntity(geometry.EntityId);
                    parent = owner.Root.transform;
                }
                else if (geometry.AnchorId.Length != 0 && _anchorRoots.TryGetValue(geometry.AnchorId, out var anchor)) parent = anchor;
                var obj = NewObject("World " + geometry.Name, parent);
                _worldObjects.Add(obj);
                _geometryObjects[geometry.Id] = obj;
                if (owner != null) _dynamicGeometry[geometry.Id] = geometry;
                SetTransform(obj.transform, geometry.Position, geometry.Rotation, geometry.Scale);
                var data = _renderGeometrySources[geometry.Id];
                if (!meshes.TryGetValue(data.Id, out var mesh))
                {
                    mesh = MakeGeometryMesh(data);
                    if (mesh != null) { meshes[data.Id] = mesh; _worldMeshes.Add(mesh); }
                }
                if (mesh == null)
                {
                    var bounds = NewObject("Bounds proxy", obj.transform);
                    bounds.SetActive(_showSkeletons);
                    bounds.transform.localPosition = ToVector(geometry.BoundsCenter);
                    var size = ToVector(geometry.BoundsSize);
                    bounds.transform.localScale = new Vector3(Mathf.Max(0.02f, Math.Abs(size.x)), Mathf.Max(0.02f, Math.Abs(size.y)), Mathf.Max(0.02f, Math.Abs(size.z)));
                    AddMesh(bounds, _cube!, GetMaterial(GeometryColor(geometry)));
                }
                else
                {
                    var materials = _appearance.Resolve(geometry, GetMaterial(GeometryColor(geometry)), data);
                    if (owner != null && geometry.BonePaths.Count > 0 && geometry.BoneWeights.Length == mesh.vertexCount * 4)
                        AddSkin(obj, mesh, materials, geometry, owner);
                    else
                    {
                        AddMesh(obj, mesh, materials[0]);
                        obj.GetComponent<MeshRenderer>().sharedMaterials = materials;
                    }
                }
                if (owner == null && geometry.LodGroupId.Length != 0 && obj.GetComponent<Renderer>() is Renderer lodRenderer)
                {
                    if (!_naturalLods.TryGetValue(geometry.LodGroupId, out var members))
                        _naturalLods[geometry.LodGroupId] = members = new List<KeyValuePair<Renderer, GeometrySnapshot>>();
                    members.Add(new KeyValuePair<Renderer, GeometrySnapshot>(lodRenderer, geometry));
                }
                if (geometry.IsInterior)
                {
                    var renderer = obj.GetComponent<Renderer>();
                    if (renderer)
                    {
                        _interiorRenderers.Add(new KeyValuePair<Renderer, string>(renderer, geometry.RoomId));
                        if (geometry.RoomId.Length != 0 && world.Rooms.Count == 0)
                        {
                            if (_interiorRooms.TryGetValue(geometry.RoomId, out var room)) { room.Encapsulate(renderer.bounds); _interiorRooms[geometry.RoomId] = room; }
                            else _interiorRooms[geometry.RoomId] = renderer.bounds;
                        }
                    }
                }
                else if (owner == null)
                {
                    var renderer = obj.GetComponent<Renderer>();
                    if (renderer) _exteriorRenderers.Add(renderer);
                }
                if (owner != null) { owner.GeometryCount++; owner.Proxy.SetActive(false); }
            }
            var exteriorShadows = 0;
            var interiorShadows = 0;
            foreach (var snapshot in world.Lights)
            {
                var parent = snapshot.AnchorId.Length != 0 && _anchorRoots.TryGetValue(snapshot.AnchorId, out var anchor) ? anchor : _root!.transform;
                var obj = NewObject("Recorded light " + snapshot.Id, parent);
                _worldObjects.Add(obj);
                SetTransform(obj.transform, snapshot.Position, snapshot.Rotation, Vec3.One);
                var light = obj.AddComponent<Light>();
                light.type = snapshot.Type == "Directional" ? LightType.Directional : snapshot.Type == "Spot" ? LightType.Spot : LightType.Point;
                light.color = new Color(snapshot.Color[0], snapshot.Color[1], snapshot.Color[2], snapshot.Color[3]);
                light.range = snapshot.Range;
                light.spotAngle = snapshot.SpotAngle;
                var shadowCount = snapshot.IsInterior ? interiorShadows : exteriorShadows;
                var castShadow = snapshot.Shadows && shadowCount < (snapshot.IsInterior ? 4 : 2);
                light.shadows = castShadow ? LightShadows.Soft : LightShadows.None;
                if (castShadow) { if (snapshot.IsInterior) interiorShadows++; else exteriorShadows++; }
                light.shadowResolution = LightShadowResolution.Low;
                light.cullingMask = 1 << ReplayLayer;
                AddRenderPipelineLightData(obj, snapshot.Intensity);
                light.intensity = snapshot.Intensity;
                _worldLights.Add(new KeyValuePair<Light, bool>(light, snapshot.IsInterior));
            }
            var localFogType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.LocalVolumetricFog");
            if (localFogType != null && typeof(Behaviour).IsAssignableFrom(localFogType))
                foreach (var snapshot in world.LocalFogs)
                {
                    try
                    {
                        var obj = NewObject("Recorded local fog " + snapshot.Name, _root!.transform);
                        _worldObjects.Add(obj);
                        SetTransform(obj.transform, snapshot.Position, snapshot.Rotation, Vec3.One);
                        var fog = (Behaviour)obj.AddComponent(localFogType);
                        RestoreLocalFog(fog, snapshot);
                        _localFogs.Add(new KeyValuePair<Behaviour, LocalFogSnapshot>(fog, snapshot));
                    }
                    catch { /* Older HDRP versions may not expose local fog. */ }
                }
            foreach (var snapshot in world.ParticleEmitters)
            {
                var parent = snapshot.EntityId.Length != 0 ? GetEntity(snapshot.EntityId).Root.transform : _root!.transform;
                var obj = NewObject("Recorded particles " + snapshot.Name, parent);
                _worldObjects.Add(obj);
                SetTransform(obj.transform, snapshot.Position, snapshot.Rotation, Vec3.One);
                var effect = obj.AddComponent<ParticleSystem>();
                var main = effect.main;
                main.loop = true; main.duration = Math.Max(1f, snapshot.Lifetime);
                main.startLifetime = snapshot.Lifetime; main.startSpeed = snapshot.Speed;
                main.startSize = snapshot.Size; main.maxParticles = 512;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.startColor = new Color(snapshot.Color[0], snapshot.Color[1], snapshot.Color[2], snapshot.Color[3]);
                var emission = effect.emission; emission.rateOverTime = Mathf.Max(1f, snapshot.Rate);
                var shape = effect.shape; shape.enabled = snapshot.Radius > 0;
                if (shape.enabled) { shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = snapshot.Radius; }
                var renderer = obj.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.sharedMaterial = _appearance.Resolve(snapshot.MaterialId,
                    GetMaterial(new Color(snapshot.Color[0], snapshot.Color[1], snapshot.Color[2], snapshot.Color[3])));
                if (snapshot.IsInterior) _interiorRenderers.Add(new KeyValuePair<Renderer, string>(renderer, snapshot.RoomId));
                else if (snapshot.EntityId.Length == 0) _exteriorRenderers.Add(renderer);
                _worldParticles.Add(effect);
                effect.Play(true);
            }
            // Geometry can refer to entities absent from this particular frame.
            var present = new HashSet<string>(_frame.Entities.Where(entity => entity.Active && IsVisualKind(entity.Kind)).Select(entity => entity.Id));
            foreach (var pair in _entities) if (!present.Contains(pair.Key)) pair.Value.Root.SetActive(false);
            ApplyRendererPoses();
            UpdateInteriorVisibility();
            UpdatePlayerBodyOcclusion();
        }

        // Replay rooms follow the spectator camera. No live culler, collider or
        // gameplay component is enabled or moved, including while the camera crosses rooms.
        private void UpdateInteriorVisibility()
        {
            if (!_camera) return;
            var position = _camera!.transform.position;
            var distance = Mathf.Clamp(InteriorRenderDistance, 10f, 200f);
            var squaredDistance = distance * distance;
            var indoor = false;
            foreach (var room in _interiorRooms.Values)
            {
                var expanded = room;
                expanded.Expand(1f);
                if (expanded.Contains(position)) { indoor = true; break; }
            }
            _indoor = indoor;
            foreach (var renderer in _exteriorRenderers)
                if (renderer) renderer.forceRenderingOff = indoor;
            foreach (var renderer in _proceduralGrassRenderers)
                if (renderer) renderer.forceRenderingOff = indoor || renderer.bounds.SqrDistance(position) > 180f * 180f;
            foreach (var entry in _interiorRenderers)
            {
                var renderer = entry.Key;
                if (!renderer) continue;
                bool sameRoom = entry.Value.Length != 0 && _interiorRooms.TryGetValue(entry.Value, out var room) && room.Contains(position);
                renderer.forceRenderingOff = !indoor || (!sameRoom && renderer.bounds.SqrDistance(position) > squaredDistance);
            }
            foreach (var members in _naturalLods.Values)
            {
                if (members.Count == 0) continue;
                var reference = members[0].Value;
                var ground = members.Any(member => RenderVisibilityPolicy.IsGroundSurface(member.Value.Name));
                var switchDistance = Mathf.Clamp(Mathf.Max(100f, reference.LodSwitchDistance * 3f), 100f, 300f);
                var near = ground || (ToVector(reference.LodCenter) - position).sqrMagnitude <=
                    switchDistance * switchDistance;
                var desiredLevel = near ? 0 : 1;
                if (!members.Any(member => member.Value.LodLevel == desiredLevel)) desiredLevel = 1 - desiredLevel;
                foreach (var member in members)
                    if (member.Key) member.Key.forceRenderingOff |= member.Value.LodLevel != desiredLevel;
            }
            foreach (var entry in _worldLights)
            {
                var light = entry.Key;
                if (!light) continue;
                var near = !indoor || light.type == LightType.Directional ||
                    (light.transform.position - position).sqrMagnitude <= Mathf.Pow(Mathf.Min(light.range + 8f, 60f), 2f);
                light.enabled = entry.Value == indoor && near;
            }
            foreach (var entry in _localFogs)
            {
                var fog = entry.Key;
                if (!fog) continue;
                var snapshot = entry.Value;
                var inside = snapshot.IsInterior || _interiorRooms.Values.Any(room => room.Contains(fog.transform.position));
                var size = ToVector(snapshot.Size);
                var bounds = new Bounds(fog.transform.position, size);
                var near = bounds.SqrDistance(position) <= squaredDistance;
                fog.enabled = inside == indoor && near;
            }
            if (_spectatorLight) _spectatorLight!.enabled = _worldHasLighting && indoor;
            if (_exteriorFill) _exteriorFill!.enabled = _worldHasLighting && !indoor;
            _environment?.SetIndoor(indoor);
        }

        private void BuildProceduralGrass(Mesh source, Material[] materials, Matrix4x4[] matrices)
        {
            // Mesh renderers participate in both HDRP and explicit Camera.Render.
            // Group nearby instances so the spectator can cull distant patches.
            var cells = new Dictionary<Vector2Int, List<Matrix4x4>>();
            foreach (var matrix in matrices)
            {
                var translation = matrix.GetColumn(3);
                var cell = new Vector2Int(Mathf.FloorToInt(translation.x / 24f), Mathf.FloorToInt(translation.z / 24f));
                if (!cells.TryGetValue(cell, out var instances)) cells[cell] = instances = new List<Matrix4x4>();
                instances.Add(matrix);
            }
            foreach (var cell in cells.Values)
                for (var offset = 0; offset < cell.Count; offset += 256)
                {
                    var count = Math.Min(256, cell.Count - offset);
                    for (var submesh = 0; submesh < Math.Min(source.subMeshCount, materials.Length); submesh++)
                    {
                        var combine = new CombineInstance[count];
                        for (var index = 0; index < count; index++)
                            combine[index] = new CombineInstance { mesh = source, subMeshIndex = submesh, transform = cell[offset + index] };
                        var baked = new Mesh { name = "Replay procedural grass patch", indexFormat = IndexFormat.UInt32 };
                        try { baked.CombineMeshes(combine, true, true, false); }
                        catch { Object.Destroy(baked); continue; }
                        _worldMeshes.Add(baked);
                        var obj = NewObject("Recorded grass patch", _root!.transform);
                        _worldObjects.Add(obj);
                        AddMesh(obj, baked, materials[submesh]);
                        _proceduralGrassRenderers.Add(obj.GetComponent<MeshRenderer>());
                    }
                }
        }

        private Mesh? MakeGeometryMesh(GeometrySnapshot geometry)
        {
            var coordinates = geometry.Vertices;
            var triangles = geometry.Triangles;
            if (coordinates == null || triangles == null || coordinates.Length < 9 || coordinates.Length % 3 != 0 || triangles.Length < 3 || triangles.Length % 3 != 0) return null;
            var count = coordinates.Length / 3;
            for (var i = 0; i < triangles.Length; i++) if (triangles[i] < 0 || triangles[i] >= count) return null;
            var vertices = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var point = new Vector3(coordinates[i * 3], coordinates[i * 3 + 1], coordinates[i * 3 + 2]);
                if (!Finite(point)) return null;
                vertices[i] = point;
            }
            var mesh = new Mesh { name = "Replay geometry " + geometry.Id, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.vertices = vertices;
                if (geometry.SubmeshTriangles.Count > 0)
                {
                    mesh.subMeshCount = geometry.SubmeshTriangles.Count;
                    for (int i = 0; i < geometry.SubmeshTriangles.Count; i++) mesh.SetTriangles(geometry.SubmeshTriangles[i], i);
                }
                else mesh.triangles = triangles;
                if (geometry.Uvs.Length == count * 2)
                {
                    var uv = new Vector2[count];
                    for (int i = 0; i < count; i++) uv[i] = new Vector2(geometry.Uvs[i * 2], geometry.Uvs[i * 2 + 1]);
                    mesh.uv = uv;
                }
                if (geometry.Normals.Length == count * 3)
                {
                    var normals = new Vector3[count];
                    for (int i = 0; i < count; i++) normals[i] = new Vector3(geometry.Normals[i * 3], geometry.Normals[i * 3 + 1], geometry.Normals[i * 3 + 2]);
                    mesh.normals = normals;
                }
                else mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { Object.Destroy(mesh); throw; }
        }

        private void AddSkin(GameObject obj, Mesh mesh, Material[] materials, GeometrySnapshot geometry, EntityVisual owner)
        {
            var bindposes = new Matrix4x4[geometry.BonePaths.Count];
            var bones = new Transform[geometry.BonePaths.Count];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = geometry.BonePaths[i].Length == 0 ? owner.Root.transform : GetBone(owner, geometry.BonePaths[i]);
                for (int j = 0; j < 16; j++) bindposes[i][j] = geometry.BindPoses[i * 16 + j];
            }
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++)
            {
                int offset = i * 4;
                weights[i] = new BoneWeight { boneIndex0 = geometry.BoneIndices[offset], boneIndex1 = geometry.BoneIndices[offset + 1],
                    boneIndex2 = geometry.BoneIndices[offset + 2], boneIndex3 = geometry.BoneIndices[offset + 3],
                    weight0 = geometry.BoneWeights[offset], weight1 = geometry.BoneWeights[offset + 1],
                    weight2 = geometry.BoneWeights[offset + 2], weight3 = geometry.BoneWeights[offset + 3] };
            }
            mesh.bindposes = bindposes; mesh.boneWeights = weights;
            var renderer = obj.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh; renderer.sharedMaterials = materials; renderer.bones = bones;
            renderer.rootBone = owner.Root.transform; renderer.updateWhenOffscreen = true;
            renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes; renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }

        private void SuspendOtherViews()
        {
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera == null || camera == _camera || !camera.gameObject.scene.IsValid() || !camera.gameObject.scene.isLoaded) continue;
                if (!_cameraStates.ContainsKey(camera)) _cameraStates.Add(camera, camera.enabled);
                camera.enabled = false;
            }
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas == null || !canvas.gameObject.scene.IsValid() || !canvas.gameObject.scene.isLoaded || canvas.gameObject.scene == _scene) continue;
                if (!_canvasStates.ContainsKey(canvas)) _canvasStates.Add(canvas, canvas.enabled);
                canvas.enabled = false;
            }
            // Mods may already use layer 31. Hide those renderers so this camera
            // cannot accidentally include menu or other non-recorded geometry.
            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || renderer.gameObject.layer != ReplayLayer || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded || renderer.gameObject.scene == _scene) continue;
                if (!_rendererStates.ContainsKey(renderer)) _rendererStates.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
            // A hidden Canvas can still receive EventSystem submit/navigation.
            // Suspend both dispatchers and input modules, including systems added later.
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<Behaviour>())
            {
                if (behaviour == null || !behaviour.gameObject.scene.IsValid() || !behaviour.gameObject.scene.isLoaded || behaviour.gameObject.scene == _scene || !IsUiInput(behaviour.GetType())) continue;
                RememberCurrentEventSystem(behaviour.GetType());
                if (!_uiInputStates.ContainsKey(behaviour)) _uiInputStates.Add(behaviour, behaviour.enabled);
                behaviour.enabled = false;
            }
        }

        private bool IsUiInput(Type type)
        {
            if (_uiInputTypes.TryGetValue(type, out var result)) return result;
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.FullName != "UnityEngine.EventSystems.EventSystem" && current.FullName != "UnityEngine.EventSystems.BaseInputModule") continue;
                _uiInputTypes[type] = true;
                return true;
            }
            _uiInputTypes[type] = false;
            return false;
        }

        private void RememberCurrentEventSystem(Type type)
        {
            if (_eventSystemRemembered) return;
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.FullName != "UnityEngine.EventSystems.EventSystem") continue;
                try
                {
                    _eventSystemCurrentProperty = current.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    _savedEventSystem = _eventSystemCurrentProperty?.GetValue(null, null) as Component;
                    _eventSystemRemembered = true;
                }
                catch (Exception) { /* The enabled states still restore on older UI versions. */ }
                break;
            }
        }

        private void SetLooking(bool looking)
        {
            _looking = looking;
            Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !looking;
        }

        private void SelectPlayer(string id, bool focus)
        {
            _selectedId = id;
            if (focus) FocusSelected();
        }

        private EntitySnapshot? SelectedPlayer() => _players.Find(player => player.Id == _selectedId);
        private void FocusSelected() { var selected = SelectedPlayer(); if (selected != null) Focus(selected); }

        private void Focus(EntitySnapshot entity)
        {
            if (_camera == null) return;
            _pitch = 12;
            var eye = ToVector(entity.Position) + Vector3.up * 1.4f;
            var playerYaw = ToQuaternion(entity.Rotation).eulerAngles.y;
            var bestDistance = -1f;
            var bestPosition = eye;
            var bestYaw = playerYaw;
            foreach (var offset in new[] { 0f, 90f, -90f, 180f })
            {
                var yaw = playerYaw + offset;
                var rotation = Quaternion.Euler(_pitch, yaw, 0);
                var position = ClearCameraPosition(eye, eye - rotation * Vector3.forward * 2.5f, entity.Id);
                var distance = Vector3.Distance(eye, position);
                if (distance > bestDistance) { bestDistance = distance; bestPosition = position; bestYaw = yaw; }
                if (distance >= 2.25f) break;
            }
            _yaw = bestYaw;
            if (bestDistance < 1f && (!entity.State.TryGetValue("isInsideFactory", out var inside) ||
                !string.Equals(inside, "True", StringComparison.OrdinalIgnoreCase)))
            {
                // A player inside the narrow ship may have no safe third-person pose.
                // Frame the ship from above instead of starting inside its hull.
                var elevated = eye + Vector3.up * 7f - Quaternion.Euler(0, playerYaw, 0) * Vector3.forward * 14f;
                var look = Quaternion.LookRotation(eye + Vector3.up - elevated, Vector3.up);
                var angles = look.eulerAngles;
                _yaw = angles.y; _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
                _camera.transform.SetPositionAndRotation(elevated, look);
                _followDistance = Vector3.Distance(eye, elevated);
            }
            else
            {
                _camera.transform.SetPositionAndRotation(bestPosition, Quaternion.Euler(_pitch, _yaw, 0));
                _followDistance = Math.Max(0.25f, bestDistance);
            }
            UpdatePlayerBodyOcclusion();
        }

        private void UpdatePlayerBodyOcclusion()
        {
            if (_camera == null) return;
            var selected = SelectedPlayer();
            var hideSelected = selected != null &&
                (_camera.transform.position - (ToVector(selected.Position) + Vector3.up * 1.4f)).sqrMagnitude < 5.0625f;
            foreach (var entry in _dynamicGeometry)
            {
                if (!_entities.TryGetValue(entry.Value.EntityId, out var owner) || owner.Kind != "player" ||
                    !_geometryObjects.TryGetValue(entry.Key, out var obj) || !obj) continue;
                var renderer = obj.GetComponent<Renderer>();
                if (renderer) renderer.forceRenderingOff = hideSelected && entry.Value.EntityId == _selectedId;
            }
        }

        // Camera framing uses recorded triangles, not colliders or the live game's physics scene.
        private Vector3 ClearCameraPosition(Vector3 start, Vector3 desired, string ignoredEntity)
        {
            if (_worldIndex < 0 || _worldIndex >= _session.Worlds.Count) return desired;
            var world = _session.Worlds[_worldIndex].World;
            if (world == null) return desired;
            float nearest = 1;
            foreach (var geometry in world.Geometry)
            {
                if (geometry.IsBoundsProxy || geometry.EntityId == ignoredEntity || geometry.BonePaths.Count > 0 ||
                    !_geometryObjects.TryGetValue(geometry.Id, out var obj) || !obj || !obj.activeInHierarchy) continue;
                var a = obj.transform.InverseTransformPoint(start);
                var delta = obj.transform.InverseTransformPoint(desired) - a;
                if (delta.sqrMagnitude < 0.000001f) continue;
                var bounds = new Bounds(ToVector(geometry.BoundsCenter), ToVector(geometry.BoundsSize));
                if (bounds.size.sqrMagnitude > 0 && !bounds.IntersectRay(new Ray(a, delta.normalized))) continue;
                var data = _renderGeometrySources.TryGetValue(geometry.Id, out var source) ? source : geometry;
                var vertices = data.Vertices; var indices = data.Triangles;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    var p = RecordedVertex(vertices, indices[i]);
                    var e1 = RecordedVertex(vertices, indices[i + 1]) - p;
                    var e2 = RecordedVertex(vertices, indices[i + 2]) - p;
                    var h = Vector3.Cross(delta, e2); float determinant = Vector3.Dot(e1, h);
                    if (Math.Abs(determinant) < 0.0000001f) continue;
                    float inverse = 1 / determinant; var s = a - p;
                    float u = inverse * Vector3.Dot(s, h); if (u < 0 || u > 1) continue;
                    var q = Vector3.Cross(s, e1); float v = inverse * Vector3.Dot(delta, q); if (v < 0 || u + v > 1) continue;
                    float hit = inverse * Vector3.Dot(e2, q);
                    if (hit > 0.02f && hit < nearest) nearest = hit;
                }
            }
            float length = Vector3.Distance(start, desired);
            return start + (desired - start).normalized * Math.Max(0.25f, length * nearest - (nearest < 1 ? 0.15f : 0));
        }
        private static Vector3 RecordedVertex(float[] vertices, int index) =>
            new Vector3(vertices[index * 3], vertices[index * 3 + 1], vertices[index * 3 + 2]);

        private GameObject NewObject(string name, Transform? parent)
        {
            var obj = new GameObject(name) { layer = ReplayLayer, hideFlags = HideFlags.DontSave };
            if (parent != null) obj.transform.SetParent(parent, false);
            return obj;
        }

        private static void AddMesh(GameObject obj, Mesh mesh, Material material)
        {
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }

        private Material GetMaterial(Color color)
        {
            Color32 key = color;
            if (_materials.TryGetValue(key, out var existing)) return existing;
            var material = new Material(_shader!) { name = "Replay color", hideFlags = HideFlags.DontSave };
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_CullMode")) material.SetFloat("_CullMode", 0);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0);
            if (material.HasProperty("_DoubleSidedEnable")) material.SetFloat("_DoubleSidedEnable", 1);
            material.EnableKeyword("_DOUBLESIDED_ON");
            _materials.Add(key, material);
            return material;
        }

        private static Shader? FindShader()
        {
            foreach (var name in new[] { "HDRP/Lit", "Universal Render Pipeline/Lit", "Standard", "HDRP/Unlit", "Universal Render Pipeline/Unlit", "Unlit/Color" })
            {
                var shader = Shader.Find(name);
                if (shader != null && shader.isSupported) return shader;
            }
            return null;
        }

        private void RestoreLocalFog(Behaviour fog, LocalFogSnapshot snapshot)
        {
            var parameterField = fog.GetType().GetField("parameters", BindingFlags.Public | BindingFlags.Instance);
            var parameters = parameterField?.GetValue(fog);
            if (parameters == null) return;
            // Replayed HDRI faces are bounded 8-bit images; HDRP's volumetric
            // scattering consequently washes the recorded fog hue toward white.
            // Increase chroma around the recorded luminance without increasing
            // fog density or changing an originally neutral white volume.
            var sourceColor = new Color(snapshot.Albedo[0], snapshot.Albedo[1], snapshot.Albedo[2], 1f);
            var luminance = sourceColor.r * .2126f + sourceColor.g * .7152f + sourceColor.b * .0722f;
            const float fogChroma = 2.5f;
            SetFogField(parameters, "albedo", new Color(
                Mathf.Clamp01(luminance + (sourceColor.r - luminance) * fogChroma),
                Mathf.Clamp01(luminance + (sourceColor.g - luminance) * fogChroma),
                Mathf.Clamp01(luminance + (sourceColor.b - luminance) * fogChroma), 1f));
            SetFogField(parameters, "meanFreePath", snapshot.MeanFreePath);
            SetFogField(parameters, "anisotropy", snapshot.Anisotropy);
            SetFogField(parameters, "size", ToVector(snapshot.Size));
            SetFogField(parameters, "positiveFade", ToVector(snapshot.PositiveFade));
            SetFogField(parameters, "negativeFade", ToVector(snapshot.NegativeFade));
            SetFogField(parameters, "distanceFadeStart", snapshot.DistanceFadeStart);
            SetFogField(parameters, "distanceFadeEnd", snapshot.DistanceFadeEnd);
            SetFogField(parameters, "priority", snapshot.Priority);
            SetFogField(parameters, "blendingMode", snapshot.BlendingMode);
            SetFogField(parameters, "falloffMode", snapshot.FalloffMode);
            SetFogField(parameters, "invertFade", snapshot.InvertFade);
            SetFogField(parameters, "maskMode", snapshot.MaskMode);
            SetFogField(parameters, "textureTiling", ToVector(snapshot.TextureTiling));
            SetFogField(parameters, "textureScrollingSpeed", ToVector(snapshot.TextureScrollingSpeed));
            if (snapshot.MaskRgba.Length == snapshot.MaskWidth * snapshot.MaskHeight * snapshot.MaskDepth * 4 &&
                snapshot.MaskRgba.Length != 0)
            {
                Texture3D? mask = null;
                try
                {
                    mask = new Texture3D(snapshot.MaskWidth, snapshot.MaskHeight, snapshot.MaskDepth, TextureFormat.RGBA32, false, true)
                    { name = "Replay fog mask " + snapshot.Id, hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
                    var pixels = new Color32[snapshot.MaskRgba.Length / 4];
                    for (var index = 0; index < pixels.Length; index++)
                        pixels[index] = new Color32(snapshot.MaskRgba[index * 4], snapshot.MaskRgba[index * 4 + 1],
                            snapshot.MaskRgba[index * 4 + 2], snapshot.MaskRgba[index * 4 + 3]);
                    mask.SetPixels32(pixels, 0);
                    mask.Apply(false, true);
                    SetFogField(parameters, "volumeMask", mask);
                    _worldFogMasks.Add(mask);
                }
                catch { if (mask) Object.Destroy(mask); }
            }
            parameterField!.SetValue(fog, parameters);
        }

        private static void SetFogField(object parameters, string name, object value)
        {
            var field = parameters.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return;
            try { field.SetValue(parameters, field.FieldType.IsEnum ? Enum.ToObject(field.FieldType, value) : value); }
            catch { /* Optional fog field varies across HDRP versions. */ }
        }

        private static void AddRenderPipelineCameraData(GameObject camera)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData", false);
                if (type == null || !typeof(Component).IsAssignableFrom(type)) continue;
                var data = camera.AddComponent(type);
                SetOptionalMember(data, "volumeLayerMask", (LayerMask)(1 << ReplayLayer));
                SetOptionalMember(data, "probeLayerMask", (LayerMask)0);
                SetOptionalMember(data, "backgroundColorHDR", new Color(0.025f, 0.035f, 0.05f));
                SetOptionalEnum(data, "clearColorMode", "Sky");
                EnableAtmosphericRendering(data);
                break;
            }
        }

        private static void EnableAtmosphericRendering(Component data)
        {
            // The menu camera's HDRP frame settings need not include fog. A replay
            // camera owns its own overrides so it can render recorded atmospheric
            // fog and LocalVolumetricFog regardless of the menu scene defaults.
            try
            {
                var type = data.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var frameField = type.GetField("m_RenderingPathCustomFrameSettings", flags);
                var maskField = type.GetField("renderingPathCustomFrameSettingsOverrideMask", flags);
                if (frameField == null || maskField == null) return;
                var frame = frameField.GetValue(data);
                var mask = maskField.GetValue(data);
                if (frame == null || mask == null) return;
                var fieldType = frame.GetType().Assembly.GetType("UnityEngine.Rendering.HighDefinition.FrameSettingsField");
                var setEnabled = frame.GetType().GetMethod("SetEnabled", new[] { fieldType!, typeof(bool) });
                var bitsField = mask.GetType().GetField("mask");
                var bits = bitsField?.GetValue(mask);
                var indexer = bits?.GetType().GetProperty("Item", new[] { typeof(uint) });
                if (fieldType == null || setEnabled == null || bitsField == null || bits == null || indexer == null) return;
                foreach (var name in new[] { "AtmosphericScattering", "Volumetrics", "ReprojectionForVolumetrics" })
                {
                    var field = Enum.Parse(fieldType, name);
                    setEnabled.Invoke(frame, new[] { field, (object)true });
                    indexer.SetValue(bits, true, new object[] { Convert.ToUInt32(field) });
                }
                bitsField.SetValue(mask, bits);
                frameField.SetValue(data, frame);
                maskField.SetValue(data, mask);
                SetOptionalMember(data, "customRenderingSettings", true);
            }
            catch { /* Different HDRP versions retain their camera defaults. */ }
        }

        private static void AddRenderPipelineLightData(GameObject obj, float intensity)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData", false);
                if (type == null || !typeof(Component).IsAssignableFrom(type)) continue;
                try
                {
                    var data = obj.AddComponent(type);
                    SetOptionalMember(data, "intensity", intensity);
                }
                catch { /* Built-in and URP lights need no HDRP companion. */ }
                break;
            }
        }

        private static void SetOptionalMember(Component component, string name, object value)
        {
            try
            {
                var type = component.GetType();
                var field = type.GetField(name);
                if (field != null) field.SetValue(component, value);
                else type.GetProperty(name)?.SetValue(component, value, null);
            }
            catch (Exception) { /* Optional HDRP member may differ across game versions. */ }
        }

        private static void SetOptionalEnum(Component component, string name, string value)
        {
            try
            {
                var type = component.GetType();
                var field = type.GetField(name);
                if (field != null && field.FieldType.IsEnum) field.SetValue(component, Enum.Parse(field.FieldType, value));
                var property = type.GetProperty(name);
                if (property != null && property.PropertyType.IsEnum) property.SetValue(component, Enum.Parse(property.PropertyType, value), null);
            }
            catch (Exception) { /* Optional HDRP member may differ across game versions. */ }
        }

        private static Mesh MakeCube()
        {
            var mesh = new Mesh { name = "Replay unit box" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f),
                new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f)
            };
            mesh.triangles = new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Color GeometryColor(GeometrySnapshot geometry)
        {
            var values = geometry.Color;
            return values != null && values.Length >= 3 ? new Color(Mathf.Clamp01(values[0]), Mathf.Clamp01(values[1]), Mathf.Clamp01(values[2]), 1) : new Color(0.45f, 0.48f, 0.52f);
        }

        private static Color EntityColor(string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "player": return new Color(1f, 0.48f, 0.16f);
                case "enemy": return new Color(0.9f, 0.21f, 0.22f);
                case "item": return new Color(0.35f, 0.78f, 0.96f);
                case "door": return new Color(0.55f, 0.68f, 0.57f);
                default: return new Color(0.76f, 0.73f, 0.54f);
            }
        }

        private static bool IsVisualKind(string kind) => !kind.Equals("round", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("time", StringComparison.OrdinalIgnoreCase) && !kind.Equals("terminal", StringComparison.OrdinalIgnoreCase);

        private static void SetTransform(Transform transform, Vec3 position, Quat rotation, Vec3 scale)
        {
            var point = ToVector(position);
            var size = ToVector(scale);
            transform.localPosition = Finite(point) ? point : Vector3.zero;
            transform.localRotation = ToQuaternion(rotation);
            transform.localScale = Finite(size) ? size : Vector3.one;
        }

        private static Vector3 ToVector(Vec3 value) => new Vector3(value.X, value.Y, value.Z);
        private static Quaternion ToQuaternion(Quat value)
        {
            var square = value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W;
            if (float.IsNaN(square) || float.IsInfinity(square) || square < 0.000001f) return Quaternion.identity;
            return new Quaternion(value.X, value.Y, value.Z, value.W).normalized;
        }
        private static bool Finite(Vector3 value) => !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
        private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
        private static string SafeText(string value) => (value ?? "").Replace("<", "‹").Replace(">", "›");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsPlaying = false;
            _partCancellation?.Cancel();
            _partCancellation?.Dispose();
            _partCancellation = null;
            if (_partRead != null)
                _ = _partRead.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _partRead = null;
            try
            {
                _hud?.Dispose();
                _hud = null;
                _environment?.Dispose();
                _environment = null;
                _appearance?.Dispose();
                _appearance = null;
                if (_camera) _camera!.targetTexture = null;
                if (_displayImage) _displayImage!.texture = null;
                if (_displayTexture != null) { _displayTexture.Release(); Object.Destroy(_displayTexture); _displayTexture = null; }
                if (_root != null) { _root.SetActive(false); Object.Destroy(_root); }
                foreach (var mesh in _worldMeshes) if (mesh != null) Object.Destroy(mesh);
                foreach (var mask in _worldFogMasks) if (mask) Object.Destroy(mask);
                foreach (var visual in _entities.Values) if (visual.SkeletonMesh != null) Object.Destroy(visual.SkeletonMesh);
                if (_cube != null) Object.Destroy(_cube);
                foreach (var material in _materials.Values) if (material != null) Object.Destroy(material);
                if (_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
            }
            finally
            {
                foreach (var pair in _cameraStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _canvasStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _rendererStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _uiInputStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                if (_savedEventSystem != null && _eventSystemCurrentProperty?.CanWrite == true)
                {
                    try { _eventSystemCurrentProperty.SetValue(null, _savedEventSystem, null); }
                    catch (Exception) { /* Preserve the restored enabled systems if the active system changed. */ }
                }
                Cursor.lockState = _savedCursorLock;
                Cursor.visible = _savedCursorVisible;
                _cameraStates.Clear();
                _canvasStates.Clear();
                _rendererStates.Clear();
                _uiInputStates.Clear();
                _uiInputTypes.Clear();
                _entities.Clear();
                _materials.Clear();
                _worldMeshes.Clear();
                _worldFogMasks.Clear();
                _instancedGeometry.Clear();
                _worldParticles.Clear();
                _localFogs.Clear();
                _worldObjects.Clear();
                _geometryObjects.Clear();
                _dynamicGeometry.Clear();
                _completeRendererLists.Clear();
                _interiorRenderers.Clear();
                _interiorRooms.Clear();
                _renderGeometrySources.Clear();
                _combinedWorlds.Clear();
            }
        }

        private sealed class EntityVisual
        {
            internal readonly GameObject Root;
            internal readonly GameObject Proxy;
            internal readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();
            internal string Kind = "";
            internal int GeometryCount;
            internal GameObject? Skeleton;
            internal Mesh? SkeletonMesh;
            internal GameObject? Swarm;
            internal ParticleSystem? SwarmParticles;
            internal EntityVisual(GameObject root, GameObject proxy) { Root = root; Proxy = proxy; }
        }

        private sealed class InstancedGeometry
        {
            internal readonly Mesh Mesh;
            internal readonly Material[] Materials;
            internal readonly Matrix4x4[] Matrices;
            internal readonly Matrix4x4[] Scratch = new Matrix4x4[1023];
            internal InstancedGeometry(Mesh mesh, Material[] materials, Matrix4x4[] matrices)
            { Mesh = mesh; Materials = materials; Matrices = matrices; }
        }
    }
}
