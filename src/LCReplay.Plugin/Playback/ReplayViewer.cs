using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using LCReplay.Plugin.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    /// <summary>
    /// Displays recorded data using renderer-only objects. A matching built-in
    /// moon scene may be loaded as inert scenery; gameplay methods are not replayed.
    /// During connected playback the live session continues behind the viewer.
    /// </summary>
    public sealed class ReplayViewer : IDisposable
    {
        private const int ReplayLayer = 31;
        private ReplaySession _session;
        private readonly ReplayRecordingTimeline _recording;
        private readonly Dictionary<int, ReplaySession> _recentParts = new Dictionary<int, ReplaySession>();
        private int _partIndex;
        private int _readPart = -1;
        private Task<ReplaySession>? _partRead;
        private CancellationTokenSource? _partCancellation;
        private int _requestedPart = -1;
        private double _requestedTime;
        private Exception? _playbackError;
        private readonly Dictionary<Camera, bool> _cameraStates = new Dictionary<Camera, bool>();
        private readonly Dictionary<Canvas, bool> _canvasStates = new Dictionary<Canvas, bool>();
        private readonly Dictionary<AudioListener, bool> _listenerStates = new Dictionary<AudioListener, bool>();
        private readonly Dictionary<AudioSource, bool> _audioSourceStates = new Dictionary<AudioSource, bool>();
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
        private readonly Dictionary<string, Bounds[]> _additionalRoomVolumes = new Dictionary<string, Bounds[]>();
        internal float InteriorRenderDistance { get; set; } = 105f;
        private ReplayAppearance? _appearance;
        private SceneAssetReplay? _assetScene;
        private ReplayVisualState? _visualState;
        private ReplayEnvironment? _environment;
        private RenderTexture? _displayTexture;
        private RawImage? _displayImage;
        private float _resolutionScale;
        private float _gamma;
        private bool _disableInteriorCulling;
        private bool _mutePlayerAudio;
        private readonly Action<float>? _saveResolution;
        private readonly Action<float>? _saveGamma;
        private readonly Action<bool>? _saveDisableInteriorCulling;
        private readonly Action<bool>? _saveMutePlayerAudio;
        private Transform? _shipCabinAnchor;
        private Vector3 _shipCabinCenter;
        private int _screenWidth, _screenHeight;
        private readonly Dictionary<string, GameObject> _geometryObjects = new Dictionary<string, GameObject>();
        private readonly List<KeyValuePair<Behaviour, LocalFogSnapshot>> _localFogs = new List<KeyValuePair<Behaviour, LocalFogSnapshot>>();
        private readonly List<Texture3D> _worldFogMasks = new List<Texture3D>();
        private readonly Dictionary<string, GeometrySnapshot> _dynamicGeometry = new Dictionary<string, GeometrySnapshot>();
        private readonly Dictionary<string, GeometrySnapshot> _movingSceneGeometry = new Dictionary<string, GeometrySnapshot>();
        private readonly HashSet<string> _completeRendererLists = new HashSet<string>();
        private readonly Dictionary<string, RenderPose> _currentRendererPoses = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
        private readonly Dictionary<string, RenderPose> _currentSceneRendererPoses = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
        private readonly HashSet<string> _inactiveEntities = new HashSet<string>(StringComparer.Ordinal);
        private bool _hasRendererPoseCapability;
        private readonly List<EntitySnapshot> _players = new List<EntitySnapshot>();
        private readonly CursorLockMode _savedCursorLock;
        private readonly bool _savedCursorVisible;
        private Scene _scene;
        private GameObject? _root;
        private Camera? _camera;
        private AudioSource? _replayAudio;
        private AudioClip? _replayAudioClip;
        private ReplayEvent[] _spatialAudioBlocks = Array.Empty<ReplayEvent>();
        private readonly List<SpatialAudioVoice> _spatialVoices = new List<SpatialAudioVoice>();
        private int _spatialAudioCursor;
        private double _lastAudioTime = -1;
        private bool _spatialAudioPaused;
        private ParticleSystem? _burstParticles;
        private ReplayParticleAssets? _particleAssets;
        private readonly Dictionary<string, ParticleSystem> _emitterBursts = new Dictionary<string, ParticleSystem>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ParticlePose>> _emitterSamples = new Dictionary<string, List<ParticlePose>>(StringComparer.Ordinal);
        private readonly HashSet<string> _simulatedEmitters = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _activeEmitters = new HashSet<string>(StringComparer.Ordinal);
        private Material? _burstMaterial;
        private Material? _particleTemplate;
        private bool _particleTemplateSearched;
        private readonly Dictionary<int, Material> _particleMaterials = new Dictionary<int, Material>();
        private Mesh? _swarmMesh;
        private Texture2D? _burstTexture;
        private readonly ParticleSystem.Particle[] _burstBuffer = new ParticleSystem.Particle[256];
        private readonly Dictionary<string, LineRenderer> _frameLines = new Dictionary<string, LineRenderer>(StringComparer.Ordinal);
        private readonly HashSet<string> _inactiveLines = new HashSet<string>(StringComparer.Ordinal);
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
        private readonly ReplayTimelineSampler _sampler = new ReplayTimelineSampler();
        private WorldSnapshot? _displayWorld;
        private int _worldIndex = -2;
        private IEnumerator<float>? _worldBuild;
        private float _worldBuildProgress;
        private bool _initialPrefetchPending = true;
        private string _selectedId = "";
        private string _sceneName = "No world geometry recorded";
        private bool _follow;
        private bool _showLabels;
        private GUIStyle? _diagnosticLabelStyle;
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
        private double? _pendingHudSeek;
        private float _pendingHudSeekAt;

        public bool IsPlaying { get; private set; } = true;
        public double Time { get; private set; }
        public double Duration => _recording.Duration;
        public double LocalTime => _recording.LocalTime(_partIndex, Time);
        public bool IsBuffering => _requestedPart >= 0 || _worldBuild != null || _assetScene?.IsLoading == true;
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
            : this(session, recording, partIndex, resolutionScale, gamma, saveResolution, saveGamma, false, null, false, null) { }

        public ReplayViewer(ReplaySession session, ReplayRecordingTimeline? recording, int partIndex,
            float resolutionScale, float gamma, Action<float>? saveResolution, Action<float>? saveGamma,
            bool disableInteriorCulling, Action<bool>? saveDisableInteriorCulling,
            bool mutePlayerAudio, Action<bool>? saveMutePlayerAudio)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _recording = recording ?? new ReplayRecordingTimeline(new[] { new ReplayRecordingPart { FilePath = "single.lcr", Duration = session.Duration } });
            _partIndex = Math.Max(0, Math.Min(_recording.Parts.Count - 1, partIndex));
            _hasRendererPoseCapability = session.Header.Capabilities.Contains("child-renderer-poses");
            _savedCursorLock = Cursor.lockState;
            _savedCursorVisible = Cursor.visible;
            _resolutionScale = Mathf.Clamp(resolutionScale, 0.25f, 1f);
            _gamma = Mathf.Clamp(gamma, 0.5f, 2f);
            _disableInteriorCulling = disableInteriorCulling;
            _mutePlayerAudio = mutePlayerAudio;
            _saveResolution = saveResolution;
            _saveGamma = saveGamma;
            _saveDisableInteriorCulling = saveDisableInteriorCulling;
            _saveMutePlayerAudio = saveMutePlayerAudio;
            try
            {
                _shader = FindShader();
                _unlitShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                if (_shader == null) throw new InvalidOperationException("No supported replay shader is available in this game version.");
                _scene = SceneManager.CreateScene("LCReplay_" + Guid.NewGuid().ToString("N"), new CreateSceneParameters(LocalPhysicsMode.None));
                ReplayIsolation.Register(_scene);
                _root = NewObject("LCReplay (render only)", null);
                SceneManager.MoveGameObjectToScene(_root, _scene);
                _cube = MakeCube();
                SuspendOtherViews();
                var cameraObject = NewObject("Replay camera", _root.transform);
                _camera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
                _replayAudio = cameraObject.AddComponent<AudioSource>();
                _replayAudio.playOnAwake = false;
                _replayAudio.spatialBlend = 0f;
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
                _assetScene = new SceneAssetReplay(ReplayLayer, UpdateInteriorVisibility);
                CreateBurstRenderer();
                SuspendOtherViews();
                SetLooking(false);
                Seek(_recording.Parts[_partIndex].Offset);
                RebuildAudio();
                if (_players.Count != 0) SelectPlayer(_players[0].Id, true);
                else
                {
                    var visible = _frame.Entities.FirstOrDefault(entity => entity.Active && IsVisualKind(entity.Kind));
                    if (visible != null) Focus(visible);
                }
                _hud = new NativePlaybackHud(session, TogglePause, SeekFromHud, CycleSpeed, () => _follow = !_follow,
                    FocusSelected, id => SelectPlayer(id, true), ToggleDiagnostics, () => CloseRequested = true,
                    _resolutionScale, _gamma, SetResolutionScale, SetGamma,
                    _disableInteriorCulling, SetDisableInteriorCulling,
                    _mutePlayerAudio, SetMutePlayerAudio);
                SceneManager.MoveGameObjectToScene(_hud.Root, _scene);
                _hud.SetLoading(true, "Building replay scene", .85f);
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
            var hudSeekApplied = false;
            if (_pendingHudSeek.HasValue)
            {
                var target = _pendingHudSeek.Value;
                var targetPart = _recording.Locate(target);
                var sameWorld = targetPart == _partIndex && _worldBuild == null &&
                    FindWorld(_recording.LocalTime(targetPart, target)) == _worldIndex;
                if (sameWorld || UnityEngine.Time.realtimeSinceStartup - _pendingHudSeekAt >= .12f)
                {
                    _pendingHudSeek = null;
                    Seek(target);
                    hudSeekApplied = true;
                }
            }
            CompletePartRead();
            if (_worldBuild != null)
            {
                var watch = Stopwatch.StartNew();
                // Budget elapsed work, not object count. The old 24-object cap
                // stretched cheap mesh/texture setup across hundreds of frames.
                while (watch.Elapsed.TotalMilliseconds < 12)
                {
                    if (!_worldBuild.MoveNext())
                    { _worldBuild.Dispose(); _worldBuild = null; _worldBuildProgress = 1f; ApplyFrame(); break; }
                    _worldBuildProgress = Mathf.Clamp01(_worldBuild.Current);
                    if (_assetScene?.IsLoading == true) break;
                }
                _hud?.SetLoading(true, _assetScene?.IsLoading == true ? "Loading moon scenery" : "Building replay scene",
                    .85f + .13f * _worldBuildProgress);
                if (_worldBuild != null) return;
            }
            if (_assetScene?.IsLoading == true)
            { _hud?.SetLoading(true, "Loading moon scenery", .98f); return; }
            _hud?.SetLoading(false, "", 1f);
            if (_initialPrefetchPending) { _initialPrefetchPending = false; PrefetchNextPart(); }
            var delta = float.IsNaN(unscaledDeltaTime) || float.IsInfinity(unscaledDeltaTime) ? 0 : Mathf.Max(0f, unscaledDeltaTime);
            // A menu transition can introduce another camera or canvas while replay is open.
            _environmentScanElapsed += delta;
            if (_environmentScanElapsed >= 0.5f)
            {
                _environmentScanElapsed = 0;
                SuspendOtherViews();
            }
            if (IsPlaying && !IsBuffering && !hudSeekApplied && !_pendingHudSeek.HasValue)
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
            ApplyBurstParticles();
            ApplyLines();
            UpdatePlayerBodyOcclusion();
            foreach (var effect in _worldParticles) if (effect) { var main = effect.main; main.simulationSpeed = IsPlaying ? Speed : 0f; }
            SyncAudio();
            _hud?.Update(_session, _frame, _players, _selectedId, _sceneName, Time, IsPlaying, Speed, _follow, _showSkeletons, Duration, LocalTime, IsBuffering);
        }

        private void SeekFromHud(double time)
        {
            if (double.IsNegativeInfinity(time) || double.IsPositiveInfinity(time))
            { _pendingHudSeek = null; Seek(double.IsNegativeInfinity(time) ? Time - 5 : Time + 5); return; }
            if (double.IsNaN(time)) return;
            _pendingHudSeek = Math.Max(0, Math.Min(Duration, time));
            _pendingHudSeekAt = UnityEngine.Time.realtimeSinceStartup;
        }

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
                if (!_recentParts.ContainsKey(targetPart)) RequestPartRead(targetPart);
                CompletePartRead();
                return;
            }
            _requestedPart = -1;
            Time = target;
            _frame = _sampler.Sample(_session, LocalTime);
            var index = FindWorld(LocalTime);
            if (index != _worldIndex)
            {
                var world = index < 0 ? null : AssembleWorld(index);
                _worldBuild?.Dispose();
                _visualState?.Dispose();
                _visualState = null;
                _worldBuild = RebuildWorldSteps(world).GetEnumerator();
                _worldBuildProgress = 0;
                _worldIndex = index;
            }
            ApplyFrame();
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
            if (_requestedPart < 0) return; // Keep exactly one prefetched part.
            ReplaySession? ready;
            if (_recentParts.TryGetValue(_requestedPart, out var cached))
            {
                ready = cached;
                _recentParts.Remove(_requestedPart);
            }
            else
            {
                if (_partRead == null) { RequestPartRead(_requestedPart); return; }
                if (!_partRead.IsCompleted) return;
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
                ready = task.Result;
            }
            var selectedName = SelectedPlayer()?.Name;
            var part = _requestedPart;
            var target = _requestedTime;
            _requestedPart = -1;
            var previousWindow = _recording.Parts[_partIndex].Window;
            var targetWindow = _recording.Parts[part].Window;
            var retainWorld = _displayWorld != null && previousWindow != null && targetWindow != null &&
                ReferenceEquals(previousWindow.Index, targetWindow.Index) &&
                previousWindow.Index.WorldRevisionAt(previousWindow.Start + LocalTime) ==
                targetWindow.Index.WorldRevisionAt(targetWindow.Start + _recording.LocalTime(part, target));
            if (!retainWorld) { _visualState?.Dispose(); _visualState = null; }
            if (!retainWorld) foreach (var entity in _entities.Values)
            {
                if (entity.SkeletonMesh != null) Object.Destroy(entity.SkeletonMesh);
                if (entity.Root != null) { entity.Root.SetActive(false); Object.Destroy(entity.Root); }
            }
            if (!retainWorld) _entities.Clear();
            if (previousWindow != null)
            {
                _recentParts[_partIndex] = _session;
                if (_recentParts.Count > 1) _recentParts.Remove(_recentParts.Keys.First());
            }
            _session = ready;
            _partIndex = part;
            if (retainWorld && _displayWorld != null)
            {
                _visualState?.Dispose();
                _visualState = new ReplayVisualState(_displayWorld, _session.Events,
                    _geometryObjects, _anchorRoots, _assetScene, _root!.transform, ReplayLayer);
            }
            _hasRendererPoseCapability = _session.Header.Capabilities.Contains("child-renderer-poses");
            _worldIndex = retainWorld ? FindWorld(_recording.LocalTime(part, target)) : -2;
            Seek(target);
            RebuildAudio();
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
            if (_diagnosticLabelStyle == null)
            {
                _diagnosticLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                    clipping = TextClipping.Clip
                };
                _diagnosticLabelStyle.normal.textColor = NativeReplayUi.White;
            }
            var priorColor = GUI.color;
            foreach (var entity in _frame.Entities)
            {
                if (!entity.Active || (entity.Kind != "player" && entity.Kind != "enemy")) continue;
                var screen = _camera.WorldToScreenPoint(ToVector(entity.Position) + Vector3.up * 2.1f);
                if (screen.z <= 0 || screen.x < 0 || screen.x > Screen.width || screen.y < 0 || screen.y > Screen.height) continue;
                var rect = new Rect(screen.x - 95, Screen.height - screen.y, 190, 30);
                GUI.color = NativeReplayUi.Orange;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = NativeReplayUi.Black;
                GUI.DrawTexture(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), Texture2D.whiteTexture);
                GUI.color = priorColor;
                GUI.Label(rect, SafeText(entity.Name), _diagnosticLabelStyle);
            }
            GUI.color = priorColor;
        }

        private void ApplyFrame()
        {
            foreach (var anchor in _frame.Anchors)
                if (_anchorRoots.TryGetValue(anchor.Id, out var root)) SetTransform(root, anchor.Position, anchor.Rotation, anchor.Scale);
            _inactiveEntities.Clear();
            foreach (var id in _entities.Keys) _inactiveEntities.Add(id);
            _players.Clear();
            foreach (var entity in _frame.Entities)
            {
                var visual = GetEntity(entity.Id);
                _inactiveEntities.Remove(entity.Id);
                SetActiveIfChanged(visual.Root, entity.Active && IsVisualKind(entity.Kind));
                SetTransform(visual.Root.transform, entity.Position, entity.Rotation, entity.Scale);
                ConfigureProxy(visual, entity);
                ConfigureSwarm(visual, entity);
                UpdateBones(visual, entity.Bones);
                // The game keeps unused player slots in the scene. Their hidden spawn
                // positions must not become the initial camera target or a selectable player.
                if (entity.Active && entity.Kind.Equals("player", StringComparison.OrdinalIgnoreCase)) _players.Add(entity);
            }
            foreach (var id in _inactiveEntities)
                if (_entities.TryGetValue(id, out var visual)) SetActiveIfChanged(visual.Root, false);
            if (_players.Count != 0 && !_players.Any(player => player.Id == _selectedId)) _selectedId = _players[0].Id;
            if (_worldBuild == null)
            {
                ApplyRendererPoses();
                _visualState?.Sync(LocalTime);
            }
        }

        private void ApplyRendererPoses()
        {
            _completeRendererLists.Clear();
            _currentRendererPoses.Clear();
            _currentSceneRendererPoses.Clear();
            foreach (var entity in _frame.Entities)
            {
                if ((_hasRendererPoseCapability || entity.Renderers.Count != 0) && !entity.State.ContainsKey("$omittedRenderers"))
                    _completeRendererLists.Add(entity.Id);
                foreach (var pose in entity.Renderers)
                    if (_dynamicGeometry.TryGetValue(pose.Id, out var geometry) && geometry.EntityId == entity.Id)
                        _currentRendererPoses[pose.Id] = pose;
            }
            foreach (var pose in _frame.SceneRenderers)
                _currentSceneRendererPoses[pose.Id] = pose;
            // Apply each renderer's final state once. Resetting all to the snapshot
            // first toggled active geometry twice on every playback frame.
            foreach (var pair in _dynamicGeometry)
            {
                if (!_geometryObjects.TryGetValue(pair.Key, out var obj) || obj == null) continue;
                var baseline = pair.Value;
                if (_currentRendererPoses.TryGetValue(pair.Key, out var pose))
                {
                    SetTransform(obj.transform, pose.Position, pose.Rotation, pose.Scale);
                    SetActiveIfChanged(obj, pose.Active);
                }
                else
                {
                    SetTransform(obj.transform, baseline.Position, baseline.Rotation, baseline.Scale);
                    // Missing IDs in a complete list are destroyed renderers.
                    SetActiveIfChanged(obj, !_completeRendererLists.Contains(baseline.EntityId));
                }
            }
            foreach (var pair in _movingSceneGeometry)
            {
                if (!_geometryObjects.TryGetValue(pair.Key, out var obj) || !obj) continue;
                if (_currentSceneRendererPoses.TryGetValue(pair.Key, out var pose))
                { SetTransform(obj.transform, pose.Position, pose.Rotation, pose.Scale); SetActiveIfChanged(obj, pose.Active); }
                else
                { SetTransform(obj.transform, pair.Value.Position, pair.Value.Rotation, pair.Value.Scale); SetActiveIfChanged(obj, pair.Value.Active); }
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

        private void ConfigureProxy(EntityVisual visual, EntitySnapshot entity)
        {
            var kind = entity.Kind;
            visual.Proxy.SetActive(visual.GeometryCount == 0 && (kind == "hazard" || _showSkeletons && IsVisualKind(kind)));
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
                case "hazard":
                    var mine = entity.Name.IndexOf("mine", StringComparison.OrdinalIgnoreCase) >= 0;
                    visual.Proxy.transform.localPosition = Vector3.up * (mine ? .08f : .65f);
                    visual.Proxy.transform.localScale = mine ? new Vector3(.65f, .16f, .65f) : new Vector3(.5f, 1.3f, .5f);
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
                main.startSpeed = 0.55f; main.startSize = 0.11f;
                main.startColor = entity.Name.IndexOf("locust", StringComparison.OrdinalIgnoreCase) >= 0 ?
                    new Color(0.68f, 0.24f, 0.13f, 0.9f) : new Color(0.89f, 0.69f, 0.17f, 0.95f);
                main.maxParticles = 160; main.simulationSpace = ParticleSystemSimulationSpace.Local;
                var emission = particles.emission; emission.rateOverTime = 95f;
                var shape = particles.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 1.05f;
                var noise = particles.noise; noise.enabled = true; noise.strength = 0.35f; noise.frequency = 1.2f;
                var renderer = effect.GetComponent<ParticleSystemRenderer>();
                // The live Circuit Bees use VFX Graph, which cannot be serialized
                // as a Unity ParticleSystem. A tiny shaded body is a safer proxy
                // than a billboard when a game's particle shader ignores alpha.
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.mesh = _swarmMesh ? _swarmMesh : (_swarmMesh = MakeSwarmMesh());
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.sharedMaterial = GetMaterial(entity.Name.IndexOf("locust", StringComparison.OrdinalIgnoreCase) >= 0 ?
                    new Color(0.45f, 0.19f, 0.08f) : new Color(0.62f, 0.40f, 0.06f));
                visual.Swarm = effect;
                visual.SwarmParticles = particles;
                if (entity.Name.IndexOf("bee", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    visual.SwarmLightning = new LineRenderer[2];
                    for (var i = 0; i < visual.SwarmLightning.Length; i++)
                    {
                        var lineObject = NewObject("Recorded bee lightning", effect.transform);
                        var line = lineObject.AddComponent<LineRenderer>();
                        line.useWorldSpace = false; line.positionCount = 5;
                        line.widthMultiplier = 0.025f;
                        line.startColor = new Color(1f, 0.86f, 0.3f, 0.85f);
                        line.endColor = new Color(1f, 0.96f, 0.65f, 0.15f);
                        line.sharedMaterial = GetMaterial(new Color(1f, 0.8f, 0.28f));
                        line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
                        visual.SwarmLightning[i] = line;
                    }
                }
                particles.Play(true);
            }
            var dead = entity.State.TryGetValue("isEnemyDead", out var value) &&
                string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
            visual.Swarm!.SetActive(entity.Active && !dead);
            var playback = visual.SwarmParticles!.main;
            playback.simulationSpeed = IsPlaying ? Speed : 0f;
            if (visual.SwarmLightning != null)
                for (var index = 0; index < visual.SwarmLightning.Length; index++)
                {
                    var line = visual.SwarmLightning[index];
                    line.enabled = visual.Swarm.activeInHierarchy && ((int)(_frame.Time * 8) + index) % 3 == 0;
                    if (!line.enabled) continue;
                    var phase = (float)_frame.Time * 11f + index * 2.7f;
                    for (var vertex = 0; vertex < 5; vertex++)
                    {
                        var fraction = vertex / 4f;
                        line.SetPosition(vertex, new Vector3(Mathf.Sin(phase + vertex * 1.7f) * (0.12f + fraction * 0.8f),
                            Mathf.Sin(phase * 0.8f + vertex * 2f) * 0.23f,
                            Mathf.Cos(phase + vertex * 1.4f) * (0.12f + fraction * 0.8f)));
                    }
                }
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
            var source = exteriors.LastOrDefault(part => part.AssetScene.Length != 0) ?? exteriors.First();
            var combined = new WorldSnapshot
            {
                Scene = selected.Scene, CaptureSetId = selected.CaptureSetId,
                AssetScene = source.AssetScene, AssetGameVersion = source.AssetGameVersion,
                AssetBuildIndex = source.AssetBuildIndex,
                MapSeed = source.MapSeed, LevelId = source.LevelId,
                DungeonSeed = source.DungeonSeed, DungeonFlow = source.DungeonFlow,
                AssetRendererPaths = exteriors.SelectMany(part => part.AssetRendererPaths).Distinct(StringComparer.Ordinal).ToList(),
                AssetTerrainPaths = exteriors.SelectMany(part => part.AssetTerrainPaths).Distinct(StringComparer.Ordinal).ToList(),
                Geometry = parts.SelectMany(part => part.Geometry).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Textures = parts.SelectMany(part => part.Textures).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Materials = parts.SelectMany(part => part.Materials).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Lights = parts.SelectMany(part => part.Lights).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                ParticleEmitters = parts.SelectMany(part => part.ParticleEmitters).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                LocalFogs = parts.SelectMany(part => part.LocalFogs).GroupBy(item => item.Id).Select(group => group.Last()).ToList(),
                Rooms = interior?.Rooms ?? new List<RoomSnapshot>(),
                Environment = exteriors.Select(part => part.Environment).LastOrDefault(environment => environment != null)
            };
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

        private void SetDisableInteriorCulling(bool value)
        {
            _disableInteriorCulling = value;
            _saveDisableInteriorCulling?.Invoke(value);
            UpdateInteriorVisibility();
        }

        private void SetMutePlayerAudio(bool value)
        {
            _mutePlayerAudio = value;
            _saveMutePlayerAudio?.Invoke(value);
            foreach (var voice in _spatialVoices)
                if (voice.Player) voice.Source.mute = value;
        }

        private IEnumerable<float> RebuildWorldSteps(WorldSnapshot? world)
        {
            ClearSampledParticles();
            _visualState?.Dispose(); _visualState = null;
            _displayWorld = world;
            _environment?.Dispose(); _environment = null;
            for (var i = 0; i < _worldObjects.Count; i++)
            {
                var obj = _worldObjects[i];
                if (obj != null) { obj.SetActive(false); Object.Destroy(obj); }
                _worldObjects[i] = null!;
                if (i % 48 == 47) yield return .02f;
            }
            for (var i = 0; i < _worldMeshes.Count; i++)
            {
                var mesh = _worldMeshes[i];
                if (mesh != null) Object.Destroy(mesh);
                _worldMeshes[i] = null!;
                if (i % 48 == 47) yield return .035f;
            }
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
            _movingSceneGeometry.Clear();
            _renderGeometrySources.Clear();
            _interiorRenderers.Clear();
            _exteriorRenderers.Clear();
            _naturalLods.Clear();
            _worldLights.Clear();
            _anchorRoots.Clear();
            _interiorRooms.Clear();
            _additionalRoomVolumes.Clear();
            _shipCabinAnchor = null;
            _appearance?.Dispose(); _appearance = null;
            foreach (var material in _particleMaterials.Values) if (material) Object.Destroy(material);
            _particleMaterials.Clear();
            _worldHasLighting = world != null && world.Lights.Count != 0;
            foreach (var visual in _entities.Values) { visual.GeometryCount = 0; visual.Proxy.SetActive(_showSkeletons && IsVisualKind(visual.Kind)); }
            _sceneName = world?.Scene ?? "No world geometry recorded";
            if (world == null)
            {
                _assetScene?.SetWorld(new WorldSnapshot());
                if (_spectatorLight) _spectatorLight!.enabled = false;
                if (_exteriorFill) _exteriorFill!.enabled = false;
                yield break;
            }
            _environment = new ReplayEnvironment(world, _root!.transform, ReplayLayer);
            _assetScene?.SetWorld(world);
            // Native material and particle templates arrive with the installed moon.
            // Indexing resources before its asynchronous load completes would cache
            // false misses and recreate effects with fallback shaders for this world.
            while (_assetScene?.IsLoading == true) yield return .08f;
            _environment.SetGamma(_gamma);
            _appearance = new ReplayAppearance();
            foreach (var step in _appearance.BuildSteps(world,
                world.Lights.Count == 0 && _unlitShader != null && _unlitShader.isSupported ? _unlitShader : _shader!))
                yield return .08f + .37f * step;
            foreach (var room in world.Rooms)
            {
                _interiorRooms[room.Id] = new Bounds(ToVector(room.Center), ToVector(room.Size));
                if (room.AdditionalVolumes.Count > 0)
                    _additionalRoomVolumes[room.Id] = room.AdditionalVolumes.Select(volume =>
                        new Bounds(ToVector(volume.Center), ToVector(volume.Size))).ToArray();
            }
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
            var completedGeometry = 0;
            foreach (var geometry in world.Geometry)
            {
                completedGeometry++;
                if (geometry.AnchorId == "ship-elevator" && geometry.Name == "ShipInside" &&
                    _anchorRoots.TryGetValue(geometry.AnchorId, out var cabinAnchor))
                { _shipCabinAnchor = cabinAnchor; _shipCabinCenter = ToVector(geometry.Position); }
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
                    yield return .45f + .45f * completedGeometry / Math.Max(1, world.Geometry.Count);
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
                else if (geometry.IsMovingSceneRenderer) _movingSceneGeometry[geometry.Id] = geometry;
                SetTransform(obj.transform, geometry.Position, geometry.Rotation, geometry.Scale);
                if (!geometry.Active) obj.SetActive(false);
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
                yield return .45f + .45f * completedGeometry / Math.Max(1, world.Geometry.Count);
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
                light.useColorTemperature = snapshot.UseColorTemperature;
                if (snapshot.UseColorTemperature) light.colorTemperature = snapshot.ColorTemperature;
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
                _particleAssets ??= new ReplayParticleAssets(_root!.transform, ReplayLayer);
                var effect = _particleAssets.Create(parent, snapshot.Style, snapshot.Name, snapshot.MaterialId,
                    _appearance, _burstMaterial, false, out var nativeEffect);
                var obj = effect.gameObject;
                _worldObjects.Add(obj);
                SetTransform(obj.transform, snapshot.Position, snapshot.Rotation, snapshot.Style?.Scale ?? Vec3.One);
                var main = effect.main;
                if (!nativeEffect)
                {
                    main.loop = true; main.duration = Math.Max(1f, snapshot.Lifetime);
                    main.startLifetime = snapshot.Lifetime; main.startSpeed = snapshot.Speed;
                    main.startSize = snapshot.Size; main.maxParticles = 512;
                    main.simulationSpace = ParticleSystemSimulationSpace.Local;
                    main.startColor = new Color(snapshot.Color[0], snapshot.Color[1], snapshot.Color[2], snapshot.Color[3]);
                    var emission = effect.emission; emission.rateOverTime = snapshot.Rate;
                    var shape = effect.shape; shape.enabled = snapshot.Radius > 0;
                    if (shape.enabled) { shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = snapshot.Radius; }
                }
                var renderer = obj.GetComponent<ParticleSystemRenderer>();
                if (snapshot.IsInterior) _interiorRenderers.Add(new KeyValuePair<Renderer, string>(renderer, snapshot.RoomId));
                else if (snapshot.EntityId.Length == 0) _exteriorRenderers.Add(renderer);
                _worldParticles.Add(effect);
                effect.Play(true);
            }
            yield return .98f;
            // Geometry can refer to entities absent from this particular frame.
            var present = new HashSet<string>(_frame.Entities.Where(entity => entity.Active && IsVisualKind(entity.Kind)).Select(entity => entity.Id));
            foreach (var pair in _entities) if (!present.Contains(pair.Key)) pair.Value.Root.SetActive(false);
            ApplyRendererPoses();
            _visualState = new ReplayVisualState(world, _session.Events,
                _geometryObjects, _anchorRoots, _assetScene, _root!.transform, ReplayLayer);
            _visualState.Sync(LocalTime);
            UpdateInteriorVisibility();
            UpdatePlayerBodyOcclusion();
            yield return 1f;
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
            foreach (var room in _interiorRooms.Keys)
            {
                if (RoomContains(room, position, 1f)) { indoor = true; break; }
            }
            _indoor = indoor;
            var inShipCabin = false;
            if (_shipCabinAnchor)
            {
                var cabinOffset = _shipCabinAnchor!.InverseTransformPoint(position) - _shipCabinCenter;
                inShipCabin = Mathf.Abs(cabinOffset.x) < 8.5f && Mathf.Abs(cabinOffset.y) < 4f &&
                    Mathf.Abs(cabinOffset.z) < 5.5f;
            }
            foreach (var renderer in _exteriorRenderers)
                if (renderer) renderer.forceRenderingOff = indoor;
            _assetScene?.SetIndoor(indoor);
            foreach (var renderer in _proceduralGrassRenderers)
                if (renderer) renderer.forceRenderingOff = indoor || renderer.bounds.SqrDistance(position) > 180f * 180f;
            foreach (var entry in _interiorRenderers)
            {
                var renderer = entry.Key;
                if (!renderer) continue;
                bool sameRoom = entry.Value.Length != 0 && RoomContains(entry.Value, position, 1f);
                renderer.forceRenderingOff = !_disableInteriorCulling && (!indoor ||
                    !sameRoom && renderer.bounds.SqrDistance(position) > squaredDistance);
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
                var lightDistance = Mathf.Max(distance, light.range + 12f);
                var near = !indoor || light.type == LightType.Directional ||
                    (light.transform.position - position).sqrMagnitude <= lightDistance * lightDistance;
                light.enabled = entry.Value ? (_disableInteriorCulling || indoor && near) : !indoor;
            }
            foreach (var entry in _localFogs)
            {
                var fog = entry.Key;
                if (!fog) continue;
                var snapshot = entry.Value;
                var inside = snapshot.IsInterior || _interiorRooms.Keys.Any(room => RoomContains(room, fog.transform.position));
                var size = ToVector(snapshot.Size);
                var bounds = new Bounds(fog.transform.position, size);
                var near = bounds.SqrDistance(position) <= squaredDistance;
                fog.enabled = inside ? (_disableInteriorCulling || indoor && near) : !indoor && near;
            }
            if (_spectatorLight) _spectatorLight!.enabled = _worldHasLighting && (indoor || inShipCabin);
            if (_exteriorFill) _exteriorFill!.enabled = _worldHasLighting && !indoor && !inShipCabin;
            _environment?.SetIndoor(indoor || inShipCabin);
        }

        private bool RoomContains(string id, Vector3 position, float padding = 0)
        {
            if (_interiorRooms.TryGetValue(id, out var bounds))
            {
                bounds.Expand(padding);
                if (bounds.Contains(position)) return true;
            }
            if (_additionalRoomVolumes.TryGetValue(id, out var volumes))
                foreach (var volume in volumes)
                {
                    var expanded = volume;
                    expanded.Expand(padding);
                    if (expanded.Contains(position)) return true;
                }
            return false;
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
                if (geometry.Uvs.Length == count * 2) mesh.RecalculateTangents();
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
            renderer.rootBone = geometry.RootBonePath.Length == 0 ? owner.Root.transform : GetBone(owner, geometry.RootBonePath);
            renderer.updateWhenOffscreen = true;
            renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes; renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }

        private void RebuildAudio()
        {
            ClearSpatialAudio();
            if (!_replayAudio) return;
            _replayAudio!.Stop();
            _replayAudio.clip = null;
            if (_replayAudioClip) Object.Destroy(_replayAudioClip);
            _replayAudioClip = null;
            var blocks = _session.Events.Where(value => value.Category == "audio" && value.Name == "source-block").ToArray();
            if (blocks.Length == 0) return;
            if (blocks.Any(block => block.Data.ContainsKey("spatial")))
            {
                _spatialAudioBlocks = blocks.Where(block => block.Data.ContainsKey("spatial"))
                    .OrderBy(block => block.Time).ToArray();
                return;
            }
            // New single-file recordings are opened in bounded playback windows.
            // The cap also protects direct/legacy reads from one enormous PCM allocation.
            var seconds = Math.Min(600, Math.Max(0.1, _session.Duration));
            var mix = new float[Math.Max(1, (int)Math.Ceiling(seconds * ReplayAudioCodec.SampleRate))];
            foreach (var block in blocks)
            {
                try
                {
                    if (!block.Data.TryGetValue("rate", out var rate) || rate != ReplayAudioCodec.SampleRate.ToString() ||
                        !block.Data.TryGetValue("samples", out var countText) ||
                        !int.TryParse(countText, out var count) || count < 1 || count > ReplayAudioCodec.MaxSamplesPerBlock ||
                        !block.Data.TryGetValue("adpcm", out var encoded)) continue;
                    var samples = ReplayAudioCodec.Decode(Convert.FromBase64String(encoded), count);
                    var start = (int)Math.Round(block.Time * ReplayAudioCodec.SampleRate);
                    for (var i = Math.Max(0, -start); i < samples.Length && start + i < mix.Length; i++)
                        mix[start + i] += samples[i];
                }
                catch (Exception) { /* A malformed optional audio event cannot break visual playback. */ }
            }
            for (var i = 0; i < mix.Length; i++) mix[i] = Mathf.Clamp(mix[i], -1f, 1f);
            _replayAudioClip = AudioClip.Create("LC Replay audio", mix.Length, 1, ReplayAudioCodec.SampleRate, false);
            _replayAudioClip.SetData(mix, 0);
            _replayAudio.clip = _replayAudioClip;
        }

        private void CreateBurstRenderer()
        {
            var obj = NewObject("Recorded short-lived particles", _root!.transform);
            _burstParticles = obj.AddComponent<ParticleSystem>();
            var main = _burstParticles.main;
            main.loop = false; main.startLifetime = 1f; main.maxParticles = 256;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.simulationSpeed = 0f;
            var emission = _burstParticles.emission; emission.enabled = false;
            var shape = _burstParticles.shape; shape.enabled = false;
            var renderer = obj.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _burstTexture = new Texture2D(64, 64, TextureFormat.RGBA32, false, true)
            { name = "LC Replay soft particle", hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[64 * 64];
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                {
                    var dx = (x + .5f - 32f) / 31.5f;
                    var dy = (y + .5f - 32f) / 31.5f;
                    var alpha = Mathf.Clamp01((1f - Mathf.Sqrt(dx * dx + dy * dy)) * 3f);
                    pixels[y * 64 + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            _burstTexture.SetPixels32(pixels);
            _burstTexture.Apply(false, true);
            _burstMaterial = CreateParticleMaterial(_burstTexture, "LC Replay burst particles");
            renderer.sharedMaterial = _burstMaterial;
            _burstParticles.Play(true);
        }

        private Material ParticleMaterial(Texture2D texture)
        {
            var key = texture.GetInstanceID();
            if (_particleMaterials.TryGetValue(key, out var existing)) return existing;
            var material = CreateParticleMaterial(texture, "LC Replay particle texture");
            _particleMaterials.Add(key, material);
            return material;
        }

        private Material CreateParticleMaterial(Texture texture, string name)
        {
            // Use a transparent variant that the installed game has actually
            // rendered. Player builds can strip the transparent variants of a
            // Shader.Find result, leaving opaque white particle quads.
            if (!_particleTemplateSearched)
            {
                _particleTemplateSearched = true;
                var candidates = Resources.FindObjectsOfTypeAll<Material>().Where(candidate =>
                    candidate && candidate.shader && candidate.shader.isSupported &&
                    (candidate.shader.name.StartsWith("HDRP/Particles/", StringComparison.Ordinal) ||
                     candidate.shader.name == "HDRP/Unlit" || candidate.shader.name == "HDRP/Lit") &&
                    candidate.renderQueue >= (int)RenderQueue.Transparent &&
                    !candidate.IsKeywordEnabled("_ALPHATEST_ON") &&
                    (candidate.HasProperty("_BaseColorMap") && candidate.GetTexture("_BaseColorMap") ||
                     candidate.HasProperty("_UnlitColorMap") && candidate.GetTexture("_UnlitColorMap") ||
                     candidate.HasProperty("_MainTex") && candidate.GetTexture("_MainTex")))
                    .OrderBy(candidate => candidate.shader.name == "HDRP/Particles/Unlit" ? 0 :
                        candidate.shader.name.StartsWith("HDRP/Particles/", StringComparison.Ordinal) ? 1 :
                        candidate.shader.name == "HDRP/Unlit" ? 2 : 3)
                    .ThenBy(candidate => candidate.HasProperty("_DstBlend") &&
                        Mathf.Approximately(candidate.GetFloat("_DstBlend"), (float)BlendMode.OneMinusSrcAlpha) ? 0 : 1)
                    .ToArray();
                _particleTemplate = candidates.FirstOrDefault();
                UnityEngine.Debug.Log(_particleTemplate
                    ? "LC Replay particles: cloned loaded transparent material " + _particleTemplate!.name +
                      " (" + _particleTemplate.shader.name + ")."
                    : "LC Replay particles: no loaded transparent HDRP material; using configured shader fallback.");
            }
            var shader = new[] { Shader.Find("HDRP/Particles/Unlit"), Shader.Find("HDRP/Unlit"),
                Shader.Find("Universal Render Pipeline/Particles/Unlit"), _unlitShader, _shader }
                .FirstOrDefault(candidate => candidate && candidate!.isSupported)!;
            var material = _particleTemplate ? new Material(_particleTemplate) : new Material(shader);
            material.name = name;
            material.hideFlags = HideFlags.DontSave;
            foreach (var property in new[] { "_BaseColorMap", "_UnlitColorMap", "_MainTex", "_BaseMap" })
                if (material.HasProperty(property)) material.SetTexture(property, texture);
            foreach (var property in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                if (material.HasProperty(property)) material.SetColor(property, Color.white);
            if (!_particleTemplate)
            {
                material.EnableKeyword("_UNLIT_COLOR_MAP");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHATEST_ON");
                if (material.HasProperty("_AlphaCutoffEnable")) material.SetFloat("_AlphaCutoffEnable", 0f);
                if (material.HasProperty("_SurfaceType")) material.SetFloat("_SurfaceType", 1f);
                if (material.HasProperty("_BlendMode")) material.SetFloat("_BlendMode", 0f);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
                if (material.HasProperty("_TransparentZWrite")) material.SetFloat("_TransparentZWrite", 0f);
                if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_AlphaSrcBlend")) material.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
                if (material.HasProperty("_AlphaDstBlend")) material.SetFloat("_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetOverrideTag("RenderType", "Transparent");
                ReplayAppearance.ValidateHdrpMaterial(material);
            }
            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }

        private void ApplyBurstParticles()
        {
            if (!_burstParticles) return;
            foreach (var list in _emitterSamples.Values) list.Clear();
            _activeEmitters.Clear();
            if (_appearance != null)
                foreach (var style in _frame.ParticleStyles)
                {
                    if (style.IsInterior ? !_indoor && !_disableInteriorCulling : _indoor) continue;
                    if (!_emitterBursts.TryGetValue(style.Id, out var system))
                    {
                        if (_emitterBursts.Count >= 256) break;
                        _particleAssets ??= new ReplayParticleAssets(_root!.transform, ReplayLayer);
                        system = _particleAssets.Create(_root!.transform, style, style.Name, "", _appearance, _burstMaterial,
                            !style.Simulate, out var native);
                        system.transform.position = Vector3.zero;
                        system.transform.rotation = Quaternion.identity;
                        system.Play(false);
                        _emitterBursts.Add(style.Id, system);
                        if (style.Simulate && native) _simulatedEmitters.Add(style.Id);
                    }
                    _activeEmitters.Add(style.Id);
                    if (_simulatedEmitters.Contains(style.Id))
                    {
                        SetTransform(system.transform, style.Position, style.Rotation, style.Scale);
                        if (!system.isPlaying) system.Play(false);
                        if (Math.Abs(system.time - style.Time) > .25f) system.Simulate(style.Time, false, true, false);
                        var main = system.main; main.simulationSpeed = IsPlaying ? Speed : 0f;
                    }
                }
            foreach (var id in _simulatedEmitters)
                if (!_activeEmitters.Contains(id)) _emitterBursts[id].Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (!_emitterSamples.ContainsKey("")) _emitterSamples.Add("", new List<ParticlePose>());
            foreach (var pose in _frame.Particles)
            {
                if (pose.IsInterior ? !_indoor && !_disableInteriorCulling : _indoor) continue;
                if (_simulatedEmitters.Contains(pose.EmitterId)) continue;
                var key = pose.EmitterId.Length != 0 && _emitterBursts.ContainsKey(pose.EmitterId) ? pose.EmitterId : "";
                if (!_emitterSamples.TryGetValue(key, out var list))
                    _emitterSamples.Add(key, list = new List<ParticlePose>());
                list.Add(pose);
            }
            foreach (var pair in _emitterSamples)
            {
                var system = pair.Key.Length == 0 ? _burstParticles : _emitterBursts[pair.Key];
                var count = 0;
                foreach (var pose in pair.Value)
                {
                    if (count == _burstBuffer.Length) break;
                    var native = pose.EmitterId.Length != 0;
                    _burstBuffer[count++] = new ParticleSystem.Particle
                    {
                        position = ToVector(pose.Position), velocity = ToVector(pose.Velocity),
                        startSize3D = native ? ToVector(pose.Size3D) : Vector3.one * pose.Size,
                        rotation3D = native ? ToVector(pose.Rotation3D) : new Vector3(0, 0, pose.Rotation),
                        startLifetime = pose.Lifetime, remainingLifetime = pose.RemainingLifetime,
                        randomSeed = pose.RandomSeed,
                        startColor = new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(pose.Color[0]) * 255),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(pose.Color[1]) * 255),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(pose.Color[2]) * 255),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(pose.Color[3]) * 255))
                    };
                }
                system!.SetParticles(_burstBuffer, count);
            }
        }

        private void ClearSampledParticles()
        {
            foreach (var effect in _emitterBursts.Values)
                if (effect) { effect.gameObject.SetActive(false); Object.Destroy(effect.gameObject); }
            _emitterBursts.Clear();
            _emitterSamples.Clear();
            _simulatedEmitters.Clear(); _activeEmitters.Clear();
            _particleAssets?.Dispose(); _particleAssets = null;
        }

        private void ApplyLines()
        {
            _inactiveLines.Clear();
            foreach (var id in _frameLines.Keys) _inactiveLines.Add(id);
            foreach (var pose in _frame.Lines)
            {
                if ((pose.IsInterior ? !_indoor && !_disableInteriorCulling : _indoor) || pose.Positions.Length < 6) continue;
                if (!_frameLines.TryGetValue(pose.Id, out var line) || !line)
                {
                    if (_frameLines.Count >= 512) continue;
                    var obj = NewObject("Recorded line " + pose.Id, _root!.transform);
                    line = obj.AddComponent<LineRenderer>();
                    line.useWorldSpace = true;
                    line.shadowCastingMode = ShadowCastingMode.Off;
                    line.receiveShadows = false;
                    line.sharedMaterial = _appearance?.ResolveParticleMaterial("", pose.MaterialName, pose.ShaderName)
                        ?? (_burstMaterial ? _burstMaterial : GetMaterial(Color.white));
                    _frameLines[pose.Id] = line;
                }
                _inactiveLines.Remove(pose.Id);
                if (!line.sharedMaterial)
                    line.sharedMaterial = _appearance?.ResolveParticleMaterial("", pose.MaterialName, pose.ShaderName)
                        ?? (_burstMaterial ? _burstMaterial : GetMaterial(Color.white));
                if (!line.enabled) line.enabled = true;
                line.positionCount = pose.Positions.Length / 3;
                line.textureMode = (LineTextureMode)pose.TextureMode;
                line.alignment = (LineAlignment)pose.Alignment;
                line.startWidth = pose.StartWidth;
                line.endWidth = pose.EndWidth;
                line.startColor = new Color(pose.StartColor[0], pose.StartColor[1], pose.StartColor[2], pose.StartColor[3]);
                line.endColor = new Color(pose.EndColor[0], pose.EndColor[1], pose.EndColor[2], pose.EndColor[3]);
                for (var i = 0; i < line.positionCount; i++)
                    line.SetPosition(i, new Vector3(pose.Positions[i * 3], pose.Positions[i * 3 + 1], pose.Positions[i * 3 + 2]));
            }
            foreach (var id in _inactiveLines)
                if (_frameLines.TryGetValue(id, out var line) && line && line.enabled) line.enabled = false;
        }

        private void SyncAudio()
        {
            if (_spatialAudioBlocks.Length != 0) { SyncSpatialAudio(); return; }
            if (!_replayAudio || !_replayAudio!.clip) return;
            if (!IsPlaying || IsBuffering || Speed > 3f)
            { if (_replayAudio.isPlaying) _replayAudio.Pause(); return; }
            var target = Mathf.Clamp((float)LocalTime, 0, Mathf.Max(0, _replayAudio.clip.length - .02f));
            _replayAudio.pitch = Speed;
            if (!_replayAudio.isPlaying)
            {
                _replayAudio.time = target;
                _replayAudio.Play();
            }
            else if (Mathf.Abs(_replayAudio.time - target) > .18f)
                _replayAudio.time = target;
        }

        private void SyncSpatialAudio()
        {
            var target = LocalTime;
            if (!IsPlaying || IsBuffering || Speed > 3f)
            {
                if (!_spatialAudioPaused)
                    foreach (var voice in _spatialVoices) if (voice.Clip) voice.Source.Pause();
                _spatialAudioPaused = true;
                return;
            }
            if (target < _lastAudioTime - .05 || target - _lastAudioTime > .45)
            {
                foreach (var voice in _spatialVoices) ReleaseSpatialVoice(voice);
                _spatialAudioCursor = FindAudioCursor(target - .6);
            }
            _lastAudioTime = target;
            if (_spatialAudioPaused)
                foreach (var voice in _spatialVoices) if (voice.Clip) voice.Source.UnPause();
            _spatialAudioPaused = false;
            foreach (var voice in _spatialVoices)
            {
                if (!voice.Clip) continue;
                if (voice.EndTime <= target - .03) ReleaseSpatialVoice(voice);
                else voice.Source.pitch = Speed;
            }
            while (_spatialAudioCursor < _spatialAudioBlocks.Length &&
                _spatialAudioBlocks[_spatialAudioCursor].Time <= target + .025)
            {
                var block = _spatialAudioBlocks[_spatialAudioCursor++];
                if (target - block.Time < ReplayAudioCodec.MaxSamplesPerBlock / (double)ReplayAudioCodec.SampleRate)
                    StartSpatialAudioBlock(block, target);
            }
        }

        private int FindAudioCursor(double time)
        {
            var low = 0;
            var high = _spatialAudioBlocks.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (_spatialAudioBlocks[mid].Time < time) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private void StartSpatialAudioBlock(ReplayEvent block, double target)
        {
            try
            {
                var data = block.Data;
                var player = data.TryGetValue("player", out var playerValue) &&
                    string.Equals(playerValue, "true", StringComparison.OrdinalIgnoreCase);
                if (player && _mutePlayerAudio) return;
                if (!data.TryGetValue("rate", out var rate) || rate != ReplayAudioCodec.SampleRate.ToString(CultureInfo.InvariantCulture) ||
                    !data.TryGetValue("samples", out var countText) || !int.TryParse(countText, out var count) ||
                    count < 1 || count > ReplayAudioCodec.MaxSamplesPerBlock ||
                    !data.TryGetValue("adpcm", out var encoded) ||
                    !ReadAudioFloat(data, "x", out var x) || !ReadAudioFloat(data, "y", out var y) ||
                    !ReadAudioFloat(data, "z", out var z) || !ReadAudioFloat(data, "spatial", out var spatial) ||
                    !ReadAudioFloat(data, "min", out var min) || !ReadAudioFloat(data, "max", out var max)) return;
                var voice = _spatialVoices.FirstOrDefault(value => !value.Clip);
                if (voice == null)
                {
                    if (_spatialVoices.Count >= 32) return;
                    var obj = NewObject("Recorded audio source", _root!.transform);
                    var source = obj.AddComponent<AudioSource>();
                    source.playOnAwake = false;
                    voice = new SpatialAudioVoice(source);
                    _spatialVoices.Add(voice);
                }
                var pcm = ReplayAudioCodec.Decode(Convert.FromBase64String(encoded), count);
                var clip = AudioClip.Create("LC Replay sound", pcm.Length, 1, ReplayAudioCodec.SampleRate, false);
                clip.SetData(pcm, 0);
                voice.Clip = clip;
                voice.Player = player;
                voice.EndTime = block.Time + pcm.Length / (double)ReplayAudioCodec.SampleRate;
                var sourceAudio = voice.Source;
                sourceAudio.transform.position = new Vector3(x, y, z);
                // New recordings mark true global music/UI explicitly. A world
                // source must be spatialized from the free spectator listener,
                // even if the recording player's source was locally mixed in 2D.
                sourceAudio.spatialBlend = data.TryGetValue("global", out var global) ?
                    (global == "true" ? 0f : 1f) : Mathf.Clamp01(spatial);
                sourceAudio.minDistance = Mathf.Clamp(min, .01f, 1000f);
                sourceAudio.maxDistance = Mathf.Clamp(max, sourceAudio.minDistance, 10000f);
                if (data.TryGetValue("rolloff", out var mode) && int.TryParse(mode, out var rolloff) && rolloff >= 0 && rolloff <= 2)
                    sourceAudio.rolloffMode = (AudioRolloffMode)rolloff;
                sourceAudio.dopplerLevel = 0f;
                sourceAudio.mute = _mutePlayerAudio && player;
                sourceAudio.pitch = Speed;
                sourceAudio.clip = clip;
                sourceAudio.time = Mathf.Clamp((float)(target - block.Time), 0, Mathf.Max(0, clip.length - .005f));
                sourceAudio.Play();
            }
            catch { /* A malformed optional sound block cannot stop the replay. */ }
        }

        private static bool ReadAudioFloat(Dictionary<string, string> data, string key, out float value)
        {
            value = 0;
            return data.TryGetValue(key, out var text) &&
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void ReleaseSpatialVoice(SpatialAudioVoice voice)
        {
            voice.Source.Stop(); voice.Source.clip = null;
            if (voice.Clip) Object.Destroy(voice.Clip);
            voice.Clip = null; voice.EndTime = 0; voice.Player = false;
        }

        private void ClearSpatialAudio()
        {
            foreach (var voice in _spatialVoices) ReleaseSpatialVoice(voice);
            _spatialAudioBlocks = Array.Empty<ReplayEvent>();
            _spatialAudioCursor = 0;
            _lastAudioTime = -1;
            _spatialAudioPaused = false;
        }

        private sealed class SpatialAudioVoice
        {
            internal readonly AudioSource Source;
            internal AudioClip? Clip;
            internal double EndTime;
            internal bool Player;
            internal SpatialAudioVoice(AudioSource source) { Source = source; }
        }

        private void SuspendOtherViews()
        {
            foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (!source || source == _replayAudio || !source.gameObject.scene.IsValid() ||
                    !source.gameObject.scene.isLoaded || source.gameObject.scene == _scene) continue;
                if (!_audioSourceStates.ContainsKey(source)) _audioSourceStates.Add(source, source.mute);
                source.mute = true;
            }
            foreach (var listener in Resources.FindObjectsOfTypeAll<AudioListener>())
            {
                if (!listener || listener.gameObject.scene == _scene || !listener.gameObject.scene.IsValid() ||
                    !listener.gameObject.scene.isLoaded) continue;
                if (!_listenerStates.ContainsKey(listener)) _listenerStates.Add(listener, listener.enabled);
                listener.enabled = false;
            }
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
                if (renderer == null || renderer.gameObject.layer != ReplayLayer || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded || renderer.gameObject.scene == _scene ||
                    _assetScene != null && renderer.gameObject.scene == _assetScene.Scene) continue;
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
            var world = _displayWorld;
            if (world == null) return desired;
            float nearest = 1;
            var segment = desired - start;
            var segmentLength = segment.magnitude;
            if (segmentLength < .0001f) return desired;
            foreach (var geometry in world.Geometry)
            {
                if (geometry.IsBoundsProxy || geometry.EntityId == ignoredEntity || geometry.BonePaths.Count > 0 ||
                    !_geometryObjects.TryGetValue(geometry.Id, out var obj) || !obj || !obj.activeInHierarchy) continue;
                var renderer = obj.GetComponent<Renderer>();
                if (renderer && (!renderer.bounds.IntersectRay(new Ray(start, segment / segmentLength), out var worldHit) ||
                    worldHit > segmentLength * nearest)) continue;
                var a = obj.transform.InverseTransformPoint(start);
                var delta = obj.transform.InverseTransformPoint(desired) - a;
                if (delta.sqrMagnitude < 0.000001f) continue;
                var bounds = new Bounds(ToVector(geometry.BoundsCenter), ToVector(geometry.BoundsSize));
                if (bounds.size.sqrMagnitude > 0 && (!bounds.IntersectRay(new Ray(a, delta.normalized), out var localHit) ||
                    localHit > delta.magnitude * nearest)) continue;
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

        private static Mesh MakeSwarmMesh()
        {
            var mesh = new Mesh { name = "Replay swarm body", hideFlags = HideFlags.DontSave };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0.75f), new Vector3(0f, 0f, -0.85f),
                new Vector3(-0.32f, 0f, 0f), new Vector3(0.32f, 0f, 0f),
                new Vector3(0f, 0.29f, 0f), new Vector3(0f, -0.29f, 0f)
            };
            mesh.triangles = new[]
            {
                0, 4, 2, 0, 3, 4, 0, 2, 5, 0, 5, 3,
                1, 2, 4, 1, 4, 3, 1, 5, 2, 1, 3, 5
            };
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
            point = Finite(point) ? point : Vector3.zero;
            size = Finite(size) ? size : Vector3.one;
            var orientation = ToQuaternion(rotation);
            if (transform.localPosition != point) transform.localPosition = point;
            var prior = transform.localRotation;
            if (prior.x != orientation.x || prior.y != orientation.y || prior.z != orientation.z || prior.w != orientation.w)
                transform.localRotation = orientation;
            if (transform.localScale != size) transform.localScale = size;
        }

        private static void SetActiveIfChanged(GameObject obj, bool active)
        {
            if (obj.activeSelf != active) obj.SetActive(active);
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
            _worldBuild?.Dispose(); _worldBuild = null;
            IsPlaying = false;
            _partCancellation?.Cancel();
            _partCancellation?.Dispose();
            _partCancellation = null;
            if (_partRead != null)
                _ = _partRead.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _partRead = null;
            _recentParts.Clear();
            try
            {
                ClearSpatialAudio();
                ClearSampledParticles();
                if (_replayAudio) { _replayAudio!.Stop(); _replayAudio.clip = null; }
                if (_replayAudioClip) Object.Destroy(_replayAudioClip);
                _replayAudioClip = null;
                if (_burstMaterial) Object.Destroy(_burstMaterial);
                _burstMaterial = null;
                foreach (var material in _particleMaterials.Values) if (material) Object.Destroy(material);
                _particleMaterials.Clear();
                if (_burstTexture) Object.Destroy(_burstTexture);
                _burstTexture = null;
                _hud?.Dispose();
                _hud = null;
                _environment?.Dispose();
                _environment = null;
                _assetScene?.Dispose();
                _assetScene = null;
                _visualState?.Dispose(); _visualState = null;
                _appearance?.Dispose();
                _appearance = null;
                if (_camera) _camera!.targetTexture = null;
                if (_displayImage) _displayImage!.texture = null;
                if (_displayTexture != null) { _displayTexture.Release(); Object.Destroy(_displayTexture); _displayTexture = null; }
                if (_root != null) { _root.SetActive(false); Object.Destroy(_root); }
                foreach (var mesh in _worldMeshes) if (mesh != null) Object.Destroy(mesh);
                foreach (var mask in _worldFogMasks) if (mask) Object.Destroy(mask);
                foreach (var visual in _entities.Values) if (visual.SkeletonMesh != null) Object.Destroy(visual.SkeletonMesh);
                if (_swarmMesh != null) Object.Destroy(_swarmMesh);
                if (_cube != null) Object.Destroy(_cube);
                foreach (var material in _materials.Values) if (material != null) Object.Destroy(material);
                ReplayIsolation.Unregister(_scene);
                if (_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
            }
            finally
            {
                foreach (var pair in _cameraStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _canvasStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _listenerStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _audioSourceStates) if (pair.Key != null) pair.Key.mute = pair.Value;
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
                _listenerStates.Clear();
                _audioSourceStates.Clear();
                _rendererStates.Clear();
                _uiInputStates.Clear();
                _uiInputTypes.Clear();
                _entities.Clear();
                _frameLines.Clear();
                _materials.Clear();
                _worldMeshes.Clear();
                _worldFogMasks.Clear();
                _instancedGeometry.Clear();
                _worldParticles.Clear();
                _localFogs.Clear();
                _worldObjects.Clear();
                _geometryObjects.Clear();
                _dynamicGeometry.Clear();
                _movingSceneGeometry.Clear();
                _completeRendererLists.Clear();
                _interiorRenderers.Clear();
                _interiorRooms.Clear();
                _additionalRoomVolumes.Clear();
                _renderGeometrySources.Clear();
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
            internal LineRenderer[]? SwarmLightning;
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
