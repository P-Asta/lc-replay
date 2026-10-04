using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin.Capture
{
    internal sealed class Recorder : IDisposable
    {
        private readonly ReplayWriter writer;
        private readonly ReplayCaptureBuffer storage;
        private readonly EntityTracker tracker;
        private readonly ItemMotionCapture itemMotion = new ItemMotionCapture();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<ReplayEvent> events = new ConcurrentQueue<ReplayEvent>();
        private readonly object eventGate = new object();
        private readonly int rate, maxFields, maxObjects, maxVertices;
        private readonly bool captureBones, captureWorld;
        private readonly Action<string> log;
        private ReplayFrame? previous;
        private double nextFrame, nextDiscovery, nextWorld = 2;
        private double nextPostFx = .5;
        private readonly Dictionary<string, EnvironmentComponentSnapshot> lastPostFx =
            new Dictionary<string, EnvironmentComponentSnapshot>(StringComparer.Ordinal);
        private bool postFxInitialized;
        private double nextSun;
        private readonly Dictionary<string, SunLighting> lastSun = new Dictionary<string, SunLighting>(StringComparer.Ordinal);
        private double nextLateEntityScan;
        private bool worldDirty = true;
        private bool generationInProgress;
        private bool generationCompletionSeen;
        private bool refreshInteriorVisibility;
        private bool preGenerationExteriorCaptured;
        private bool worldJobAssetOnly;
        private bool worldVisibilityPending;
        private double generationStartedAt;
        private double worldVisibleAt;
        private bool awaitingWorldActivation;
        private bool environmentDirty;
        private int worldLayerPhase;
        private int exteriorChunkIndex;
        private string worldCaptureSetId = "";
        private readonly HashSet<string> capturedWorldIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> emittedGeometryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> lateEntityRetryAt = new Dictionary<string, double>(StringComparer.Ordinal);
        private MeshSnapshotReader.StaticBatchReader? staticBatches;
        private SceneAssetCapture? sceneAssets;
        private NativeSoundCapture? nativeSounds;
        private WorldCapture.CaptureJob? worldJob;
        private WorldCapture.CaptureJob? lateEntityJob;
        private string[] lateEntityIds = Array.Empty<string>();
        private EntityTracker.Entry[] lateScanEntries = Array.Empty<EntityTracker.Entry>();
        private int lateScanCursor;
        private readonly List<string> lateScanIds = new List<string>(64);
        private WorldSnapshot? lateCompletedWorld;
        private WorldSnapshot? completedWorldAtStop;
        private string lateCompletedSummary = "";
        private int lateVerifyCursor;
        private bool disposed;
        private Task? finishTask;
        private bool hasWorld;
        private readonly List<WorldSnapshot> reusableWorlds = new List<WorldSnapshot>();
        private long reusableWorldBytes;
        private bool reusableWorldOverflowed;
        private Exception? error;
        private double? storagePausedAt;
        private long droppedEvents;
        private bool indexFailureReported;
        private int maintenancePhase;
        private int consecutiveSnapshots;
        private IEnumerator<ReplayRecord?>? sampleOutput;
        private bool extraWorldTurn;
        private readonly EnvironmentCapture.ChangingCapture changingPostFx = new EnvironmentCapture.ChangingCapture();
        // Native probes inspect the work selected for each Unity frame.
        internal string LastWork { get; private set; } = "";
        private const long MaxDayExpandedBytes = 24L * 1024 * 1024 * 1024;
        private static readonly string[] TransitionFields = { "health", "isPlayerDead", "isEnemyDead", "enemyHP", "isHeld", "playerHeldBy", "currentItemSlot", "ItemOnlySlot", "isDoorOpened", "isDoorOpen", "isLocked", "hasExploded", "currentBehaviourStateIndex", "turretMode", "inShipPhase", "shipHasLanded", "scrapValue", "groupCredits", "insertedBattery.empty", "carDestroyed" };
        public string FilePath { get; }
        public double Duration => clock.Elapsed.TotalSeconds;
        public double RecordedDuration => writer.WrittenDuration;
        public int BookmarkCount { get; private set; }
        public int WrittenBookmarkCount => writer.WrittenBookmarkCount;
        public int EntityCount => tracker.Count;
        public Exception? Error { get => error ?? storage.Error ?? writer.Error; private set => error = value; }
        // Carry the complete paired capture set across safety-part boundaries.
        // The immutable snapshots are queued by reference, avoiding another costly
        // Unity scene scan while the same quota/deadline day continues.
        public IReadOnlyList<WorldSnapshot>? ReusableWorld => !worldDirty && reusableWorlds.Count != 0
            ? reusableWorlds.ToArray() : null;

        public Recorder(string directory, int sampleRate, bool bones, bool world, int fields, int objects, int vertices,
            string extraTypes, EventHooks hooks, bool chat, Action<string> logger, string recordingGroup = "", int part = 1,
            string? outputPath = null, IDictionary<string, string>? archiveMetadata = null, IReadOnlyList<WorldSnapshot>? initialWorld = null)
        {
            if (sampleRate < 1 || sampleRate > 60) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (fields < 1 || fields > 512) throw new ArgumentOutOfRangeException(nameof(fields));
            if (objects < 1 || objects > 20000 || vertices < 1 || vertices > 1000000) throw new ArgumentOutOfRangeException(nameof(objects));
            if (part < 1) throw new ArgumentOutOfRangeException(nameof(part));
            if (recordingGroup.Length > 128 || recordingGroup.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Recording group must contain letters, digits or hyphens.", nameof(recordingGroup));
            log = logger; rate = sampleRate; captureBones = bones; captureWorld = world;
            maxFields = fields; maxObjects = objects; maxVertices = vertices;
            tracker = new EntityTracker(extraTypes, captureBones);
            PrefabAssetRegistry.Warm();
            var header = new ReplayHeader
            {
                SessionId = System.Guid.NewGuid().ToString("N"), GameVersion = Application.version,
                UnityVersion = Application.unityVersion, RecorderVersion = ReplayPlugin.Version,
                StartedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), SampleRate = sampleRate,
                Perspective = GameAccess.IsHost ? "host-observed" : "client-observed"
            };
            header.Capabilities.AddRange(new[] { "entity-transforms", "child-renderer-poses", "sparse-entity-renderer-poses", "primitive-game-fields", "state-transitions", "observed-method-calls", "round-state", "custom-provider-api" });
            header.Capabilities.Add("actor-animation-state-events");
            header.Capabilities.Add("enemy-visual-bone-poses");
            if (!captureBones) header.Capabilities.Add("actor-animation-parameter-events");
            if (captureBones) header.Capabilities.Add("actor-bone-poses");
            if (captureWorld) header.Capabilities.AddRange(new[] { "render-geometry-and-bounds", "embedded-render-assets",
                "layered-world-capture", "recorded-sky-fog", "shader-properties", "instanced-grass", "looping-particle-emitters" });
            header.Capabilities.Add("visual-effect-swarm-approximation");
            header.Capabilities.Add("moving-scene-renderers");
            header.Capabilities.Add("sparse-static-visibility");
            header.Capabilities.Add("spray-decal-events");
            header.Capabilities.Add("sampled-line-renderers");
            header.Capabilities.Add("late-entity-geometry");
            header.Capabilities.Add("installed-prefab-render-assets");
            header.Capabilities.Add("sparse-item-motion");
            header.Capabilities.Add("native-sound-events");
            header.Capabilities.Add("native-ambient-sound-events");
            header.Capabilities.Add("recording-bookmarks");
            header.Capabilities.Add("game-clock");
            if (captureWorld) header.Capabilities.Add("sparse-sun-lighting");
            if (captureWorld) header.Capabilities.Add("world-postfx-without-player-filters");
            if (captureWorld) header.Capabilities.Add("sparse-hdrp-post-processing");
            if (chat) header.Capabilities.Add("local-chat");
            header.Warnings.Add("Only data visible to this recorder is available. Remote/private state is not reconstructed.");
            header.Warnings.Add("GPU-only particle states, baked lighting, custom post-processing textures, RNG execution and arbitrary object graphs are not recorded. Short-lived Unity particles are sampled with a per-frame cap; looping emitters and GPU swarms use bounded approximations.");
            header.Warnings.Add("Game sound actions reference installed clips; no waveform or voice chat is recorded.");
            header.Warnings.Add("Fields, arrays, bones, entities, provider state and world geometry are bounded; omissions are recorded.");
            header.Warnings.Add("Enemy visual bones and rigid model ancestors, including inactive forms, are sampled after animation and procedural movement with a 512-transform limit per enemy. Matching installed prefabs supply controllers, rigs and meshes; custom shader deformation and uncaptured runtime meshes may differ.");
            if (!captureBones) header.Warnings.Add("Player and item animation uses sparse Animator state and parameter changes plus reusable short motion tracks. Matching installed controllers and avatars reproduce their blend trees; unavailable assets use observed tracks where available. Enable CaptureBones for their independent pose streams.");
            if (captureWorld) header.Warnings.Add("HDRP volume settings are sampled only when their effective values change. Scalar, vector and bounded color-curve settings are saved; custom-pass textures need the installed game assets and may be unavailable after updates.");
            header.Warnings.Add("Method calls may include rejected actions, RPC duplicates or miss overrides; use snapshot transitions for outcomes.");
            foreach (var type in tracker.MissingTypes) header.Warnings.Add("Unavailable component type: " + type);
            foreach (var hook in hooks.Missing) header.Warnings.Add("Unavailable optional hook: " + hook);
            header.Metadata["hooks"] = string.Join(",", hooks.Installed);
            header.Metadata["maxFieldsPerEntity"] = fields.ToString();
            header.Metadata["maxWorldObjects"] = objects.ToString();
            header.Metadata["maxWorldVertices"] = vertices.ToString();
            header.Metadata["maxEntitiesPerFrame"] = "4096";
            header.Metadata["maxEstimatedEntityBytesPerFrame"] = (24L * 1024 * 1024).ToString(CultureInfo.InvariantCulture);
            header.Metadata["extraTrackedTypes"] = extraTypes;
            var gameNetworkVersion = GameAccess.Scalar(GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "gameVersionNum"));
            if (gameNetworkVersion != null) header.Metadata["gameNetworkVersion"] = gameNetworkVersion;
            var gameAssembly = GameAccess.Type("GameNetworkManager")?.Assembly;
            if (gameAssembly != null)
            {
                header.Metadata["gameAssemblyVersion"] = gameAssembly.GetName().Version?.ToString() ?? "unknown";
                header.Metadata["gameAssemblyModuleId"] = gameAssembly.ManifestModule.ModuleVersionId.ToString("D");
            }
            var group = string.IsNullOrEmpty(recordingGroup) ? header.SessionId : recordingGroup;
            header.Metadata["recordingGroup"] = group;
            header.Metadata["part"] = part.ToString(CultureInfo.InvariantCulture);
            header.Metadata["singleFile"] = "true";
            header.Metadata["segmentBoundary"] = "return-to-orbit-or-disconnect";
            if (archiveMetadata != null)
                foreach (var entry in archiveMetadata) header.Metadata[entry.Key] = entry.Value;
            var timeOfDay = GameAccess.Singleton("TimeOfDay");
            foreach (var field in new[] { "normalizedTimeOfDay", "numberOfHours", "currentDayTime" })
                if (GameAccess.Scalar(GameAccess.Read(timeOfDay, field)) is string text &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) && !double.IsInfinity(value))
                    header.Metadata[field == "normalizedTimeOfDay" ? "gameStartNormalizedTime" :
                        field == "numberOfHours" ? "gameNumberOfHours" : "gameStartTime"] = value.ToString("R", CultureInfo.InvariantCulture);
            foreach (var plugin in Chainloader.PluginInfos.Values)
                header.Metadata["mod:" + plugin.Metadata.GUID] = plugin.Metadata.Version.ToString();
            Directory.CreateDirectory(directory);
            FilePath = outputPath ?? Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + group.Substring(0, Math.Min(8, group.Length))
                + "-part" + part.ToString("D4", CultureInfo.InvariantCulture) + ".lcr");
            writer = new ReplayWriter(FilePath, header, 64, indexed: true);
            storage = new ReplayCaptureBuffer(writer);
            try
            {
                SceneManager.sceneLoaded += SceneLoaded;
                SceneManager.sceneUnloaded += SceneUnloaded;
                nativeSounds = new NativeSoundCapture(tracker, Event);
                if (initialWorld != null && initialWorld.Count != 0)
                {
                    foreach (var snapshot in initialWorld)
                    {
                        Write(new ReplayRecord { Kind = "world", Time = 0, World = snapshot });
                        RememberWorld(snapshot);
                        foreach (var geometry in snapshot.Geometry)
                        {
                            emittedGeometryIds.Add(geometry.Id);
                        }
                    }
                    if (Error != null) throw Error;
                    worldCaptureSetId = initialWorld[0].CaptureSetId;
                    hasWorld = true; worldDirty = false;
                }
            }
            catch
            {
                SceneManager.sceneLoaded -= SceneLoaded;
                SceneManager.sceneUnloaded -= SceneUnloaded;
                nativeSounds?.Dispose();
                storage.CompleteAsync().GetAwaiter().GetResult();
                throw;
            }
        }
        public void Event(ReplayEvent evt)
        {
            lock (eventGate)
            {
                if (disposed || Error != null) return;
                if (events.Count >= 2048) { System.Threading.Interlocked.Increment(ref droppedEvents); return; }
                // Callers retain their event instance; queued records own an immutable copy.
                events.Enqueue(new ReplayEvent { Time = Duration, Category = evt.Category, Name = evt.Name, EntityId = evt.EntityId,
                    Data = new Dictionary<string, string>(evt.Data) });
                if (evt.Category == "marker" && evt.Name == "bookmark") BookmarkCount++;
            }
        }
        public void NoticeSpiderWeb()
        {
            if (disposed || !captureWorld || worldDirty) return;
            tracker.RestartDiscovery();
            nextDiscovery = 0;
            nextLateEntityScan = nextLateEntityScan <= Duration ? Duration + .35 :
                Math.Min(nextLateEntityScan, Duration + .35);
        }
        public void MarkWorldDirty()
        {
            worldJob?.Dispose(); worldJob = null; worldJobAssetOnly = false;
            tracker.CancelWorldVisibility(); worldVisibilityPending = false;
            lateEntityJob?.Dispose(); lateEntityJob = null; lateEntityIds = Array.Empty<string>();
            lateScanEntries = Array.Empty<EntityTracker.Entry>(); lateScanIds.Clear(); lateScanCursor = 0;
            lateCompletedWorld = null; lateVerifyCursor = 0;
            tracker.Visual.Reset();
            tracker.ClearMovingSceneRenderers();
            tracker.ClearEntityRendererBaselines();
            worldDirty = true; environmentDirty = false; worldLayerPhase = 0; exteriorChunkIndex = 0;
            refreshInteriorVisibility = false; preGenerationExteriorCaptured = false;
            reusableWorlds.Clear();
            reusableWorldBytes = 0; reusableWorldOverflowed = false;
            emittedGeometryIds.Clear(); lateEntityRetryAt.Clear();
            capturedWorldIds.Clear(); staticBatches = null;
            worldVisibleAt = Duration; awaitingWorldActivation = false;
            nextWorld = Duration + (generationInProgress ? .15 : 2);
        }
        public void RefreshEnvironmentAfterLanding()
        {
            if (!captureWorld) return;
            if (worldDirty) return;
            if (!hasWorld) { MarkWorldDirty(); return; }
            environmentDirty = true;
            nextWorld = Math.Max(nextWorld, Duration + 2);
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (ReplayIsolation.PlaybackActive || ReplayIsolation.IsReplayScene(scene)) return;
            nativeSounds?.RefreshAmbient();
            Event(new ReplayEvent { Category = "scene", Name = "loaded", Data = new Dictionary<string, string> { ["scene"] = scene.name } });
            var expected = GameAccess.Scalar(GameAccess.Read(GameAccess.Read(GameAccess.Singleton("StartOfRound"), "currentLevel"), "sceneName"));
            sceneAssets = SceneAssetCapture.TryCreate(scene, expected);
            generationCompletionSeen = false;
            tracker.RestartDiscovery(); nextDiscovery = 0;
            MarkWorldDirty();
        }
        private void SceneUnloaded(Scene scene)
        {
            if (ReplayIsolation.PlaybackActive || ReplayIsolation.IsReplayScene(scene)) return;
            Event(new ReplayEvent { Category = "scene", Name = "unloaded", Data = new Dictionary<string, string> { ["scene"] = scene.name } });
            if (sceneAssets?.SceneName == scene.name) sceneAssets = null;
            MarkWorldDirty();
        }

        public void Tick(bool forceFrame = false, bool suspendWorldCapture = false)
        {
            if (disposed) return;
            if (writer.Error != null) Error = writer.Error;
            if (writer.ExpandedBytes >= MaxDayExpandedBytes)
                Error = new IOException("The single-day replay reached its 24 GiB expanded-data safety limit.");
            if (Error != null) return;
            if (!indexFailureReported && writer.IndexError != null)
            { indexFailureReported = true; log("Replay index saving failed; recording continues and the index can be rebuilt: " + writer.IndexError.Message); }
            LastWork = "storage";
            var tickStarted = Stopwatch.GetTimestamp();
            storage.Drain(32, .25);
            var now = Duration;
            if (storage.ShouldPauseCapture)
            {
                if (!storagePausedAt.HasValue)
                { storagePausedAt = now; log("Disk saving is catching up; capture sampling is temporarily paused and accepted data is retained."); }
                return;
            }
            var missedEvents = System.Threading.Interlocked.Exchange(ref droppedEvents, 0);
            if (storagePausedAt.HasValue || missedEvents != 0)
            {
                Write(new ReplayRecord { Kind = "event", Time = now, Event = new ReplayEvent { Time = now, Category = "capture", Name = "storage-gap",
                    Data = new Dictionary<string, string> { ["seconds"] = (now - (storagePausedAt ?? now)).ToString("F4", CultureInfo.InvariantCulture),
                        ["droppedEvents"] = missedEvents.ToString(CultureInfo.InvariantCulture) } } });
                if (storagePausedAt.HasValue) log("Disk saving caught up; recording sampling resumed.");
                storagePausedAt = null;
            }
            // Flush owned output before taking another snapshot, but never let
            // the old 128-entity limit turn a large lobby into multi-second gaps.
            // Deadline pressure gets a small extra CPU budget, not an unbounded
            // synchronous drain or an ever-growing queue of snapshots.
            if (sampleOutput != null)
            {
                LastWork = "sample-output";
                var outputStarted = Stopwatch.GetTimestamp();
                var budget = forceFrame || now >= nextFrame ? 1.5 : .4;
                var remaining = 32;
                for (var work = 0; work < 4096 && remaining > 0; work++)
                {
                    if (!sampleOutput.MoveNext())
                    { sampleOutput.Dispose(); sampleOutput = null; break; }
                    if (sampleOutput.Current != null) { Write(sampleOutput.Current); remaining--; }
                    if (Elapsed(outputStarted) >= budget || storage.ShouldPauseCapture) break;
                }
                if (sampleOutput != null || Error != null || storage.ShouldPauseCapture) return;
                // Refresh the timestamp after output work. Never stamp a new
                // Unity observation with a time from before that work ran.
                now = Duration;
            }
            if (forceFrame || now >= nextFrame && consecutiveSnapshots < 2)
            {
                LastWork = "snapshot";
                if (now - nextFrame > 1.0 / rate && previous != null)
                    Write(new ReplayRecord { Kind = "event", Time = now, Event = new ReplayEvent { Time = now,
                        Category = "capture", Name = "sample-gap", Data = new Dictionary<string, string> {
                            ["seconds"] = (now - previous.Time).ToString("F4", CultureInfo.InvariantCulture) } } });
                // Keep player, held-item and view poses at one observation time.
                // The pure-data transition/output pass can safely run later.
                var frame = tracker.Capture(now, captureBones, maxFields);
                if (awaitingWorldActivation && exteriorChunkIndex == 0 &&
                    frame.State.TryGetValue("StartOfRound.inShipPhase", out var phase) &&
                    bool.TryParse(phase, out var inShipPhase) && !inShipPhase)
                {
                    worldVisibleAt = now;
                    awaitingWorldActivation = false;
                }
                ReplayApi.CaptureProviders(frame.State, log);
                var motions = itemMotion.Observe(frame, tracker).ToList();
                sampleOutput = SampleRecords(frame, previous, motions).GetEnumerator();
                previous = frame;
                // Keep the configured cadence across fractional Unity frames.
                // Skip expired deadlines rather than fabricating catch-up poses.
                nextFrame = forceFrame ? now + 1.0 / rate :
                    nextFrame + (Math.Floor(Math.Max(0, now - nextFrame) * rate) + 1) / rate;
                consecutiveSnapshots++;
                return;
            }
            // Non-pose work uses the frames between sampling deadlines. An
            // event burst cannot consume every deadline before Capture runs.
            // If the requested rate exceeds the game's available frames, still
            // reserve a maintenance turn after two consecutive observations.
            consecutiveSnapshots = 0;
            foreach (var animation in tracker.AnimationChangesStep(now, 4, .25))
                Write(new ReplayRecord { Kind = "event", Time = animation.Time, Event = animation });
            if (Error != null || storage.ShouldPauseCapture || Elapsed(tickStarted) >= .75) return;
            DrainEvents(now, tickStarted);
            if (Error != null || storage.ShouldPauseCapture || Elapsed(tickStarted) >= .5) return;
            // Give an unfinished map extra idle frames without running multiple
            // maintenance stages together or starving discovery and ambience.
            if (captureWorld && !suspendWorldCapture && worldDirty && extraWorldTurn)
            {
                extraWorldTurn = false;
                LastWork = "world"; CaptureWorldWork(now, false); return;
            }
            extraWorldTurn = true;
            switch (maintenancePhase++ % 6)
            {
                case 0:
                    LastWork = "discovery";
                    if (now >= nextDiscovery && tracker.DiscoverStep()) nextDiscovery = now + 1;
                    break;
                case 1:
                    LastWork = "sounds"; nativeSounds?.Tick(); break;
                case 2:
                    LastWork = "postfx"; CapturePostFx(now); break;
                case 3:
                    LastWork = "world"; CaptureWorldWork(now, suspendWorldCapture); break;
                case 4:
                    LastWork = "visual";
                    if (captureWorld && worldCaptureSetId.Length != 0)
                        foreach (var visualEvent in tracker.Visual.Scan(now, worldCaptureSetId))
                            Write(new ReplayRecord { Kind = "event", Time = visualEvent.Time, Event = visualEvent });
                    break;
                case 5:
                    LastWork = "sun"; CaptureSun(now); break;
            }
        }

        private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        private void CaptureSun(double now)
        {
            if (captureWorld && now >= nextSun)
            {
                nextSun = now + 1;
                var timeOfDay = GameAccess.Singleton("TimeOfDay");
                foreach (var role in new[] { "sunDirect", "sunIndirect" })
                    if (GameAccess.Read(timeOfDay, role) is Light light && light)
                    {
                        var sun = SunLighting.Capture(light, now);
                        // TimeOfDay dims indirect sunlight for the live player's
                        // indoor camera. Replay visibility belongs to its camera.
                        if (role == "sunIndirect") sun.Dimmer = 1f;
                        if (!lastSun.TryGetValue(role, out var previousSun) || sun.Changed(previousSun))
                        { Write(new ReplayRecord { Kind = "event", Time = now, Event = sun.Event(role) }); lastSun[role] = sun; }
                    }
            }
        }

        private void CapturePostFx(double now)
        {
            if (!captureWorld || !changingPostFx.Active && now < nextPostFx) return;
            foreach (var component in changingPostFx.Step())
            {
                    if (lastPostFx.TryGetValue(component.Type, out var previousPostFx) &&
                        !PostFxChanged(previousPostFx, component)) continue;
                    if (postFxInitialized)
                        Write(new ReplayRecord { Kind = "event", Time = now,
                            Event = new ReplayEvent { Time = now, Category = "postfx", Name = "component",
                                PostProcess = component } });
                    lastPostFx[component.Type] = component;
            }
            if (!changingPostFx.Active)
            { nextPostFx = now + .25; if (lastPostFx.Count != 0) postFxInitialized = true; }
        }

        private void DrainEvents(double now, long tickStarted)
        {
            var eventBudget = 8;
            while (eventBudget-- > 0 && events.TryDequeue(out var evt))
            {
                if (evt.Name == "PlayerControllerB.TeleportPlayer" &&
                    evt.Data.TryGetValue("instanceId", out var teleportedText) &&
                    int.TryParse(teleportedText, out var teleportedId))
                    evt.EntityId = tracker.Entries.FirstOrDefault(entry => entry.Kind == "player" &&
                        entry.Component && entry.Component.GetInstanceID() == teleportedId)?.Id ?? "";
                if (evt.Name == "EnemyAI.Start" && evt.Data.TryGetValue("instanceId", out var enemyText) &&
                    int.TryParse(enemyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enemyId))
                {
                    var spawned = EventHooks.TakeSpawnedEnemy(enemyId);
                    if (spawned)
                    {
                        tracker.RegisterSpawnedEnemy(spawned!);
                        nextLateEntityScan = 0;
                    }
                }
                if (evt.Name == "PlayerControllerB.DropHeldItem" &&
                    evt.Data.TryGetValue("itemsFall", out var falls) &&
                    bool.TryParse(falls, out var didFall) && didFall &&
                    evt.Data.TryGetValue("itemInstanceId", out var droppedText) &&
                    int.TryParse(droppedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var droppedId))
                {
                    var dropped = tracker.Entries.FirstOrDefault(entry => entry.Kind == "item" &&
                        entry.Component && entry.Component.GetInstanceID() == droppedId);
                    if (dropped != null)
                    {
                        var dropper = evt.Data.TryGetValue("instanceId", out var playerText) &&
                            int.TryParse(playerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerId)
                            ? tracker.Entries.FirstOrDefault(entry => entry.Kind == "player" &&
                                entry.Component && entry.Component.GetInstanceID() == playerId)?.Id ?? "" : "";
                        evt.Category = "item"; evt.Name = "drop"; evt.EntityId = dropped.Id;
                        evt.Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["dropper"] = dropper };
                    }
                }
                if (evt.Name == "GrabbableObject.PlayDropSFX" &&
                    evt.Data.TryGetValue("instanceId", out var instanceText) &&
                    int.TryParse(instanceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
                {
                    var item = tracker.Entries.FirstOrDefault(entry => entry.Kind == "item" &&
                        entry.Component && entry.Component.GetInstanceID() == instanceId);
                    if (item != null)
                    {
                        var properties = GameAccess.Read(item.Component, "itemProperties");
                        var clip = GameAccess.Read(properties, "dropSFX") as AudioClip;
                        var source = item.Component.GetComponent<AudioSource>();
                        evt.Category = "item"; evt.Name = "impact"; evt.EntityId = item.Id;
                        evt.Data = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["itemId"] = GameAccess.Scalar(GameAccess.Read(properties, "itemId")) ?? "",
                            ["clip"] = clip != null && clip ? clip.name : "",
                            ["source"] = source != null && source ? source.GetInstanceID().ToString(CultureInfo.InvariantCulture) : ""
                        };
                    }
                }
                if (evt.Name == "RoundManager.GenerateNewFloor")
                {
                    generationInProgress = true;
                    generationStartedAt = now;
                    generationCompletionSeen = false;
                    MarkWorldDirty();
                }
                else if (evt.Name == "RoundManager.FinishGeneratingLevel" || evt.Name == "RoundManager.FinishGeneratingNewLevelClientRpc")
                {
                    // Keep installed-scene references already written while the
                    // dungeon was generating. Generated geometry is captured now.
                    if (!generationInProgress && !generationCompletionSeen && !worldDirty) MarkWorldDirty();
                    if (generationInProgress) refreshInteriorVisibility = true;
                    if (!generationCompletionSeen) { tracker.RestartDiscovery(); nextDiscovery = 0; }
                    generationInProgress = false;
                    generationCompletionSeen = true;
                    worldVisibleAt = now;
                    awaitingWorldActivation = true;
                }
                else if (evt.Name == "StartOfRound.SetShipReadyToLand")
                {
                    generationCompletionSeen = false;
                    MarkWorldDirty();
                }
                Write(new ReplayRecord { Kind = "event", Time = evt.Time, Event = evt });
                if (Elapsed(tickStarted) >= .3 || storage.ShouldPauseCapture) break;
            }
        }

        private void CaptureWorldWork(double now, bool suspendWorldCapture)
        {
            if (captureWorld && !suspendWorldCapture && sceneAssets?.Ready == false)
            { sceneAssets.StepBounded(.4, 64); return; }
            // Some game versions omit one of the optional finish hooks.
            var manager = generationInProgress ? GameAccess.Singleton("RoundManager") : null;
            if (generationInProgress && now - generationStartedAt > .5 &&
                GameAccess.Bool(manager, "dungeonCompletedGenerating") && !GameAccess.Bool(manager, "dungeonIsGenerating"))
            {
                generationInProgress = false;
                generationCompletionSeen = true;
                worldVisibleAt = now;
                awaitingWorldActivation = true;
                refreshInteriorVisibility = true;
                tracker.RestartDiscovery(); nextDiscovery = 0;
            }
            if (captureWorld && !suspendWorldCapture && worldDirty && now >= nextWorld &&
                (!generationInProgress || !preGenerationExteriorCaptured && sceneAssets != null && worldLayerPhase == 0) &&
                sceneAssets?.Ready != false && tracker.WorldDiscoveryReady)
            {
                if (worldJob == null && worldLayerPhase == 0 && exteriorChunkIndex == 0 &&
                    !preGenerationExteriorCaptured && staticBatches == null)
                {
                    worldCaptureSetId = Guid.NewGuid().ToString("N");
                    staticBatches = new MeshSnapshotReader.StaticBatchReader();
                    // Room/LOD discovery is needed for a new world, not on every
                    // one-second entity discovery during the entire expedition.
                    if (!generationInProgress)
                    { tracker.BeginWorldVisibility(); worldVisibilityPending = true; }
                    refreshInteriorVisibility = false;
                }
                if (worldJob == null && preGenerationExteriorCaptured && refreshInteriorVisibility)
                {
                    tracker.BeginWorldVisibility(); worldVisibilityPending = true;
                    refreshInteriorVisibility = false;
                }
                if (worldVisibilityPending)
                {
                    if (!tracker.StepWorldVisibility(.4)) return;
                    worldVisibilityPending = false;
                }
                if (worldJob == null)
                {
                    worldJobAssetOnly = generationInProgress;
                    worldJob = WorldCapture.Begin(tracker, maxObjects, maxVertices,
                        worldLayerPhase == 0 ? "exterior" : "interior", worldCaptureSetId, capturedWorldIds,
                        worldLayerPhase != 0 || exteriorChunkIndex == 0, staticBatches!, sceneAssets,
                        onlySceneAssets: worldJobAssetOnly);
                }
                // Keep scene capture on Unity's main thread, but bound its work per
                // game frame. Unity objects and GPU readback cannot be moved to the
                // background writer thread.
                if (!worldJob.StepBounded(.4, 64)) return;
                var snapshot = worldJob.Snapshot;
                nativeSounds?.RefreshAmbient();
                var captured = worldJob.Captured;
                var deferred = worldJob.Deferred;
                var summary = worldJob.Summary;
                var assetOnly = worldJobAssetOnly;
                worldJob = null; worldJobAssetOnly = false;
                var time = Duration;
                snapshot.CaptureCompletedAt = time;
                if (!assetOnly || snapshot.AssetRendererPaths.Count + snapshot.AssetTerrainPaths.Count != 0)
                {
                    Write(new ReplayRecord { Kind = "world", Time = assetOnly ? time : hasWorld ? worldVisibleAt : 0, World = snapshot });
                    RememberWorld(snapshot); hasWorld = true;
                }
                foreach (var id in captured) capturedWorldIds.Add(id);
                foreach (var geometry in snapshot.Geometry)
                {
                    emittedGeometryIds.Add(geometry.Id);
                }
                if (assetOnly)
                {
                    preGenerationExteriorCaptured = true;
                    nextWorld = time + .15;
                    log(summary);
                    return;
                }
                if (worldLayerPhase == 0)
                {
                    exteriorChunkIndex++;
                    if (deferred == 0 || captured.Count == 0 || exteriorChunkIndex >= 3) worldLayerPhase = 1;
                    nextWorld = time + 0.15;
                }
                else
                {
                    worldLayerPhase = 0; exteriorChunkIndex = 0; capturedWorldIds.Clear(); staticBatches = null;
                    worldDirty = false; nextWorld = time + 2;
                    nextLateEntityScan = time + 2;
                }
                var report = new ReplayEvent { Time = time, Category = "capture", Name = "world-captured", Data = new Dictionary<string, string> { ["coverage"] = summary } };
                if (!storage.ShouldPauseCapture)
                    Write(new ReplayRecord { Kind = "event", Time = time, Event = report });
                log(summary);
                return;
            }
            if (captureWorld && !suspendWorldCapture && environmentDirty && !worldDirty && now >= nextWorld && worldCaptureSetId.Length != 0)
            {
                var update = new WorldSnapshot
                {
                    Scene = SceneManager.GetActiveScene().name,
                    Layer = "exterior",
                    CaptureSetId = worldCaptureSetId,
                    Environment = EnvironmentCapture.Capture()
                };
                Write(new ReplayRecord { Kind = "world", Time = Duration, World = update });
                RememberWorld(update);
                environmentDirty = false;
                nextWorld = Duration + 2;
                log("Refreshed replay sky and fog after landing without recapturing static map geometry.");
                return;
            }
            if (captureWorld && !suspendWorldCapture && !worldDirty && worldCaptureSetId.Length != 0)
                CaptureLateEntities();

        }

        private static bool PostFxChanged(EnvironmentComponentSnapshot oldValue, EnvironmentComponentSnapshot newValue)
        {
            if (oldValue.Parameters.Count != newValue.Parameters.Count) return true;
            for (var index = 0; index < oldValue.Parameters.Count; index++)
            {
                var left = oldValue.Parameters[index]; var right = newValue.Parameters[index];
                if (left.Name != right.Name || left.Kind != right.Kind || left.Text != right.Text ||
                    left.Values.Length != right.Values.Length || left.CurveKeys.Length != right.CurveKeys.Length) return true;
                for (var value = 0; value < left.Values.Length; value++)
                    if (Math.Abs(left.Values[value] - right.Values[value]) > .005f) return true;
                for (var key = 0; key < left.CurveKeys.Length; key++)
                    if (Math.Abs(left.CurveKeys[key] - right.CurveKeys[key]) > .005f) return true;
            }
            return false;
        }

        private void CaptureLateEntities()
        {
            if (lateCompletedWorld != null)
            {
                var cameraMask = CaptureVisibility.GameplayMask();
                var started = Stopwatch.GetTimestamp();
                for (var count = 0; count < 8 && lateVerifyCursor < lateEntityIds.Length; count++)
                {
                    var id = lateEntityIds[lateVerifyCursor++];
                    var entry = tracker.Entries.FirstOrDefault(candidate => candidate.Id == id);
                    if (entry != null && !HasMissingEntityGeometry(entry, cameraMask)) lateEntityRetryAt.Remove(id);
                    else lateEntityRetryAt[id] = Duration + 10;
                    if (Elapsed(started) >= .4) break;
                }
                if (lateVerifyCursor < lateEntityIds.Length) return;
                var completed = lateCompletedWorld; lateCompletedWorld = null; lateEntityIds = Array.Empty<string>();
                if (completed.Geometry.Count != 0)
                {
                    Write(new ReplayRecord { Kind = "world", Time = Duration, World = completed });
                    RememberWorld(completed); log(lateCompletedSummary);
                }
                return;
            }
            if (lateEntityJob == null)
            {
                if (lateScanEntries.Length == 0)
                {
                    if (Duration < nextLateEntityScan) return;
                    nextLateEntityScan = Duration + 2;
                    lateScanEntries = tracker.Entries.ToArray(); lateScanCursor = 0; lateScanIds.Clear();
                }
                var mask = CaptureVisibility.GameplayMask();
                var started = Stopwatch.GetTimestamp();
                for (var count = 0; count < 8 && lateScanCursor < lateScanEntries.Length && lateScanIds.Count < 64; count++)
                {
                    var entry = lateScanEntries[lateScanCursor++];
                    if (entry.Kind != "round" && entry.Kind != "time" && entry.Kind != "terminal" && entry.Component &&
                        (!lateEntityRetryAt.TryGetValue(entry.Id, out var retryAt) || Duration >= retryAt) && HasMissingEntityGeometry(entry, mask))
                        lateScanIds.Add(entry.Id);
                    if (Elapsed(started) >= .4) break;
                }
                if (lateScanCursor < lateScanEntries.Length && lateScanIds.Count < 64) return;
                lateScanEntries = Array.Empty<EntityTracker.Entry>();
                lateEntityIds = lateScanIds.ToArray(); lateScanIds.Clear();
                if (lateEntityIds.Length == 0) return;
                var selected = new HashSet<string>(lateEntityIds, StringComparer.Ordinal);
                lateEntityJob = WorldCapture.Begin(tracker, maxObjects, maxVertices, "exterior", worldCaptureSetId,
                    emittedGeometryIds, false, new MeshSnapshotReader.StaticBatchReader(), null, selected);
                return;
            }
            if (!lateEntityJob.StepBounded(.4, 16)) return;
            var snapshot = lateEntityJob.Snapshot;
            var summary = lateEntityJob.Summary;
            lateEntityJob = null;
            // A selected actor may have every mesh culled or temporarily disabled.
            // Retry it on a later scan instead of permanently losing its body/web.
            foreach (var geometry in snapshot.Geometry) emittedGeometryIds.Add(geometry.Id);
            lateCompletedWorld = snapshot; lateCompletedSummary = summary; lateVerifyCursor = 0;
        }
        private bool HasMissingEntityGeometry(EntityTracker.Entry entry, int mask)
        {
            if (!entry.Component) return false;
            foreach (var renderer in entry.Component.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer || renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer ||
                    CaptureVisibility.IsDebugRenderer(renderer) ||
                    entry.Kind != "player" && tracker.Visibility.OtherLods.Contains(renderer) ||
                    !EntityTracker.IsCapturedRenderer(entry, renderer) ||
                    !CaptureVisibility.VisibleLayer(renderer, mask, true)) continue;
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
                    renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh && !emittedGeometryIds.Contains("g" + renderer.GetInstanceID())) return true;
            }
            return false;
        }
        private IEnumerable<ReplayRecord?> SampleRecords(ReplayFrame current, ReplayFrame? beforeFrame, List<ReplayEvent> motions)
        {
            yield return new ReplayRecord { Kind = "frame", Time = current.Time, Frame = current };
            foreach (var motion in motions)
                yield return new ReplayRecord { Kind = "event", Time = motion.Time, Event = motion };
            if (beforeFrame == null)
            {
                // The first full frame already describes every entity; avoid thousands of redundant startup events.
                var baseline = new ReplayEvent { Time = current.Time, Category = "capture", Name = "initial-state",
                    Data = new Dictionary<string, string> { ["entities"] = current.Entities.Count.ToString(CultureInfo.InvariantCulture) } };
                yield return new ReplayRecord { Kind = "event", Time = current.Time, Event = baseline };
                yield break;
            }
            var old = beforeFrame.Entities.ToDictionary(e => e.Id) ?? new Dictionary<string, EntitySnapshot>();
            foreach (var entity in current.Entities)
            {
                yield return null; // Unchanged entities still consume a work unit.
                if (!old.TryGetValue(entity.Id, out var before)) yield return Transition(current.Time, entity, "observed", "", entity.Name);
                else
                {
                    if (before.Active != entity.Active) yield return Transition(current.Time, entity, "active", before.Active.ToString(), entity.Active.ToString());
                    foreach (var field in TransitionFields)
                    {
                        before.State.TryGetValue(field, out var a); entity.State.TryGetValue(field, out var b);
                        if (a != b) yield return Transition(current.Time, entity, field, a ?? "", b ?? "");
                    }
                }
                old.Remove(entity.Id);
            }
            // A capture-budget omission is not proof that an object was destroyed.
            if (!current.State.ContainsKey("$omittedEntities"))
                foreach (var entity in old.Values) yield return Transition(current.Time, entity, "disappeared", entity.Name, "");
        }
        private ReplayRecord Transition(double time, EntitySnapshot entity, string name, string from, string to)
        {
            var evt = new ReplayEvent { Time = time, Category = "state", Name = name, EntityId = entity.Id,
                Data = new Dictionary<string, string> { ["from"] = from, ["to"] = to, ["entity"] = entity.Name } };
            return new ReplayRecord { Kind = "event", Time = time, Event = evt };
        }
        private void RememberWorld(WorldSnapshot snapshot)
        {
            if (reusableWorldOverflowed) return;
            var bytes = ReplayRecordMemory.Estimate(new ReplayRecord { Kind = "world", World = snapshot });
            if (bytes > 32L * 1024 * 1024 - reusableWorldBytes)
            {
                // This is only a rescan shortcut, never the authoritative recording.
                // Retain a whole paired set or none, so no asset reference is lost.
                reusableWorlds.Clear(); reusableWorldBytes = 0; reusableWorldOverflowed = true;
                log("World reuse cache released after its 32 MiB budget; recorded world data remains on disk.");
                return;
            }
            reusableWorlds.Add(snapshot); reusableWorldBytes += bytes;
        }
        private void Write(ReplayRecord record)
        {
            if (Error != null) return;
            if (!storage.TryWrite(record)) Error = writer.Error ?? storage.Error ?? new IOException("Recording output is closed; previously saved data is preserved.");
        }
        public void Dispose()
        {
            FinishAsync().GetAwaiter().GetResult();
        }

        // All Unity reads ended in Tick. This tail enumerates only owned data
        // on a worker and waits for output capacity rather than copying it into
        // a second burst of queued snapshots on the game frame.
        private IEnumerable<ReplayRecord> FinalRecords(double stoppedAt, long missedEvents)
        {
            try
            {
                if (sampleOutput != null)
                    while (sampleOutput.MoveNext())
                        if (sampleOutput.Current != null) yield return sampleOutput.Current;
            }
            finally { sampleOutput?.Dispose(); sampleOutput = null; }
            if (completedWorldAtStop != null)
                yield return new ReplayRecord { Kind = "world", Time = hasWorld ? stoppedAt : 0, World = completedWorldAtStop };
            completedWorldAtStop = null;
            if (lateCompletedWorld != null && lateCompletedWorld.Geometry.Count != 0)
                yield return new ReplayRecord { Kind = "world", Time = stoppedAt, World = lateCompletedWorld };
            lateCompletedWorld = null;
            while (events.TryDequeue(out var evt)) yield return new ReplayRecord { Kind = "event", Time = evt.Time, Event = evt };
            if (storagePausedAt.HasValue || missedEvents != 0)
                yield return new ReplayRecord { Kind = "event", Time = stoppedAt, Event = new ReplayEvent { Time = stoppedAt,
                    Category = "capture", Name = "storage-gap", Data = new Dictionary<string, string> {
                        ["seconds"] = (stoppedAt - (storagePausedAt ?? stoppedAt)).ToString("F4", CultureInfo.InvariantCulture),
                        ["droppedEvents"] = missedEvents.ToString(CultureInfo.InvariantCulture) } } };
            yield return new ReplayRecord { Kind = "event", Time = stoppedAt, Event = new ReplayEvent { Time = stoppedAt,
                Category = "capture", Name = "recording-stopped" } };
        }

        public Task FinishAsync()
        {
            lock (eventGate) { if (disposed) return finishTask ?? Task.CompletedTask; disposed = true; }
            try
            {
                completedWorldAtStop = worldJob?.FinishAvailable();
                worldJob?.Dispose(); worldJob = null;
                tracker.CancelWorldVisibility(); worldVisibilityPending = false;
                lateCompletedWorld = lateEntityJob?.FinishAvailable() ?? lateCompletedWorld;
                lateEntityJob?.Dispose(); lateEntityJob = null;
                lateScanEntries = Array.Empty<EntityTracker.Entry>(); lateScanIds.Clear(); lateEntityIds = Array.Empty<string>();
                nativeSounds?.Dispose(); nativeSounds = null;
                changingPostFx.Dispose(); tracker.Visual.Reset();
                SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
            }
            catch (Exception failure) { error = error ?? failure; }
            finally
            {
                var stoppedAt = Duration;
                var missedEvents = System.Threading.Interlocked.Exchange(ref droppedEvents, 0);
                clock.Stop();
                previous = null;
                reusableWorlds.Clear(); reusableWorldBytes = 0;
                // Even cleanup/capture allocation failure must close the writer
                // and drain the snapshots it already accepted.
                finishTask = Task.Run(async () =>
                {
                    try { await storage.CompleteAsync(FinalRecords(stoppedAt, missedEvents)).ConfigureAwait(false); }
                    catch (Exception failure) { error = error ?? failure; throw; }
                });
            }
            return finishTask;
        }
    }
}
