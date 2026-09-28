using System;
using System.Collections.Generic;
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
            Pair("Turret", "hazard"), Pair("ShipTeleporter", "ship"), Pair("VehicleController", "vehicle"),
            Pair("DeadBodyInfo", "body"), Pair("AnimatedObjectTrigger", "mechanism"),
            Pair("ItemDropship", "ship"), Pair("MineshaftElevatorController", "mechanism"),
            Pair("StartOfRound", "round"), Pair("RoundManager", "round"),
            Pair("TimeOfDay", "time"), Pair("Terminal", "terminal")
        };
        private static KeyValuePair<string, string> Pair(string type, string kind) => new KeyValuePair<string, string>(type, kind);
        private readonly List<KeyValuePair<string, string>> types;
        private readonly Dictionary<int, Entry> identities = new Dictionary<int, Entry>();
        private readonly List<Entry> tracked = new List<Entry>();
        private readonly Dictionary<string, List<Transform>> bones = new Dictionary<string, List<Transform>>();
        private readonly Dictionary<string, Renderer[]> renderers = new Dictionary<string, Renderer[]>();
        private readonly Dictionary<string, HashSet<string>> capturedBones = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<int, MovingSceneRenderer> movingSceneRenderers = new Dictionary<int, MovingSceneRenderer>();
        internal readonly ReplayVisualCapture Visual = new ReplayVisualCapture();
        private readonly ParticleSystem.Particle[] particleBuffer = new ParticleSystem.Particle[64];
        private Bounds[] interiorBounds = Array.Empty<Bounds>();
        private int nextId;
        public int Count => tracked.Count;
        public IEnumerable<Entry> Entries => tracked;
        internal CaptureVisibility.SceneVisibility Visibility { get; private set; } = new CaptureVisibility.SceneVisibility();
        public sealed class Entry
        {
            public Component Component = null!;
            public string Id = "", Kind = "";
        }

        public EntityTracker(string extraTypes)
        {
            types = BuiltinTypes.ToList();
            types.AddRange(extraTypes.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => Pair(x, "mod")));
        }
        public List<string> MissingTypes => types.Where(t => GameAccess.Type(t.Key) == null).Select(t => t.Key).ToList();
        public string Identify(Component component)
        {
            var key = component.GetInstanceID();
            if (identities.TryGetValue(key, out var existing) && existing.Component == component) return existing.Id;
            var entry = new Entry { Component = component, Id = "e" + (++nextId), Kind = "other" };
            identities[key] = entry;
            return entry.Id;
        }
        public void Discover()
        {
            tracked.Clear();
            var seen = new HashSet<int>();
            foreach (var type in types)
            {
                foreach (var component in GameAccess.Find(type.Key))
                {
                    if (!component || !seen.Add(component.GetInstanceID())) continue;
                    Identify(component);
                    var entry = identities[component.GetInstanceID()];
                    entry.Kind = type.Value;
                    tracked.Add(entry);
                }
            }
            foreach (var id in identities.Where(p => !p.Value.Component).Select(p => p.Key).ToArray())
            { bones.Remove(identities[id].Id); renderers.Remove(identities[id].Id); identities.Remove(id); }
            // Keep destroyed renderers until the next world capture. Their last
            // pose must be emitted as inactive, or playback revives the baseline.
            foreach (var entry in tracked)
            {
                if (!CaptureRendererPoses(entry.Kind)) continue;
                renderers[entry.Id] = entry.Component.GetComponentsInChildren<Renderer>(true)
                    .Where(renderer => (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) &&
                        !CaptureVisibility.IsDebugRenderer(renderer) && (entry.Kind == "player" || !Visibility.OtherLods.Contains(renderer))).Take(513).ToArray();
            }
        }

        public void RefreshWorldVisibility()
        {
            Visibility = CaptureVisibility.Snapshot();
            interiorBounds = GameAccess.Find("DunGen.Tile").Take(4096)
                .Select(tile => GameAccess.Read(tile, "Bounds"))
                .OfType<Bounds>().Where(bounds => GameAccess.Finite(bounds.center) &&
                    GameAccess.Finite(bounds.size) && bounds.size.x > 0 && bounds.size.y > 0 && bounds.size.z > 0).ToArray();
        }

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
                if (!component) continue;
                var transform = component.transform;
                if (!GameAccess.Finite(transform.position) || !GameAccess.Finite(transform.rotation) || !GameAccess.Finite(transform.lossyScale))
                { omitted++; continue; }
                var state = GameAccess.CaptureFields(component, Identify, maxFields);
                var name = entry.Kind == "player" ? GameAccess.Read(component, "playerUsername") as string : null;
                if (entry.Kind == "item") name = GameAccess.Read(GameAccess.Read(component, "itemProperties"), "itemName") as string;
                if (entry.Kind == "enemy") name = GameAccess.Read(GameAccess.Read(component, "enemyType"), "enemyName") as string;
                var entity = new EntitySnapshot
                {
                    Id = entry.Id, Kind = entry.Kind, Name = GameAccess.Scalar(name ?? component.name) ?? entry.Kind,
                    Position = GameAccess.Vec(transform.position), Rotation = GameAccess.Rot(transform.rotation),
                    Scale = GameAccess.Vec(transform.lossyScale), Active = component.gameObject.activeInHierarchy, State = state
                };
                if (entry.Kind == "player")
                    entity.Active &= GameAccess.Bool(component, "isPlayerControlled") || GameAccess.Bool(component, "isPlayerDead");
                if (captureBones && (entry.Kind == "player" || entry.Kind == "enemy" ||
                    renderers.TryGetValue(entry.Id, out var skins) && skins.Any(renderer => renderer is SkinnedMeshRenderer)))
                    CaptureBones(entry, entity);
                CaptureRenderers(entry, entity);
                var entityBytes = 1024L + entity.State.Sum(p => 128L + 6L * (p.Key.Length + p.Value.Length))
                    + entity.Bones.Sum(b => 640L + 6L * b.Path.Length)
                    + entity.Renderers.Sum(renderer => 640L + 6L * renderer.Id.Length);
                if (estimatedBytes + entityBytes > 24L * 1024 * 1024) { omitted++; continue; }
                estimatedBytes += entityBytes;
                frame.Entities.Add(entity);
                capturedBones[entity.Id] = new HashSet<string>(entity.Bones.Select(bone => bone.Path), StringComparer.Ordinal);
                }
                catch (UnityException) { omitted++; }
            }
            if (omitted > 0)
            {
                frame.State["$omittedEntities"] = omitted.ToString(System.Globalization.CultureInfo.InvariantCulture);
                frame.State["$entityOmissionReason"] = "invalid transform, destroyed component, entity count or frame byte budget";
            }
            foreach (var name in new[] { "StartOfRound", "RoundManager", "TimeOfDay" })
            {
                var singleton = GameAccess.Singleton(name);
                foreach (var key in new[] { "randomMapSeed", "currentLevelID", "inShipPhase", "shipHasLanded", "globalTime", "currentDayTime", "normalizedTimeOfDay", "profitQuota", "quotaFulfilled", "timeUntilDeadline", "daysUntilDeadline", "currentLevelWeather", "currentDungeonType", "dungeonCompletedGenerating", "dungeonIsGenerating" })
                {
                    var value = GameAccess.Scalar(GameAccess.Read(singleton, key));
                    if (value != null) frame.State[name + "." + key] = value;
                }
            }
            CaptureShortLivedParticles(frame);
            CaptureLines(frame);
            CaptureMovingSceneRenderers(frame);
            return frame;
        }

        internal void TrackMovingSceneRenderer(Renderer renderer, Transform? anchor)
        {
            if (renderer && movingSceneRenderers.Count < 512)
                movingSceneRenderers[renderer.GetInstanceID()] = new MovingSceneRenderer(renderer, anchor);
        }

        internal void ClearMovingSceneRenderers() => movingSceneRenderers.Clear();

        private void CaptureMovingSceneRenderers(ReplayFrame frame)
        {
            foreach (var moving in movingSceneRenderers.Values)
            {
                if (frame.SceneRenderers.Count >= 512) break;
                var renderer = moving.Renderer;
                if (!renderer)
                {
                    if (moving.LastPose != null)
                        frame.SceneRenderers.Add(new RenderPose { Id = moving.LastPose.Id,
                            Position = moving.LastPose.Position, Rotation = moving.LastPose.Rotation,
                            Scale = moving.LastPose.Scale, Active = false });
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
                frame.SceneRenderers.Add(pose);
            }
        }

        private sealed class MovingSceneRenderer
        {
            internal readonly Renderer Renderer;
            internal readonly Transform? Anchor;
            internal RenderPose? LastPose;
            internal MovingSceneRenderer(Renderer renderer, Transform? anchor)
            { Renderer = renderer; Anchor = anchor; }
        }

        private void CaptureShortLivedParticles(ReplayFrame frame)
        {
            var cameraMask = CaptureVisibility.GameplayMask();
            var tileType = GameAccess.Type("DunGen.Tile");
            var turretType = GameAccess.Type("Turret");
            foreach (var system in UnityEngine.Object.FindObjectsOfType<ParticleSystem>(false))
            {
                if (frame.Particles.Count >= 256 || frame.ParticleStyles.Count >= 128) break;
                if (!system || !system.isPlaying || (!system.main.loop && system.particleCount == 0) ||
                    ReplayIsolation.IsReplayScene(system.gameObject.scene)) continue;
                // Turret muzzle flashes are emitted by a looping system only
                // while it fires; sample its actual particles in each frame.
                if (system.main.loop && (turretType == null || system.GetComponentInParent(turretType) == null)) continue;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (!renderer || !Visibility.Enabled(renderer) || CaptureVisibility.IsDebugRenderer(renderer) ||
                    !CaptureVisibility.VisibleLayer(renderer, cameraMask, Visibility.CullerManaged.Contains(renderer))) continue;
                int count;
                try { count = system.GetParticles(particleBuffer, Math.Min(particleBuffer.Length, 256 - frame.Particles.Count)); }
                catch { continue; }
                var main = system.main;
                var style = ParticleCapture.Style(system, renderer);
                style.Simulate = main.loop;
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
            foreach (var line in UnityEngine.Object.FindObjectsOfType<LineRenderer>(false))
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

        internal bool RendererActive(Entry entry, Renderer renderer) => renderer.gameObject.activeInHierarchy &&
            (entry.Kind == "player" ? GameAccess.Bool(entry.Component, "isPlayerControlled") && !GameAccess.Bool(entry.Component, "isPlayerDead") : Visibility.Enabled(renderer));

        private void CaptureRenderers(Entry entry, EntitySnapshot entity)
        {
            if (!renderers.TryGetValue(entry.Id, out var list)) return;
            int cameraMask = CaptureVisibility.GameplayMask();
            var root = entry.Component.transform;
            var omitted = Math.Max(0, list.Length - 512);
            foreach (var renderer in list.Take(512))
            {
                if (!renderer || !IsCapturedRenderer(entry, renderer) || (entry.Kind != "player" &&
                    !CaptureVisibility.VisibleLayer(renderer, cameraMask, Visibility.CullerManaged.Contains(renderer)))) continue;
                var transform = renderer.transform;
                var position = root.InverseTransformPoint(transform.position);
                var rotation = Quaternion.Inverse(root.rotation) * transform.rotation;
                var rootScale = root.lossyScale;
                var scale = new Vector3(Div(transform.lossyScale.x, rootScale.x), Div(transform.lossyScale.y, rootScale.y), Div(transform.lossyScale.z, rootScale.z));
                if (!GameAccess.Finite(position) || !GameAccess.Finite(rotation) || !GameAccess.Finite(scale)) { omitted++; continue; }
                entity.Renderers.Add(new RenderPose { Id = "g" + renderer.GetInstanceID(), Position = GameAccess.Vec(position),
                    Rotation = GameAccess.Rot(rotation), Scale = GameAccess.Vec(scale), Active = RendererActive(entry, renderer) });
            }
            if (omitted > 0) entity.State["$omittedRenderers"] = "at least " + omitted;
        }
        private static float Div(float a, float b) => Math.Abs(b) < 0.00001f ? 1 : a / b;

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
