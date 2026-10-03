using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    internal sealed class EntityTracker
    {
        internal static readonly KeyValuePair<string, string>[] BuiltinTypes =
        {
            Pair("GameNetcodeStuff.PlayerControllerB", "player"), Pair("EnemyAI", "enemy"),
            Pair("GrabbableObject", "item"), Pair("DoorLock", "door"),
            Pair("TerminalAccessibleObject", "facility"), Pair("Landmine", "hazard"),
            Pair("SandSpiderWebTrap", "web"),
            Pair("Turret", "hazard"), Pair("ShipTeleporter", "ship"), Pair("VehicleController", "vehicle"),
            Pair("DeadBodyInfo", "body"), Pair("AnimatedObjectTrigger", "mechanism"),
            Pair("ItemDropship", "ship"), Pair("MineshaftElevatorController", "mechanism"),
            Pair("StartOfRound", "round"), Pair("RoundManager", "round"),
            Pair("TimeOfDay", "time"), Pair("Terminal", "terminal")
        };
        private static KeyValuePair<string, string> Pair(string type, string kind) => new KeyValuePair<string, string>(type, kind);
        private readonly List<KeyValuePair<string, string>> types;
        private readonly bool captureBonesEnabled;
        private readonly Dictionary<int, Entry> identities = new Dictionary<int, Entry>();
        private readonly List<Entry> tracked = new List<Entry>();
        private readonly HashSet<int> trackedIds = new HashSet<int>();
        private readonly HashSet<int> discoverySeen = new HashSet<int>();
        private readonly HashSet<int> justSpawned = new HashSet<int>();
        private int discoveryPhase;
        private IEnumerator<bool>? discoveryWork;
        internal bool WorldDiscoveryReady { get; private set; }
        private readonly Dictionary<string, List<Transform>> bones = new Dictionary<string, List<Transform>>();
        private readonly Dictionary<string, Renderer[]> renderers = new Dictionary<string, Renderer[]>();
        private readonly Dictionary<string, List<EntityRendererBaseline>> entityRendererBaselines =
            new Dictionary<string, List<EntityRendererBaseline>>(StringComparer.Ordinal);
        private sealed class EntityRendererBaseline
        {
            internal Renderer Renderer = null!;
            internal string Id = "";
            internal Vec3 Position;
            internal Quat Rotation;
            internal Vec3 Scale;
            internal bool Active;
        }
        private readonly Dictionary<string, HashSet<string>> capturedBones = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, Animator> animators = new Dictionary<string, Animator>();
        private readonly Dictionary<string, Entry> animatorOwners = new Dictionary<string, Entry>();
        private readonly Dictionary<string, string> animatorPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, AnimationSample[]> animationStates = new Dictionary<string, AnimationSample[]>();
        private readonly Dictionary<string, AnimatorControllerParameter[]> animatorParameters =
            new Dictionary<string, AnimatorControllerParameter[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> animationParameterValues =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly AnimationTrackCapture animationTracks = new AnimationTrackCapture();
        private string[] animationOrder = Array.Empty<string>();
        private int animationCursor;
        private bool animationOrderDirty = true;
        private readonly List<AnimatorClipInfo> clipInfo = new List<AnimatorClipInfo>(8);
        private struct AnimationSample
        {
            internal int Hash;
            internal float Normalized;
            internal float Duration;
            internal float Speed;
            internal float Weight;
            internal double Time;
        }
        private readonly Dictionary<int, MovingSceneRenderer> movingSceneRenderers = new Dictionary<int, MovingSceneRenderer>();
        internal readonly ReplayVisualCapture Visual = new ReplayVisualCapture();
        private readonly ParticleSystem.Particle[] particleBuffer = new ParticleSystem.Particle[64];
        private ParticleSystem[] particleSystems = Array.Empty<ParticleSystem>();
        private LineRenderer[] lineRenderers = Array.Empty<LineRenderer>();
        private Bounds[] interiorBounds = Array.Empty<Bounds>();
        private CaptureVisibility.SnapshotJob? visibilityJob;
        private static readonly KeyValuePair<string, string[]>[] RoundStateFields =
        {
            new KeyValuePair<string, string[]>("StartOfRound", new[] { "inShipPhase", "shipHasLanded" }),
            new KeyValuePair<string, string[]>("TimeOfDay", new[] { "globalTime", "currentDayTime", "normalizedTimeOfDay", "timeUntilDeadline", "numberOfHours", "totalTime" })
        };
        private int nextId;
        public int Count => tracked.Count;
        public IReadOnlyList<Entry> Entries => tracked;
        internal CaptureVisibility.SceneVisibility Visibility { get; private set; } = new CaptureVisibility.SceneVisibility();
        public sealed class Entry
        {
            public Component Component = null!;
            public string Id = "", Kind = "", Name = "";
        }

        public EntityTracker(string extraTypes, bool captureBones)
        {
            captureBonesEnabled = captureBones;
            types = BuiltinTypes.ToList();
            types.AddRange(extraTypes.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => Pair(x, "mod")));
        }
        public List<string> MissingTypes => types.Where(t => GameAccess.Type(t.Key) == null).Select(t => t.Key).ToList();
        internal void RestartDiscovery()
        {
            discoveryWork?.Dispose(); discoveryWork = null;
            discoveryPhase = 0;
            discoverySeen.Clear();
            WorldDiscoveryReady = false;
        }
        public string Identify(Component component)
        {
            var key = component.GetInstanceID();
            if (identities.TryGetValue(key, out var existing) && existing.Component == component) return existing.Id;
            var entry = new Entry { Component = component, Id = "e" + (++nextId), Kind = "other" };
            identities[key] = entry;
            return entry.Id;
        }
        private static string AnimatorKey(string entityId, string path) => entityId + "\u001f" + path;
        private void RemoveAnimationBindings(string entityId)
        {
            animationOrderDirty = true;
            foreach (var key in animators.Keys.Where(key => key.StartsWith(entityId + "\u001f", StringComparison.Ordinal)).ToArray())
            {
                animators.Remove(key); animatorOwners.Remove(key); animatorPaths.Remove(key);
                animationStates.Remove(key); animatorParameters.Remove(key); animationParameterValues.Remove(key);
            }
        }
        internal void RegisterSpawnedEnemy(Component component)
        {
            animationOrderDirty = true;
            if (!component || !component.gameObject.scene.IsValid() ||
                ReplayIsolation.IsReplayScene(component.gameObject.scene)) return;
            var id = component.GetInstanceID();
            Identify(component);
            var entry = identities[id];
            entry.Kind = "enemy";
            entry.Name = GameAccess.Scalar(GameAccess.Read(GameAccess.Read(component, "enemyType"), "enemyName") as string
                ?? component.name) ?? "enemy";
            if (trackedIds.Add(id)) tracked.Add(entry);
            justSpawned.Add(id);
            var preferred = GameAccess.Read(component, "creatureAnimator") as Animator;
            foreach (var animator in component.GetComponentsInChildren<Animator>(true)
                .Where(value => value && value.runtimeAnimatorController && !RadarAnimator(value)).Take(8))
            {
                var path = RelativePath(component.transform, animator.transform);
                var key = AnimatorKey(entry.Id, path);
                animators[key] = animator; animatorOwners[key] = entry; animatorPaths[key] = path;
                AnimationAssetRegistry.Remember(animator, "enemy");
                if (!captureBonesEnabled && !animatorParameters.ContainsKey(key))
                    try { animatorParameters[key] = animator.parameters
                        .Where(parameter => parameter.type != AnimatorControllerParameterType.Trigger).Take(64).ToArray(); }
                    catch { animatorParameters[key] = Array.Empty<AnimatorControllerParameter>(); }
            }
            if (preferred != null && preferred && preferred.runtimeAnimatorController)
                AnimationAssetRegistry.Remember(preferred, "enemy");
        }
        // One Unity type search, then at most two component registrations per
        // game frame. Keep the previous cycle's
        // entries usable while a new cycle is still being discovered.
        public bool DiscoverStep()
        {
            if (discoveryPhase == 0 && discoveryWork == null) discoverySeen.Clear();
            if (discoveryPhase < types.Count)
            {
                discoveryWork ??= DiscoverComponents(types[discoveryPhase]).GetEnumerator();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                for (var count = 0; count < 2; count++)
                {
                    if (!discoveryWork.MoveNext())
                    { discoveryWork.Dispose(); discoveryWork = null; discoveryPhase++; break; }
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                        System.Diagnostics.Stopwatch.Frequency >= .5) break;
                }
                return false;
            }
            if (discoveryPhase == types.Count)
            {
                particleSystems = UnityEngine.Object.FindObjectsOfType<ParticleSystem>(false);
                discoveryPhase++;
                return false;
            }
            if (discoveryPhase == types.Count + 1)
            {
                lineRenderers = UnityEngine.Object.FindObjectsOfType<LineRenderer>(false);
                discoveryPhase++;
                return false;
            }
            tracked.RemoveAll(entry => !entry.Component ||
                !discoverySeen.Contains(entry.Component.GetInstanceID()) && !justSpawned.Contains(entry.Component.GetInstanceID()));
            justSpawned.Clear();
            trackedIds.Clear();
            foreach (var entry in tracked) trackedIds.Add(entry.Component.GetInstanceID());
            foreach (var id in identities.Where(p => !p.Value.Component).Select(p => p.Key).ToArray())
            { bones.Remove(identities[id].Id); renderers.Remove(identities[id].Id);
              entityRendererBaselines.Remove(identities[id].Id); capturedBones.Remove(identities[id].Id);
              animationTracks.Forget(identities[id].Id);
              RemoveAnimationBindings(identities[id].Id); identities.Remove(id); }
            foreach (var id in movingSceneRenderers.Where(pair => !pair.Value.Renderer).Select(pair => pair.Key).ToArray()) movingSceneRenderers.Remove(id);
            discoveryPhase = 0;
            WorldDiscoveryReady = true;
            return true;
        }

        private IEnumerable<bool> DiscoverComponents(KeyValuePair<string, string> type)
        {
            foreach (var component in GameAccess.Find(type.Key))
            {
                yield return true;
                if (!component || !discoverySeen.Add(component.GetInstanceID())) continue;
                var recycled = identities.TryGetValue(component.GetInstanceID(), out var known) && known.Component != component;
                Identify(component);
                var entry = identities[component.GetInstanceID()];
                entry.Kind = type.Value;
                var isNew = trackedIds.Add(component.GetInstanceID());
                if (!isNew && recycled)
                {
                    // Unity may reuse an instance ID after its old object dies.
                    tracked.RemoveAll(old => old.Component && old.Component.GetInstanceID() == component.GetInstanceID());
                    isNew = true;
                }
                if (isNew) tracked.Add(entry);
                if (isNew || entry.Name.Length == 0)
                {
                    var name = entry.Kind == "item" ? GameAccess.Read(GameAccess.Read(component, "itemProperties"), "itemName") as string :
                        entry.Kind == "enemy" ? GameAccess.Read(GameAccess.Read(component, "enemyType"), "enemyName") as string : null;
                    entry.Name = GameAccess.Scalar(name ?? component.name) ?? entry.Kind;
                }
                if (entry.Kind == "player" && !GameAccess.Bool(component, "isPlayerControlled") &&
                    !GameAccess.Bool(component, "isPlayerDead")) continue;
                if (entry.Kind == "player" || entry.Kind == "enemy")
                {
                    // Several vanilla enemies have independent body, baby,
                    // mask or effect Animators. Keep their paths distinct.
                    var preferred = entry.Kind == "player"
                        ? GameAccess.Read(component, "playerBodyAnimator") as Animator
                        : GameAccess.Read(component, "creatureAnimator") as Animator;
                    var found = component.GetComponentsInChildren<Animator>(true)
                        .Where(animator => animator && animator.runtimeAnimatorController && !RadarAnimator(animator)).ToList();
                    if (preferred && preferred!.runtimeAnimatorController &&
                        (preferred.transform == component.transform || preferred.transform.IsChildOf(component.transform)))
                    { found.Remove(preferred); found.Insert(0, preferred); }
                    var current = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var animator in found.Take(8))
                    {
                        var path = RelativePath(component.transform, animator.transform);
                        var key = AnimatorKey(entry.Id, path);
                        current.Add(key);
                        var changed = !animators.TryGetValue(key, out var previous) || previous != animator;
                        if (changed) animationOrderDirty = true;
                        animators[key] = animator; animatorOwners[key] = entry; animatorPaths[key] = path;
                        AnimationAssetRegistry.Remember(animator, entry.Kind);
                        if (changed) { animationStates.Remove(key); animationParameterValues.Remove(key); }
                        if (!captureBonesEnabled && (changed || !animatorParameters.ContainsKey(key)))
                            try { animatorParameters[key] = animator.parameters
                                .Where(parameter => parameter.type != AnimatorControllerParameterType.Trigger)
                                .Take(64).ToArray(); }
                            catch { animatorParameters[key] = Array.Empty<AnimatorControllerParameter>(); }
                    }
                    foreach (var key in animators.Keys.Where(key => key.StartsWith(entry.Id + "\u001f", StringComparison.Ordinal) &&
                        !current.Contains(key)).ToArray())
                    { animators.Remove(key); animatorOwners.Remove(key); animatorPaths.Remove(key);
                      animationStates.Remove(key); animatorParameters.Remove(key); animationParameterValues.Remove(key); }
                }
                if (!captureBonesEnabled || !CaptureRendererPoses(entry.Kind)) continue;
                if (!isNew && entry.Kind != "player" && entry.Kind != "enemy" &&
                    renderers.TryGetValue(entry.Id, out var previousRenderers) && previousRenderers.Any(renderer => renderer)) continue;
                renderers[entry.Id] = component.GetComponentsInChildren<Renderer>(true)
                    .Where(renderer => (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) &&
                        !CaptureVisibility.IsDebugRenderer(renderer) && (entry.Kind == "player" || !Visibility.OtherLods.Contains(renderer))).Take(513).ToArray();
            }
        }

        internal IEnumerable<ReplayEvent> AnimationChanges(double time) => AnimationChangesStep(time, int.MaxValue, double.PositiveInfinity);

        internal IEnumerable<ReplayEvent> AnimationChangesStep(double time, int maxAnimators, double maxMilliseconds)
        {
            if (animationOrderDirty || animationOrder.Length != animators.Count)
            { animationOrder = animators.Keys.ToArray(); animationOrderDirty = false; animationCursor = 0; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var checkedCount = 0; checkedCount < Math.Min(maxAnimators, animationOrder.Length); checkedCount++)
            {
                if (animationCursor >= animationOrder.Length) animationCursor = 0;
                var key = animationOrder[animationCursor++];
                if (animators.TryGetValue(key, out var animator))
                    foreach (var evt in AnimationChangesFor(new KeyValuePair<string, Animator>(key, animator), time)) yield return evt;
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency >= maxMilliseconds) break;
            }
        }

        private IEnumerable<ReplayEvent> AnimationChangesFor(KeyValuePair<string, Animator> pair, double time)
        {
            {
                var animator = pair.Value;
                if (!animator || !animator.isActiveAndEnabled || animator.layerCount == 0) yield break;
                animatorOwners.TryGetValue(pair.Key, out var owner);
                if (owner == null) yield break;
                animatorPaths.TryGetValue(pair.Key, out var animatorPath);
                animatorPath ??= "";
                if (owner != null && owner.Kind == "player" &&
                    !GameAccess.Bool(owner.Component, "isPlayerControlled") && !GameAccess.Bool(owner.Component, "isPlayerDead")) yield break;
                if (!animationStates.TryGetValue(pair.Key, out var samples) || samples.Length != animator.layerCount)
                    animationStates[pair.Key] = samples = new AnimationSample[animator.layerCount];
                for (var layer = 0; layer < samples.Length; layer++)
                {
                    AnimatorStateInfo state;
                    try { state = animator.GetCurrentAnimatorStateInfo(layer); }
                    catch { continue; }
                    if (state.fullPathHash == 0 || !GameAccess.Finite(state.normalizedTime) ||
                        !GameAccess.Finite(state.length) || state.length <= 0) continue;
                    var speed = animator.speed * state.speed * state.speedMultiplier;
                    if (!GameAccess.Finite(speed)) speed = 1;
                    var weight = animator.GetLayerWeight(layer);
                    if (!GameAccess.Finite(weight)) weight = 0;
                    var previous = samples[layer];
                    var expected = previous.Normalized + (float)((time - previous.Time) * previous.Speed / Math.Max(.001f, previous.Duration));
                    var unchanged = weight < .001f && previous.Weight < .001f ||
                        previous.Hash == state.fullPathHash && time - previous.Time < 10 &&
                        Math.Abs(expected - state.normalizedTime) < .25f &&
                        Math.Abs(previous.Speed - speed) < .05f && Math.Abs(previous.Weight - weight) < .1f &&
                        (previous.Weight >= .05f) == (weight >= .05f);
                    // Native actor controllers need clip names only when a state
                    // is emitted. Avoid allocating clip arrays for unchanged legs.
                    if ((owner!.Kind == "player" || owner.Kind == "enemy") && unchanged) continue;
                    string clip = "";
                    if (owner != null)
                    {
                        try
                        {
                            clipInfo.Clear(); animator.GetCurrentAnimatorClipInfo(layer, clipInfo);
                            var bestWeight = -1f;
                            foreach (var activeClip in clipInfo)
                                if (activeClip.clip && activeClip.weight > bestWeight)
                                { bestWeight = activeClip.weight; clip = GameAccess.Scalar(activeClip.clip.name) ?? ""; }
                        }
                        catch { }
                        if (!captureBonesEnabled && owner.Kind != "enemy" && owner.Kind != "player")
                        {
                            // A base-layer sample taken during an emote or spawn
                            // would bake that overlay into every later walk/idle loop.
                            var trackClip = layer == 0 && HasActiveAnimationOverlay(animator) ||
                                owner.Kind == "player" && animator.GetLayerName(layer) == "HoldingItemsRightHand" &&
                                HasActiveBothHandsLayer(animator) ? "" : clip;
                            var track = animationTracks.Observe(owner, animator, animatorPath,
                                layer, state, trackClip, weight, time);
                            if (track != null) yield return track;
                        }
                    }
                    if (unchanged) continue;
                    samples[layer] = new AnimationSample { Hash = state.fullPathHash, Normalized = state.normalizedTime,
                        Duration = state.length, Speed = speed, Weight = weight, Time = time };
                    var data = new Dictionary<string, string>
                    {
                        ["layer"] = layer.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["hash"] = state.fullPathHash.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["normalizedTime"] = state.normalizedTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["duration"] = state.length.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["speed"] = speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        ["weight"] = weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    };
                    if (clip.Length != 0) data["clip"] = clip;
                    data["animatorPath"] = animatorPath;
                    data["layerName"] = animator.GetLayerName(layer);
                    if (animator.runtimeAnimatorController)
                        data["controller"] = animator.runtimeAnimatorController.name;
                    if (animator.avatar) data["avatar"] = animator.avatar.name;
                    yield return new ReplayEvent { Time = time, Category = "animation", Name = "state",
                        EntityId = owner?.Id ?? "", Data = data };
                }
                if (!captureBonesEnabled)
                {
                    var parameterChange = CaptureAnimationParameters(owner?.Id ?? "", pair.Key, animatorPath, animator, time);
                    if (parameterChange != null) yield return parameterChange;
                }
            }
        }

        private static bool HasActiveAnimationOverlay(Animator animator)
        {
            for (var layer = 1; layer < animator.layerCount; layer++)
            {
                if (animator.GetLayerWeight(layer) < .05f) continue;
                try
                {
                    var clips = animator.GetCurrentAnimatorClipInfo(layer);
                    if (clips.Any(item => item.clip && item.weight > .01f)) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool HasActiveBothHandsLayer(Animator animator)
        {
            var layer = animator.GetLayerIndex("HoldingItemsBothHands");
            return layer >= 0 && animator.GetLayerWeight(layer) >= .05f;
        }

        private ReplayEvent? CaptureAnimationParameters(string entityId, string bindingKey,
            string animatorPath, Animator animator, double time)
        {
            if (!animatorParameters.TryGetValue(bindingKey, out var parameters)) return null;
            if (!animationParameterValues.TryGetValue(bindingKey, out var previous))
                animationParameterValues[bindingKey] = previous = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string>? changed = null;
            foreach (var parameter in parameters)
            {
                string key, value;
                try
                {
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            var number = animator.GetFloat(parameter.nameHash);
                            if (!GameAccess.Finite(number) || Math.Abs(number) > 1000000f) continue;
                            key = "f" + parameter.nameHash.ToString(CultureInfo.InvariantCulture);
                            value = Math.Round(number, 2).ToString("0.##", CultureInfo.InvariantCulture);
                            break;
                        case AnimatorControllerParameterType.Int:
                            key = "i" + parameter.nameHash.ToString(CultureInfo.InvariantCulture);
                            value = animator.GetInteger(parameter.nameHash).ToString(CultureInfo.InvariantCulture);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            key = "b" + parameter.nameHash.ToString(CultureInfo.InvariantCulture);
                            value = animator.GetBool(parameter.nameHash) ? "1" : "0";
                            break;
                        default: continue; // State events already describe trigger-driven transitions.
                    }
                }
                catch { continue; }
                if (previous.TryGetValue(key, out var old) && old == value) continue;
                previous[key] = value;
                changed ??= new Dictionary<string, string>(StringComparer.Ordinal);
                changed[key] = value;
            }
            if (changed == null) return null;
            changed["animatorPath"] = animatorPath;
            return new ReplayEvent
                { Time = time, Category = "animation", Name = "parameters", EntityId = entityId, Data = changed };
        }

        internal void BeginWorldVisibility()
        {
            visibilityJob?.Dispose();
            visibilityJob = CaptureVisibility.BeginSnapshot();
        }

        internal bool StepWorldVisibility(double milliseconds)
        {
            if (visibilityJob == null) return true;
            if (!visibilityJob.Step(milliseconds)) return false;
            Visibility = visibilityJob.Result;
            visibilityJob.Dispose(); visibilityJob = null;
            interiorBounds = GameAccess.Find("DunGen.Tile").Take(4096)
                .Select(tile => GameAccess.Read(tile, "Bounds"))
                .OfType<Bounds>().Where(bounds => GameAccess.Finite(bounds.center) &&
                    GameAccess.Finite(bounds.size) && bounds.size.x > 0 && bounds.size.y > 0 && bounds.size.z > 0).ToArray();
            return true;
        }

        internal void CancelWorldVisibility() { visibilityJob?.Dispose(); visibilityJob = null; }

        private bool IsInsideTile(Vector3 position) => interiorBounds.Any(bounds => bounds.Contains(position));

        public ReplayFrame Capture(double time, bool captureBones, int maxFields)
        {
            capturedBones.Clear();
            var frame = new ReplayFrame { Time = time };
            var roundRoot = GameAccess.Singleton("StartOfRound");
            foreach (var anchor in WorldCapture.MovingAnchors(roundRoot))
            {
                var transform = anchor.Value;
                if (!transform || !GameAccess.Finite(transform.position) || !GameAccess.Finite(transform.rotation) || !GameAccess.Finite(transform.lossyScale)) continue;
                frame.Anchors.Add(new AnchorPose { Id = anchor.Key, Position = GameAccess.Vec(transform.position),
                    Rotation = GameAccess.Rot(transform.rotation), Scale = GameAccess.Vec(transform.lossyScale) });
            }
            var omitted = 0;
            long estimatedBytes = 0;
            foreach (var entry in tracked)
            {
                if (frame.Entities.Count >= 4096) { omitted++; continue; }
                try
                {
                var component = entry.Component;
                if (!component || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded ||
                    ReplayIsolation.IsReplayScene(component.gameObject.scene)) continue;
                // The game keeps remote player slots alive before anyone joins.
                // They have no replay body or actions until controlled or dead.
                if (entry.Kind == "player" && !GameAccess.Bool(component, "isPlayerControlled") &&
                    !GameAccess.Bool(component, "isPlayerDead")) continue;
                var transform = component.transform;
                if (!GameAccess.Finite(transform.position) || !GameAccess.Finite(transform.rotation) || !GameAccess.Finite(transform.lossyScale))
                { omitted++; continue; }
                var state = GameAccess.CaptureFields(component, Identify, maxFields, entry.Kind);
                if (entry.Kind == "round")
                {
                    var level = GameAccess.Read(component, "currentLevel");
                    var weather = GameAccess.Scalar(GameAccess.Read(level, "currentWeather"));
                    if (weather != null) state["currentLevelWeather"] = weather;
                }
                if (entry.Kind == "enemy" && component.GetType().Name == "RedLocustBees")
                {
                    var target = GameAccess.Read(component, "beeParticlesTarget") as Transform;
                    var hive = GameAccess.Read(component, "hive") as Component;
                    if (target)
                    {
                        var overridden = GameAccess.Bool(component, "overrideBeeParticleTarget");
                        state["$beeTarget"] = overridden ? "override" : hive && (target!.position - hive!.transform.position).sqrMagnitude < .001f ? "hive" : "self";
                        if (overridden && GameAccess.Finite(target!.position)) state["$beeTargetPosition"] = GameAccess.Scalar(target.position)!;
                    }
                    if (GameAccess.Read(component, "timesChangingZapModes") is int changes && GameAccess.Read(GameAccess.Singleton("StartOfRound"), "randomMapSeed") is int seed)
                        state["$beeZapSeed"] = (seed + changes).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                var name = entry.Kind == "player" ? GameAccess.Read(component, "playerUsername") as string : null;
                var entity = new EntitySnapshot
                {
                    Id = entry.Id, Kind = entry.Kind, Name = GameAccess.Scalar(name) ?? entry.Name,
                    Position = GameAccess.Vec(transform.position), Rotation = GameAccess.Rot(transform.rotation),
                    Scale = GameAccess.Vec(transform.lossyScale), Active = component.gameObject.activeInHierarchy, State = state
                };
                if (entry.Kind == "player")
                {
                    entity.Active &= GameAccess.Bool(component, "isPlayerControlled") || GameAccess.Bool(component, "isPlayerDead");
                    if (GameAccess.Read(component, "gameplayCamera") is Camera gameplayCamera && gameplayCamera &&
                        GameAccess.Finite(gameplayCamera.transform.rotation))
                    {
                        entity.ViewRotation = GameAccess.Rot(gameplayCamera.transform.rotation);
                        if (GameAccess.Finite(gameplayCamera.transform.position))
                            entity.ViewPosition = GameAccess.Vec(gameplayCamera.transform.position);
                    }
                }
                else if (entry.Kind == "item" && GameAccess.Bool(component, "isHeld") &&
                    GameAccess.Read(component, "playerHeldBy") is Component holder && holder)
                    entity.State["$heldBy"] = Identify(holder);
                if (captureBones && (entry.Kind == "player" || entry.Kind == "enemy" ||
                    renderers.TryGetValue(entry.Id, out var skins) && skins.Any(renderer => renderer is SkinnedMeshRenderer)))
                    CaptureBones(entry, entity);
                else if (!captureBones && entry.Kind == "enemy" &&
                    component.GetType().Name == "SandSpiderAI")
                    CaptureSpiderProceduralBones(entry, entity);
                else if (!captureBones && entry.Kind == "enemy")
                    CaptureEnemyFacing(entry, entity);
                CaptureRenderers(entry, entity);
                var entityBytes = 1024L + entity.State.Sum(p => 128L + 6L * (p.Key.Length + p.Value.Length))
                    + entity.Bones.Sum(b => 640L + 6L * b.Path.Length)
                    + entity.Renderers.Sum(renderer => 640L + 6L * renderer.Id.Length);
                if (estimatedBytes + entityBytes > 24L * 1024 * 1024) { omitted++; continue; }
                estimatedBytes += entityBytes;
                frame.Entities.Add(entity);
                if (entity.Bones.Count != 0)
                    capturedBones[entity.Id] = new HashSet<string>(entity.Bones.Select(bone => bone.Path), StringComparer.Ordinal);
                }
                catch (UnityException) { omitted++; }
            }
            if (omitted > 0)
            {
                frame.State["$omittedEntities"] = omitted.ToString(System.Globalization.CultureInfo.InvariantCulture);
                frame.State["$entityOmissionReason"] = "invalid transform, destroyed component, entity count or frame byte budget";
            }
            foreach (var source in RoundStateFields)
            {
                var singleton = GameAccess.Singleton(source.Key);
                foreach (var key in source.Value)
                {
                    var value = GameAccess.Scalar(GameAccess.Read(singleton, key));
                    if (value != null) frame.State[source.Key + "." + key] = value;
                }
            }
            CaptureShortLivedParticles(frame);
            CaptureLines(frame);
            CaptureMovingSceneRenderers(frame);
            return frame;
        }

        internal void TrackMovingSceneRenderer(Renderer renderer, Transform? anchor, GeometrySnapshot baseline)
        {
            if (renderer && movingSceneRenderers.Count < 512)
                movingSceneRenderers[renderer.GetInstanceID()] = new MovingSceneRenderer(renderer, anchor, baseline);
        }

        internal void ClearMovingSceneRenderers() => movingSceneRenderers.Clear();

        internal void ClearEntityRendererBaselines() => entityRendererBaselines.Clear();

        internal void TrackEntityRenderer(Renderer renderer, Entry owner, GeometrySnapshot geometry)
        {
            if (!entityRendererBaselines.TryGetValue(owner.Id, out var list))
                entityRendererBaselines[owner.Id] = list = new List<EntityRendererBaseline>();
            if (list.Any(item => item.Id == geometry.Id)) return;
            list.Add(new EntityRendererBaseline { Renderer = renderer, Id = geometry.Id,
                Position = geometry.Position, Rotation = geometry.Rotation, Scale = geometry.Scale, Active = geometry.Active });
        }

        private void CaptureMovingSceneRenderers(ReplayFrame frame)
        {
            foreach (var moving in movingSceneRenderers.Values)
            {
                if (frame.SceneRenderers.Count >= 512) break;
                var renderer = moving.Renderer;
                if (!renderer)
                {
                    var last = moving.LastPose ?? moving.Baseline;
                    if (last.Active) frame.SceneRenderers.Add(new RenderPose { Id = last.Id,
                        Position = last.Position, Rotation = last.Rotation,
                        Scale = last.Scale, Active = false });
                    continue;
                }
                if (!GameAccess.Finite(renderer.transform.position) ||
                    !GameAccess.Finite(renderer.transform.rotation) || !GameAccess.Finite(renderer.transform.lossyScale)) continue;
                var anchor = moving.Anchor;
                var position = anchor ? anchor!.InverseTransformPoint(renderer.transform.position) : renderer.transform.position;
                var rotation = anchor ? Quaternion.Inverse(anchor!.rotation) * renderer.transform.rotation : renderer.transform.rotation;
                var anchorScale = anchor ? anchor!.lossyScale : Vector3.one;
                var scale = new Vector3(Div(renderer.transform.lossyScale.x, anchorScale.x),
                    Div(renderer.transform.lossyScale.y, anchorScale.y), Div(renderer.transform.lossyScale.z, anchorScale.z));
                if (!GameAccess.Finite(position) || !GameAccess.Finite(rotation) || !GameAccess.Finite(scale)) continue;
                var pose = new RenderPose
                {
                    Id = "g" + renderer.GetInstanceID(), Position = GameAccess.Vec(position),
                    Rotation = GameAccess.Rot(rotation), Scale = GameAccess.Vec(scale),
                    Active = renderer.gameObject.activeInHierarchy && Visibility.Enabled(renderer)
                };
                moving.LastPose = pose;
                if (!SamePose(pose, moving.Baseline)) frame.SceneRenderers.Add(pose);
            }
        }

        private static bool SamePose(RenderPose a, RenderPose b)
        {
            if (a.Active != b.Active) return false;
            static bool Near(float x, float y) => Math.Abs(x - y) < .001f;
            if (!Near(a.Position.X, b.Position.X) || !Near(a.Position.Y, b.Position.Y) || !Near(a.Position.Z, b.Position.Z) ||
                !Near(a.Scale.X, b.Scale.X) || !Near(a.Scale.Y, b.Scale.Y) || !Near(a.Scale.Z, b.Scale.Z)) return false;
            var dot = (double)a.Rotation.X * b.Rotation.X + (double)a.Rotation.Y * b.Rotation.Y +
                (double)a.Rotation.Z * b.Rotation.Z + (double)a.Rotation.W * b.Rotation.W;
            return Math.Abs(dot) > .999999;
        }

        private sealed class MovingSceneRenderer
        {
            internal readonly Renderer Renderer;
            internal readonly Transform? Anchor;
            internal readonly RenderPose Baseline;
            internal RenderPose? LastPose;
            internal MovingSceneRenderer(Renderer renderer, Transform? anchor, GeometrySnapshot baseline)
            {
                Renderer = renderer; Anchor = anchor;
                Baseline = new RenderPose { Id = baseline.Id, Position = baseline.Position,
                    Rotation = baseline.Rotation, Scale = baseline.Scale, Active = baseline.Active };
            }
        }

        private void CaptureShortLivedParticles(ReplayFrame frame)
        {
            var cameraMask = CaptureVisibility.GameplayMask();
            var tileType = GameAccess.Type("DunGen.Tile");
            var turretType = GameAccess.Type("Turret");
            // Turret.bulletParticles is often a sibling of TurretScript in the
            // prefab, so GetComponentInParent<Turret>() cannot identify it.
            var turretBullets = turretType == null ? new HashSet<ParticleSystem>() :
                new HashSet<ParticleSystem>(tracked.Where(entry => entry.Component && turretType.IsInstanceOfType(entry.Component))
                    .Select(entry => GameAccess.Read(entry.Component, "bulletParticles") as ParticleSystem)
                    .Where(system => system != null).Cast<ParticleSystem>());
            foreach (var system in particleSystems)
            {
                if (frame.Particles.Count >= 256 || frame.ParticleStyles.Count >= 128) break;
                if (!system || !system.isPlaying || (!system.main.loop && system.particleCount == 0) ||
                    ReplayIsolation.IsReplayScene(system.gameObject.scene)) continue;
                // Turret muzzle flashes are emitted by a looping system only
                // while it fires; sample its actual particles in each frame.
                var turretBullet = turretBullets.Contains(system) || turretBullets.Any(source =>
                    source && system.transform.IsChildOf(source.transform));
                if (system.main.loop && !turretBullet) continue;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (!renderer || !Visibility.Enabled(renderer) || CaptureVisibility.IsDebugRenderer(renderer) ||
                    !CaptureVisibility.VisibleLayer(renderer, cameraMask, Visibility.CullerManaged.Contains(renderer))) continue;
                int count;
                try { count = system.GetParticles(particleBuffer, Math.Min(particleBuffer.Length, 256 - frame.Particles.Count)); }
                catch { continue; }
                var main = system.main;
                var style = ParticleCapture.Style(system, renderer);
                // Firing starts and stops between recording ticks. Native loop
                // simulation would generate bullets that never existed in the
                // source frame and can leave a beam visible after firing stops.
                style.Simulate = false;
                if (count > 0 || style.Simulate) frame.ParticleStyles.Add(style);
                var interior = (tileType != null && system.GetComponentInParent(tileType) != null) ||
                    Visibility.Rooms.ContainsKey(renderer) || IsInsideTile(system.transform.position);
                style.IsInterior = interior;
                for (var i = 0; i < count && frame.Particles.Count < 256; i++)
                {
                    var particle = particleBuffer[i];
                    var position = particle.position;
                    var velocity = particle.velocity;
                    if (main.simulationSpace == ParticleSystemSimulationSpace.Local)
                    {
                        position = system.transform.TransformPoint(position);
                        velocity = system.transform.TransformVector(velocity);
                    }
                    else if (main.simulationSpace == ParticleSystemSimulationSpace.Custom && main.customSimulationSpace)
                    {
                        position = main.customSimulationSpace.TransformPoint(position);
                        velocity = main.customSimulationSpace.TransformVector(velocity);
                    }
                    if (!GameAccess.Finite(position)) continue;
                    var color = particle.GetCurrentColor(system);
                    var size = particle.GetCurrentSize(system);
                    if (!GameAccess.Finite(size) || size <= 0 || size > 100 ||
                        !GameAccess.Finite(particle.rotation) || Math.Abs(particle.rotation) > 3600) continue;
                    var size3D = particle.GetCurrentSize3D(system);
                    var rotation3D = particle.rotation3D;
                    var particleScale = main.scalingMode == ParticleSystemScalingMode.Hierarchy ? system.transform.lossyScale :
                        main.scalingMode == ParticleSystemScalingMode.Local ? system.transform.localScale : Vector3.one;
                    size3D = Vector3.Scale(size3D, new Vector3(Mathf.Abs(particleScale.x), Mathf.Abs(particleScale.y), Mathf.Abs(particleScale.z)));
                    if (renderer.renderMode == ParticleSystemRenderMode.Mesh || renderer.alignment == ParticleSystemRenderSpace.Local)
                    {
                        var orientation = main.simulationSpace == ParticleSystemSimulationSpace.Local ? system.transform.rotation :
                            main.simulationSpace == ParticleSystemSimulationSpace.Custom && main.customSimulationSpace ?
                                main.customSimulationSpace.rotation : Quaternion.identity;
                        rotation3D = (orientation * Quaternion.Euler(rotation3D)).eulerAngles;
                    }
                    if (!GameAccess.Finite(size3D) || !GameAccess.Finite(rotation3D) || !GameAccess.Finite(velocity) ||
                        !GameAccess.Finite(particle.startLifetime) || particle.startLifetime <= 0 || particle.startLifetime > 86400) continue;
                    frame.Particles.Add(new ParticlePose { EmitterId = style.Id,
                        Position = GameAccess.Vec(position), Velocity = GameAccess.Vec(velocity), Size = size,
                        Size3D = GameAccess.Vec(size3D), Rotation3D = GameAccess.Vec(rotation3D),
                        Lifetime = particle.startLifetime, RemainingLifetime = Mathf.Clamp(particle.remainingLifetime, 0, particle.startLifetime),
                        RandomSeed = particle.randomSeed,
                        Rotation = particle.rotation, IsInterior = interior,
                        Color = new[] { color.r / 255f, color.g / 255f, color.b / 255f, color.a / 255f } });
                }
            }
        }

        private void CaptureLines(ReplayFrame frame)
        {
            var mask = CaptureVisibility.GameplayMask();
            var tileType = GameAccess.Type("DunGen.Tile");
            foreach (var line in lineRenderers)
            {
                if (frame.Lines.Count >= 128) break;
                if (!line || !Visibility.Enabled(line) || !line.gameObject.activeInHierarchy || line.positionCount < 2 ||
                    ReplayIsolation.IsReplayScene(line.gameObject.scene) || CaptureVisibility.IsDebugRenderer(line) ||
                    !CaptureVisibility.VisibleLayer(line, mask, Visibility.CullerManaged.Contains(line))) continue;
                var count = Math.Min(32, line.positionCount);
                var positions = new float[count * 3];
                var valid = true;
                for (var i = 0; i < count; i++)
                {
                    var sourceIndex = count == line.positionCount ? i : (int)Math.Round(i * (line.positionCount - 1.0) / (count - 1));
                    var point = line.GetPosition(sourceIndex);
                    if (!line.useWorldSpace) point = line.transform.TransformPoint(point);
                    if (!GameAccess.Finite(point)) { valid = false; break; }
                    positions[i * 3] = point.x; positions[i * 3 + 1] = point.y; positions[i * 3 + 2] = point.z;
                }
                if (!valid || !GameAccess.Finite(line.startWidth) || !GameAccess.Finite(line.endWidth)) continue;
                var start = line.startColor; var end = line.endColor;
                var material = line.sharedMaterial;
                if (!GameAccess.Finite(start.r) || !GameAccess.Finite(start.g) || !GameAccess.Finite(start.b) || !GameAccess.Finite(start.a) ||
                    !GameAccess.Finite(end.r) || !GameAccess.Finite(end.g) || !GameAccess.Finite(end.b) || !GameAccess.Finite(end.a)) continue;
                frame.Lines.Add(new LinePose
                {
                    Id = "line" + line.GetInstanceID(), Positions = positions,
                    MaterialName = material ? material.name : "", ShaderName = material && material.shader ? material.shader.name : "",
                    TextureMode = (int)line.textureMode, Alignment = (int)line.alignment,
                    StartColor = new[] { start.r, start.g, start.b, start.a },
                    EndColor = new[] { end.r, end.g, end.b, end.a },
                    StartWidth = Mathf.Clamp(line.startWidth, 0.001f, 100f),
                    EndWidth = Mathf.Clamp(line.endWidth, 0.001f, 100f),
                    IsInterior = (tileType != null && line.GetComponentInParent(tileType) != null) ||
                        Visibility.Rooms.ContainsKey(line) || IsInsideTile(line.bounds.center)
                });
            }
            foreach (var entry in tracked)
            {
                if (frame.Lines.Count >= 128) break;
                if (!entry.Component || entry.Component.GetType().Name != "Turret" ||
                    !GameAccess.Bool(entry.Component, "turretActive")) continue;
                var mode = GameAccess.Read(entry.Component, "turretMode")?.ToString();
                var firing = mode == "Firing" || mode == "Berserk" &&
                    GameAccess.Read(entry.Component, "bulletParticles") is ParticleSystem bullets && bullets.isPlaying;
                if (!firing && mode != "Charging") continue;
                if (!(GameAccess.Read(entry.Component, "aimPoint") is Transform aim) || !aim ||
                    !GameAccess.Finite(aim.position) || !GameAccess.Finite(aim.forward)) continue;
                var distance = 30f;
                var rayMask = GameAccess.Read(GameAccess.Singleton("StartOfRound"), "collidersAndRoomMask") is int layers
                    ? layers : ~0;
                if (Physics.Raycast(aim.position, aim.forward, out var hit, distance, rayMask, QueryTriggerInteraction.Ignore))
                    distance = hit.distance;
                var end = aim.position + aim.forward * distance;
                frame.Lines.Add(new LinePose
                {
                    Id = "turret-laser-" + entry.Id, MaterialName = "Replay turret beam", IsInterior = true,
                    Positions = new[] { aim.position.x, aim.position.y, aim.position.z, end.x, end.y, end.z },
                    StartColor = firing ? new[] { 1f, .32f, .08f, .95f } : new[] { 1f, .06f, .03f, .7f },
                    EndColor = firing ? new[] { 1f, .16f, .03f, .85f } : new[] { 1f, .04f, .02f, .5f },
                    StartWidth = firing ? .024f : .012f, EndWidth = firing ? .018f : .008f
                });
            }
        }

        internal bool HasCapturedSkeleton(string entityId, List<string> paths) =>
            capturedBones.TryGetValue(entityId, out var captured) && paths.All(path => path.Length == 0 || captured.Contains(path));

        private static bool CaptureRendererPoses(string kind) => kind != "round" && kind != "time" && kind != "terminal";

        internal static bool IsCapturedRenderer(Entry entry, Renderer renderer)
        {
            if (entry.Kind != "player") return true;
            var body = GameAccess.Read(entry.Component, "thisPlayerModel") as Renderer;
            if (body) return renderer == body;
            return renderer != GameAccess.Read(entry.Component, "thisPlayerModelArms") as Renderer &&
                renderer != GameAccess.Read(entry.Component, "thisPlayerModelLOD1") as Renderer &&
                renderer != GameAccess.Read(entry.Component, "thisPlayerModelLOD2") as Renderer;
        }

        private static bool RadarAnimator(Animator animator)
        {
            for (var node = animator.transform; node; node = node.parent)
                if (node.name == "MapDot" || node.name.StartsWith("MapDot (", StringComparison.Ordinal)) return true;
            return false;
        }

        internal bool RendererActive(Entry entry, Renderer renderer) => renderer.gameObject.activeInHierarchy &&
            (entry.Kind == "player" ? GameAccess.Bool(entry.Component, "isPlayerControlled") && !GameAccess.Bool(entry.Component, "isPlayerDead") : Visibility.Enabled(renderer));

        private void CaptureRenderers(Entry entry, EntitySnapshot entity)
        {
            if (!entityRendererBaselines.TryGetValue(entry.Id, out var list)) return;
            var root = entry.Component.transform;
            var omitted = Math.Max(0, list.Count - 512);
            foreach (var baseline in list.Take(512))
            {
                var renderer = baseline.Renderer;
                if (!renderer)
                {
                    entity.Renderers.Add(new RenderPose { Id = baseline.Id, Position = baseline.Position,
                        Rotation = baseline.Rotation, Scale = baseline.Scale, Active = false });
                    continue;
                }
                var transform = renderer.transform;
                var position = root.InverseTransformPoint(transform.position);
                var rotation = Quaternion.Inverse(root.rotation) * transform.rotation;
                var rootScale = root.lossyScale;
                var scale = new Vector3(Div(transform.lossyScale.x, rootScale.x), Div(transform.lossyScale.y, rootScale.y), Div(transform.lossyScale.z, rootScale.z));
                if (!GameAccess.Finite(position) || !GameAccess.Finite(rotation) || !GameAccess.Finite(scale)) { omitted++; continue; }
                var active = RendererActive(entry, renderer);
                if (active == baseline.Active &&
                    (position - new Vector3(baseline.Position.X, baseline.Position.Y, baseline.Position.Z)).sqrMagnitude < 1e-8f &&
                    Quaternion.Angle(rotation, new Quaternion(baseline.Rotation.X, baseline.Rotation.Y,
                        baseline.Rotation.Z, baseline.Rotation.W)) < .01f &&
                    (scale - new Vector3(baseline.Scale.X, baseline.Scale.Y, baseline.Scale.Z)).sqrMagnitude < 1e-8f) continue;
                entity.Renderers.Add(new RenderPose { Id = baseline.Id, Position = GameAccess.Vec(position),
                    Rotation = GameAccess.Rot(rotation), Scale = GameAccess.Vec(scale), Active = active });
            }
            if (omitted > 0) entity.State["$omittedRenderers"] = "at least " + omitted;
        }
        private static float Div(float a, float b) => Math.Abs(b) < 0.00001f ? 1 : a / b;

        private static void CaptureSpiderProceduralBones(Entry entry, EntitySnapshot entity)
        {
            // SandSpiderAI drives its mesh root and leg IK outside the Animator.
            // Preserve only those non-reproducible transforms; its other bones
            // still come from the reusable native animation track.
            var root = entry.Component.transform;
            var meshRoot = GameAccess.Read(entry.Component, "meshContainer") as Transform;
            var body = GameAccess.Read(entry.Component, "spiderNormalMesh") as SkinnedMeshRenderer;
            if (!meshRoot || !body) return;
            var selected = new HashSet<Transform> { meshRoot! };
            foreach (var bone in body!.bones)
                if (bone && (bone.name.EndsWith("Thigh", StringComparison.Ordinal) ||
                    bone.name.EndsWith("Leg", StringComparison.Ordinal))) selected.Add(bone);
            foreach (var bone in selected)
            {
                if (!bone || !bone.IsChildOf(root) || !GameAccess.Finite(bone.localPosition) ||
                    !GameAccess.Finite(bone.localRotation) || !GameAccess.Finite(bone.localScale)) continue;
                entity.Bones.Add(new BonePose { Path = RelativePath(root, bone),
                    Position = GameAccess.Vec(bone.localPosition), Rotation = GameAccess.Rot(bone.localRotation),
                    Scale = GameAccess.Vec(bone.localScale) });
            }
        }

        private static void CaptureEnemyFacing(Entry entry, EntitySnapshot entity)
        {
            // AI rotation is often applied to the visual container rather than
            // EnemyAI.transform. Keep only its small transform chain.
            var root = entry.Component.transform;
            var selected = new HashSet<Transform>();
            var meshRoot = GameAccess.Read(entry.Component, "meshContainer") as Transform;
            var animator = GameAccess.Read(entry.Component, "creatureAnimator") as Animator;
            foreach (var candidate in new[] { meshRoot, animator != null && animator ? animator.transform : null })
                for (var bone = candidate; bone != null && bone && bone != root && bone.IsChildOf(root); bone = bone.parent)
                    selected.Add(bone);
            foreach (var bone in selected.Take(12))
            {
                if (!GameAccess.Finite(bone.localPosition) || !GameAccess.Finite(bone.localRotation) ||
                    !GameAccess.Finite(bone.localScale)) continue;
                entity.Bones.Add(new BonePose { Path = RelativePath(root, bone),
                    Position = GameAccess.Vec(bone.localPosition), Rotation = GameAccess.Rot(bone.localRotation),
                    Scale = GameAccess.Vec(bone.localScale) });
            }
        }

        private void CaptureBones(Entry entry, EntitySnapshot entity)
        {
            if (!bones.TryGetValue(entry.Id, out var list) || list.Count == 0)
            {
                // Include ancestors as well so recorded local poses form a complete hierarchy.
                var all = new HashSet<Transform>();
                foreach (var renderer in entry.Component.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => IsCapturedRenderer(entry, renderer) &&
                    (entry.Kind == "player" || !Visibility.OtherLods.Contains(renderer))))
                    foreach (var bone in renderer.bones)
                        for (var t = bone; t && t != entry.Component.transform && t.IsChildOf(entry.Component.transform); t = t.parent)
                            all.Add(t);
                list = all.OrderBy(t => RelativePath(entry.Component.transform, t), StringComparer.Ordinal).Take(513).ToList();
                bones[entry.Id] = list;
            }
            var omitted = Math.Max(0, list.Count - 512);
            foreach (var bone in list.Take(512))
            {
                if (!bone) continue;
                var path = RelativePath(entry.Component.transform, bone);
                if (path.Length > 4096 || path.Count(c => c == '/') > 127 || !GameAccess.Finite(bone.localPosition)
                    || !GameAccess.Finite(bone.localRotation) || !GameAccess.Finite(bone.localScale)) { omitted++; continue; }
                entity.Bones.Add(new BonePose { Path = path,
                    Position = GameAccess.Vec(bone.localPosition), Rotation = GameAccess.Rot(bone.localRotation), Scale = GameAccess.Vec(bone.localScale) });
            }
            if (omitted > 0) entity.State["$omittedBones"] = "at least " + omitted;
        }
        internal static string RelativePath(Transform root, Transform child)
        {
            var names = new Stack<string>();
            // Siblings may share a name; paths must remain unique for replay validation and reconstruction.
            for (var t = child; t && t != root; t = t.parent)
                names.Push(t.name.Replace("/", "%2F") + "[" + t.GetSiblingIndex() + "]");
            return string.Join("/", names);
        }
    }
}
