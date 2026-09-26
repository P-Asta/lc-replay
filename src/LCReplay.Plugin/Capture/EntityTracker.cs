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
            foreach (var entry in tracked)
            {
                if (!CaptureRendererPoses(entry.Kind)) continue;
                renderers[entry.Id] = entry.Component.GetComponentsInChildren<Renderer>(true)
                    .Where(renderer => (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) &&
                        !CaptureVisibility.IsDebugRenderer(renderer) && (entry.Kind == "player" || !Visibility.OtherLods.Contains(renderer))).Take(513).ToArray();
            }
        }

        public void RefreshWorldVisibility() => Visibility = CaptureVisibility.Snapshot();

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
                if (captureBones && (entry.Kind == "player" || entry.Kind == "enemy")) CaptureBones(entry, entity);
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
            return frame;
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
                if (!renderer || !IsCapturedRenderer(entry, renderer) || (entry.Kind != "player" && !CaptureVisibility.VisibleLayer(renderer, cameraMask))) continue;
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
                list = all.OrderBy(t => RelativePath(entry.Component.transform, t), StringComparer.Ordinal).Take(129).ToList();
                bones[entry.Id] = list;
            }
            var omitted = Math.Max(0, list.Count - 128);
            foreach (var bone in list.Take(128))
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
