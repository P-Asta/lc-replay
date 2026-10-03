using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using LCReplay.Plugin.UI;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
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
        private static readonly MethodInfo ShallowClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private ReplaySession _session;
        private readonly HashSet<string> _windowActorIds = new HashSet<string>(StringComparer.Ordinal);
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
        private readonly bool _preserveLiveAudio;
        private AudioListener? _liveListener;
        private readonly Dictionary<Renderer, bool> _rendererStates = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Behaviour, bool> _uiInputStates = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<Type, bool> _uiInputTypes = new Dictionary<Type, bool>();
        private readonly Dictionary<string, EntityVisual> _entities = new Dictionary<string, EntityVisual>();
        private readonly Dictionary<string, Dictionary<int, List<ReplayEvent>>> _animationEvents =
            new Dictionary<string, Dictionary<int, List<ReplayEvent>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, List<KeyValuePair<double, string>>>> _animationParameters =
            new Dictionary<string, Dictionary<string, List<KeyValuePair<double, string>>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, AnimationTrackSnapshot>> _animationTracks =
            new Dictionary<string, Dictionary<string, AnimationTrackSnapshot>>(StringComparer.Ordinal);
        private readonly Dictionary<string, AnimationTrackSnapshot> _fallbackAnimationTracks =
            new Dictionary<string, AnimationTrackSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _animationPaths =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ReplayEvent>> _itemMotionEvents =
            new Dictionary<string, List<ReplayEvent>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ReplayEvent>> _postFxEvents =
            new Dictionary<string, List<ReplayEvent>>(StringComparer.Ordinal);
        private ReplayEvent[] _itemImpactEvents = Array.Empty<ReplayEvent>();
        private NativeSoundPlayback? _nativeSounds;
        private NativeStructureAudio? _structureAudio;
        private readonly HashSet<string> _nativeBulletEmitters = new HashSet<string>(StringComparer.Ordinal);
        private bool _showWorldLoading = true;
        private readonly Dictionary<string, AudioSource> _itemSoundSources =
            new Dictionary<string, AudioSource>(StringComparer.Ordinal);
        private int _itemImpactCursor;
        private double _lastItemImpactTime = -1;
        private readonly Dictionary<Color32, Material> _materials = new Dictionary<Color32, Material>();
        private readonly List<GameObject> _worldObjects = new List<GameObject>();
        private readonly List<Mesh> _worldMeshes = new List<Mesh>();
        private readonly List<InstancedGeometry> _instancedGeometry = new List<InstancedGeometry>();
        private readonly List<Renderer> _proceduralGrassRenderers = new List<Renderer>();
        private readonly List<ParticleSystem> _worldParticles = new List<ParticleSystem>();
        private readonly Dictionary<string, GeometrySnapshot> _renderGeometrySources = new Dictionary<string, GeometrySnapshot>();
        private readonly List<KeyValuePair<Renderer, string>> _interiorRenderers = new List<KeyValuePair<Renderer, string>>();
        private readonly List<Renderer> _exteriorRenderers = new List<Renderer>();
        private readonly List<KeyValuePair<Renderer, double>> _deferredWorldRenderers =
            new List<KeyValuePair<Renderer, double>>();
        private readonly List<KeyValuePair<Light, double>> _deferredWorldLights = new List<KeyValuePair<Light, double>>();
        private readonly List<KeyValuePair<Behaviour, double>> _deferredWorldFogs = new List<KeyValuePair<Behaviour, double>>();
        private readonly List<KeyValuePair<ParticleSystem, double>> _deferredWorldParticles =
            new List<KeyValuePair<ParticleSystem, double>>();
        private readonly Dictionary<string, List<KeyValuePair<Renderer, GeometrySnapshot>>> _naturalLods =
            new Dictionary<string, List<KeyValuePair<Renderer, GeometrySnapshot>>>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<Light, LightSnapshot>> _worldLights = new List<KeyValuePair<Light, LightSnapshot>>();
        private readonly Dictionary<string, List<SunLighting>> _sunEvents = new Dictionary<string, List<SunLighting>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Transform> _anchorRoots = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<string, Bounds> _interiorRooms = new Dictionary<string, Bounds>();
        private readonly Dictionary<string, Bounds[]> _additionalRoomVolumes = new Dictionary<string, Bounds[]>();
        internal float InteriorRenderDistance { get; set; } = 105f;
        private ReplayAppearance? _appearance;
        private SceneAssetReplay? _assetScene;
        private SceneAssetReplay? _futureAssetScene;
        private string _futureAssetSceneName = "";
        private WorldSnapshot? _futureWorld;
        private ReplayAppearance? _futureAppearance;
        private IEnumerator<float>? _futureAppearanceBuild;
        private Shader? _futureAppearanceShader;
        private readonly Dictionary<string, Mesh> _futureMeshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _futureWorldFirstSeen = new Dictionary<string, double>(StringComparer.Ordinal);
        private string _landingMapSet = "";
        private double _landingMapStart;
        private int _futureMeshIndex;
        private long _futureMeshBytes;
        private bool _useFutureMeshes;
        private bool _futureWarmFailed;
        private ReplayVisualState? _visualState;
        private ReplayEnvironment? _environment;
        private RenderTexture? _displayTexture;
        private RawImage? _displayImage;
        private float _resolutionScale;
        private float _gamma;
        private bool _disableInteriorCulling;
        private bool _mutePlayerAudio;
        private bool _fogEnabled = true;
        private bool _appliedFogVisible = true;
        private bool _noShadow;
        private readonly Action<bool>? _saveNoShadow;
        private readonly Dictionary<Light, LightShadows> _nativeShadowModes = new Dictionary<Light, LightShadows>();
        private readonly Action<float>? _saveResolution;
        private readonly Action<float>? _saveGamma;
        private readonly Action<bool>? _saveDisableInteriorCulling;
        private readonly Action<bool>? _saveMutePlayerAudio;
        private readonly Action<bool>? _saveFogEnabled;
        private Transform? _shipCabinAnchor;
        private Vector3 _shipCabinCenter;
        private int _screenWidth, _screenHeight;
        private readonly Dictionary<string, GameObject> _geometryObjects = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, List<GameObject>> _maskEyesObjects = new Dictionary<string, List<GameObject>>();
        private readonly Dictionary<string, EntitySnapshot> _audioEntities = new Dictionary<string, EntitySnapshot>();
        private readonly HashSet<string> _hazardsWithModel = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, BakedPlayerBody> _bakedPlayers = new Dictionary<string, BakedPlayerBody>(StringComparer.Ordinal);
        private readonly HashSet<string> _nativeSkinIds = new HashSet<string>(StringComparer.Ordinal);
        private Material? _playerOutlineMaterial;
        private Material? _turretBeamMaterial;
        private Texture2D? _turretBeamTexture;
        private readonly List<KeyValuePair<Behaviour, LocalFogSnapshot>> _localFogs = new List<KeyValuePair<Behaviour, LocalFogSnapshot>>();
        private readonly List<Renderer> _fogGeometry = new List<Renderer>();
        private readonly List<Texture3D> _worldFogMasks = new List<Texture3D>();
        private readonly Dictionary<string, GeometrySnapshot> _dynamicGeometry = new Dictionary<string, GeometrySnapshot>();
        private readonly Dictionary<string, GeometrySnapshot> _movingSceneGeometry = new Dictionary<string, GeometrySnapshot>();
        private readonly Dictionary<string, (string EntityId, Vector3 LocalPosition, Quaternion LocalRotation)> _doorMeshLinks =
            new Dictionary<string, (string, Vector3, Quaternion)>(StringComparer.Ordinal);
        private readonly Dictionary<string, EntitySnapshot> _firstDoorPoses = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        private readonly HashSet<string> _completeRendererLists = new HashSet<string>();
        private readonly Dictionary<string, RenderPose> _currentRendererPoses = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
        private readonly Dictionary<string, RenderPose> _currentSceneRendererPoses = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
        private readonly HashSet<string> _inactiveEntities = new HashSet<string>(StringComparer.Ordinal);
        private bool _hasRendererPoseCapability;
        private bool _sparseRendererPoses;
        private readonly List<EntitySnapshot> _players = new List<EntitySnapshot>();
        private readonly List<ReplayPlayerDeath> _playerDeaths = new List<ReplayPlayerDeath>();
        private Task<List<ReplayPlayerDeath>>? _playerDeathLoad;
        private readonly CancellationTokenSource _playerDeathCancellation = new CancellationTokenSource();
        private string _followTargetName = "";
        private string _followTargetId = "";
        private string _bookmarkPlayerName = "";
        private string _bookmarkPlayerId = "";
        private Vector3 _lastFollowPosition;
        private bool _hasLastFollowPosition;
        private CursorLockMode _savedCursorLock;
        private bool _savedCursorVisible;
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
        private bool _allowEquivalentWorldReuse;
        private IEnumerator<float>? _worldBuild;
        private float _worldBuildProgress;
        private bool _initialPrefetchPending = true;
        private string _selectedId = "";
        private string _sceneName = "No world geometry recorded";
        private bool _follow;
        private bool _showLabels;
        private GUIStyle? _diagnosticLabelStyle;
        private bool _showSkeletons;
        private bool _disposed, _parked, _resumePlaying, _resumeHudVisible;
        private bool _looking;
        private float _yaw;
        private float _pitch = 12f;
        private float _lookTargetYaw;
        private float _lookTargetPitch = 12f;
        private float _lookYawVelocity;
        private float _lookPitchVelocity;
        private float _speed = 1f;
        private float _cameraSpeed = 8f;
        private bool _cinematicMove;
        private Vector3 _cinematicVelocity;
        private Action<bool>? _saveCinematicMove;
        private Action<float>? _saveCameraSpeed;
        private float _environmentScanElapsed;
        private float _interiorScanElapsed;
        private float _followClearanceElapsed;
        private float _followDistance = 2.5f;
        private float _followTargetDistance = 2.5f;
        private bool _uiCursor;
        private double? _pendingHudSeek;
        private float _pendingHudSeekAt;

        public bool IsPlaying { get; private set; } = true;
        public double Time { get; private set; }
        public double Duration => _recording.Duration;
        public double LocalTime => _recording.LocalTime(_partIndex, Time);
        public bool IsBuffering => _requestedPart >= 0 || _worldBuild != null || _assetScene?.IsLoading == true;
        internal bool CanPark => !_disposed && !_parked && !IsBuffering && !_pendingHudSeek.HasValue &&
            _root && _scene.IsValid() && _scene.isLoaded && _playbackError == null;
        internal string[] SourceFiles => _recording.Parts.Select(part => part.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            bool mutePlayerAudio, Action<bool>? saveMutePlayerAudio,
            bool cinematicMove = false, float cameraSpeed = 8f,
            Action<bool>? saveCinematicMove = null, Action<float>? saveCameraSpeed = null,
            bool fogEnabled = true, Action<bool>? saveFogEnabled = null,
            bool preserveLiveAudio = false, bool noShadow = false, Action<bool>? saveNoShadow = null)
        {
            _preserveLiveAudio = preserveLiveAudio;
            _noShadow = noShadow; _saveNoShadow = saveNoShadow;
            if (_preserveLiveAudio)
            {
                var localPlayer = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController");
                _liveListener = GameAccess.Read(localPlayer, "activeAudioListener") as AudioListener;
                var gameplayCamera = GameAccess.Read(localPlayer, "gameplayCamera") as Camera;
                if (!_liveListener) _liveListener = gameplayCamera != null ? gameplayCamera.GetComponent<AudioListener>() : null;
                if (!_liveListener) _liveListener = (localPlayer as Component)?.GetComponentInChildren<AudioListener>(true);
            }
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _recording = recording ?? new ReplayRecordingTimeline(new[] { new ReplayRecordingPart { FilePath = "single.lcr", Duration = session.Duration } });
            _playerDeaths.AddRange(DeathsInSession(session,
                _recording.Parts[Math.Max(0, Math.Min(_recording.Parts.Count - 1, partIndex))].Offset));
            var deathSources = _recording.Parts.Where(part => part.Window != null)
                .GroupBy(part => part.FilePath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            if (deathSources.Length != 0)
                _playerDeathLoad = Task.Run(() => deathSources.SelectMany(part =>
                {
                    var offset = part.Offset - part.Window!.Start;
                    return ReplayReader.ReadPlayerDeaths(part.Window.Index, _playerDeathCancellation.Token)
                        .Select(death => new ReplayPlayerDeath { Time = death.Time + offset,
                            EntityId = death.EntityId, Name = death.Name, Position = death.Position });
                }).OrderBy(death => death.Time).ToList(), _playerDeathCancellation.Token);
            _partIndex = Math.Max(0, Math.Min(_recording.Parts.Count - 1, partIndex));
            AlignLandingMap(_session, _partIndex);
            _hasRendererPoseCapability = session.Header.Capabilities.Contains("child-renderer-poses");
            _sparseRendererPoses = session.Header.Capabilities.Contains("sparse-entity-renderer-poses");
            _savedCursorLock = Cursor.lockState;
            _savedCursorVisible = Cursor.visible;
            _resolutionScale = Mathf.Clamp(resolutionScale, 0.25f, 1f);
            _gamma = Mathf.Clamp(gamma, 0.5f, 2f);
            _disableInteriorCulling = disableInteriorCulling;
            _mutePlayerAudio = mutePlayerAudio;
            _fogEnabled = fogEnabled;
            _saveFogEnabled = saveFogEnabled;
            _saveResolution = saveResolution;
            _saveGamma = saveGamma;
            _saveDisableInteriorCulling = saveDisableInteriorCulling;
            _saveMutePlayerAudio = saveMutePlayerAudio;
            _cinematicMove = cinematicMove;
            _cameraSpeed = Mathf.Clamp(cameraSpeed, 1f, 30f);
            _saveCinematicMove = saveCinematicMove;
            _saveCameraSpeed = saveCameraSpeed;
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
                var replayListener = cameraObject.AddComponent<AudioListener>();
                if (_preserveLiveAudio && _liveListener) replayListener.enabled = false;
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
                // Camera-local fill has no equivalent shadow caster in the live game.
                _spectatorLight.shadows = LightShadows.None;
                _spectatorLight.shadowResolution = LightShadowResolution.Low;
                _spectatorLight.cullingMask = 1 << ReplayLayer;
                AddRenderPipelineLightData(cameraObject, 60f);
                _spectatorLight.intensity = 60f;
                ConfigureNightVision(_spectatorLight);
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
                IndexAnimationEvents();
                SuspendOtherViews();
                SetLooking(Application.isFocused);
                Application.onBeforeRender += RefreshCursor;
                Seek(_recording.Parts[_partIndex].Offset);
                PreloadNextMap();
                RebuildAudio();
                if (_players.Count != 0) { _selectedId = _players[0].Id; Focus(_players[0]); }
                else
                {
                    var visible = _frame.Entities.FirstOrDefault(entity => entity.Active && IsVisualKind(entity.Kind));
                    if (visible != null) Focus(visible);
                }
                _hud = new NativePlaybackHud(session, TogglePause, SeekFromHud, CycleSpeed,
                    SelectCamera, ToggleDiagnostics, () => CloseRequested = true,
                    _resolutionScale, _gamma, SetResolutionScale, SetGamma,
                    _disableInteriorCulling, SetDisableInteriorCulling,
                    _noShadow, SetNoShadow,
                    _cinematicMove, SetCinematicMove, _cameraSpeed, SetCameraSpeed,
                    _fogEnabled, SetFogEnabled);
                SceneManager.MoveGameObjectToScene(_hud.Root, _scene);
                var bookmarkSources = _recording.Parts.GroupBy(part => part.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToArray();
                var bookmarks = bookmarkSources.SelectMany(part => part.Window != null
                    ? part.Window.Index.Bookmarks.Select(time => time + part.Offset - part.Window.Start)
                    : part.FilePath == _recording.Parts[_partIndex].FilePath ? session.Events.Where(evt => evt.Category == "marker" && evt.Name == "bookmark")
                        .Select(evt => evt.Time + part.Offset) : Enumerable.Empty<double>()).OrderBy(time => time).ToArray();
                _hud.SetBookmarks(bookmarks, Duration);
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
            if (_disposed || _parked || _camera == null) return;
            CompletePlayerDeaths();
            if (ReplayInput.WasPressed("L")) ToggleReplayUi();
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
                // Recording's sub-millisecond capture slices do not apply here.
                while (watch.Elapsed.TotalMilliseconds < 12)
                {
                    if (!_worldBuild.MoveNext())
                    { _worldBuild.Dispose(); _worldBuild = null; _worldBuildProgress = 1f; ApplyFrame(); break; }
                    _worldBuildProgress = Mathf.Clamp01(_worldBuild.Current);
                    if (_assetScene?.IsLoading == true) break;
                }
                _hud?.SetLoading(_showWorldLoading, _assetScene?.IsLoading == true ? "Loading moon scenery" : "Building replay scene",
                    .85f + .13f * _worldBuildProgress);
                if (_worldBuild != null) return;
            }
            if (_assetScene?.IsLoading == true)
            { _hud?.SetLoading(true, "Loading moon scenery", .98f); return; }
            _hud?.SetLoading(false, "", 1f);
            WarmFutureMap();
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
            if (ReplayInput.WasPressed("C")) SetCinematicMove(!_cinematicMove);
            if (ReplayInput.WasPressed("B")) _uiCursor = !_uiCursor;
            if (ReplayInput.WasPressed("F") && SelectedPlayer() is EntitySnapshot focusPlayer && !IsDead(focusPlayer))
                SelectCamera(focusPlayer.Id);
            if (ReplayInput.WasPressed("Tab")) CyclePlayer();
            if (_follow && SelectedPlayer() != null && Application.isFocused && ReplayInput.WasMousePressed("leftButton") &&
                !PointerOverReplayUi(_looking ? new Vector2(Screen.width * .5f, Screen.height * .5f) : ReplayInput.MousePosition)) CyclePlayer();
            if (ReplayInput.WasPressed("LeftArrow")) Seek(Time - 5);
            if (ReplayInput.WasPressed("RightArrow")) Seek(Time + 5);
            SetLooking(Application.isFocused && (!_uiCursor || ReplayInput.IsMousePressed("rightButton")));
            if (!_follow && Application.isFocused)
            {
                var scroll = ReplayInput.MouseScroll.y;
                if (Mathf.Abs(scroll) > .001f && (_looking || !PointerOverReplayUi(ReplayInput.MousePosition)))
                    ChangeFreeCameraSpeed(scroll);
                if (ReplayInput.WasMousePressed("leftButton"))
                    SelectCameraAtPointer();
            }
            if (_looking)
            {
                var mouse = ReplayInput.MouseDelta;
                _lookTargetYaw += mouse.x * 0.12f;
                _lookTargetPitch = Mathf.Clamp(_lookTargetPitch - mouse.y * 0.12f, -89f, 89f);
            }
            if (_cinematicMove)
            {
                var step = Mathf.Min(delta, .1f);
                _yaw = Mathf.SmoothDampAngle(_yaw, _lookTargetYaw, ref _lookYawVelocity, .15f, Mathf.Infinity, step);
                _pitch = Mathf.SmoothDampAngle(_pitch, _lookTargetPitch, ref _lookPitchVelocity, .15f, Mathf.Infinity, step);
            }
            else
            {
                _yaw = _lookTargetYaw;
                _pitch = _lookTargetPitch;
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
            if (direction.sqrMagnitude > 0) { _follow = false; _bookmarkPlayerName = _bookmarkPlayerId = ""; }
            if (_cinematicMove)
            {
                var fast = ReplayInput.IsPressed("LeftShift") || ReplayInput.IsPressed("RightShift") ? 4f : 1f;
                var target = direction.sqrMagnitude > 0 ? direction.normalized * (_cameraSpeed * fast) : Vector3.zero;
                var rate = target.sqrMagnitude > _cinematicVelocity.sqrMagnitude ? 18f : 12f;
                _cinematicVelocity = Vector3.MoveTowards(_cinematicVelocity, target, rate * Mathf.Min(delta, .1f));
                if (!_follow) _camera.transform.position += _cinematicVelocity * Mathf.Min(delta, .1f);
                else _cinematicVelocity = Vector3.zero;
            }
            else if (direction.sqrMagnitude > 0)
            {
                var fast = ReplayInput.IsPressed("LeftShift") || ReplayInput.IsPressed("RightShift") ? 4f : 1f;
                _camera.transform.position += direction.normalized * (_cameraSpeed * fast * Mathf.Min(delta, 0.1f));
            }
            if (_follow)
            {
                var player = SelectedTarget();
                if (player == null || !player.Active || IsUnavailableTarget(player))
                    StopFollowingAtDeath(player);
                else
                {
                    _lastFollowPosition = ToVector(player.Position);
                    _hasLastFollowPosition = true;
                    var eye = TargetEye(player);
                    if (_looking)
                    {
                        var scroll = ReplayInput.MouseScroll.y;
                        if (Mathf.Abs(scroll) > .001f)
                        {
                            var wasFirstPerson = _followTargetDistance < .45f;
                            _followTargetDistance = Mathf.Clamp(_followTargetDistance - scroll * .005f, .05f, 30f);
                            if (!wasFirstPerson && _followTargetDistance < .45f)
                                _lookTargetYaw = _yaw = ToQuaternion(player.Rotation).eulerAngles.y;
                            _followClearanceElapsed = .2f;
                        }
                    }
                    if (_followTargetDistance < .45f)
                    {
                        _followDistance = 0f;
                        _camera.transform.position = eye;
                        var view = player.ViewRotation.HasValue ? ToQuaternion(player.ViewRotation.Value) :
                            Quaternion.Euler(0f, ToQuaternion(player.Rotation).eulerAngles.y, 0f);
                        _camera.transform.rotation = view;
                        var angles = view.eulerAngles;
                        _pitch = Mathf.DeltaAngle(0f, angles.x);
                        _yaw = angles.y;
                        _lookTargetPitch = _pitch; _lookTargetYaw = _yaw;
                        _followClearanceElapsed = 0f;
                    }
                    else
                    {
                    _followClearanceElapsed += delta;
                    if (_followClearanceElapsed >= 0.2f)
                    {
                        _followClearanceElapsed = 0;
                        _followDistance = Vector3.Distance(eye, ClearCameraPosition(eye, eye - _camera.transform.forward * _followTargetDistance, player.Id));
                    }
                    _camera.transform.position = eye - _camera.transform.forward * _followDistance;
                    }
                }
            }
            _interiorScanElapsed += delta;
            if (_interiorScanElapsed >= 0.2f) { _interiorScanElapsed = 0; UpdateInteriorVisibility(); }
            var listener = _preserveLiveAudio && _liveListener ? _liveListener!.transform.position : _camera!.transform.position;
            var derivedAudio = !_session.Header.Capabilities.Contains("source-audio-adpcm-22050") && !_replayAudioClip;
            ApplyHeldItemPlacement();
            _audioEntities.Clear();
            foreach (var entity in _frame.Entities)
            {
                _audioEntities[entity.Id] = entity;
                if (_entities.TryGetValue(entity.Id, out var visual))
                {
                    if (entity.Kind == "hazard" && entity.Name.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!visual.TurretAttempted) { visual.TurretAttempted = true; visual.NativeTurret = NativeTurretEffect.Create(entity, visual.Root.transform, ReplayLayer, _nativeSounds); }
                        if (visual.NativeTurret != null) visual.NativeTurret.PlayAudio = derivedAudio;
                        visual.NativeTurret?.Sync(entity, _frame, LocalTime, IsPlaying && !IsBuffering, Speed, listener, _nativeSounds);
                    }
                    if (visual.NativeSwarm != null) visual.NativeSwarm.Sync(entity, LocalTime, IsPlaying && !IsBuffering, Speed, _camera.transform.position, _frame, _camera);
                    if (entity.Kind == "hazard" && entity.Name == "Landmine")
                    {
                        if (!visual.MineAttempted)
                        {
                            visual.MineAttempted = true; visual.NativeMine = NativeLandmineEffect.Create(visual.Root.transform, ReplayLayer);
                            CacheMineMeshes(visual, entity);
                        }
                        visual.NativeMine?.Sync(entity, LocalTime, IsPlaying && !IsBuffering, Speed);
                        if (visual.NativeMine != null)
                        {
                            visual.Proxy.SetActive(false);
                            if (visual.NativeHazard) visual.NativeHazard!.SetActive(false);
                            foreach (var renderer in visual.MineMeshes) if (renderer) renderer.forceRenderingOff = true;
                        }
                    }
                }
            }
            ApplyMaskEffects();
            _structureAudio?.Sync(derivedAudio && IsPlaying && !IsBuffering, Speed, listener);
            ApplyBurstParticles();
            ApplyLines();
            UpdatePlayerBodyOcclusion();
            ApplyHeldItemPlacement();
            foreach (var effect in _worldParticles) if (effect) { var main = effect.main; main.simulationSpeed = IsPlaying ? Speed : 0f; }
            SyncAudio();
            SyncItemImpacts();
            if (_nativeSounds != null) _nativeSounds.ListenerPosition = _preserveLiveAudio && _liveListener
                ? _liveListener!.transform.position : _camera ? _camera!.transform.position : (Vector3?)null;
            _nativeSounds?.Sync(LocalTime, IsPlaying && !IsBuffering, Speed, _mutePlayerAudio,
                ResolveSoundParent,
                id => _audioEntities.TryGetValue(id, out var entity) ? entity : null);
            _hud?.Update(_session, _frame, _players, _selectedId, _sceneName, Time, IsPlaying, Speed, _follow, _showSkeletons,
                Duration, LocalTime, IsBuffering && _showWorldLoading, DeathBookmarkTime(),
                _follow && SelectedPlayer() == null ? SelectedTarget()?.Name : null);
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

        internal void ToggleReplayUi()
        {
            if (_disposed || _hud == null) return;
            _hud.Root.SetActive(!_hud.Root.activeSelf);
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
                // Late actors in this capture set were prepared during the
                // initial build. Their appearance only changes active roots.
                if (world != null && CanReuseWorld(world, _allowEquivalentWorldReuse))
                {
                    _allowEquivalentWorldReuse = false;
                    RefreshWorldEnvironment(world);
                    _worldIndex = index;
                    ApplyFrame();
                    UpdateInteriorVisibility();
                    return;
                }
                _allowEquivalentWorldReuse = false;
                _showWorldLoading = _displayWorld == null || world == null || world.CaptureSetId.Length == 0 ||
                    world.CaptureSetId != _displayWorld.CaptureSetId;
                _worldBuild?.Dispose();
                if (_useFutureMeshes && (world == null || _futureWorld?.CaptureSetId != world.CaptureSetId))
                    ReleaseFutureResources();
                _visualState?.Dispose();
                _visualState = null;
                // Start the installed moon scene load now, while the generated
                // exterior and interior are still being built in bounded steps.
                if (world != null)
                {
                    if (_futureAssetScene != null && _futureWorld != null &&
                        world.CaptureSetId == _futureWorld.CaptureSetId &&
                        world.AssetScene.Length != 0 && world.AssetScene == _futureAssetSceneName)
                    {
                        _assetScene?.Dispose();
                        _assetScene = _futureAssetScene;
                        _futureAssetScene = null;
                        _futureAssetSceneName = "";
                        _assetScene!.SetWorld(world);
                        _assetScene.SetSuspended(PreLandingOrbit());
                    }
                    else
                    {
                        if (_futureWorld != null && _displayWorld != null &&
                            world.CaptureSetId != _displayWorld.CaptureSetId &&
                            world.CaptureSetId != _futureWorld.CaptureSetId)
                        {
                            _futureAssetScene?.Dispose();
                            _futureAssetScene = null;
                            _futureAssetSceneName = "";
                            ReleaseFutureResources();
                        }
                        _assetScene?.SetSuspended(PreLandingOrbit());
                        _assetScene?.SetWorld(world);
                    }
                }
                _worldBuild = RebuildWorldSteps(world).GetEnumerator();
                _worldBuildProgress = 0;
                _worldIndex = index;
            }
            ApplyFrame();
        }

        private bool CanReuseWorld(WorldSnapshot world, bool acceptEquivalent = false)
        {
            var displayed = _displayWorld;
            if (_worldBuild != null || displayed == null || world.CaptureSetId.Length == 0 ||
                world.CaptureSetId != displayed.CaptureSetId || world.AssetScene != displayed.AssetScene ||
                world.AssetBuildIndex != displayed.AssetBuildIndex) return false;
            return world.Geometry.All(geometry => _renderGeometrySources.ContainsKey(geometry.Id)) &&
                world.Rooms.All(room => _interiorRooms.ContainsKey(room.Id)) &&
                ContainsAll(displayed.AssetRendererPaths, world.AssetRendererPaths, path => path) &&
                ContainsAll(displayed.AssetTerrainPaths, world.AssetTerrainPaths, path => path) &&
                (acceptEquivalent
                    ? ContainsEquivalent(displayed.Materials, world.Materials, material => material.Id, SameSnapshot) &&
                      ContainsEquivalent(displayed.Textures, world.Textures, texture => texture.Id, SameTexture) &&
                      ContainsEquivalent(displayed.Lights, world.Lights, light => light.Id, SameSnapshot) &&
                      ContainsEquivalent(displayed.ParticleEmitters, world.ParticleEmitters, emitter => emitter.Id, SameSnapshot) &&
                      ContainsEquivalent(displayed.LocalFogs, world.LocalFogs, fog => fog.Id, SameSnapshot)
                    : ContainsSame(displayed.Materials, world.Materials, material => material.Id) &&
                      ContainsSame(displayed.Textures, world.Textures, texture => texture.Id) &&
                      ContainsSame(displayed.Lights, world.Lights, light => light.Id) &&
                      ContainsSame(displayed.ParticleEmitters, world.ParticleEmitters, emitter => emitter.Id) &&
                      ContainsSame(displayed.LocalFogs, world.LocalFogs, fog => fog.Id));
        }

        private static bool ContainsAll<T>(IEnumerable<T> available, IEnumerable<T> required, Func<T, string> id)
        {
            var known = new HashSet<string>(available.Select(id), StringComparer.Ordinal);
            return required.All(item => known.Contains(id(item)));
        }

        private static bool ContainsSame<T>(IEnumerable<T> available, IEnumerable<T> required, Func<T, string> id)
            where T : class
        {
            var known = available.GroupBy(id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return required.All(item => known.TryGetValue(id(item), out var value) && ReferenceEquals(value, item));
        }

        private static bool ContainsEquivalent<T>(IEnumerable<T> available, IEnumerable<T> required,
            Func<T, string> id, Func<T, T, bool> equivalent) where T : class
        {
            var known = available.GroupBy(id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return required.All(item => known.TryGetValue(id(item), out var value) &&
                (ReferenceEquals(value, item) || equivalent(value, item)));
        }

        private static bool SameSnapshot<T>(T left, T right) where T : class =>
            string.Equals(JsonConvert.SerializeObject(left), JsonConvert.SerializeObject(right), StringComparison.Ordinal);

        private static bool SameTexture(TextureSnapshot left, TextureSnapshot right) =>
            left.Name == right.Name && left.Width == right.Width && left.Height == right.Height &&
            left.Linear == right.Linear && left.FilterMode == right.FilterMode && left.Png.SequenceEqual(right.Png);

        private void RefreshWorldEnvironment(WorldSnapshot world)
        {
            if (_displayWorld == null || ReferenceEquals(_displayWorld.Environment, world.Environment)) return;
            _environment?.Dispose();
            _environment = new ReplayEnvironment(world, _root!.transform, ReplayLayer,
                _session.Header.Capabilities.Contains("world-postfx-without-player-filters"));
            _environment.SetFogEnabled(_fogEnabled);
            _environment.SetGamma(_gamma);
            _displayWorld.Environment = world.Environment;
            UpdateInteriorVisibility();
        }

        private void PreloadNextMap()
        {
            if (_assetScene == null || _worldIndex < 0 || _futureWorld != null) return;
            var current = _session.Worlds[_worldIndex].World;
            if (current == null || current.AssetScene.Length != 0) return;
            for (var i = _worldIndex + 1; i < _session.Worlds.Count; i++)
            {
                var candidate = _session.Worlds[i].World;
                if (candidate == null || candidate.CaptureSetId.Length == 0 ||
                    candidate.CaptureSetId == current.CaptureSetId || candidate.Layer != "exterior" ||
                    !_session.Worlds.Skip(i + 1).Any(record => record.World?.CaptureSetId == candidate.CaptureSetId &&
                        record.World.Layer == "interior")) continue;
                var world = AssembleWorld(i);
                if (world.AssetScene.Length != 0 && world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count != 0)
                {
                    var preload = new SceneAssetReplay(ReplayLayer, UpdateInteriorVisibility);
                    try
                    {
                        preload.SetSuspended(true);
                        preload.SetWorld(world);
                        _futureAssetScene = preload;
                        _futureAssetSceneName = world.AssetScene;
                    }
                    catch (Exception error) when (error is InvalidDataException || error is InvalidOperationException)
                    { preload.Dispose(); break; }
                }
                _futureWorld = world;
                _futureWorldFirstSeen.Clear();
                foreach (var record in _session.Worlds.Where(record => record.World?.CaptureSetId == world.CaptureSetId))
                {
                    foreach (var geometry in record.World!.Geometry)
                        if (geometry.EntityId.Length == 0 && !_futureWorldFirstSeen.ContainsKey(geometry.Id))
                            _futureWorldFirstSeen[geometry.Id] = record.Time + _recording.Parts[_partIndex].Offset;
                    foreach (var light in record.World.Lights)
                        if (light.EntityId.Length == 0) _futureWorldFirstSeen.TryAdd("light:" + light.Id,
                            record.Time + _recording.Parts[_partIndex].Offset);
                    foreach (var fog in record.World.LocalFogs)
                        _futureWorldFirstSeen.TryAdd("fog:" + fog.Id, record.Time + _recording.Parts[_partIndex].Offset);
                    foreach (var emitter in record.World.ParticleEmitters)
                        if (emitter.EntityId.Length == 0) _futureWorldFirstSeen.TryAdd("particle:" + emitter.Id,
                            record.Time + _recording.Parts[_partIndex].Offset);
                }
                break;
            }
        }

        private void WarmFutureMap()
        {
            if (_futureWarmFailed || _futureWorld == null) return;
            if (_futureAssetScene == null && _futureWorld.AssetScene.Length != 0 &&
                _futureWorld.AssetRendererPaths.Count + _futureWorld.AssetTerrainPaths.Count != 0) return;
            if (_futureAssetScene != null && (_futureAssetScene.IsLoading ||
                !_futureAssetScene.Scene.IsValid() || !_futureAssetScene.Scene.isLoaded)) return;
            try
            {
                var watch = Stopwatch.StartNew();
                if (_futureAppearance == null)
                {
                    _futureAppearance = new ReplayAppearance();
                    _futureAppearanceShader = _futureWorld.Lights.Count == 0 && _unlitShader != null && _unlitShader.isSupported
                        ? _unlitShader : _shader!;
                    _futureAppearanceBuild = _futureAppearance.BuildSteps(_futureWorld, _futureAppearanceShader).GetEnumerator();
                }
                while (watch.Elapsed.TotalMilliseconds < 4)
                {
                    if (_futureAppearanceBuild != null)
                    {
                        if (_futureAppearanceBuild.MoveNext()) continue;
                        _futureAppearanceBuild.Dispose();
                        _futureAppearanceBuild = null;
                    }
                    if (_futureMeshIndex >= _futureWorld.Geometry.Count) break;
                    var geometry = _futureWorld.Geometry[_futureMeshIndex++];
                    if (geometry.EntityId.Length != 0 || geometry.MeshSourceId.Length != 0 ||
                        geometry.IsBoundsProxy || _futureMeshes.ContainsKey(geometry.Id)) continue;
                    var estimatedBytes = 4L * (geometry.Vertices.Length + (long)geometry.Triangles.Length +
                        geometry.Uvs.Length + geometry.Normals.Length + geometry.Tangents.Length);
                    if (_futureMeshBytes + estimatedBytes > 96L * 1024 * 1024) continue;
                    var mesh = MakeGeometryMesh(geometry);
                    if (mesh == null) continue;
                    _futureMeshes.Add(geometry.Id, mesh);
                    _futureMeshBytes += estimatedBytes;
                }
            }
            catch (Exception)
            {
                // Warming is optional; a normal world build retains the error
                // handling and source data used before this optimization.
                ReleaseFutureResources(keepMap: true);
                _futureWarmFailed = true;
            }
        }

        private void ReleaseFutureResources(bool keepMap = false)
        {
            _futureAppearanceBuild?.Dispose();
            _futureAppearanceBuild = null;
            _futureAppearance?.Dispose();
            _futureAppearance = null;
            foreach (var mesh in _futureMeshes.Values) if (mesh) Object.Destroy(mesh);
            _futureMeshes.Clear();
            _futureMeshBytes = 0;
            _futureMeshIndex = 0;
            if (!keepMap)
            {
                _futureWorld = null;
                _futureWorldFirstSeen.Clear();
            }
            _futureAppearanceShader = null;
            _useFutureMeshes = false;
            _futureWarmFailed = false;
        }

        private bool CanReuseWindowWorld(ReplaySession session, double localTime)
        {
            if (_displayWorld == null || _worldBuild != null) return false;
            var current = session.Worlds.LastOrDefault(record => record.Time <= localTime)?.World;
            if (current == null || current.CaptureSetId.Length == 0 ||
                current.CaptureSetId != _displayWorld.CaptureSetId) return false;
            if (session.Frames.SelectMany(frame => frame.Anchors)
                .Any(anchor => !_anchorRoots.ContainsKey(anchor.Id))) return false;
            var actors = new HashSet<string>(session.Frames.SelectMany(frame => frame.Entities)
                .Select(entity => entity.Id), StringComparer.Ordinal);
            var parts = session.Worlds.Where(record => record.Time <= localTime)
                .Select(record => record.World)
                .Where(world => world != null && world.CaptureSetId == current.CaptureSetId)
                .Select(world => world!).ToArray();
            if (parts.Any(part => part.AssetScene.Length != 0 &&
                (part.AssetScene != _displayWorld.AssetScene || part.AssetBuildIndex != _displayWorld.AssetBuildIndex))) return false;
            var candidate = new WorldSnapshot
            {
                CaptureSetId = current.CaptureSetId,
                AssetScene = _displayWorld.AssetScene,
                AssetBuildIndex = _displayWorld.AssetBuildIndex,
                AssetRendererPaths = parts.SelectMany(part => part.AssetRendererPaths).Distinct(StringComparer.Ordinal).ToList(),
                AssetTerrainPaths = parts.SelectMany(part => part.AssetTerrainPaths).Distinct(StringComparer.Ordinal).ToList(),
                Geometry = parts.SelectMany(part => part.Geometry)
                    .Where(geometry => geometry.EntityId.Length == 0 || actors.Contains(geometry.EntityId))
                    .GroupBy(geometry => geometry.Id).Select(group => group.First()).ToList(),
                Materials = parts.SelectMany(part => part.Materials)
                    .GroupBy(material => material.Id).Select(group => group.First()).ToList(),
                Textures = parts.SelectMany(part => part.Textures)
                    .GroupBy(texture => texture.Id).Select(group => group.First()).ToList(),
                Lights = parts.SelectMany(part => part.Lights)
                    .Where(light => light.EntityId.Length == 0 || actors.Contains(light.EntityId))
                    .GroupBy(light => light.Id).Select(group => group.First()).ToList(),
                ParticleEmitters = parts.SelectMany(part => part.ParticleEmitters)
                    .Where(emitter => emitter.EntityId.Length == 0 || actors.Contains(emitter.EntityId))
                    .GroupBy(emitter => emitter.Id).Select(group => group.First()).ToList(),
                LocalFogs = parts.SelectMany(part => part.LocalFogs)
                    .GroupBy(fog => fog.Id).Select(group => group.Last()).ToList(),
                Rooms = parts.LastOrDefault(part => part.Layer == "interior")?.Rooms ?? new List<RoomSnapshot>()
            };
            var displayed = _displayWorld;
            return displayed != null && candidate.Geometry.All(geometry => _renderGeometrySources.ContainsKey(geometry.Id)) &&
                candidate.Rooms.All(room => _interiorRooms.ContainsKey(room.Id)) &&
                ContainsAll(displayed.AssetRendererPaths, candidate.AssetRendererPaths, path => path) &&
                ContainsAll(displayed.AssetTerrainPaths, candidate.AssetTerrainPaths, path => path) &&
                ContainsEquivalent(displayed.Materials, candidate.Materials, material => material.Id, SameSnapshot) &&
                ContainsEquivalent(displayed.Textures, candidate.Textures, texture => texture.Id, SameTexture) &&
                ContainsEquivalent(displayed.Lights, candidate.Lights, light => light.Id, SameSnapshot) &&
                ContainsEquivalent(displayed.ParticleEmitters, candidate.ParticleEmitters, emitter => emitter.Id, SameSnapshot) &&
                ContainsEquivalent(displayed.LocalFogs, candidate.LocalFogs, fog => fog.Id, SameSnapshot);
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
            AlignLandingMap(ready, part);
            var previousWindow = _recording.Parts[_partIndex].Window;
            var targetWindow = _recording.Parts[part].Window;
            var sameRevision = _displayWorld != null && previousWindow != null && targetWindow != null &&
                ReferenceEquals(previousWindow.Index, targetWindow.Index) &&
                previousWindow.Index.WorldRevisionAt(previousWindow.Start + LocalTime) ==
                targetWindow.Index.WorldRevisionAt(targetWindow.Start + _recording.LocalTime(part, target));
            // An initial map can already contain later exterior/interior world
            // records, even though the file revision changes at this boundary.
            var retainWorld = sameRevision || CanReuseWindowWorld(ready, _recording.LocalTime(part, target));
            _allowEquivalentWorldReuse = retainWorld && !sameRevision;
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
            if (_recording.Parts[part].Window == null)
                _playerDeaths.AddRange(DeathsInSession(ready, _recording.Parts[part].Offset));
            IndexAnimationEvents();
            _partIndex = part;
            if (retainWorld && _displayWorld != null)
            {
                _visualState?.Dispose();
                _visualState = new ReplayVisualState(_displayWorld, _session.Events,
                    _geometryObjects, _anchorRoots, _assetScene, _root!.transform, ReplayLayer);
            }
            _hasRendererPoseCapability = _session.Header.Capabilities.Contains("child-renderer-poses");
            _sparseRendererPoses = _session.Header.Capabilities.Contains("sparse-entity-renderer-poses");
            _worldIndex = sameRevision ? FindWorld(_recording.LocalTime(part, target)) : -2;
            Seek(target);
            RebuildAudio();
            var selected = _players.FirstOrDefault(player => player.Name == selectedName);
            if (selected != null) _selectedId = selected.Id;
            PrefetchNextPart();
        }

        private void AlignLandingMap(ReplaySession session, int part)
        {
            var offset = _recording.Parts[part].Offset;
            if (_landingMapSet.Length == 0 &&
                ReplayMapTiming.TryFindFirstLandingMap(session, out var captureSetId, out var localTime))
            {
                _landingMapSet = captureSetId;
                _landingMapStart = offset + localTime;
            }
            if (_landingMapSet.Length == 0) return;
            ReplayMapTiming.AlignInitialCapture(session, _landingMapSet, Math.Max(0, _landingMapStart - offset));
            if (_futureWorld?.CaptureSetId != _landingMapSet) return;
            foreach (var key in _futureWorldFirstSeen.Keys.ToArray())
                _futureWorldFirstSeen[key] = Math.Min(_futureWorldFirstSeen[key], _landingMapStart);
        }

        public void DrawGui()
        {
            if (_disposed || _camera == null) return;
            DrawLabels();
            RefreshCursor();
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
            ApplyPostProcessing();
            ApplyDaylight();
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
                if (entity.PoseFromItemEvents) ApplyItemMotion(visual, entity.Id, LocalTime);
                else SetTransform(visual.Root.transform, entity.Position, entity.Rotation, entity.Scale);
                ConfigureProxy(visual, entity);
                ConfigureSwarm(visual, entity);
                visual.NativeRig?.ResetPose();
                visual.NativeRig?.ApplyView(entity.ViewRotation);
                if (!_session.Header.Capabilities.Contains("actor-bone-poses"))
                    ApplyAnimation(visual, entity.Id, LocalTime);
                UpdateBones(visual, entity.Bones);
                visual.NativeRig?.ApplyPlayerState(entity);
                visual.NativeRig?.Evaluate();
                if (visual.NativeRig != null && entity.Bones.Count == 0)
                    BlendNativeAnimationTransition(visual, entity, LocalTime);
                // The game keeps unused player slots in the scene. Their hidden spawn
                // positions must not become the initial camera target or a selectable player.
                if (entity.Active && entity.Kind.Equals("player", StringComparison.OrdinalIgnoreCase)) _players.Add(entity);
            }
            foreach (var id in _inactiveEntities)
                if (_entities.TryGetValue(id, out var visual)) SetActiveIfChanged(visual.Root, false);
            if (!_follow && _players.Count != 0 && !_players.Any(player => player.Id == _selectedId))
                _selectedId = _players[0].Id;
            if (_worldBuild == null)
            {
                ApplyRendererPoses();
                UpdateBakedPlayers();
                _visualState?.Sync(LocalTime);
            }
            ApplyMaskEffects();
        }

        private bool MaskAttaching(EntitySnapshot entity)
        {
            if (entity.State.ContainsKey("attaching") || entity.State.ContainsKey("finishedAttaching")) return NativeMaskState.Attaching(entity);
            // Older captures have the native Animator parameter instead of
            // the private field. An ordinary held mask must remain unlit.
            return entity.Active && _entities.TryGetValue(entity.Id, out var visual) && visual.Animators.Values.Any(animator =>
                animator && animator.parameters.Any(parameter => parameter.name == "attaching" && parameter.type == AnimatorControllerParameterType.Bool) && animator.GetBool("attaching"));
        }
        private void ApplyMaskEffects()
        {
            foreach (var entity in _frame.Entities)
            {
                if (!NativeMaskState.IsMask(entity)) continue;
                var attaching = MaskAttaching(entity);
                if (_maskEyesObjects.TryGetValue(entity.Id, out var eyes))
                    foreach (var obj in eyes) if (obj) SetActiveIfChanged(obj, attaching);
                foreach (var pair in _worldLights)
                    if (pair.Key && pair.Value.EntityId == entity.Id) pair.Key.enabled &= attaching;
            }
        }
        private void CacheMineMeshes(EntityVisual visual, EntitySnapshot entity)
        {
            visual.MineMeshes.Clear();
            foreach (var pair in _geometryObjects)
            {
                var obj = pair.Value; if (!obj || !(obj.GetComponent<Renderer>() is Renderer renderer)) continue;
                if (_dynamicGeometry.TryGetValue(pair.Key, out var geometry) && geometry.EntityId == entity.Id ||
                    obj.name.IndexOf("Landmine", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    renderer.bounds.SqrDistance(visual.Root.transform.position) < .25f)
                    visual.MineMeshes.Add(renderer);
            }
        }

        private void ApplyHeldItemPlacement()
        {
            // The local game's held object follows LocalItemHolder on the
            // first-person rig. Remote objects follow ServerItemHolder on the
            // animated third-person hand. Old files contain only the former
            // for the recorder, so derive the third-person pose from the hand
            // and the installed Item offsets without storing another stream.
            foreach (var holder in _players)
            {
                if (!holder.State.TryGetValue("currentlyHeldObjectServer", out var itemId) ||
                    itemId.Length == 0 || IsDead(holder)) continue;
                var firstPerson = _follow && _followTargetDistance < .45f && _selectedId == holder.Id;
                var item = _frame.Entities.FirstOrDefault(entity => entity.Id == itemId && entity.Kind == "item" && entity.Active);
                if (item == null || !item.State.TryGetValue("isHeld", out var held) ||
                    !string.Equals(held, "True", StringComparison.OrdinalIgnoreCase) ||
                    item.State.TryGetValue("$heldBy", out var ownerId) && ownerId != holder.Id ||
                    !_entities.TryGetValue(itemId, out var itemVisual)) continue;
                if (firstPerson)
                {
                    // Restore the captured view-model pose immediately when
                    // the camera crosses from follow into first person.
                    SetTransform(itemVisual.Root.transform, item.Position, item.Rotation, item.Scale);
                    continue;
                }
                if (!TryHeldItemPose(item, out var positionOffset, out var rotationOffset) ||
                    !_entities.TryGetValue(holder.Id, out var playerVisual)) continue;
                var nativeHolder = playerVisual.NativeRig?.ServerItemHolder;
                var hand = playerVisual.Bones.FirstOrDefault(pair =>
                    pair.Key.EndsWith("/hand.R[0]", StringComparison.Ordinal) && pair.Value).Value;
                if (!nativeHolder && !hand) continue;
                // v81 PlayerControllerB.serverItemHolder is a child of hand.R
                // at (0, .06, -.05), with identity rotation. GrabbableObject
                // applies Item.positionOffset/rotationOffset after that holder.
                var holderPosition = new Vector3(0f, .06f, -.05f);
                // GrabbableObject.LateUpdate rotates the item offset without
                // scaling it by the hand's non-unit bone transform.
                var targetPosition = nativeHolder ? nativeHolder!.position + nativeHolder.rotation * positionOffset :
                    hand.TransformPoint(holderPosition) + hand.rotation * positionOffset;
                var targetRotation = (nativeHolder ? nativeHolder!.rotation : hand.rotation) * rotationOffset;
                if (GameAccess.Finite(targetPosition) && GameAccess.Finite(targetRotation))
                    itemVisual.Root.transform.SetPositionAndRotation(targetPosition, targetRotation);
            }
        }

        private static bool TryHeldItemPose(EntitySnapshot item, out Vector3 position, out Quaternion rotation)
        {
            if (item.State.TryGetValue("itemProperties.positionOffset", out var positionText) &&
                item.State.TryGetValue("itemProperties.rotationOffset", out var rotationText) &&
                TryVector(positionText, out position) && TryVector(rotationText, out var euler))
            { rotation = Quaternion.Euler(euler); return true; }
            return ItemAssetRegistry.TryResolveHeldPose(item.Name, out position, out rotation);
        }

        private static bool TryVector(string text, out Vector3 value)
        {
            value = Vector3.zero; var parts = text.Split(',');
            if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
            value = new Vector3(x, y, z); return GameAccess.Finite(value);
        }

        private void ApplyPostProcessing()
        {
            if (_environment == null) return;
            foreach (var pair in _postFxEvents)
            {
                var events = pair.Value;
                var low = 0; var high = events.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (events[middle].Time <= LocalTime) low = middle + 1; else high = middle;
                }
                _environment.SyncPostProcess(pair.Key, low == 0 ? null : events[low - 1].PostProcess);
            }
        }

        private void ApplyDaylight()
        {
            if (_sunEvents.Count == 0)
            {
                var normalized = ReplayGameClock.Normalized(_frame.State);
                if (normalized.HasValue) _assetScene?.ApplyTimeOfDay(normalized.Value, _worldLights);
                return;
            }
            foreach (var events in _sunEvents.Values)
            {
                var low = 0; var high = events.Count;
                while (low < high)
                { var middle = low + (high - low) / 2; if (events[middle].Time <= LocalTime) low = middle + 1; else high = middle; }
                var current = events[Math.Max(0, low - 1)];
                var next = low < events.Count ? events[low] : current;
                var light = _worldLights.FirstOrDefault(pair => pair.Value.Id == current.Id).Key;
                if (!light && current.Name.Length != 0)
                    light = _worldLights.FirstOrDefault(pair => pair.Value.Name == current.Name && pair.Value.Type == "Directional").Key;
                var blend = next.Time <= current.Time ? 0f : (float)Math.Max(0, Math.Min(1, (LocalTime - current.Time) / (next.Time - current.Time)));
                current.Apply(light, next, blend);
            }
        }

        private void ApplyRendererPoses()
        {
            _completeRendererLists.Clear();
            _currentRendererPoses.Clear();
            _currentSceneRendererPoses.Clear();
            foreach (var entity in _frame.Entities)
            {
                if (!_sparseRendererPoses && (_hasRendererPoseCapability || entity.Renderers.Count != 0) &&
                    !entity.State.ContainsKey("$omittedRenderers"))
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
                    if (!_nativeSkinIds.Contains(pair.Key)) SetTransform(obj.transform, pose.Position, pose.Rotation, pose.Scale);
                    SetActiveIfChanged(obj, pose.Active);
                }
                else
                {
                    if (!_nativeSkinIds.Contains(pair.Key)) SetTransform(obj.transform, baseline.Position, baseline.Rotation, baseline.Scale);
                    // Older files use complete lists: a missing ID marks a
                    // destroyed child. Sparse files store explicit tombstones.
                    SetActiveIfChanged(obj, baseline.AttachedBonePath.Length != 0 || _sparseRendererPoses ? baseline.Active :
                        !_completeRendererLists.Contains(baseline.EntityId));
                }
            }
            foreach (var player in _players)
            {
                if (IsDead(player)) continue;
                foreach (var pair in _dynamicGeometry)
                    if (pair.Value.EntityId == player.Id && pair.Value.Name.StartsWith("LOD", StringComparison.Ordinal) &&
                        _geometryObjects.TryGetValue(pair.Key, out var body) && body)
                    {
                        SetActiveIfChanged(body, true);
                        if (body.GetComponent<Renderer>() is Renderer skin) skin.enabled = true;
                    }
            }
            foreach (var pair in _movingSceneGeometry)
            {
                if (!_geometryObjects.TryGetValue(pair.Key, out var obj) || !obj) continue;
                if (_doorMeshLinks.TryGetValue(pair.Key, out var link))
                {
                    var door = _frame.Entities.FirstOrDefault(entity => entity.Id == link.EntityId && entity.Kind == "door");
                    if (door == null) _firstDoorPoses.TryGetValue(link.EntityId, out door);
                    if (door != null)
                    {
                        var rotation = ToQuaternion(door.Rotation);
                        obj.transform.SetPositionAndRotation(ToVector(door.Position) + rotation * link.LocalPosition,
                            rotation * link.LocalRotation);
                        SetActiveIfChanged(obj, _currentSceneRendererPoses.TryGetValue(pair.Key, out var doorPose)
                            ? doorPose.Active : pair.Value.Active);
                        continue;
                    }
                }
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
            if (entity.Name == "Landmine" && visual.GeometryCount == 0 &&
                !entity.State.ContainsKey("hasExploded") && !visual.NativeHazard)
            {
                var source = Resources.FindObjectsOfTypeAll<MeshFilter>().FirstOrDefault(filter =>
                    filter && filter.name == "Landmine" && filter.sharedMesh && filter.GetComponent<Renderer>()?.sharedMaterial);
                if (source)
                {
                    var native = NewObject("Native landmine", visual.Root.transform);
                    AddMesh(native, source!.sharedMesh, source.GetComponent<Renderer>().sharedMaterial);
                    visual.NativeHazard = native;
                }
            }
            if (visual.GeometryCount != 0 && visual.NativeHazard)
            { visual.NativeHazard!.SetActive(false); Object.Destroy(visual.NativeHazard); visual.NativeHazard = null; }
            visual.Proxy.SetActive(visual.GeometryCount == 0 &&
                (kind == "hazard" && !visual.NativeHazard && !HasNearbyHazardModel(entity) || _showSkeletons && IsVisualKind(kind)));
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

        private bool HasNearbyHazardModel(EntitySnapshot entity)
        {
            var modelName = entity.Name == "TurretScript" ? "World Mount" :
                entity.Name == "Landmine" ? "World Landmine" : "";
            if (modelName.Length == 0) return false;
            if (_hazardsWithModel.Contains(entity.Id)) return true;
            var position = ToVector(entity.Position);
            foreach (var obj in _geometryObjects.Values)
            {
                if (!obj || obj.name != modelName) continue;
                var renderer = obj.GetComponent<Renderer>();
                if (renderer && (renderer.bounds.center - position).sqrMagnitude < (entity.Name == "Landmine" ? .25f : 4f))
                { _hazardsWithModel.Add(entity.Id); return true; }
            }
            if (entity.Name == "Landmine")
                foreach (var visual in _entities.Values)
                    if (visual.NativeHazard && (visual.NativeHazard!.transform.position - position).sqrMagnitude < .25f)
                    { _hazardsWithModel.Add(entity.Id); return true; }
            return false;
        }

        // Circuit bees and docile locusts are VFX Graph particles in the live
        // game, so they have no MeshRenderer that a world scan can archive.
        // Rebuild a bounded visual swarm around their recorded entity pose.
        private void ConfigureSwarm(EntityVisual visual, EntitySnapshot entity)
        {
            var swarm = entity.Kind == "enemy" && (entity.Name.IndexOf("bee", StringComparison.OrdinalIgnoreCase) >= 0 ||
                entity.Name.IndexOf("locust", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!swarm) return;
            if (!visual.SwarmAttempted)
            {
                visual.SwarmAttempted = true;
                visual.NativeSwarm = NativeSwarmEffect.Create(entity, visual.Root.transform, ReplayLayer);
                if (visual.NativeSwarm != null) visual.NativeSwarm.ItemPosition = ResolveSwarmItemPosition;
            }
            visual.NativeSwarm?.Sync(entity, LocalTime, IsPlaying && !IsBuffering, Speed, _camera!.transform.position, _frame, _camera);
        }

        private Vector3? ResolveSwarmItemPosition(string id) => _entities.TryGetValue(id, out var visual) && visual.Root.activeInHierarchy ? visual.Root.transform.position : (Vector3?)null;

        private Transform? ResolveSoundParent(string id)
        {
            if (id.Length == 0) return _root ? _root!.transform : null;
            if (id.StartsWith("anchor:", StringComparison.Ordinal))
                return _anchorRoots.TryGetValue(id.Substring(7), out var anchor) ? anchor : null;
            return _entities.TryGetValue(id, out var entity) ? entity.Root.transform : null;
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

        private void IndexAnimationEvents()
        {
            _windowActorIds.Clear();
            foreach (var frame in _session.Frames)
                foreach (var actor in frame.Entities) _windowActorIds.Add(actor.Id);
            _nativeSounds?.Dispose();
            // PCM is authoritative for older recordings. Never layer
            // the same native events on top of a captured source stream.
            _nativeSounds = _session.Header.Capabilities.Contains("source-audio-adpcm-22050")
                ? null : new NativeSoundPlayback(_session);
            _animationEvents.Clear();
            _animationParameters.Clear();
            _animationTracks.Clear();
            _fallbackAnimationTracks.Clear();
            _animationPaths.Clear();
            _itemMotionEvents.Clear();
            _postFxEvents.Clear();
            _sunEvents.Clear();
            _itemImpactEvents = _session.Events.Where(evt => evt.Category == "item" && evt.Name == "impact")
                .OrderBy(evt => evt.Time).ToArray();
            _itemImpactCursor = 0;
            _lastItemImpactTime = -1;
            foreach (var evt in _session.Events)
            {
                if (evt.Category == "animation" &&
                    RenderVisibilityPolicy.IsRadarPath(evt.AnimationTrack?.AnimatorPath ?? evt.Data.GetValueOrDefault("animatorPath", ""))) continue;
                if (evt.Category == "lighting" && evt.Name == "sun")
                {
                    var sun = SunLighting.Read(evt);
                    if (sun != null)
                    {
                        if (!_sunEvents.TryGetValue(evt.EntityId, out var changes)) _sunEvents[evt.EntityId] = changes = new List<SunLighting>();
                        changes.Add(sun);
                    }
                    continue;
                }
                if (evt.Category == "postfx" && evt.Name == "component" && evt.PostProcess != null)
                {
                    if (!_postFxEvents.TryGetValue(evt.PostProcess.Type, out var changes))
                        _postFxEvents[evt.PostProcess.Type] = changes = new List<ReplayEvent>();
                    changes.Add(evt);
                    continue;
                }
                if (evt.Category == "item" && evt.Name == "pose" && evt.ItemMotion != null)
                {
                    if (!_itemMotionEvents.TryGetValue(evt.EntityId, out var motions))
                        _itemMotionEvents[evt.EntityId] = motions = new List<ReplayEvent>();
                    motions.Add(evt);
                    continue;
                }
                if (evt.Category == "animation" && evt.Name == "track" && evt.AnimationTrack != null)
                {
                    var binding = AnimationBindingKey(evt.EntityId, evt.AnimationTrack.AnimatorPath);
                    if (!_animationTracks.TryGetValue(binding, out var tracks))
                        _animationTracks[binding] = tracks = new Dictionary<string, AnimationTrackSnapshot>(StringComparer.Ordinal);
                    tracks[AnimationTrackKey(evt.AnimationTrack.Layer,
                        evt.AnimationTrack.StateHash, evt.AnimationTrack.Clip)] = evt.AnimationTrack;
                    continue;
                }
                if (evt.Category == "animation" && evt.Name == "parameters")
                {
                    var path = evt.Data.TryGetValue("animatorPath", out var recordedPath) ? recordedPath : "";
                    var binding = AnimationBindingKey(evt.EntityId, path);
                    if (!_animationParameters.TryGetValue(binding, out var parameters))
                        _animationParameters[binding] = parameters =
                            new Dictionary<string, List<KeyValuePair<double, string>>>(StringComparer.Ordinal);
                    foreach (var change in evt.Data)
                    {
                        if (change.Key == "animatorPath") continue;
                        if (!parameters.TryGetValue(change.Key, out var changes))
                            parameters[change.Key] = changes = new List<KeyValuePair<double, string>>();
                        changes.Add(new KeyValuePair<double, string>(evt.Time, change.Value));
                    }
                    continue;
                }
                if (evt.Category != "animation" || evt.Name != "state" ||
                    !evt.Data.TryGetValue("layer", out var layerText) ||
                    !int.TryParse(layerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer) ||
                    layer < 0 || layer > 31) continue;
                var animatorPath = evt.Data.TryGetValue("animatorPath", out var statePath) ? statePath : "";
                var animatorKey = AnimationBindingKey(evt.EntityId, animatorPath);
                if (!_animationPaths.TryGetValue(evt.EntityId, out var paths))
                    _animationPaths[evt.EntityId] = paths = new HashSet<string>(StringComparer.Ordinal);
                paths.Add(animatorPath);
                if (!_animationEvents.TryGetValue(animatorKey, out var layers))
                    _animationEvents[animatorKey] = layers = new Dictionary<int, List<ReplayEvent>>();
                if (!layers.TryGetValue(layer, out var events)) layers[layer] = events = new List<ReplayEvent>();
                events.Add(evt);
            }
            foreach (var layers in _animationEvents.Values)
                foreach (var events in layers.Values) events.Sort((a, b) => a.Time.CompareTo(b.Time));
            foreach (var parameters in _animationParameters.Values)
                foreach (var changes in parameters.Values) changes.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var motions in _itemMotionEvents.Values)
                motions.Sort((a, b) => a.Time.CompareTo(b.Time));
            foreach (var changes in _postFxEvents.Values)
                changes.Sort((a, b) => a.Time.CompareTo(b.Time));
            foreach (var pair in _animationTracks)
            {
                var fallback = pair.Value.Values.Where(track => track.Layer == 0)
                    .OrderByDescending(FallbackTrackPriority).FirstOrDefault();
                if (fallback != null && FallbackTrackPriority(fallback) > 0)
                    _fallbackAnimationTracks[pair.Key] = fallback;
            }
        }

        private static int FallbackTrackPriority(AnimationTrackSnapshot track)
        {
            if (track.Clip.StartsWith("Idle", StringComparison.OrdinalIgnoreCase) && !track.Looping &&
                track.Phases.Length == 2 && track.Phases[0] == 0 && track.Phases[1] == 1) return 100;
            if (track.Clip.Equals("Walk", StringComparison.OrdinalIgnoreCase)) return 90;
            if (track.Clip.StartsWith("Walk", StringComparison.OrdinalIgnoreCase)) return 80;
            if (track.Clip.StartsWith("Sprint", StringComparison.OrdinalIgnoreCase) ||
                track.Clip.StartsWith("Run", StringComparison.OrdinalIgnoreCase)) return 70;
            return 0;
        }

        private bool ApplyItemMotion(EntityVisual visual, string entityId, double time)
        {
            if (!_itemMotionEvents.TryGetValue(entityId, out var events)) return false;
            var low = 0; var high = events.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (events[middle].Time <= time) low = middle + 1; else high = middle;
            }
            if (low == 0) return false;
            var evt = events[low - 1];
            var motion = evt.ItemMotion!;
            var position = ToVector(motion.Position);
            var rotation = ToQuaternion(motion.Rotation);
            if (motion.Mode == "fall")
            {
                var phase = motion.FallTime + (float)((time - evt.Time) * motion.FallRate);
                var clamped = Mathf.Clamp01(phase);
                var index = Mathf.Min(15, Mathf.FloorToInt(clamped * 16f));
                var fraction = clamped * 16f - index;
                var amount = Mathf.LerpUnclamped(motion.Curve[index], motion.Curve[index + 1], fraction);
                position = Vector3.Lerp(position, ToVector(motion.Target), amount);
                var rotationAmount = Mathf.Clamp01(1f - Mathf.Exp(-2.3333333f *
                    Mathf.Max(0f, phase - motion.FallTime)));
                rotation = phase >= 1f ? ToQuaternion(motion.TargetRotation) :
                    Quaternion.Slerp(rotation, ToQuaternion(motion.TargetRotation), rotationAmount);
            }
            if (motion.AnchorId.Length != 0)
            {
                var anchor = _frame.Anchors.FirstOrDefault(value => value.Id == motion.AnchorId);
                if (anchor != null)
                {
                    var anchorRotation = ToQuaternion(anchor.Rotation);
                    position = ToVector(anchor.Position) + anchorRotation * Vector3.Scale(position, ToVector(anchor.Scale));
                    rotation = anchorRotation * rotation;
                }
            }
            SetTransform(visual.Root.transform, GameAccess.Vec(position), GameAccess.Rot(rotation), motion.Scale);
            return true;
        }

        private static string AnimationBindingKey(string entityId, string path) => entityId + "\u001f" + path;
        private static string AnimationTrackKey(int layer, int stateHash, string clip) =>
            layer.ToString(CultureInfo.InvariantCulture) + ":" +
            stateHash.ToString(CultureInfo.InvariantCulture) + ":" + clip;

        private bool? HeldTwoHandedAnimation(string bindingKey)
        {
            var separator = bindingKey.IndexOf('\u001f');
            if (separator < 0) return null;
            var player = _frame.Entities.FirstOrDefault(entity =>
                entity.Id == bindingKey.Substring(0, separator) && entity.Kind == "player");
            if (player == null || !player.State.TryGetValue("currentlyHeldObjectServer", out var itemId) ||
                itemId.Length == 0) return null;
            if (player.State.TryGetValue("twoHandedAnimation", out var recorded) &&
                bool.TryParse(recorded, out var twoHanded)) return twoHanded;
            // Earlier files did not save this bit. The installed Item asset has
            // the same flag the game's PlayerControllerB uses for its hand layer.
            var item = _frame.Entities.FirstOrDefault(entity => entity.Id == itemId && entity.Kind == "item");
            return item == null ? null : ItemAssetRegistry.ResolveTwoHandedAnimation(item.Name);
        }

        private static bool IsHandLayer(ReplayEvent state, int layer, string name, int legacyLayer) =>
            state.Data.TryGetValue("layerName", out var recordedName) && recordedName.Length != 0
                ? string.Equals(recordedName, name, StringComparison.Ordinal)
                : layer == legacyLayer;

        private void ApplyAnimationParameters(EntityVisual visual, string bindingKey,
            Animator animator, HashSet<string> acceptedKeys, double time)
        {
            if (!_animationParameters.TryGetValue(bindingKey, out var parameters)) return;
            var playerController = visual.NativeRig?.PlayerController(animator);
            foreach (var parameter in parameters)
            {
                var key = parameter.Key;
                if (!acceptedKeys.Contains(key)) continue;
                if (key.Length < 2 || !int.TryParse(key.Substring(1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var hash)) continue;
                var changes = parameter.Value;
                var low = 0; var high = changes.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (changes[middle].Key <= time) low = middle + 1; else high = middle;
                }
                if (low == 0) continue;
                var value = changes[low - 1].Value;
                if (key[0] == 'f' && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    GameAccess.Finite(number)) { if (playerController.HasValue) playerController.Value.SetFloat(hash, number); else animator.SetFloat(hash, number); }
                else if (key[0] == 'i' && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    { if (playerController.HasValue) playerController.Value.SetInteger(hash, integer); else animator.SetInteger(hash, integer); }
                else if (key[0] == 'b') { if (playerController.HasValue) playerController.Value.SetBool(hash, value == "1"); else animator.SetBool(hash, value == "1"); }
            }
        }

        private void ApplyAnimation(EntityVisual visual, string entityId, double time)
        {
            if (visual.NativeRig != null && !visual.NativeRig.Prepare()) return;
            if (!_animationPaths.TryGetValue(entityId, out var paths)) return;
            foreach (var path in paths)
            {
                var bindingKey = AnimationBindingKey(entityId, path);
                if (!_animationEvents.TryGetValue(bindingKey, out var layers)) continue;
                // A learned track is the pose the installed game actually
                // produced. It also avoids a native controller retaining its
                // bind pose when Unity cannot bind the reconstructed skeleton.
                var nativeAnimator = visual.NativeRig != null ? EnsureReplayAnimator(visual, path, layers) : null;
                if (!nativeAnimator && ApplyRecordedTrack(visual, bindingKey, layers, time))
                {
                    if (visual.Animators.TryGetValue(path, out var unused) && unused) unused.enabled = false;
                    ApplyNativeHeldPose(visual, bindingKey, layers, time);
                    BlendAnimationTransition(visual, bindingKey, layers, time);
                    continue;
                }
                // A missing player clip must not leave the previous spawn or
                // interaction pose on the skeleton. The reconstructed player
                // rig cannot reliably bind the installed Animator in the menu.
                if (!nativeAnimator && visual.Kind == "player" && ApplyFallbackPose(visual, bindingKey, layers, time))
                {
                    if (visual.Animators.TryGetValue(path, out var unused) && unused) unused.enabled = false;
                    ApplyNativeHeldPose(visual, bindingKey, layers, time);
                    BlendAnimationTransition(visual, bindingKey, layers, time);
                    continue;
                }
                var animator = nativeAnimator ? nativeAnimator : EnsureReplayAnimator(visual, path, layers);
                if (animator == null || !animator)
                {
                    if (ApplyFallbackPose(visual, bindingKey, layers, time))
                    {
                        ApplyNativeHeldPose(visual, bindingKey, layers, time);
                        BlendAnimationTransition(visual, bindingKey, layers, time);
                    }
                    continue;
                }
                if (!animator.enabled) { animator.enabled = true; animator.Rebind(); }
                if (visual.NativeRig != null && visual.Kind != "player") animator.WriteDefaultValues();
                var acceptedKeys = visual.AnimatorParameterKeysByPath.TryGetValue(path, out var keys)
                    ? keys : visual.AnimatorParameterKeys;
                ApplyAnimationParameters(visual, bindingKey, animator, acceptedKeys, time);
                var playerController = visual.NativeRig?.PlayerController(animator);
                foreach (var pair in layers)
                {
                    if (pair.Key >= animator.layerCount) continue;
                    var events = pair.Value;
                    var low = 0; var high = events.Count;
                    while (low < high)
                    {
                        var middle = low + (high - low) / 2;
                        if (events[middle].Time <= time) low = middle + 1; else high = middle;
                    }
                    if (low == 0) continue;
                    var evt = events[low - 1];
                    if (!evt.Data.TryGetValue("hash", out var hashText) ||
                        !int.TryParse(hashText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash) ||
                        !evt.Data.TryGetValue("normalizedTime", out var normalizedText) ||
                        !float.TryParse(normalizedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var normalized) ||
                        !(playerController.HasValue ? playerController.Value.HasState(pair.Key, hash) : animator.HasState(pair.Key, hash))) continue;
                    if (evt.Data.TryGetValue("duration", out var durationText) &&
                        float.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) &&
                        duration > .001f && evt.Data.TryGetValue("speed", out var speedText) &&
                        float.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                        normalized += (float)((time - evt.Time) * speed / duration);
                    if (!GameAccess.Finite(normalized)) continue;
                    if (evt.Data.TryGetValue("weight", out var weightText) &&
                        float.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) &&
                        GameAccess.Finite(weight))
                    { if (playerController.HasValue) playerController.Value.SetLayerWeight(pair.Key, Mathf.Clamp01(weight)); else animator.SetLayerWeight(pair.Key, Mathf.Clamp01(weight)); }
                    if (playerController.HasValue) playerController.Value.Play(hash, pair.Key, normalized);
                    else animator.Play(hash, pair.Key, normalized);
                }
                if (!playerController.HasValue) animator.Update(0);
                if (visual.NativeRig == null) ApplyNativeHeldPose(visual, bindingKey, layers, time);
            }
        }

        private void ApplyNativeHeldPose(EntityVisual visual, string bindingKey,
            Dictionary<int, List<ReplayEvent>> layers, double time)
        {
            if (visual.Kind != "player" || HeldTwoHandedAnimation(bindingKey) is not bool bothHands) return;
            ReplayEvent? selected = null;
            var selectedLayer = -1;
            var selectedWeight = 0f;
            foreach (var pair in layers)
            {
                if (pair.Key == 0 || pair.Key <= selectedLayer) continue;
                var low = 0; var high = pair.Value.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (pair.Value[middle].Time <= time) low = middle + 1; else high = middle;
                }
                if (low == 0) continue;
                var evt = pair.Value[low - 1];
                if (!evt.Data.TryGetValue("clip", out var clip) || clip.Length == 0 ||
                    !evt.Data.TryGetValue("weight", out var text) ||
                    !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) ||
                    weight < .05f || !bothHands && IsHandLayer(evt, pair.Key, "HoldingItemsBothHands", 4)) continue;
                selected = evt; selectedLayer = pair.Key; selectedWeight = weight;
            }
            if (selected == null) return;
            var name = selected.Data["clip"];
            if (name.StartsWith("Hold", StringComparison.Ordinal) && (bothHands ?
                IsHandLayer(selected, selectedLayer, "HoldingItemsBothHands", 4) :
                IsHandLayer(selected, selectedLayer, "HoldingItemsRightHand", 2)))
            {
                int.TryParse(selected.Data.GetValueOrDefault("hash"), out var hash);
                NativeHeldItemPose.Apply(visual.Bones, bothHands, name, hash, selectedWeight);
            }
        }

        private void BlendAnimationTransition(EntityVisual visual, string bindingKey,
            Dictionary<int, List<ReplayEvent>> layers, double time)
        {
            const float blendSeconds = .18f;
            var boundary = AnimationTransitionBoundary(layers, time, blendSeconds);
            if (!double.IsFinite(boundary) || time - boundary >= blendSeconds || visual.Bones.Count == 0) return;
            var incoming = visual.Bones.Values.Where(bone => bone)
                .Select(bone => (Bone: bone, Position: bone.localPosition, Rotation: bone.localRotation)).ToArray();
            var before = Math.Max(0, boundary - .001);
            if (!ApplyRecordedTrack(visual, bindingKey, layers, before) &&
                !ApplyFallbackPose(visual, bindingKey, layers, before)) return;
            ApplyNativeHeldPose(visual, bindingKey, layers, before);
            var amount = Mathf.SmoothStep(0f, 1f, (float)((time - boundary) / blendSeconds));
            foreach (var pose in incoming)
            {
                var fromPosition = pose.Bone.localPosition;
                var fromRotation = pose.Bone.localRotation;
                pose.Bone.localPosition = Vector3.LerpUnclamped(fromPosition, pose.Position, amount);
                pose.Bone.localRotation = Quaternion.SlerpUnclamped(fromRotation, pose.Rotation, amount);
            }
        }

        private static double AnimationTransitionBoundary(Dictionary<int, List<ReplayEvent>> layers, double time, float blendSeconds)
        {
            var boundary = double.NegativeInfinity;
            foreach (var pair in layers)
            {
                var states = pair.Value;
                var low = 0; var high = states.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (states[middle].Time <= time) low = middle + 1; else high = middle;
                }
                for (var index = low - 1; index > 0 && time - states[index].Time <= blendSeconds; index--)
                {
                    var now = states[index].Data;
                    var prior = states[index - 1].Data;
                    if (now.GetValueOrDefault("hash") == prior.GetValueOrDefault("hash") &&
                        now.GetValueOrDefault("clip") == prior.GetValueOrDefault("clip")) continue;
                    if (pair.Key != 0 && !Active(now) && !Active(prior)) break;
                    boundary = Math.Max(boundary, states[index].Time);
                    break;
                }
            }
            return boundary;
        }

        private void BlendNativeAnimationTransition(EntityVisual visual, EntitySnapshot entity, double time)
        {
            const float blendSeconds = .18f;
            var entityId = entity.Id;
            if (visual.NativeRig == null || !_animationPaths.TryGetValue(entityId, out var paths)) return;
            var boundary = double.NegativeInfinity;
            foreach (var path in paths)
                if (_animationEvents.TryGetValue(AnimationBindingKey(entityId, path), out var layers))
                    boundary = Math.Max(boundary, AnimationTransitionBoundary(layers, time, blendSeconds));
            if (!double.IsFinite(boundary) || time - boundary >= blendSeconds) return;
            var incoming = visual.NativeRig.Bones.Values.Where(bone => bone)
                .Select(bone => (Bone: bone, Position: bone.localPosition, Rotation: bone.localRotation)).ToArray();
            // Evaluate both sides through the same native controller and IK rig.
            // Restore current controller state before displaying the mixed pose.
            visual.NativeRig.ResetPose();
            visual.NativeRig.ApplyView(entity.ViewRotation);
            ApplyAnimation(visual, entityId, Math.Max(0, boundary - .001));
            visual.NativeRig.ApplyPlayerState(entity);
            visual.NativeRig.Evaluate();
            var previous = incoming.Select(pose => (Position: pose.Bone.localPosition, Rotation: pose.Bone.localRotation)).ToArray();
            visual.NativeRig.ResetPose();
            visual.NativeRig.ApplyView(entity.ViewRotation);
            ApplyAnimation(visual, entityId, time);
            visual.NativeRig.ApplyPlayerState(entity);
            visual.NativeRig.Evaluate();
            var amount = Mathf.SmoothStep(0f, 1f, (float)((time - boundary) / blendSeconds));
            for (var i = 0; i < incoming.Length; i++)
            {
                incoming[i].Bone.localPosition = Vector3.LerpUnclamped(previous[i].Position, incoming[i].Position, amount);
                incoming[i].Bone.localRotation = Quaternion.SlerpUnclamped(previous[i].Rotation, incoming[i].Rotation, amount);
            }
        }

        private static bool Active(Dictionary<string, string> state) =>
            state.TryGetValue("weight", out var text) &&
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) && weight >= .05f;

        private bool ApplyFallbackPose(EntityVisual visual, string bindingKey,
            Dictionary<int, List<ReplayEvent>> layers, double time)
        {
            if (!_fallbackAnimationTracks.TryGetValue(bindingKey, out var track)) return false;
            var sample = 0;
            var neutralIdle = false;
            if (layers.TryGetValue(0, out var states) &&
                _animationTracks.TryGetValue(bindingKey, out var tracks))
            {
                var low = 0; var high = states.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (states[middle].Time <= time) low = middle + 1; else high = middle;
                }
                if (low != 0 && states[low - 1].Data.TryGetValue("clip", out var clip) &&
                    clip.StartsWith("Crouch", StringComparison.OrdinalIgnoreCase))
                {
                    var crouch = tracks.Values.FirstOrDefault(candidate => candidate.Layer == 0 &&
                        candidate.Clip.Equals("CrouchDown", StringComparison.OrdinalIgnoreCase));
                    if (crouch != null) { track = crouch; sample = track.Phases.Length - 1; }
                }
                else if (low != 0 && states[low - 1].Data.TryGetValue("clip", out clip) &&
                    clip.StartsWith("Idle", StringComparison.OrdinalIgnoreCase) &&
                    !track.Clip.StartsWith("Idle", StringComparison.OrdinalIgnoreCase))
                    neutralIdle = true;
            }
            var count = track.BonePaths.Count;
            for (var i = 0; i < count; i++)
            {
                var bone = GetBone(visual, track.BonePaths[i]);
                var position = (sample * count + i) * 3;
                var rotation = (sample * count + i) * 4;
                var posePosition = new Vector3(track.Positions[position], track.Positions[position + 1],
                    track.Positions[position + 2]);
                var poseRotation = new Quaternion(track.Rotations[rotation], track.Rotations[rotation + 1],
                    track.Rotations[rotation + 2], track.Rotations[rotation + 3]);
                if (neutralIdle)
                {
                    // A missing idle clip must settle to a neutral stance,
                    // rather than freeze at the first stride of a walk cycle.
                    for (var phase = 1; phase < track.Phases.Length; phase++)
                    {
                        var p = (phase * count + i) * 3;
                        var r = (phase * count + i) * 4;
                        var nextPosition = new Vector3(track.Positions[p], track.Positions[p + 1], track.Positions[p + 2]);
                        var nextRotation = new Quaternion(track.Rotations[r], track.Rotations[r + 1],
                            track.Rotations[r + 2], track.Rotations[r + 3]);
                        posePosition = Vector3.LerpUnclamped(posePosition, nextPosition, 1f / (phase + 1));
                        poseRotation = Quaternion.SlerpUnclamped(poseRotation, nextRotation, 1f / (phase + 1));
                    }
                }
                bone.localPosition = posePosition;
                bone.localRotation = poseRotation;
            }
            return true;
        }

        private Animator? EnsureReplayAnimator(EntityVisual visual, string path,
            Dictionary<int, List<ReplayEvent>> layers)
        {
            if (visual.Animators.TryGetValue(path, out var existing) && existing) return existing;
            if (path.Length == 0 && visual.Animator) return visual.Animator;
            if (!visual.UnresolvedAnimatorPaths.Add(path)) return null;
            var states = layers.Values.SelectMany(list => list).ToArray();
            var source = states.FirstOrDefault(state => state.Data.ContainsKey("controller"));
            if (source == null || !source.Data.TryGetValue("controller", out var controllerName) ||
                controllerName.Length == 0) return null;
            var avatarName = source.Data.TryGetValue("avatar", out var name) ? name : "";
            var clips = states.Select(state => state.Data.TryGetValue("clip", out var clip) ? clip : "");
            var (controller, avatar) = AnimationAssetRegistry.Resolve(controllerName, avatarName, visual.Kind, clips);
            if (controller == null || (avatarName.Length != 0 && avatar == null)) return null;
            var root = path.Length == 0 ? visual.Root.transform : GetBone(visual, path);
            var animator = root.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            if (avatar != null) animator.avatar = avatar;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.speed = 0;
            RegisterAnimator(visual, path, animator);
            return animator;
        }

        private void EnsureNativeActorRig(EntityVisual visual, string key, string entityId)
        {
            if ((visual.Kind != "enemy" && visual.Kind != "player") || visual.NativeRig != null) return;
            Component? prefab = null;
            RuntimeAnimatorController? remote = null;
            if (visual.Kind == "enemy") prefab = key.Length == 0 ? null : PrefabAssetRegistry.ResolvePrefab(key);
            else
            {
                var round = GameAccess.Singleton("StartOfRound") ?? GameAccess.Find("StartOfRound").FirstOrDefault();
                if (GameAccess.Read(round, "otherClientsAnimatorController") == null && GameAccess.Type("StartOfRound") is Type roundType)
                    round = Resources.FindObjectsOfTypeAll(roundType).FirstOrDefault(candidate =>
                        GameAccess.Read(candidate, "otherClientsAnimatorController") is RuntimeAnimatorController) ?? round;
                var player = GameAccess.Read(round, "playerPrefab") as GameObject;
                if (!player && GameAccess.Type("GameNetcodeStuff.PlayerControllerB") is Type type)
                    player = Resources.FindObjectsOfTypeAll(type).OfType<Component>()
                        .Where(component => component && !component.gameObject.scene.IsValid()).Select(component => component.gameObject).FirstOrDefault();
                if (player && GameAccess.Type("GameNetcodeStuff.PlayerControllerB") is Type playerType) prefab = player!.GetComponent(playerType);
                remote = GameAccess.Read(round, "otherClientsAnimatorController") as RuntimeAnimatorController;
                // Preserve the recorded local/remote body controller using the
                // verified player assets, even when they share state hashes.
                if (prefab && GameAccess.Read(prefab, "playerBodyAnimator") is Animator body &&
                    _animationEvents.TryGetValue(AnimationBindingKey(entityId, EntityTracker.RelativePath(prefab!.transform, body.transform)), out var layers) &&
                    layers.TryGetValue(0, out var states))
                {
                    var state = states.LastOrDefault(evt => evt.Time <= LocalTime && evt.Data.ContainsKey("controller")) ??
                        states.FirstOrDefault(evt => evt.Data.ContainsKey("controller"));
                    var local = GameAccess.Read(round, "localClientAnimatorController") as RuntimeAnimatorController;
                    if (state != null && local && state.Data["controller"] == local!.name) remote = local;
                }
            }
            if (!prefab) return;
            visual.NativeRig = new NativeActorRig(prefab!, visual.Root.transform, ReplayLayer, remote);
            foreach (var bone in visual.NativeRig.Bones) visual.Bones[bone.Key] = bone.Value;
            foreach (var animator in visual.Animators.Values) if (animator) Object.Destroy(animator);
            visual.Animator = null; visual.Animators.Clear(); visual.AnimatorParameterKeys.Clear();
            visual.AnimatorParameterKeysByPath.Clear(); visual.UnresolvedAnimatorPaths.Clear();
            foreach (var animator in visual.NativeRig.Animators) RegisterAnimator(visual, animator.Key, animator.Value);
        }

        private void RegisterAnimator(EntityVisual visual, string path, Animator animator)
        {
            visual.Animators[path] = animator;
            if (visual.Animator == null) visual.Animator = animator;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in animator.parameters)
            {
                var prefix = parameter.type == AnimatorControllerParameterType.Float ? "f" :
                    parameter.type == AnimatorControllerParameterType.Int ? "i" :
                    parameter.type == AnimatorControllerParameterType.Bool ? "b" : "";
                if (prefix.Length != 0) keys.Add(prefix + parameter.nameHash.ToString(CultureInfo.InvariantCulture));
            }
            visual.AnimatorParameterKeysByPath[path] = keys;
            if (visual.Animator == animator) visual.AnimatorParameterKeys.UnionWith(keys);
        }

        private bool ApplyRecordedTrack(EntityVisual visual, string bindingKey,
            Dictionary<int, List<ReplayEvent>> layers, double time, int? onlyLayer = null)
        {
            // The recorded track is an observed output of the game's Animator.
            if (!_animationTracks.TryGetValue(bindingKey, out var tracks)) return false;
            var oneHanded = visual.Kind == "player" && HeldTwoHandedAnimation(bindingKey) == false;
            var selectedLayer = -1;
            ReplayEvent? selectedEvent = null;
            foreach (var pair in layers)
            {
                if (onlyLayer.HasValue && pair.Key != onlyLayer.Value) continue;
                var events = pair.Value;
                var low = 0; var high = events.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (events[middle].Time <= time) low = middle + 1; else high = middle;
                }
                if (low == 0) continue;
                var candidate = events[low - 1];
                if (oneHanded && !onlyLayer.HasValue &&
                    IsHandLayer(candidate, pair.Key, "HoldingItemsBothHands", 4)) continue;
                if (pair.Key != 0 && (!candidate.Data.TryGetValue("clip", out var overlayClip) ||
                    overlayClip.Length == 0 || !candidate.Data.TryGetValue("weight", out var weightText) ||
                    !float.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) ||
                    weight < .05f)) continue;
                if (pair.Key > selectedLayer) { selectedLayer = pair.Key; selectedEvent = candidate; }
            }
            if (selectedEvent == null) return false;
            // An active upper layer takes precedence even in older recordings
            // that have no learned track for it. Try the installed Animator.
            var evt = selectedEvent;
            var layer = selectedLayer;
            if (!evt.Data.TryGetValue("clip", out var clip)) return false;
            var bothHanded = visual.Kind == "player" && HeldTwoHandedAnimation(bindingKey) == true;
            var defaultHold = clip.StartsWith("Hold", StringComparison.Ordinal) &&
                (oneHanded && IsHandLayer(evt, layer, "HoldingItemsRightHand", 2) ||
                 bothHanded && IsHandLayer(evt, layer, "HoldingItemsBothHands", 4));
            if (defaultHold && !onlyLayer.HasValue &&
                (ApplyRecordedTrack(visual, bindingKey, layers, time, 0) ||
                 ApplyFallbackPose(visual, bindingKey, layers, time)))
            {
                // These static holds also drive IK targets outside the skin.
                // Replay the settled native rig result over moving legs/torso;
                // a clip-only finger pose or contaminated full-body track loses it.
                return true;
            }
            var hash = 0;
            if (evt.Data.TryGetValue("hash", out var hashText))
                int.TryParse(hashText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hash);
            if (!tracks.TryGetValue(AnimationTrackKey(layer, hash, clip), out var track) &&
                !tracks.TryGetValue(AnimationTrackKey(layer, 0, clip), out track)) return false;
            if (!evt.Data.TryGetValue("normalizedTime", out var normalizedText) ||
                !float.TryParse(normalizedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var normalized)) return false;
            if (evt.Data.TryGetValue("duration", out var durationText) &&
                float.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) && duration > .001f &&
                evt.Data.TryGetValue("speed", out var speedText) &&
                float.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                normalized += (float)((time - evt.Time) * speed / duration);
            if (!GameAccess.Finite(normalized)) return false;
            var phase = track.Looping ? Mathf.Repeat(normalized, 1f) : Mathf.Clamp01(normalized);
            var phases = track.Phases;
            var upper = Array.BinarySearch(phases, phase);
            if (upper < 0) upper = ~upper;
            var lower = Math.Max(0, upper - 1);
            float blend;
            if (upper >= phases.Length)
            {
                if (track.Looping) { upper = 0; blend = (phase - phases[lower]) / Math.Max(.0001f, 1f + phases[0] - phases[lower]); }
                else { upper = lower; blend = 0; }
            }
            else if (upper == 0 && track.Looping && phase < phases[0])
            {
                lower = phases.Length - 1;
                blend = (phase + 1f - phases[lower]) / Math.Max(.0001f, 1f + phases[0] - phases[lower]);
            }
            else blend = upper == lower ? 0 : (phase - phases[lower]) / Math.Max(.0001f, phases[upper] - phases[lower]);
            blend = Mathf.Clamp01(blend);
            var count = track.BonePaths.Count;
            for (var i = 0; i < count; i++)
            {
                var bone = GetBone(visual, track.BonePaths[i]);
                var firstPosition = (lower * count + i) * 3;
                var nextPosition = (upper * count + i) * 3;
                bone.localPosition = Vector3.LerpUnclamped(
                    new Vector3(track.Positions[firstPosition], track.Positions[firstPosition + 1], track.Positions[firstPosition + 2]),
                    new Vector3(track.Positions[nextPosition], track.Positions[nextPosition + 1], track.Positions[nextPosition + 2]), blend);
                var firstRotation = (lower * count + i) * 4;
                var nextRotation = (upper * count + i) * 4;
                bone.localRotation = Quaternion.SlerpUnclamped(
                    new Quaternion(track.Rotations[firstRotation], track.Rotations[firstRotation + 1],
                        track.Rotations[firstRotation + 2], track.Rotations[firstRotation + 3]),
                    new Quaternion(track.Rotations[nextRotation], track.Rotations[nextRotation + 1],
                        track.Rotations[nextRotation + 2], track.Rotations[nextRotation + 3]), blend);
            }
            if (oneHanded && !onlyLayer.HasValue &&
                IsHandLayer(evt, layer, "HoldingItemsRightHand", 2))
            {
                // The learned upper-layer pose is a full blended skeleton. It
                // may have been learned while a two-hand item was equipped, so
                // retain only the right arm and let locomotion animate the rest.
                var rightArm = visual.Bones.Where(pair => pair.Key.IndexOf("/shoulder.R[", StringComparison.Ordinal) >= 0 && pair.Value)
                    .Select(pair => (Bone: pair.Value, Position: pair.Value.localPosition,
                        Rotation: pair.Value.localRotation)).ToArray();
                if (rightArm.Length != 0 &&
                    (ApplyRecordedTrack(visual, bindingKey, layers, time, 0) ||
                     ApplyFallbackPose(visual, bindingKey, layers, time)))
                    foreach (var pose in rightArm)
                    {
                        pose.Bone.localPosition = pose.Position;
                        pose.Bone.localRotation = pose.Rotation;
                    }
            }
            return true;
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
                    var bracket = segment.LastIndexOf('[');
                    var name = bracket > 0 && segment.EndsWith("]", StringComparison.Ordinal)
                        ? segment.Substring(0, bracket) : segment;
                    bone = NewObject(name.Replace("%2F", "/"), parent).transform;
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
            // The recorder writes exterior chunks and then the interior a few
            // ticks apart. Include the bounded initial map prefetched by the
            // reader, so those timestamps do not rebuild the same geometry.
            var first = index;
            while (first > 0 && _session.Worlds[first - 1].World?.CaptureSetId == selected.CaptureSetId) first--;
            var mapEnd = first;
            for (var i = first; i < _session.Worlds.Count && i < first + 9; i++)
            {
                var part = _session.Worlds[i].World;
                if (part?.CaptureSetId != selected.CaptureSetId) break;
                mapEnd = i;
                if (part.Layer == "interior") break;
            }
            for (var i = 0; i <= Math.Max(index, mapEnd); i++)
            {
                var part = _session.Worlds[i].World;
                if (part == null || part.CaptureSetId != selected.CaptureSetId) continue;
                if (part.Layer == "exterior") exteriors.Add(part);
                else if (part.Layer == "interior") interior = part;
            }
            if (exteriors.Count == 0) return selected;
            var parts = interior == null ? exteriors : exteriors.Concat(new[] { interior! }).ToList();
            var source = exteriors.LastOrDefault(part => part.AssetScene.Length != 0) ?? exteriors.First();
            var generation = exteriors.LastOrDefault(part => part.LevelId >= 0) ?? source;
            var combined = new WorldSnapshot
            {
                Scene = selected.Scene, CaptureSetId = selected.CaptureSetId,
                AssetScene = source.AssetScene, AssetGameVersion = source.AssetGameVersion,
                AssetBuildIndex = source.AssetBuildIndex,
                MapSeed = generation.MapSeed, LevelId = generation.LevelId,
                DungeonSeed = generation.DungeonSeed, DungeonFlow = generation.DungeonFlow,
                AssetRendererPaths = exteriors.SelectMany(part => part.AssetRendererPaths).Distinct(StringComparer.Ordinal).ToList(),
                AssetTerrainPaths = exteriors.SelectMany(part => part.AssetTerrainPaths).Distinct(StringComparer.Ordinal).ToList(),
                Geometry = parts.SelectMany(part => part.Geometry).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Textures = parts.SelectMany(part => part.Textures).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Materials = parts.SelectMany(part => part.Materials).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                Lights = parts.SelectMany(part => part.Lights).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                ParticleEmitters = parts.SelectMany(part => part.ParticleEmitters).GroupBy(item => item.Id).Select(group => group.First()).ToList(),
                LocalFogs = parts.SelectMany(part => part.LocalFogs).GroupBy(item => item.Id).Select(group => group.Last()).ToList(),
                Rooms = interior?.Rooms ?? new List<RoomSnapshot>(),
                Environment = _session.Worlds.Take(index + 1).Select(record => record.World)
                    .Where(part => part != null && part.CaptureSetId == selected.CaptureSetId && part.Layer == "exterior")
                    .Select(part => part!.Environment).LastOrDefault(environment => environment != null)
            };
            // Some short spawn clips finish before the late world pass writes
            // the actor mesh. Prepare only those already-recorded rigs ahead of
            // time; their entity roots stay hidden until the actor is observed.
            var prefetchedActors = new HashSet<string>(_session.Events.Where(evt =>
                    evt.Category == "animation" && evt.Name == "state" && evt.EntityId.Length != 0 &&
                    evt.Data.TryGetValue("clip", out var clip) && !string.IsNullOrEmpty(clip) &&
                    (clip.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     clip.IndexOf("ExitVent", StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(evt => evt.EntityId), StringComparer.Ordinal);
            foreach (var actor in _session.Frames.SelectMany(frame => frame.Entities).Where(entity => IsVisualKind(entity.Kind)))
                prefetchedActors.Add(actor.Id);
            foreach (var entity in _frame.Entities.Where(entity => entity.Active && entity.Kind == "hazard"))
                prefetchedActors.Add(entity.Id);
            prefetchedActors.IntersectWith(_windowActorIds);
            if (prefetchedActors.Count != 0)
            {
                var known = new HashSet<string>(combined.Geometry.Select(geometry => geometry.Id), StringComparer.Ordinal);
                var future = _session.Worlds.Skip(index + 1).Select(record => record.World)
                    .Where(part => part != null && part.CaptureSetId == selected.CaptureSetId).ToArray();
                foreach (var part in future)
                    foreach (var geometry in part!.Geometry)
                        if (prefetchedActors.Contains(geometry.EntityId) && known.Add(geometry.Id))
                        {
                            combined.Geometry.Add(geometry);
                            if (geometry.MeshSourceId.Length != 0 && known.Add(geometry.MeshSourceId))
                            {
                                var sourceMesh = future.SelectMany(candidate => candidate!.Geometry)
                                    .FirstOrDefault(candidate => candidate.Id == geometry.MeshSourceId);
                                if (sourceMesh != null) combined.Geometry.Add(sourceMesh);
                            }
                        }
                var materialIds = new HashSet<string>(combined.Geometry.SelectMany(geometry => geometry.MaterialIds), StringComparer.Ordinal);
                var lights = new HashSet<string>(combined.Lights.Select(light => light.Id), StringComparer.Ordinal);
                foreach (var light in future.SelectMany(part => part!.Lights))
                    if (prefetchedActors.Contains(light.EntityId) && lights.Add(light.Id)) combined.Lights.Add(light);
                var emitters = new HashSet<string>(combined.ParticleEmitters.Select(emitter => emitter.Id), StringComparer.Ordinal);
                foreach (var emitter in future.SelectMany(part => part!.ParticleEmitters))
                    if (prefetchedActors.Contains(emitter.EntityId) && emitters.Add(emitter.Id))
                    { combined.ParticleEmitters.Add(emitter); materialIds.Add(emitter.MaterialId); }
                var ownedMaterials = new HashSet<string>(combined.Materials.Select(material => material.Id), StringComparer.Ordinal);
                foreach (var material in future.SelectMany(part => part!.Materials))
                    if (materialIds.Contains(material.Id) && ownedMaterials.Add(material.Id)) combined.Materials.Add(material);
                var textureIds = new HashSet<string>(combined.Materials.SelectMany(material =>
                    new[] { material.TextureId }.Concat(material.Properties.Select(property => property.TextureId))), StringComparer.Ordinal);
                var ownedTextures = new HashSet<string>(combined.Textures.Select(texture => texture.Id), StringComparer.Ordinal);
                foreach (var texture in future.SelectMany(part => part!.Textures))
                    if (textureIds.Contains(texture.Id) && ownedTextures.Add(texture.Id)) combined.Textures.Add(texture);
            }
            // A capture set can contain meshes from every past spawn. Keep
            // actors needed by this bounded window (including future spawns),
            // with any shared-mesh dependency, instead of rebuilding old ones.
            var requiredMeshes = new HashSet<string>(combined.Geometry.Where(geometry =>
                geometry.EntityId.Length == 0 || _windowActorIds.Contains(geometry.EntityId))
                .Select(geometry => geometry.Id), StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var geometry in combined.Geometry)
                    if (requiredMeshes.Contains(geometry.Id) && geometry.MeshSourceId.Length != 0)
                        changed |= requiredMeshes.Add(geometry.MeshSourceId);
            }
            combined.Geometry.RemoveAll(geometry => !requiredMeshes.Contains(geometry.Id));
            combined.Lights.RemoveAll(light => light.EntityId.Length != 0 && !_windowActorIds.Contains(light.EntityId));
            combined.ParticleEmitters.RemoveAll(emitter => emitter.EntityId.Length != 0 && !_windowActorIds.Contains(emitter.EntityId));
            // The first playback window may have read the complete next map,
            // while a later bounded window carries only its current chunks.
            // Keep those prepared static surfaces and rooms through the switch.
            if (_futureWorld != null && _futureWorld.CaptureSetId == selected.CaptureSetId)
            {
                if (combined.AssetScene.Length == 0)
                {
                    combined.AssetScene = _futureWorld.AssetScene;
                    combined.AssetBuildIndex = _futureWorld.AssetBuildIndex;
                    combined.AssetGameVersion = _futureWorld.AssetGameVersion;
                }
                AddMissing(combined.AssetRendererPaths, _futureWorld.AssetRendererPaths, path => path);
                AddMissing(combined.AssetTerrainPaths, _futureWorld.AssetTerrainPaths, path => path);
                AddMissing(combined.Geometry, _futureWorld.Geometry.Where(geometry => geometry.EntityId.Length == 0), geometry => geometry.Id);
                AddMissing(combined.Textures, _futureWorld.Textures, texture => texture.Id);
                AddMissing(combined.Materials, _futureWorld.Materials, material => material.Id);
                AddMissing(combined.Lights, _futureWorld.Lights.Where(light => light.EntityId.Length == 0), light => light.Id);
                AddMissing(combined.ParticleEmitters, _futureWorld.ParticleEmitters.Where(emitter => emitter.EntityId.Length == 0), emitter => emitter.Id);
                AddMissing(combined.LocalFogs, _futureWorld.LocalFogs, fog => fog.Id);
                AddMissing(combined.Rooms, _futureWorld.Rooms, room => room.Id);
            }
            return combined;
        }

        private static void AddMissing<T>(List<T> target, IEnumerable<T> source, Func<T, string> id)
        {
            var known = new HashSet<string>(target.Select(id), StringComparer.Ordinal);
            foreach (var item in source) if (known.Add(id(item))) target.Add(item);
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

        private void CyclePlayer()
        {
            var index = _players.FindIndex(player => player.Id == _selectedId);
            for (var step = 1; step <= _players.Count; step++)
            {
                var next = _players[(index + step + _players.Count) % _players.Count];
                if (!IsDead(next)) { if (next.Id != _selectedId) SelectCamera(next.Id); return; }
            }
        }

        private void SetNoShadow(bool value)
        {
            _noShadow = value;
            _saveNoShadow?.Invoke(value);
            UpdateInteriorVisibility();
        }

        private void SetMutePlayerAudio(bool value)
        {
            _mutePlayerAudio = value;
            _saveMutePlayerAudio?.Invoke(value);
            foreach (var voice in _spatialVoices)
                if (voice.Player) voice.Source.mute = value;
        }

        private void SetFogEnabled(bool value)
        {
            _fogEnabled = value;
            _saveFogEnabled?.Invoke(value);
            _environment?.SetFogEnabled(value);
            UpdateInteriorVisibility();
        }

        private void SetCinematicMove(bool value)
        {
            _cinematicMove = value;
            _cinematicVelocity = Vector3.zero;
            _lookTargetYaw = _yaw;
            _lookTargetPitch = _pitch;
            _lookYawVelocity = _lookPitchVelocity = 0f;
            _hud?.SetCinematicMove(value);
            _saveCinematicMove?.Invoke(value);
        }

        private void SetCameraSpeed(float value)
        {
            _cameraSpeed = Mathf.Clamp(value, 1f, 30f);
            _hud?.SetCameraSpeed(_cameraSpeed);
            _saveCameraSpeed?.Invoke(_cameraSpeed);
        }

        private void ChangeFreeCameraSpeed(float scroll)
        {
            if (float.IsNaN(scroll) || float.IsInfinity(scroll) || Mathf.Abs(scroll) < .001f) return;
            var steps = Mathf.Sign(scroll) * Mathf.Clamp(Mathf.Abs(scroll) / 120f, .5f, 4f);
            SetCameraSpeed(_cameraSpeed * Mathf.Pow(1.15f, steps));
        }

        private IEnumerable<float> RebuildWorldSteps(WorldSnapshot? world)
        {
            foreach (var id in _entities.Keys.Where(id => !_windowActorIds.Contains(id)).ToArray())
            {
                var obsolete = _entities[id];
                if (obsolete.SkeletonMesh) Object.Destroy(obsolete.SkeletonMesh);
                obsolete.NativeRig?.Dispose();
                if (obsolete.Root) { obsolete.Root.SetActive(false); Object.Destroy(obsolete.Root); }
                _entities.Remove(id);
            }
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
            _bakedPlayers.Clear();
            _nativeSkinIds.Clear();
            _worldObjects.Clear();
            _worldMeshes.Clear();
            _worldFogMasks.Clear();
            _fogGeometry.Clear();
            _instancedGeometry.Clear();
            _proceduralGrassRenderers.Clear();
            _worldParticles.Clear();
            _localFogs.Clear();
            _geometryObjects.Clear();
            _maskEyesObjects.Clear();
            _hazardsWithModel.Clear();
            _dynamicGeometry.Clear();
            _movingSceneGeometry.Clear();
            _doorMeshLinks.Clear();
            _firstDoorPoses.Clear();
            _renderGeometrySources.Clear();
            _interiorRenderers.Clear();
            _exteriorRenderers.Clear();
            _deferredWorldRenderers.Clear();
            _deferredWorldLights.Clear();
            _deferredWorldFogs.Clear();
            _deferredWorldParticles.Clear();
            _naturalLods.Clear();
            _structureAudio?.Dispose(); _structureAudio = null;
            _worldLights.Clear();
            _nativeShadowModes.Clear();
            _anchorRoots.Clear();
            _interiorRooms.Clear();
            _additionalRoomVolumes.Clear();
            _shipCabinAnchor = null;
            _appearance?.Dispose(); _appearance = null;
            foreach (var material in _particleMaterials.Values) if (material) Object.Destroy(material);
            _particleMaterials.Clear();
            var legacyItemLights = world != null ? LegacyItemEmissionLights(world).ToArray() : Array.Empty<LightSnapshot>();
            _worldHasLighting = world != null && (world.Lights.Count != 0 || legacyItemLights.Length != 0);
            foreach (var visual in _entities.Values)
            { visual.GeometryCount = 0; visual.RestBones.Clear(); visual.Proxy.SetActive(_showSkeletons && IsVisualKind(visual.Kind)); }
            foreach (var actor in _session.Frames.SelectMany(frame => frame.Entities).Where(entity => IsVisualKind(entity.Kind))
                .GroupBy(entity => entity.Id).Select(group => group.First()))
            {
                var visual = GetEntity(actor.Id);
                ConfigureProxy(visual, actor);
            }
            _sceneName = world?.Scene ?? "No world geometry recorded";
            if (world == null)
            {
                _assetScene?.SetWorld(new WorldSnapshot());
                if (_spectatorLight) _spectatorLight!.enabled = false;
                if (_exteriorFill) _exteriorFill!.enabled = false;
                yield break;
            }
            _environment = new ReplayEnvironment(world, _root!.transform, ReplayLayer,
                _session.Header.Capabilities.Contains("world-postfx-without-player-filters"));
            _environment.SetFogEnabled(_fogEnabled);
            _assetScene?.SetWorld(world);
            _assetScene?.SetFogEnabled(_fogEnabled);
            // Native material and particle templates arrive with the installed moon.
            // Indexing resources before its asynchronous load completes would cache
            // false misses and recreate effects with fallback shaders for this world.
            while (_assetScene?.IsLoading == true) yield return .08f;
            _environment.SetGamma(_gamma);
            // Indexed windows share immutable file payloads. Native asset
            // repairs belong to this displayed world only, never that cache.
            world = (WorldSnapshot)ShallowClone.Invoke(world, null);
            world.Geometry = world.Geometry.Select(geometry => (GeometrySnapshot)ShallowClone.Invoke(geometry, null)).ToList();
            world.Materials = new List<MaterialSnapshot>(world.Materials);
            _displayWorld = world;
            NativeSpiderFallback.Restore(world, _frame);
            PrefabAssetRegistry.Restore(world, _session);
            BuildDoorMeshLinks(world);
            var appearanceShader = world.Lights.Count == 0 && _unlitShader != null && _unlitShader.isSupported
                ? _unlitShader : _shader!;
            _useFutureMeshes = _futureWorld != null && _futureWorld.CaptureSetId == world.CaptureSetId;
            var preparedAppearance = _useFutureMeshes && _futureAppearance != null &&
                _futureAppearanceShader == appearanceShader;
            var appearanceComplete = preparedAppearance && _futureAppearanceBuild == null;
            if (preparedAppearance)
            {
                _futureAppearanceBuild?.Dispose();
                _futureAppearanceBuild = null;
                _appearance = _futureAppearance;
                _futureAppearance = null;
            }
            else
            {
                if (_useFutureMeshes)
                {
                    _futureAppearanceBuild?.Dispose();
                    _futureAppearanceBuild = null;
                    _futureAppearance?.Dispose();
                    _futureAppearance = null;
                }
                _appearance = new ReplayAppearance();
            }
            var sameAppearance = appearanceComplete && _futureWorld != null &&
                world.Materials.Count == _futureWorld.Materials.Count &&
                world.Textures.Count == _futureWorld.Textures.Count &&
                ContainsAll(_futureWorld.Materials, world.Materials, material => material.Id) &&
                ContainsAll(_futureWorld.Textures, world.Textures, texture => texture.Id);
            if (!sameAppearance)
                foreach (var step in _appearance!.BuildSteps(world, appearanceShader))
                    yield return .08f + .37f * step;
            foreach (var room in world.Rooms)
            {
                _interiorRooms[room.Id] = new Bounds(ToVector(room.Center), ToVector(room.Size));
                if (room.AdditionalVolumes.Count > 0)
                    _additionalRoomVolumes[room.Id] = room.AdditionalVolumes.Select(volume =>
                        new Bounds(ToVector(volume.Center), ToVector(volume.Size))).ToArray();
            }
            // A prefetched map chunk may refer to an anchor absent from the
            // opening frame. Create its root now so later poses can move it.
            var knownAnchors = _session.Frames.SelectMany(frame => frame.Anchors)
                .GroupBy(anchor => anchor.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var anchorIds = knownAnchors.Keys.Concat(world.Geometry.Select(geometry => geometry.AnchorId))
                .Concat(world.Lights.Select(light => light.AnchorId))
                .Where(id => id.Length != 0).Distinct(StringComparer.Ordinal);
            foreach (var id in anchorIds)
            {
                var root = NewObject("Moving environment " + id, _root!.transform);
                _worldObjects.Add(root);
                _anchorRoots[id] = root.transform;
                var anchor = _frame.Anchors.FirstOrDefault(pose => pose.Id == id) ??
                    knownAnchors.GetValueOrDefault(id);
                if (anchor != null) SetTransform(root.transform, anchor.Position, anchor.Rotation, anchor.Scale);
            }
            var debugMaterials = new HashSet<string>(world.Materials.Where(material => RenderVisibilityPolicy.IsDebugMaterial(material.Name)).Select(material => material.Id));
            var meshes = new Dictionary<string, Mesh>();
            var firstSeen = new Dictionary<string, double>(StringComparer.Ordinal);
            if (world.CaptureSetId.Length != 0)
                foreach (var record in _session.Worlds.Where(record => record.World?.CaptureSetId == world.CaptureSetId))
                {
                    foreach (var recorded in record.World!.Geometry)
                        if (recorded.EntityId.Length == 0 && !firstSeen.ContainsKey(recorded.Id))
                            firstSeen[recorded.Id] = record.Time + _recording.Parts[_partIndex].Offset;
                    foreach (var light in record.World.Lights)
                        if (light.EntityId.Length == 0) firstSeen.TryAdd("light:" + light.Id,
                            record.Time + _recording.Parts[_partIndex].Offset);
                    foreach (var fog in record.World.LocalFogs)
                        firstSeen.TryAdd("fog:" + fog.Id, record.Time + _recording.Parts[_partIndex].Offset);
                    foreach (var emitter in record.World.ParticleEmitters)
                        if (emitter.EntityId.Length == 0) firstSeen.TryAdd("particle:" + emitter.Id,
                            record.Time + _recording.Parts[_partIndex].Offset);
                }
            if (_futureWorld?.CaptureSetId == world.CaptureSetId)
                foreach (var entry in _futureWorldFirstSeen) firstSeen[entry.Key] = entry.Value;
            var meshSources = world.Geometry
                .Concat(_futureWorld?.CaptureSetId == world.CaptureSetId ? _futureWorld.Geometry : Enumerable.Empty<GeometrySnapshot>())
                .GroupBy(geometry => geometry.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var geometry in world.Geometry)
            {
                var source = geometry;
                for (var depth = 0; depth < 16 && source.MeshSourceId.Length != 0 &&
                    meshSources.TryGetValue(source.MeshSourceId, out var next); depth++) source = next;
                _renderGeometrySources[geometry.Id] = source;
            }
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
                    var instanceMaterials = _appearance!.Resolve(geometry, GetMaterial(GeometryColor(geometry)));
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
                    EnsureNativeActorRig(owner, geometry.PrefabKey, geometry.EntityId);
                    parent = geometry.AttachedBonePath.Length == 0 ? owner.Root.transform :
                        GetBone(owner, geometry.AttachedBonePath);
                }
                else if (geometry.AnchorId.Length != 0 && _anchorRoots.TryGetValue(geometry.AnchorId, out var anchor)) parent = anchor;
                var obj = NewObject("World " + geometry.Name, parent);
                _worldObjects.Add(obj);
                _geometryObjects[geometry.Id] = obj;
                if (geometry.EntityId.Length != 0 && _frame.Entities.Any(entity => entity.Id == geometry.EntityId && NativeMaskState.IsMask(entity)) && NativeMaskState.IsEyes(geometry))
                {
                    if (!_maskEyesObjects.TryGetValue(geometry.EntityId, out var eyes)) _maskEyesObjects[geometry.EntityId] = eyes = new List<GameObject>();
                    eyes.Add(obj);
                }
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
                    var materials = _appearance!.Resolve(geometry, GetMaterial(GeometryColor(geometry)), data);
                    if (owner != null && geometry.BonePaths.Count > 0 && geometry.BoneWeights.Length == mesh.vertexCount * 4)
                        AddSkin(obj, mesh, materials, geometry, owner);
                    else
                    {
                        AddMesh(obj, mesh, materials[0]);
                        obj.GetComponent<MeshRenderer>().sharedMaterials = materials;
                    }
                }
                if (obj.GetComponent<Renderer>() is Renderer recordedRenderer)
                {
                    if (owner == null && firstSeen.TryGetValue(geometry.Id, out var availableAt))
                    {
                        _deferredWorldRenderers.Add(new KeyValuePair<Renderer, double>(recordedRenderer, availableAt));
                        if (availableAt > Time) recordedRenderer.forceRenderingOff = true;
                    }
                    recordedRenderer.shadowCastingMode = geometry.ShadowCastingMode >= 0 && geometry.ShadowCastingMode <= 3
                        ? (ShadowCastingMode)geometry.ShadowCastingMode
                        : recordedRenderer.sharedMaterials.Any(material => material && material.renderQueue >= 3000)
                            ? ShadowCastingMode.Off : ShadowCastingMode.On;
                    recordedRenderer.receiveShadows = geometry.ReceiveShadows;
                    if (geometry.Name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        recordedRenderer.sharedMaterials.Any(material => material &&
                            material.name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0))
                        _fogGeometry.Add(recordedRenderer);
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
            // Animation state may have been requested while the world was still
            // building. Bind the installed controller again after every bone
            // hierarchy exists; otherwise Unity can keep its earlier empty
            // binding and leave a moving actor in the captured rest pose.
            foreach (var visual in _entities.Values)
            {
                visual.UnresolvedAnimatorPaths.Clear();
                if (visual.GeometryCount != 0)
                    foreach (var animator in visual.Animators.Values.Distinct())
                        if (animator) animator.Rebind();
            }
            var exteriorShadows = 0;
            var interiorShadows = 0;
            foreach (var snapshot in world.Lights.Concat(legacyItemLights))
            {
                if (snapshot.Name == "NightVision" || snapshot.Name == "NightVisionRadar") continue;
                var parent = snapshot.EntityId.Length != 0 ? GetEntity(snapshot.EntityId).Root.transform :
                    snapshot.AnchorId.Length != 0 && _anchorRoots.TryGetValue(snapshot.AnchorId, out var anchor) ? anchor : _root!.transform;
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
                // A baked/mixed fixture may have Light.shadows set in the source,
                // but replaying it as a realtime point shadow creates new hard
                // occlusion. Legacy files lack bake metadata; avoid point shadows.
                var realtimeShadow = snapshot.BakeType == "Realtime";
                var castShadow = snapshot.Shadows && realtimeShadow && shadowCount < (snapshot.IsInterior ? 4 : 2);
                light.shadows = castShadow ? LightShadows.Soft : LightShadows.None;
                light.shadowStrength = Mathf.Clamp01(snapshot.ShadowStrength);
                if (castShadow) { if (snapshot.IsInterior) interiorShadows++; else exteriorShadows++; }
                light.shadowResolution = LightShadowResolution.Medium;
                light.cullingMask = 1 << ReplayLayer;
                AddRenderPipelineLightData(obj, snapshot.Intensity);
                var hdLightType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
                var hdLight = hdLightType == null ? null : obj.GetComponent(hdLightType);
                if (hdLight)
                {
                    SetOptionalMember(hdLight!, "lightDimmer", snapshot.LightDimmer);
                    SetOptionalMember(hdLight!, "shadowDimmer", snapshot.ShadowDimmer);
                }
                light.intensity = snapshot.Intensity;
                _nativeShadowModes[light] = light.shadows;
                _worldLights.Add(new KeyValuePair<Light, LightSnapshot>(light, snapshot));
                if (snapshot.EntityId.Length == 0 && firstSeen.TryGetValue("light:" + snapshot.Id, out var lightAt))
                {
                    _deferredWorldLights.Add(new KeyValuePair<Light, double>(light, lightAt));
                    if (Time + 1e-6 < lightAt) light.enabled = false;
                }
            }
            var localFogType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.LocalVolumetricFog");
            if (localFogType != null && typeof(Behaviour).IsAssignableFrom(localFogType))
                foreach (var snapshot in world.LocalFogs)
                {
                    try
                    {
                        var shipExclusion = snapshot.Name.IndexOf("FogExclusionZone", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            _shipCabinAnchor != null;
                        var obj = NewObject("Recorded local fog " + snapshot.Name,
                            shipExclusion ? _shipCabinAnchor! : _root!.transform);
                        _worldObjects.Add(obj);
                        if (shipExclusion)
                        {
                            // This volume is a child of the moving ship in the live
                            // scene. World-space capture left it behind after landing.
                            obj.transform.localPosition = _shipCabinCenter;
                            obj.transform.localRotation = Quaternion.identity;
                        }
                        else SetTransform(obj.transform, snapshot.Position, snapshot.Rotation, Vec3.One);
                        var fog = (Behaviour)obj.AddComponent(localFogType);
                        RestoreLocalFog(fog, snapshot);
                        _localFogs.Add(new KeyValuePair<Behaviour, LocalFogSnapshot>(fog, snapshot));
                        if (firstSeen.TryGetValue("fog:" + snapshot.Id, out var fogAt))
                        {
                            _deferredWorldFogs.Add(new KeyValuePair<Behaviour, double>(fog, fogAt));
                            if (Time + 1e-6 < fogAt) fog.enabled = false;
                        }
                    }
                    catch { /* Older HDRP versions may not expose local fog. */ }
                }
            foreach (var snapshot in world.ParticleEmitters)
            {
                var parent = snapshot.EntityId.Length != 0 ? GetEntity(snapshot.EntityId).Root.transform : _root!.transform;
                _particleAssets ??= new ReplayParticleAssets(_root!.transform, ReplayLayer);
                var effect = _particleAssets.Create(parent, snapshot.Style, snapshot.Name, snapshot.MaterialId,
                    _appearance!, _burstMaterial, false, out var nativeEffect);
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
                if (snapshot.Name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    snapshot.Style?.Name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0)
                    _fogGeometry.Add(renderer);
                _worldParticles.Add(effect);
                if (snapshot.EntityId.Length == 0 && firstSeen.TryGetValue("particle:" + snapshot.Id, out var particleAt))
                {
                    _deferredWorldParticles.Add(new KeyValuePair<ParticleSystem, double>(effect, particleAt));
                    if (renderer)
                    {
                        _deferredWorldRenderers.Add(new KeyValuePair<Renderer, double>(renderer, particleAt));
                        if (Time + 1e-6 < particleAt) renderer.forceRenderingOff = true;
                    }
                }
                if (!firstSeen.TryGetValue("particle:" + snapshot.Id, out var startsAt) || Time + 1e-6 >= startsAt)
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
            if (!_session.Header.Capabilities.Contains("native-ambient-sound-events"))
                _structureAudio = new NativeStructureAudio(world, _geometryObjects, _root!.transform);
            foreach (var entity in _frame.Entities)
                if (entity.Name == "Landmine" && _entities.TryGetValue(entity.Id, out var visual) && visual.NativeMine != null) CacheMineMeshes(visual, entity);
            UpdateInteriorVisibility();
            UpdatePlayerBodyOcclusion();
            if (_useFutureMeshes) ReleaseFutureResources();
            yield return 1f;
        }

        private IEnumerable<LightSnapshot> LegacyItemEmissionLights(WorldSnapshot world)
        {
            var items = _frame.Entities.Where(entity => entity.Kind == "item" && !NativeMaskState.IsMask(entity))
                .Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
            var lit = world.Lights.Where(light => light.EntityId.Length != 0)
                .Select(light => light.EntityId).ToHashSet(StringComparer.Ordinal);
            var materials = world.Materials.ToDictionary(material => material.Id, material => material, StringComparer.Ordinal);
            var strongest = new Dictionary<string, (GeometrySnapshot Geometry, Color Color, float Strength)>(StringComparer.Ordinal);
            foreach (var geometry in world.Geometry)
            {
                if (!items.Contains(geometry.EntityId) || lit.Contains(geometry.EntityId) || geometry.IsBoundsProxy) continue;
                foreach (var id in geometry.MaterialIds)
                {
                    if (!materials.TryGetValue(id, out var material)) continue;
                    var emission = material.Properties.FirstOrDefault(property => property.Name == "_EmissiveColor" &&
                        property.Values.Length >= 3);
                    if (emission == null) continue;
                    var values = emission.Values;
                    var strength = Mathf.Max(values[0], Mathf.Max(values[1], values[2]));
                    if (strength < 1f || float.IsNaN(strength) || float.IsInfinity(strength)) continue;
                    if (!strongest.TryGetValue(geometry.EntityId, out var previous) || strength > previous.Strength)
                        strongest[geometry.EntityId] = (geometry,
                            new Color(values[0] / strength, values[1] / strength, values[2] / strength, 1f), strength);
                }
            }
            foreach (var pair in strongest.Take(32))
            {
                var color = pair.Value.Color;
                yield return new LightSnapshot { Id = "legacy-item-emission:" + pair.Key,
                    EntityId = pair.Key, IsInterior = pair.Value.Geometry.IsInterior, Type = "Point",
                    Position = pair.Value.Geometry.Position, Color = new[] { color.r, color.g, color.b, 1f },
                    Intensity = Mathf.Clamp(pair.Value.Strength * 8f, 8f, 80f), Range = 4f };
            }
        }

        // Replay rooms follow the spectator camera. No live culler, collider or
        // gameplay component is enabled or moved, including while the camera crosses rooms.
        private bool PreLandingOrbit() => _frame.State.TryGetValue("StartOfRound.inShipPhase", out var phase) &&
            bool.TryParse(phase, out var inShipPhase) && inShipPhase &&
            (!_frame.State.TryGetValue("StartOfRound.shipHasLanded", out var landed) ||
                !bool.TryParse(landed, out var shipHasLanded) || !shipHasLanded);

        private void UpdateInteriorVisibility()
        {
            if (!_camera) return;
            var preLandingOrbit = PreLandingOrbit();
            _assetScene?.SetSuspended(_parked || preLandingOrbit);
            var fogVisible = _fogEnabled && !preLandingOrbit;
            if (fogVisible != _appliedFogVisible)
            {
                _appliedFogVisible = fogVisible;
                _assetScene?.SetFogEnabled(fogVisible);
                // Restore recorded fog renderers before regular room/exterior
                // culling below, including after a seek across the landing phase.
                if (fogVisible)
                    foreach (var renderer in _fogGeometry)
                        if (renderer) renderer.forceRenderingOff = false;
            }
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
                var snapshot = entry.Value;
                var lightDistance = Mathf.Max(distance, light.range + 12f);
                var near = (light.transform.position - position).sqrMagnitude <= lightDistance * lightDistance;
                // The ship's exterior floodlights sit just beyond ShipInside.
                // Their replay lights otherwise shine through the cabin shell and
                // cast shadows on fixtures that the live indoor view never sees.
                var shipExterior = false;
                if (inShipCabin && snapshot.AnchorId == "ship-elevator")
                {
                    var offset = ToVector(snapshot.Position) - _shipCabinCenter;
                    shipExterior = Mathf.Abs(offset.x) >= 8.5f || Mathf.Abs(offset.y) >= 4f ||
                        Mathf.Abs(offset.z) >= 5.5f;
                }
                // Props and scrap can move across a room boundary after the light
                // was recorded. Cull local lights by distance rather than their
                // original indoor tag; directional sky light stays outdoors.
                light.shadows = _noShadow ? LightShadows.None : _nativeShadowModes.GetValueOrDefault(light, LightShadows.None);
                light.enabled = light.type == LightType.Directional ? _noShadow || !indoor :
                    !shipExterior && (_disableInteriorCulling || near);
                var owner = snapshot.EntityId.Length == 0 ? null : _frame.Entities.FirstOrDefault(entity => entity.Id == snapshot.EntityId);
                if (owner != null && NativeMaskState.IsMask(owner)) light.enabled &= MaskAttaching(owner);
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
                fog.enabled = fogVisible && (inside ? (_disableInteriorCulling || indoor && near) : !indoor && near);
            }
            foreach (var renderer in _fogGeometry)
                if (renderer && !fogVisible) renderer.forceRenderingOff = true;
            if (_spectatorLight) _spectatorLight!.enabled = _worldHasLighting && indoor && !_noShadow;
            if (_exteriorFill) _exteriorFill!.enabled = _worldHasLighting && (_noShadow || !indoor);
            // A recorded ship fog-exclusion volume softens the outdoor fog in the
            // cabin. Keep global fog active only while that volume covers the camera;
            // without it, exterior height fog would wash the cabin out.
            var shipFogExclusion = inShipCabin && _localFogs.Any(entry => entry.Key && entry.Key.enabled &&
                entry.Value.Name.IndexOf("FogExclusionZone", StringComparison.OrdinalIgnoreCase) >= 0 &&
                new Bounds(entry.Key.transform.position, ToVector(entry.Value.Size)).Contains(position));
            _environment?.SetIndoor(indoor || inShipCabin && !shipFogExclusion, preLandingOrbit);
            foreach (var entry in _deferredWorldRenderers)
                if (entry.Key && Time + 1e-6 < entry.Value) entry.Key.forceRenderingOff = true;
            foreach (var entry in _deferredWorldLights)
                if (entry.Key && Time + 1e-6 < entry.Value) entry.Key.enabled = false;
            foreach (var entry in _deferredWorldFogs)
                if (entry.Key && Time + 1e-6 < entry.Value) entry.Key.enabled = false;
            foreach (var entry in _deferredWorldParticles)
                if (entry.Key)
                {
                    if (Time + 1e-6 < entry.Value)
                        entry.Key.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    else if (!entry.Key.isPlaying) entry.Key.Play(true);
                }
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
            if (_useFutureMeshes && _futureMeshes.TryGetValue(geometry.Id, out var prepared))
            {
                _futureMeshes.Remove(geometry.Id);
                return prepared;
            }
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
                SetUv(mesh, 1, geometry.Uvs1, count); SetUv(mesh, 2, geometry.Uvs2, count); SetUv(mesh, 3, geometry.Uvs3, count);
                if (geometry.Tangents.Length == count * 4)
                {
                    var tangents = new Vector4[count];
                    for (var i = 0; i < count; i++) tangents[i] = new Vector4(geometry.Tangents[i * 4], geometry.Tangents[i * 4 + 1],
                        geometry.Tangents[i * 4 + 2], geometry.Tangents[i * 4 + 3]);
                    mesh.tangents = tangents;
                }
                else if (geometry.Uvs.Length == count * 2) mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { Object.Destroy(mesh); throw; }
        }

        private static void SetUv(Mesh mesh, int channel, float[] values, int count)
        {
            if (values.Length != count * 2) return;
            var uvs = new List<Vector2>(count);
            for (var i = 0; i < count; i++) uvs.Add(new Vector2(values[i * 2], values[i * 2 + 1]));
            mesh.SetUVs(channel, uvs);
        }

        private void AddSkin(GameObject obj, Mesh mesh, Material[] materials, GeometrySnapshot geometry, EntityVisual owner)
        {
            var nativeTransform = owner.NativeRig?.SkinTransform(geometry);
            if (nativeTransform)
            {
                // Native bind poses are expressed in this renderer's space.
                // A captured flattened renderer offset must not be applied again
                // over the restored animated hierarchy (notably the Giant).
                obj.transform.SetParent(nativeTransform, false);
                obj.transform.localPosition = Vector3.zero; obj.transform.localRotation = Quaternion.identity;
                obj.transform.localScale = Vector3.one;
                _nativeSkinIds.Add(geometry.Id);
            }
            foreach (var pose in geometry.RigBones)
                if (pose.Path.Length != 0)
                {
                    if (owner.NativeRig == null) SetTransform(GetBone(owner, pose.Path), pose.Position, pose.Rotation, pose.Scale);
                    owner.RestBones[pose.Path] = pose;
                }
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
            if (owner.Kind == "player")
            {
                // Older recordings can reconstruct valid bones while HDRP rejects the
                // skinned draw. Draw the same bone matrices through an ordinary renderer.
                var bakedMesh = Object.Instantiate(mesh);
                bakedMesh.name = "Replay baked player " + geometry.Id;
                bakedMesh.MarkDynamic();
                var bakedObject = NewObject("Visible player " + geometry.Id, owner.Root.transform);
                bakedObject.AddComponent<MeshFilter>().sharedMesh = bakedMesh;
                var bakedRenderer = bakedObject.AddComponent<MeshRenderer>();
                bakedRenderer.sharedMaterials = materials;
                bakedRenderer.shadowCastingMode = renderer.shadowCastingMode;
                bakedRenderer.receiveShadows = renderer.receiveShadows;
                bakedRenderer.lightProbeUsage = renderer.lightProbeUsage;
                bakedRenderer.reflectionProbeUsage = renderer.reflectionProbeUsage;
                // The installed full-screen Sponge pass corrupts the detached
                // replay camera's depth buffer. Draw a player-only silhouette
                // from the same pose, with a thin surface offset and front culling.
                var outlineMesh = Object.Instantiate(bakedMesh);
                outlineMesh.name = "Replay player outline " + geometry.Id;
                outlineMesh.MarkDynamic();
                var outlineObject = NewObject("Player outline " + geometry.Id, bakedObject.transform);
                outlineObject.AddComponent<MeshFilter>().sharedMesh = outlineMesh;
                var outlineRenderer = outlineObject.AddComponent<MeshRenderer>();
                outlineRenderer.sharedMaterial = PlayerOutlineMaterial();
                outlineRenderer.shadowCastingMode = ShadowCastingMode.Off;
                outlineRenderer.receiveShadows = false;
                _bakedPlayers[geometry.Id] = new BakedPlayerBody(renderer, bakedObject, bakedMesh, bakedRenderer,
                    outlineRenderer, outlineMesh, geometry.EntityId);
                _worldObjects.Add(bakedObject);
                _worldMeshes.Add(bakedMesh);
                _worldMeshes.Add(outlineMesh);
                renderer.forceRenderingOff = true;
            }
            if (!owner.Animators.ContainsKey(geometry.AnimatorPath) && geometry.AnimatorController.Length != 0 &&
                !_session.Header.Capabilities.Contains("actor-bone-poses"))
            {
                var animationKey = AnimationBindingKey(geometry.EntityId, geometry.AnimatorPath);
                if (!_animationEvents.ContainsKey(animationKey))
                    animationKey = AnimationBindingKey(geometry.EntityId, "");
                var clips = _animationEvents.TryGetValue(animationKey, out var layers) &&
                    layers.TryGetValue(0, out var states)
                    ? states.Select(state => state.Data.TryGetValue("clip", out var clip) ? clip : "")
                    : Enumerable.Empty<string>();
                var native = PrefabAssetRegistry.ResolveAnimator(geometry.PrefabKey, geometry.AnimatorPath);
                var (controller, avatar) = native ? (native!.runtimeAnimatorController, native.avatar) : AnimationAssetRegistry.Resolve(
                    geometry.AnimatorController, geometry.AnimatorAvatar, owner.Kind, clips);
                if (controller != null && (geometry.AnimatorAvatar.Length == 0 || avatar != null))
                {
                    var root = geometry.AnimatorPath.Length == 0 ? owner.Root.transform : GetBone(owner, geometry.AnimatorPath);
                    var replayAnimator = root.gameObject.AddComponent<Animator>();
                    replayAnimator.runtimeAnimatorController = controller;
                    if (avatar != null) replayAnimator.avatar = avatar;
                    replayAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    replayAnimator.speed = 0;
                    RegisterAnimator(owner, geometry.AnimatorPath, replayAnimator);
                }
            }
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
            var nativeBullets = _entities.Values.Any(entity => entity.Root.activeInHierarchy && entity.NativeTurret?.Bullets);
            _nativeBulletEmitters.Clear();
            if (nativeBullets) foreach (var style in _frame.ParticleStyles)
                if (style.Name == "BulletParticle" || style.ParentName == "BulletParticle") _nativeBulletEmitters.Add(style.Id);
            var fogEmitters = !_fogEnabled || PreLandingOrbit() ? new HashSet<string>(_frame.ParticleStyles
                .Where(style => style.Name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(style => style.Id), StringComparer.Ordinal) : null;
            if (_appearance != null)
                foreach (var style in _frame.ParticleStyles)
                {
                    if (fogEmitters?.Contains(style.Id) == true || nativeBullets && (style.Name == "BulletParticle" || style.ParentName == "BulletParticle")) continue;
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
                if (fogEmitters?.Contains(pose.EmitterId) == true || _nativeBulletEmitters.Contains(pose.EmitterId)) continue;
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
                var turretTracer = _frame.ParticleStyles.Any(style => style.Id == pair.Key &&
                    style.Name == "BulletParticle" && style.RenderMode == (int)ParticleSystemRenderMode.None);
                foreach (var pose in pair.Value)
                {
                    if (count == _burstBuffer.Length) break;
                    var native = pose.EmitterId.Length != 0;
                    _burstBuffer[count++] = new ParticleSystem.Particle
                    {
                        position = ToVector(pose.Position), velocity = ToVector(pose.Velocity),
                        startSize3D = turretTracer ? ToVector(pose.Size3D) * .16f :
                            native ? ToVector(pose.Size3D) : Vector3.one * pose.Size,
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
            _simulatedEmitters.Clear(); _activeEmitters.Clear(); _nativeBulletEmitters.Clear();
            _particleAssets?.Dispose(); _particleAssets = null;
        }

        private void ApplyLines()
        {
            _inactiveLines.Clear();
            foreach (var id in _frameLines.Keys) _inactiveLines.Add(id);
            foreach (var pose in FrameLines())
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
                    line.sharedMaterial = pose.Id.StartsWith("turret-laser-", StringComparison.Ordinal)
                        ? TurretBeamMaterial()
                        : _appearance?.ResolveParticleMaterial("", pose.MaterialName, pose.ShaderName)
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
                var turretBeam = pose.Id.StartsWith("turret-laser-", StringComparison.Ordinal);
                line.startWidth = pose.StartWidth * (turretBeam ? 5f : 1f);
                line.endWidth = pose.EndWidth * (turretBeam ? 5f : 1f);
                line.startColor = new Color(pose.StartColor[0], pose.StartColor[1], pose.StartColor[2], pose.StartColor[3]);
                line.endColor = new Color(pose.EndColor[0], pose.EndColor[1], pose.EndColor[2], pose.EndColor[3]);
                for (var i = 0; i < line.positionCount; i++)
                    line.SetPosition(i, new Vector3(pose.Positions[i * 3], pose.Positions[i * 3 + 1], pose.Positions[i * 3 + 2]));
            }
            foreach (var id in _inactiveLines)
                if (_frameLines.TryGetValue(id, out var line) && line && line.enabled) line.enabled = false;
        }

        private IEnumerable<LinePose> FrameLines()
        {
            foreach (var pose in _frame.Lines) yield return pose;
            if (_frame.Lines.Any(pose => pose.Id.StartsWith("turret-laser-", StringComparison.Ordinal))) yield break;
            // Older recordings have no explicit aim ray, but recent ones kept
            // the vanilla non-rendering bullet emitter at the muzzle.
            foreach (var style in _frame.ParticleStyles.Where(style => style.Name == "BulletParticle"))
            {
                var origin = ToVector(style.Position);
                var velocity = _frame.Particles.FirstOrDefault(pose => pose.EmitterId == style.Id);
                var direction = velocity != null && ToVector(velocity.Velocity).sqrMagnitude > .01f
                    ? ToVector(velocity.Velocity).normalized : ToQuaternion(style.Rotation) * Vector3.forward;
                var end = origin + direction * 12f;
                yield return new LinePose { Id = "turret-laser-legacy-" + style.Id, IsInterior = style.IsInterior,
                    Positions = new[] { origin.x, origin.y, origin.z, end.x, end.y, end.z },
                    StartColor = new[] { 1f, .24f, .06f, .85f }, EndColor = new[] { 1f, .08f, .02f, .35f },
                    StartWidth = .018f, EndWidth = .005f };
            }
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

        private void SyncItemImpacts()
        {
            var target = LocalTime;
            foreach (var source in _itemSoundSources.Values) if (source) source.mute = _mutePlayerAudio || SoundOutOfRange(source);
            if (!IsPlaying || IsBuffering || Speed > 3f) return;
            if (_lastItemImpactTime < 0 || target < _lastItemImpactTime - .05 ||
                target - _lastItemImpactTime > .45)
            {
                foreach (var source in _itemSoundSources.Values) if (source) source.Stop();
                var low = 0; var high = _itemImpactEvents.Length;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (_itemImpactEvents[middle].Time <= target) low = middle + 1; else high = middle;
                }
                _itemImpactCursor = low;
                _lastItemImpactTime = target;
                return;
            }
            _lastItemImpactTime = target;
            while (_itemImpactCursor < _itemImpactEvents.Length &&
                _itemImpactEvents[_itemImpactCursor].Time <= target + .025)
            {
                var evt = _itemImpactEvents[_itemImpactCursor++];
                // The ordinary audio stream is authoritative when it captured
                // this exact item source. Use the installed clip only when that
                // transient source was unavailable to the audio tap.
                if (_nativeSounds?.ContainsImpact(evt) == true || _replayAudioClip || evt.Data.TryGetValue("source", out var sourceId) &&
                    sourceId.Length != 0 && _spatialAudioBlocks.Any(block =>
                        Math.Abs(block.Time - evt.Time) < .2 &&
                        block.Data.TryGetValue("source", out var captured) && captured == sourceId)) continue;
                var itemId = evt.Data.TryGetValue("itemId", out var id) ? id : "";
                var clipName = evt.Data.TryGetValue("clip", out var name) ? name : "";
                var clip = ItemAssetRegistry.Resolve(itemId, clipName);
                if (!clip || !_entities.TryGetValue(evt.EntityId, out var visual)) continue;
                if (!_itemSoundSources.TryGetValue(evt.EntityId, out var source) || !source)
                {
                    source = visual.Root.AddComponent<AudioSource>();
                    source.playOnAwake = false;
                    source.spatialBlend = 1f;
                    source.rolloffMode = AudioRolloffMode.Linear;
                    source.minDistance = 2f; source.maxDistance = 20f;
                    _itemSoundSources[evt.EntityId] = source;
                }
                source.pitch = Speed;
                if (_mutePlayerAudio || SoundOutOfRange(source)) continue;
                source.PlayOneShot(clip);
            }
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
                else { voice.Source.pitch = Speed; voice.Source.mute = _mutePlayerAudio && voice.Player || SoundOutOfRange(voice.Source); }
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
                sourceAudio.spatialBlend = data.TryGetValue("global", out var global) && global == "true" ? 0f : 1f;
                sourceAudio.minDistance = Mathf.Clamp(min, .01f, 1000f);
                sourceAudio.maxDistance = Mathf.Clamp(max, sourceAudio.minDistance, 10000f);
                if (data.TryGetValue("rolloff", out var mode) && int.TryParse(mode, out var rolloff) && rolloff >= 0 && rolloff <= 2)
                    sourceAudio.rolloffMode = (AudioRolloffMode)rolloff;
                sourceAudio.dopplerLevel = 0f;
                sourceAudio.mute = _mutePlayerAudio && player || SoundOutOfRange(sourceAudio);
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

        private bool SoundOutOfRange(AudioSource source)
        {
            var listener = _preserveLiveAudio && _liveListener ? _liveListener!.transform : _camera ? _camera!.transform : null;
            return listener && source.spatialBlend > 0f &&
                (source.transform.position - listener!.position).sqrMagnitude >= source.maxDistance * source.maxDistance;
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
            PruneDestroyed(_audioSourceStates); PruneDestroyed(_listenerStates);
            PruneDestroyed(_cameraStates); PruneDestroyed(_canvasStates);
            PruneDestroyed(_rendererStates); PruneDestroyed(_uiInputStates);
            foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (!source || source == _replayAudio || !source.gameObject.scene.IsValid() ||
                    !source.gameObject.scene.isLoaded || source.gameObject.scene == _scene) continue;
                // Connected playback keeps the live game's SFX, ambience and
                // voice at its original listener. Do not override live mute
                // changes; inert moon asset sources are disabled separately.
                if (_preserveLiveAudio) continue;
                if (!_audioSourceStates.ContainsKey(source)) _audioSourceStates.Add(source, source.mute);
                source.mute = true;
            }
            foreach (var listener in Resources.FindObjectsOfTypeAll<AudioListener>())
            {
                if (!listener || listener.gameObject.scene == _scene || !listener.gameObject.scene.IsValid() ||
                    !listener.gameObject.scene.isLoaded) continue;
                if (!_listenerStates.ContainsKey(listener)) _listenerStates.Add(listener, listener.enabled);
                listener.enabled = _preserveLiveAudio && listener == _liveListener && _listenerStates[listener];
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

        private static void PruneDestroyed<T>(Dictionary<T, bool> states) where T : Object
        {
            foreach (var key in states.Keys.Where(key => !key).ToArray()) states.Remove(key);
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
            RefreshCursor();
        }

        internal void RefreshCursor()
        {
            if (_disposed || _parked) return;
            var locked = _looking && Application.isFocused;
            // The game's menu/input scripts can set visible after Update. The
            // before-render callback enforces the final state without a flash.
            if (Cursor.lockState != (locked ? CursorLockMode.Locked : CursorLockMode.None))
                Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.visible == locked) Cursor.visible = !locked;
        }

        private static bool IsDead(EntitySnapshot player) => player.State.TryGetValue("isPlayerDead", out var value) &&
            string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

        private static bool IsUnavailableTarget(EntitySnapshot entity) => entity.Kind == "player" ? IsDead(entity) :
            entity.State.TryGetValue("isEnemyDead", out var value) &&
            string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

        private bool PointerOverReplayUi(Vector2 position)
        {
            if (_hud == null || !EventSystem.current) return false;
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, hits);
            return hits.Any(hit => hit.gameObject && hit.gameObject.transform.IsChildOf(_hud.Root.transform));
        }

        private void SelectCameraAtPointer()
        {
            if (!_camera || Screen.width < 1 || Screen.height < 1) return;
            var point = _looking ? new Vector2(Screen.width * .5f, Screen.height * .5f) : ReplayInput.MousePosition;
            if (PointerOverReplayUi(point)) return;
            var ray = _camera!.ViewportPointToRay(new Vector3(point.x / Screen.width, point.y / Screen.height, 0f));
            EntitySnapshot? closest = null;
            var closestDistance = float.PositiveInfinity;
            foreach (var entity in _frame.Entities)
            {
                if (!entity.Active || entity.Kind != "player" && entity.Kind != "enemy" ||
                    IsUnavailableTarget(entity) || !_entities.TryGetValue(entity.Id, out var visual) ||
                    !visual.Root.activeInHierarchy) continue;
                foreach (var renderer in visual.Root.GetComponentsInChildren<Renderer>())
                    Consider(renderer);
                if (entity.Kind == "player")
                    foreach (var body in _bakedPlayers.Values)
                        if (body.EntityId == entity.Id) Consider(body.Renderer);
                void Consider(Renderer renderer)
                {
                    if (!renderer || !renderer.enabled || renderer.forceRenderingOff ||
                        !renderer.gameObject.activeInHierarchy || !renderer.bounds.IntersectRay(ray, out var distance) ||
                        distance >= closestDistance) return;
                    closest = entity;
                    closestDistance = distance;
                }
            }
            if (closest != null) SelectCamera(closest.Id);
        }

        private void SelectCamera(string id)
        {
            if (id.Length == 0)
            {
                _follow = false;
                _bookmarkPlayerName = _bookmarkPlayerId = "";
                UpdatePlayerBodyOcclusion();
                return;
            }
            var player = _frame.Entities.FirstOrDefault(candidate => candidate.Id == id && candidate.Active &&
                (candidate.Kind == "player" || candidate.Kind == "enemy"));
            if (player == null || IsUnavailableTarget(player)) return;
            _selectedId = id;
            _followTargetId = id;
            _followTargetName = player.Name;
            _bookmarkPlayerId = player.Kind == "player" ? id : "";
            _bookmarkPlayerName = player.Kind == "player" ? player.Name : "";
            _lastFollowPosition = ToVector(player.Position);
            _hasLastFollowPosition = true;
            _follow = true;
            Focus(player);
        }

        private EntitySnapshot? SelectedPlayer() => _players.Find(player => player.Id == _selectedId);
        private EntitySnapshot? SelectedTarget() => _frame.Entities.FirstOrDefault(entity => entity.Id == _selectedId &&
            (entity.Kind == "player" || entity.Kind == "enemy"));
        private void FocusSelected() { var selected = SelectedPlayer(); if (selected != null && !IsDead(selected)) Focus(selected); }

        private static IEnumerable<ReplayPlayerDeath> DeathsInSession(ReplaySession session, double offset)
        {
            foreach (var evt in session.Events)
            {
                if (evt.Category != "state" || evt.Name != "isPlayerDead" ||
                    !evt.Data.TryGetValue("to", out var to) || !string.Equals(to, "True", StringComparison.OrdinalIgnoreCase))
                    continue;
                var player = session.Frames.Where(candidate => candidate.Time <= evt.Time + .0001)
                    .Reverse().Take(64).SelectMany(candidate => candidate.Entities)
                    .Where(candidate => candidate.Id == evt.EntityId && candidate.Kind == "player")
                    .FirstOrDefault(candidate => !IsDead(candidate)) ??
                    session.Frames.LastOrDefault(candidate => candidate.Time <= evt.Time + .0001)?
                        .Entities.Find(candidate => candidate.Id == evt.EntityId && candidate.Kind == "player");
                if (player == null) continue;
                yield return new ReplayPlayerDeath { Time = offset + evt.Time, EntityId = evt.EntityId,
                    Name = player.Name, Position = player.Position };
            }
        }

        private void CompletePlayerDeaths()
        {
            if (_playerDeathLoad == null || !_playerDeathLoad.IsCompleted) return;
            var loaded = _playerDeathLoad;
            _playerDeathLoad = null;
            if (loaded.IsCompletedSuccessfully)
            {
                _playerDeaths.Clear();
                _playerDeaths.AddRange(loaded.Result);
            }
            else _ = loaded.Exception;
        }

        private ReplayPlayerDeath? PlayerDeath(string id, string name, double atTime) => _playerDeaths
            .Where(death => (death.EntityId == id || id.Length == 0 && death.Name == name) && death.Time <= atTime + .001)
            .OrderByDescending(death => death.Time).FirstOrDefault();

        private double? DeathBookmarkTime()
        {
            if (_bookmarkPlayerName.Length == 0) return null;
            return _playerDeaths.Where(death => death.EntityId == _bookmarkPlayerId ||
                    _bookmarkPlayerId.Length == 0 && death.Name == _bookmarkPlayerName)
                .OrderBy(death => Math.Abs(death.Time - Time)).Select(death => (double?)death.Time).FirstOrDefault();
        }

        private void StopFollowingAtDeath(EntitySnapshot? player)
        {
            var death = player?.Kind == "player" ? PlayerDeath(_followTargetId, _followTargetName, Time) : null;
            var position = death != null ? ToVector(death.Position) : _hasLastFollowPosition ? _lastFollowPosition :
                player != null ? ToVector(player.Position) : _camera!.transform.position;
            _camera!.transform.position = position + Vector3.up * (player?.Kind == "enemy" ? .9f : 1.65f) -
                _camera.transform.forward * 2.5f;
            _follow = false;
            _cinematicVelocity = Vector3.zero;
            UpdateInteriorVisibility();
            UpdatePlayerBodyOcclusion();
        }

        private Vector3 TargetEye(EntitySnapshot entity)
        {
            if (entity.ViewPosition.HasValue) return ToVector(entity.ViewPosition.Value);
            if (_entities.TryGetValue(entity.Id, out var visual))
            {
                var head = visual.Bones.FirstOrDefault(pair => pair.Key.EndsWith("/spine.004[2]", StringComparison.Ordinal) ||
                    pair.Key.EndsWith("/head[0]", StringComparison.OrdinalIgnoreCase)).Value;
                if (head && (head.position - ToVector(entity.Position)).sqrMagnitude < 16f)
                    return head.position + Vector3.up * .18f + visual.Root.transform.forward * .08f;
            }
            return ToVector(entity.Position) + Vector3.up * (entity.Kind == "enemy" ? .9f : 1.65f);
        }

        private void Focus(EntitySnapshot entity)
        {
            if (_camera == null) return;
            _pitch = 12;
            var eye = TargetEye(entity);
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
                var desired = eye + Vector3.up * 2.5f - Quaternion.Euler(0, playerYaw, 0) * Vector3.forward * 4f;
                var elevated = ClearCameraPosition(eye, desired, entity.Id);
                if ((elevated - eye).sqrMagnitude < .75f * .75f) elevated = eye + Vector3.up * 1.5f;
                var look = Quaternion.LookRotation(eye - elevated, Vector3.up);
                var angles = look.eulerAngles;
                _yaw = angles.y; _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
                _camera.transform.SetPositionAndRotation(elevated, look);
                _followTargetDistance = _followDistance = Vector3.Distance(eye, elevated);
            }
            else
            {
                _camera.transform.SetPositionAndRotation(bestPosition, Quaternion.Euler(_pitch, _yaw, 0));
                _followTargetDistance = _followDistance = Math.Max(0.25f, bestDistance);
            }
            UpdatePlayerBodyOcclusion();
            _lookTargetYaw = _yaw; _lookTargetPitch = _pitch;
            _lookYawVelocity = _lookPitchVelocity = 0f;
        }

        private void UpdatePlayerBodyOcclusion()
        {
            if (_camera == null) return;
            var selected = SelectedPlayer();
            // Hide the followed body only when the camera has entered its head.
            // Free camera and ordinary third-person follow must still show it.
            var hideSelected = _follow && _followTargetDistance < .45f && selected != null;
            foreach (var entry in _dynamicGeometry)
            {
                if (!_entities.TryGetValue(entry.Value.EntityId, out var owner) || owner.Kind != "player" ||
                    !_geometryObjects.TryGetValue(entry.Key, out var obj) || !obj) continue;
                var renderer = obj.GetComponent<Renderer>();
                if (renderer) renderer.forceRenderingOff = _bakedPlayers.ContainsKey(entry.Key) ||
                    hideSelected && entry.Value.EntityId == _selectedId;
            }
            foreach (var body in _bakedPlayers.Values)
            {
                var hidden = hideSelected && body.EntityId == _selectedId;
                if (body.Renderer) body.Renderer.forceRenderingOff = hidden;
                if (body.Outline) body.Outline.forceRenderingOff = hidden;
            }
        }

        private void UpdateBakedPlayers()
        {
            foreach (var body in _bakedPlayers.Values)
            {
                if (!body.Source || !body.Object || !body.Mesh) continue;
                var active = body.Source.gameObject.activeInHierarchy && body.Source.enabled;
                SetActiveIfChanged(body.Object, active);
                if (!active) continue;
                body.Baker.Bake(body.Mesh, body.Object.transform);
                var worldCenter = body.Object.transform.TransformPoint(body.Mesh.bounds.center);
                if (body.Outline)
                {
                    body.Baker.BakeOutline(body.OutlineMesh, body.Object.transform, _camera!);
                }
                // Skin evaluation mutates an existing Mesh; HDRP can otherwise keep the
                // original empty bounds and cull the visible body from the frame.
                body.Renderer.bounds = new Bounds(worldCenter, Vector3.one * 4f);
                if (body.Outline) body.Outline.bounds = new Bounds(worldCenter, Vector3.one * 4f);
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

        private Material PlayerOutlineMaterial()
        {
            if (_playerOutlineMaterial) return _playerOutlineMaterial!;
            var material = new Material(_unlitShader ? _unlitShader! : _shader!)
                { name = "Replay player silhouette", hideFlags = HideFlags.DontSave };
            var color = Color.black;
            foreach (var property in new[] { "_UnlitColor", "_BaseColor", "_Color" })
                if (material.HasProperty(property)) material.SetColor(property, color);
            if (material.HasProperty("_CullMode")) material.SetFloat("_CullMode", (float)CullMode.Front);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Front);
            if (material.HasProperty("_DoubleSidedEnable")) material.SetFloat("_DoubleSidedEnable", 0f);
            material.DisableKeyword("_DOUBLESIDED_ON");
            material.renderQueue = (int)RenderQueue.Geometry;
            return _playerOutlineMaterial = material;
        }

        private Material TurretBeamMaterial()
        {
            if (_turretBeamMaterial) return _turretBeamMaterial!;
            _turretBeamTexture = new Texture2D(32, 64, TextureFormat.RGBA32, false, true)
                { name = "Replay turret soft beam", hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[32 * 64];
            for (var y = 0; y < 64; y++)
            {
                var distance = Mathf.Abs((y + .5f - 32f) / 32f);
                var alpha = Mathf.Exp(-distance * distance * 18f) * Mathf.Clamp01((1f - distance) * 4f);
                for (var x = 0; x < 32; x++) pixels[y * 32 + x] = new Color32(255, 255, 255, (byte)(255 * alpha));
            }
            _turretBeamTexture.SetPixels32(pixels); _turretBeamTexture.Apply(false, true);
            var material = CreateParticleMaterial(_turretBeamTexture, "Replay turret beam");
            var beamColor = new Color(1f, .025f, .012f, 1f);
            foreach (var property in new[] { "_UnlitColor", "_BaseColor", "_Color" })
                if (material.HasProperty(property)) material.SetColor(property, beamColor);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", beamColor * 4f);
            if (material.HasProperty("_CullMode")) material.SetFloat("_CullMode", (float)CullMode.Off);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Off);
            material.renderQueue = (int)RenderQueue.Transparent;
            return _turretBeamMaterial = material;
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
            SetFogField(parameters, "albedo", new Color(snapshot.Albedo[0], snapshot.Albedo[1], snapshot.Albedo[2], 1f));
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
                foreach (var name in new[] { "AtmosphericScattering", "Volumetrics", "ReprojectionForVolumetrics",
                    "CustomPass", "Postprocess", "CustomPostProcess", "MotionVectors", "ObjectMotionVectors",
                    "ContactShadows", "SSAO", "SSR", "VolumetricClouds", "ExposureControl", "DepthOfField",
                    "MotionBlur", "Bloom", "LensDistortion", "ChromaticAberration", "Vignette",
                    "ColorGrading", "FilmGrain", "Tonemapping" })
                {
                    if (!Enum.IsDefined(fieldType, name)) continue;
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

        private static void ConfigureNightVision(Light target)
        {
            var type = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
            var source = type == null ? null : Resources.FindObjectsOfTypeAll(type).OfType<Component>()
                .Select(player => GameAccess.Read(player, "nightVision") as Light).FirstOrDefault(light => light);
            var hdType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
            var data = hdType == null ? null : target.GetComponent(hdType);
            var native = source && hdType != null ? source!.GetComponent(hdType) : null;
            if (source)
            {
                target.type = source!.type; target.color = source.color; target.range = source.range;
                target.spotAngle = source.spotAngle;
            }
            if (data)
                foreach (var name in new[] { "lightUnit", "shapeRadius", "applyRangeAttenuation", "affectDiffuse", "affectSpecular", "intensity" })
                {
                    var value = native ? GameAccess.Read(native, name) : name == "shapeRadius" ? (object)2.2f :
                        name == "affectSpecular" || name == "applyRangeAttenuation" ? false : null;
                    if (value != null) SetOptionalMember(data!, name, value);
                }
            if (source) target.intensity = source!.intensity;
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

        private void BuildDoorMeshLinks(WorldSnapshot world)
        {
            if (!_session.DoorPoseReferences.TryGetValue(world.CaptureSetId, out var references)) return;
            foreach (var frame in _session.Frames)
                foreach (var door in frame.Entities.Where(entity => entity.Kind == "door"))
                    _firstDoorPoses.TryAdd(door.Id, door);
            foreach (var geometry in world.Geometry)
            {
                if (!geometry.IsMovingSceneRenderer || geometry.Name != "DoorMesh" ||
                    geometry.EntityId.Length != 0 || geometry.AnchorId.Length != 0) continue;
                var position = ToVector(geometry.Position);
                var rotation = ToQuaternion(geometry.Rotation);
                var reference = references.Where(entity =>
                        (ToVector(entity.Position) - position).sqrMagnitude < 4f &&
                        Quaternion.Angle(ToQuaternion(entity.Rotation), rotation) < 10f)
                    .OrderBy(entity => (ToVector(entity.Position) - position).sqrMagnitude).FirstOrDefault();
                if (reference == null) continue;
                var origin = ToVector(reference.Position);
                var orientation = ToQuaternion(reference.Rotation);
                _doorMeshLinks[geometry.Id] = (reference.Id, Quaternion.Inverse(orientation) * (position - origin),
                    Quaternion.Inverse(orientation) * rotation);
            }
        }

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

        internal void Park()
        {
            if (!CanPark) throw new InvalidOperationException("The replay is not ready to cache.");
            _resumePlaying = IsPlaying;
            IsPlaying = false;
            Application.onBeforeRender -= RefreshCursor;
            if (_replayAudio) _replayAudio!.Stop();
            foreach (var voice in _spatialVoices) ReleaseSpatialVoice(voice);
            _lastAudioTime = -1;
            _lastItemImpactTime = -1;
            foreach (var source in _itemSoundSources.Values) if (source) source.Stop();
            _nativeSounds?.Stop();
            _assetScene?.SetSuspended(true);
            if (_hud != null)
            {
                _resumeHudVisible = _hud.Root.activeSelf;
                _hud.Root.SetActive(false);
                // A cached HUD keeps its objects, but must release the game/mod
                // menu Selectables before the archive takes another input scope.
                _hud.ReleaseInput();
            }
            _root!.SetActive(false);
            RestoreOtherViews();
            _parked = true;
        }

        internal void Resume()
        {
            if (_disposed || !_parked || !_root || !_scene.IsValid() || !_scene.isLoaded)
                throw new InvalidOperationException("The cached replay scene is unavailable.");
            _savedCursorLock = Cursor.lockState;
            _savedCursorVisible = Cursor.visible;
            SuspendOtherViews();
            _root!.SetActive(true);
            _assetScene?.SetSuspended(false);
            if (_hud != null) { _hud.AcquireInput(); _hud.Root.SetActive(_resumeHudVisible); }
            _parked = false;
            CloseRequested = false;
            IsPlaying = _resumePlaying && Time < Duration;
            Application.onBeforeRender += RefreshCursor;
            SetLooking(Application.isFocused && !_uiCursor);
        }

        private void RestoreOtherViews()
        {
            foreach (var pair in _cameraStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            // The live player may die or the round may switch views while
            // replay is open. Re-enable its current view, not a stale camera
            // captured at entry (which can overlap the death/spectator camera).
            if (_preserveLiveAudio && GameAccess.Connected)
            {
                var round = GameAccess.Singleton("StartOfRound");
                var active = GameAccess.Read(round, "activeCamera") as Camera;
                if (active && !ReplayIsolation.IsReplayScene(active!.gameObject.scene))
                {
                    if (GameAccess.Read(round, "allPlayerScripts") is System.Collections.IEnumerable players)
                        foreach (var player in players)
                            if (GameAccess.Read(player, "gameplayCamera") is Camera gameplay && gameplay)
                                gameplay.enabled = gameplay == active;
                    if (GameAccess.Read(round, "spectateCamera") is Camera spectator && spectator)
                        spectator.enabled = spectator == active;
                    active.enabled = true;
                }
            }
            foreach (var pair in _canvasStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in _listenerStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in _audioSourceStates) if (pair.Key != null) pair.Key.mute = pair.Value;
            foreach (var pair in _rendererStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in _uiInputStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            if (_savedEventSystem != null && _eventSystemCurrentProperty?.CanWrite == true)
            {
                try { _eventSystemCurrentProperty.SetValue(null, _savedEventSystem, null); }
                catch (Exception) { /* Enabled systems are restored even if the active system changed. */ }
            }
            Cursor.lockState = _savedCursorLock;
            Cursor.visible = _savedCursorVisible;
            _cameraStates.Clear(); _canvasStates.Clear(); _listenerStates.Clear();
            _audioSourceStates.Clear(); _rendererStates.Clear(); _uiInputStates.Clear();
            _savedEventSystem = null;
            _eventSystemCurrentProperty = null;
            _eventSystemRemembered = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Application.onBeforeRender -= RefreshCursor;
            _playerDeathCancellation.Cancel();
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
                _nativeSounds?.Dispose(); _nativeSounds = null;
                foreach (var source in _itemSoundSources.Values) if (source) source.Stop();
                _itemSoundSources.Clear();
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
                _futureAssetScene?.Dispose();
                _futureAssetScene = null;
                ReleaseFutureResources();
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
                if (_cube != null) Object.Destroy(_cube);
                foreach (var material in _materials.Values) if (material != null) Object.Destroy(material);
                if (_playerOutlineMaterial) Object.Destroy(_playerOutlineMaterial);
                if (_turretBeamMaterial) Object.Destroy(_turretBeamMaterial);
                if (_turretBeamTexture) Object.Destroy(_turretBeamTexture);
                _structureAudio?.Dispose(); _structureAudio = null;
                ReplayIsolation.Unregister(_scene);
                if (_scene.IsValid() && _scene.isLoaded) SceneManager.UnloadSceneAsync(_scene);
            }
            finally
            {
                if (!_parked) RestoreOtherViews();
                _uiInputTypes.Clear();
                foreach (var entity in _entities.Values) entity.NativeRig?.Dispose();
                _entities.Clear();
                _frameLines.Clear();
                _materials.Clear();
                _worldMeshes.Clear();
                _worldFogMasks.Clear();
                _playerDeathCancellation.Dispose();
                _instancedGeometry.Clear();
                _worldParticles.Clear();
                _localFogs.Clear();
                _fogGeometry.Clear();
                _worldObjects.Clear();
                _geometryObjects.Clear();
                _hazardsWithModel.Clear();
                _dynamicGeometry.Clear();
                _movingSceneGeometry.Clear();
                _completeRendererLists.Clear();
                _interiorRenderers.Clear();
                _deferredWorldRenderers.Clear();
                _deferredWorldLights.Clear();
                _deferredWorldFogs.Clear();
                _deferredWorldParticles.Clear();
                _interiorRooms.Clear();
                _additionalRoomVolumes.Clear();
                _renderGeometrySources.Clear();
            }
        }

        private sealed class EntityVisual
        {
            internal readonly GameObject Root;
            internal readonly GameObject Proxy;
            internal GameObject? NativeHazard;
            internal NativeActorRig? NativeRig;
            internal readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();
            internal readonly Dictionary<string, BonePose> RestBones = new Dictionary<string, BonePose>(StringComparer.Ordinal);
            internal string Kind = "";
            internal int GeometryCount;
            internal Animator? Animator;
            internal readonly HashSet<string> AnimatorParameterKeys = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Animator> Animators = new Dictionary<string, Animator>(StringComparer.Ordinal);
            internal readonly Dictionary<string, HashSet<string>> AnimatorParameterKeysByPath =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            internal readonly HashSet<string> UnresolvedAnimatorPaths = new HashSet<string>(StringComparer.Ordinal);
            internal GameObject? Skeleton;
            internal Mesh? SkeletonMesh;
            internal NativeTurretEffect? NativeTurret;
            internal NativeLandmineEffect? NativeMine;
            internal bool MineAttempted;
            internal readonly List<Renderer> MineMeshes = new List<Renderer>();
            internal bool TurretAttempted;
            internal NativeSwarmEffect? NativeSwarm;
            internal bool SwarmAttempted;
            internal EntityVisual(GameObject root, GameObject proxy) { Root = root; Proxy = proxy; }
        }

        private sealed class BakedPlayerBody
        {
            internal readonly SkinnedMeshRenderer Source;
            internal readonly GameObject Object;
            internal readonly Mesh Mesh;
            internal readonly MeshRenderer Renderer;
            internal readonly MeshRenderer Outline;
            internal readonly Mesh OutlineMesh;
            internal readonly string EntityId;
            internal readonly NativePlayerSkinBake Baker;
            internal BakedPlayerBody(SkinnedMeshRenderer source, GameObject obj, Mesh mesh, MeshRenderer renderer,
                MeshRenderer outline, Mesh outlineMesh, string entityId)
            {
                Source = source; Object = obj; Mesh = mesh; Renderer = renderer; Outline = outline; EntityId = entityId;
                OutlineMesh = outlineMesh;
                Baker = new NativePlayerSkinBake(source);
            }
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
