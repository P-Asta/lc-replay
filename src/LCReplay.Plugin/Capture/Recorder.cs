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
        private readonly EntityTracker tracker;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<ReplayEvent> events = new ConcurrentQueue<ReplayEvent>();
        private readonly object eventGate = new object();
        private readonly int rate, maxFields, maxObjects, maxVertices;
        private readonly bool captureBones, captureWorld;
        private readonly Action<string> log;
        private ReplayFrame? previous;
        private double nextFrame, nextDiscovery, nextWorld = 2;
        private double nextLateEntityScan;
        private bool worldDirty = true;
        private bool environmentDirty;
        private int worldLayerPhase;
        private int exteriorChunkIndex;
        private string worldCaptureSetId = "";
        private readonly HashSet<string> capturedWorldIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> emittedGeometryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> capturedEntityIds = new HashSet<string>(StringComparer.Ordinal);
        private MeshSnapshotReader.StaticBatchReader? staticBatches;
        private SceneAssetCapture? sceneAssets;
        private ReplayAudioCapture? audioCapture;
        private WorldCapture.CaptureJob? worldJob;
        private WorldCapture.CaptureJob? lateEntityJob;
        private string[] lateEntityIds = Array.Empty<string>();
        private bool disposed;
        private Task? finishTask;
        private bool hasWorld;
        private readonly List<WorldSnapshot> reusableWorlds = new List<WorldSnapshot>();
        private Exception? error;
        private const long MaxDayExpandedBytes = 24L * 1024 * 1024 * 1024;
        public string FilePath { get; }
        public double Duration => clock.Elapsed.TotalSeconds;
        public double RecordedDuration { get; private set; }
        public int EntityCount => tracker.Count;
        public Exception? Error { get => error ?? writer.Error; private set => error = value; }
        // Carry the complete paired capture set across safety-part boundaries.
        // The immutable snapshots are queued by reference, avoiding another costly
        // Unity scene scan while the same quota/deadline day continues.
        public IReadOnlyList<WorldSnapshot>? ReusableWorld => !worldDirty && reusableWorlds.Count != 0
            ? reusableWorlds.ToArray() : null;

        public Recorder(string directory, int sampleRate, bool bones, bool world, int fields, int objects, int vertices,
            string extraTypes, EventHooks hooks, bool chat, Action<string> logger, string recordingGroup = "", int part = 1,
            string? outputPath = null, IDictionary<string, string>? archiveMetadata = null, IReadOnlyList<WorldSnapshot>? initialWorld = null,
            bool captureAudio = true, bool captureVoiceChat = false)
        {
            if (sampleRate < 1 || sampleRate > 60) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (fields < 1 || fields > 512) throw new ArgumentOutOfRangeException(nameof(fields));
            if (objects < 1 || objects > 20000 || vertices < 1 || vertices > 1000000) throw new ArgumentOutOfRangeException(nameof(objects));
            if (part < 1) throw new ArgumentOutOfRangeException(nameof(part));
            if (recordingGroup.Length > 128 || recordingGroup.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Recording group must contain letters, digits or hyphens.", nameof(recordingGroup));
            log = logger; rate = sampleRate; captureBones = bones; captureWorld = world;
            maxFields = fields; maxObjects = objects; maxVertices = vertices;
            tracker = new EntityTracker(extraTypes);
            var header = new ReplayHeader
            {
                SessionId = System.Guid.NewGuid().ToString("N"), GameVersion = Application.version,
                UnityVersion = Application.unityVersion, RecorderVersion = ReplayPlugin.Version,
                StartedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), SampleRate = sampleRate,
                Perspective = GameAccess.IsHost ? "host-observed" : "client-observed"
            };
            header.Capabilities.AddRange(new[] { "entity-transforms", "child-renderer-poses", "primitive-game-fields", "state-transitions", "observed-method-calls", "round-state", "custom-provider-api" });
            if (captureBones) header.Capabilities.Add("actor-bone-poses");
            if (captureWorld) header.Capabilities.AddRange(new[] { "render-geometry-and-bounds", "embedded-render-assets",
                "layered-world-capture", "recorded-sky-fog", "shader-properties", "instanced-grass", "looping-particle-emitters" });
            header.Capabilities.Add("visual-effect-swarm-approximation");
            header.Capabilities.Add("moving-scene-renderers");
            header.Capabilities.Add("sparse-static-visibility");
            header.Capabilities.Add("spray-decal-events");
            header.Capabilities.Add("sampled-line-renderers");
            header.Capabilities.Add("late-entity-geometry");
            if (chat) header.Capabilities.Add("local-chat");
            if (captureAudio) { header.Capabilities.Add("source-audio-adpcm-22050"); header.Capabilities.Add("spatial-source-audio");
                header.Capabilities.Add("spectator-world-audio");
                header.Capabilities.Add("player-audio-origin"); header.Capabilities.Add("virtual-audio-readable-clips"); }
            if (captureAudio && captureVoiceChat) header.Capabilities.Add("player-voice-audio-opt-in");
            header.Warnings.Add("Only data visible to this recorder is available. Remote/private state is not reconstructed.");
            header.Warnings.Add("GPU-only particle states, baked lighting, unsupported post-processing, RNG execution and arbitrary object graphs are not recorded. Short-lived Unity particles are sampled with a per-frame cap; looping emitters and GPU swarms use bounded approximations.");
            if (captureAudio) header.Warnings.Add("Audio is captured as bounded mono AudioSource blocks with sampled source positions and distance settings. Virtualized sources can be sampled from readable DecompressOnLoad clips; compressed, streamed and non-AudioSource sounds may be unavailable. Voice chat is excluded unless CaptureVoiceChat is enabled.");
            header.Warnings.Add("Fields, arrays, bones, entities, provider state and world geometry are bounded; omissions are recorded.");
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
            foreach (var plugin in Chainloader.PluginInfos.Values)
                header.Metadata["mod:" + plugin.Metadata.GUID] = plugin.Metadata.Version.ToString();
            Directory.CreateDirectory(directory);
            FilePath = outputPath ?? Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + group.Substring(0, Math.Min(8, group.Length))
                + "-part" + part.ToString("D4", CultureInfo.InvariantCulture) + ".lcr");
            writer = new ReplayWriter(FilePath, header, 1024, indexed: true);
            try
            {
                SceneManager.sceneLoaded += SceneLoaded;
                SceneManager.sceneUnloaded += SceneUnloaded;
                if (captureAudio)
                    try { audioCapture = new ReplayAudioCapture(captureVoiceChat, logger); }
                    catch (Exception ex) { log("Audio capture unavailable: " + ex.Message); }
                if (initialWorld != null && initialWorld.Count != 0)
                {
                    foreach (var snapshot in initialWorld)
                    {
                        Write(new ReplayRecord { Kind = "world", Time = 0, World = snapshot });
                        reusableWorlds.Add(snapshot);
                        foreach (var geometry in snapshot.Geometry)
                        {
                            emittedGeometryIds.Add(geometry.Id);
                            if (geometry.EntityId.Length != 0) capturedEntityIds.Add(geometry.EntityId);
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
                audioCapture?.Dispose();
                writer.Dispose();
                throw;
            }
        }
        public void Event(ReplayEvent evt)
        {
            lock (eventGate)
            {
                if (disposed || Error != null) return;
                if (events.Count >= 2048) { Error = new IOException("Event queue exceeded 2048 entries. Recording stopped to avoid silent data loss."); return; }
                // Callers retain their event instance; queued records own an immutable copy.
                events.Enqueue(new ReplayEvent { Time = Duration, Category = evt.Category, Name = evt.Name, EntityId = evt.EntityId,
                    Data = new Dictionary<string, string>(evt.Data) });
            }
        }
        public void MarkWorldDirty()
        {
            worldJob?.Dispose(); worldJob = null;
            lateEntityJob?.Dispose(); lateEntityJob = null; lateEntityIds = Array.Empty<string>();
            tracker.Visual.Reset();
            tracker.ClearMovingSceneRenderers();
            worldDirty = true; environmentDirty = false; worldLayerPhase = 0; exteriorChunkIndex = 0;
            reusableWorlds.Clear();
            emittedGeometryIds.Clear(); capturedEntityIds.Clear();
            capturedWorldIds.Clear(); staticBatches = null; nextWorld = Duration + 2;
        }
        public void RefreshEnvironmentAfterLanding()
        {
            if (!captureWorld) return;
            if (!hasWorld || worldDirty) { MarkWorldDirty(); return; }
            environmentDirty = true;
            nextWorld = Math.Max(nextWorld, Duration + 2);
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (ReplayIsolation.PlaybackActive || ReplayIsolation.IsReplayScene(scene)) return;
            Event(new ReplayEvent { Category = "scene", Name = "loaded", Data = new Dictionary<string, string> { ["scene"] = scene.name } });
            var expected = GameAccess.Scalar(GameAccess.Read(GameAccess.Read(GameAccess.Singleton("StartOfRound"), "currentLevel"), "sceneName"));
            sceneAssets = SceneAssetCapture.TryCreate(scene, expected);
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
            audioCapture?.Tick();
            if (audioCapture != null)
                foreach (var audioEvent in audioCapture.Drain())
                    Write(new ReplayRecord { Kind = "event", Time = audioEvent.Time, Event = audioEvent });
            var now = Duration;
            if (now >= nextDiscovery)
            {
                tracker.Discover(); nextDiscovery = now + 1;
            }
            while (events.TryDequeue(out var evt))
            {
                if (evt.Name == "RoundManager.FinishGeneratingLevel" || evt.Name == "RoundManager.FinishGeneratingNewLevelClientRpc"
                    || evt.Name == "RoundManager.GenerateNewFloor" || evt.Name == "StartOfRound.SetShipReadyToLand") MarkWorldDirty();
                Write(new ReplayRecord { Kind = "event", Time = evt.Time, Event = evt });
            }
            if (forceFrame || now >= nextFrame)
            {
                if (now - nextFrame > 1.0 / rate && previous != null)
                {
                    var lag = new ReplayEvent { Time = now, Category = "capture", Name = "sample-gap",
                        Data = new Dictionary<string, string> { ["seconds"] = (now - previous.Time).ToString("F4", CultureInfo.InvariantCulture) } };
                    Write(new ReplayRecord { Kind = "event", Time = now, Event = lag });
                }
                var frame = tracker.Capture(now, captureBones, maxFields);
                ReplayApi.CaptureProviders(frame.State, log);
                Transitions(frame);
                Write(new ReplayRecord { Kind = "frame", Time = now, Frame = frame });
                previous = frame;
                nextFrame = now + 1.0 / rate;
            }
            if (captureWorld && !suspendWorldCapture && worldDirty && now >= nextWorld)
            {
                if (worldJob == null && worldLayerPhase == 0 && exteriorChunkIndex == 0)
                {
                    worldCaptureSetId = Guid.NewGuid().ToString("N");
                    staticBatches = new MeshSnapshotReader.StaticBatchReader();
                    // Room/LOD discovery is needed for a new world, not on every
                    // one-second entity discovery during the entire expedition.
                    tracker.RefreshWorldVisibility();
                }
                if (worldJob == null)
                    worldJob = WorldCapture.Begin(tracker, maxObjects, maxVertices,
                        worldLayerPhase == 0 ? "exterior" : "interior", worldCaptureSetId, capturedWorldIds,
                        worldLayerPhase != 0 || exteriorChunkIndex == 0, staticBatches!, sceneAssets);
                // Keep scene capture on Unity's main thread, but bound its work per
                // game frame. Unity objects and GPU readback cannot be moved to the
                // background writer thread.
                if (!worldJob.Step(3)) return;
                var snapshot = worldJob.Snapshot;
                var captured = worldJob.Captured;
                var deferred = worldJob.Deferred;
                var summary = worldJob.Summary;
                worldJob = null;
                var time = Duration;
                Write(new ReplayRecord { Kind = "world", Time = hasWorld ? time : 0, World = snapshot });
                reusableWorlds.Add(snapshot); hasWorld = true;
                foreach (var id in captured) capturedWorldIds.Add(id);
                foreach (var geometry in snapshot.Geometry)
                {
                    emittedGeometryIds.Add(geometry.Id);
                    if (geometry.EntityId.Length != 0) capturedEntityIds.Add(geometry.EntityId);
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
                }
                var report = new ReplayEvent { Time = time, Category = "capture", Name = "world-captured", Data = new Dictionary<string, string> { ["coverage"] = summary } };
                Write(new ReplayRecord { Kind = "event", Time = time, Event = report });
                log(summary);
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
                reusableWorlds.Add(update);
                environmentDirty = false;
                nextWorld = Duration + 2;
                log("Refreshed replay sky and fog after landing without recapturing static map geometry.");
            }
            if (captureWorld && !suspendWorldCapture && !worldDirty && worldCaptureSetId.Length != 0)
                CaptureLateEntities();
            if (captureWorld && !worldDirty)
                foreach (var visualEvent in tracker.Visual.Scan(Duration, worldCaptureSetId))
                    Write(new ReplayRecord { Kind = "event", Time = visualEvent.Time, Event = visualEvent });
        }

        private void CaptureLateEntities()
        {
            if (lateEntityJob == null)
            {
                if (Duration < nextLateEntityScan) return;
                nextLateEntityScan = Duration + .5;
                lateEntityIds = tracker.Entries.Where(entry => entry.Kind != "round" && entry.Kind != "time" &&
                    entry.Kind != "terminal" && entry.Component && !capturedEntityIds.Contains(entry.Id) &&
                    entry.Component.GetComponentsInChildren<Renderer>(true).Any(renderer =>
                        renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                    .Take(12).Select(entry => entry.Id).ToArray();
                if (lateEntityIds.Length == 0) return;
                var selected = new HashSet<string>(lateEntityIds, StringComparer.Ordinal);
                lateEntityJob = WorldCapture.Begin(tracker, maxObjects, maxVertices, "exterior", worldCaptureSetId,
                    emittedGeometryIds, false, new MeshSnapshotReader.StaticBatchReader(), null, selected);
            }
            if (!lateEntityJob.Step(1)) return;
            var snapshot = lateEntityJob.Snapshot;
            var summary = lateEntityJob.Summary;
            lateEntityJob = null;
            foreach (var id in lateEntityIds) capturedEntityIds.Add(id);
            lateEntityIds = Array.Empty<string>();
            if (snapshot.Geometry.Count == 0) return;
            var time = Duration;
            Write(new ReplayRecord { Kind = "world", Time = time, World = snapshot });
            reusableWorlds.Add(snapshot);
            foreach (var geometry in snapshot.Geometry) emittedGeometryIds.Add(geometry.Id);
            log(summary);
        }
        private void Transitions(ReplayFrame current)
        {
            if (previous == null)
            {
                // The first full frame already describes every entity; avoid thousands of redundant startup events.
                var baseline = new ReplayEvent { Time = current.Time, Category = "capture", Name = "initial-state",
                    Data = new Dictionary<string, string> { ["entities"] = current.Entities.Count.ToString(CultureInfo.InvariantCulture) } };
                Write(new ReplayRecord { Kind = "event", Time = current.Time, Event = baseline });
                return;
            }
            var old = previous?.Entities.ToDictionary(e => e.Id) ?? new Dictionary<string, EntitySnapshot>();
            foreach (var entity in current.Entities)
            {
                if (!old.TryGetValue(entity.Id, out var before)) Transition(current.Time, entity, "observed", "", entity.Name);
                else
                {
                    if (before.Active != entity.Active) Transition(current.Time, entity, "active", before.Active.ToString(), entity.Active.ToString());
                    foreach (var field in new[] { "health", "isPlayerDead", "isEnemyDead", "enemyHP", "isHeld", "playerHeldBy", "currentItemSlot", "ItemOnlySlot", "isDoorOpened", "isDoorOpen", "isLocked", "hasExploded", "currentBehaviourStateIndex", "turretMode", "inShipPhase", "shipHasLanded", "scrapValue", "groupCredits", "insertedBattery.empty", "carDestroyed" })
                    {
                        before.State.TryGetValue(field, out var a); entity.State.TryGetValue(field, out var b);
                        if (a != b) Transition(current.Time, entity, field, a ?? "", b ?? "");
                    }
                }
                old.Remove(entity.Id);
            }
            // A capture-budget omission is not proof that an object was destroyed.
            if (!current.State.ContainsKey("$omittedEntities"))
                foreach (var entity in old.Values) Transition(current.Time, entity, "disappeared", entity.Name, "");
        }
        private void Transition(double time, EntitySnapshot entity, string name, string from, string to)
        {
            var evt = new ReplayEvent { Time = time, Category = "state", Name = name, EntityId = entity.Id,
                Data = new Dictionary<string, string> { ["from"] = from, ["to"] = to, ["entity"] = entity.Name } };
            Write(new ReplayRecord { Kind = "event", Time = time, Event = evt });
        }
        private void Write(ReplayRecord record)
        {
            if (Error != null) return;
            if (!writer.TryWrite(record)) { Error = writer.Error ?? new IOException("Disk writer queue is full. Recording stopped to avoid silent data loss."); return; }
            RecordedDuration = Math.Max(RecordedDuration, record.Time);
        }
        public void Dispose()
        {
            FinishAsync().GetAwaiter().GetResult();
        }

        public Task FinishAsync()
        {
            lock (eventGate) { if (disposed) return finishTask ?? Task.CompletedTask; disposed = true; }
            worldJob?.Dispose(); worldJob = null;
            lateEntityJob?.Dispose(); lateEntityJob = null;
            audioCapture?.Finish();
            if (audioCapture != null)
            {
                foreach (var audioEvent in audioCapture.Drain())
                    Write(new ReplayRecord { Kind = "event", Time = audioEvent.Time, Event = audioEvent });
                if (audioCapture.Dropped > 0)
                    Write(new ReplayRecord { Kind = "event", Time = Duration,
                        Event = new ReplayEvent { Time = Duration, Category = "audio", Name = "dropped-blocks",
                            Data = new Dictionary<string, string> { ["count"] = audioCapture.Dropped.ToString(CultureInfo.InvariantCulture) } } });
                audioCapture = null;
            }
            SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
            while (events.TryDequeue(out var evt)) Write(new ReplayRecord { Kind = "event", Time = evt.Time, Event = evt });
            var stoppedAt = Duration;
            Write(new ReplayRecord { Kind = "event", Time = stoppedAt,
                Event = new ReplayEvent { Time = stoppedAt, Category = "capture", Name = "recording-stopped" } });
            clock.Stop();
            finishTask = writer.CompleteAsync();
            return finishTask;
        }
    }
}
