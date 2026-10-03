using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin
{
    // References to installed assets only. Never instantiate enemy/item AI.
    internal static class PrefabAssetRegistry
    {
        private static object? scannedConfig;
        private static float nextScan;
        private static readonly Dictionary<string, Component> Prefabs = new Dictionary<string, Component>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Renderer> Renderers = new Dictionary<string, Renderer>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Animator> Animators = new Dictionary<string, Animator>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GeometrySnapshot> Meshes = new Dictionary<string, GeometrySnapshot>(StringComparer.Ordinal);
        private static readonly Queue<string> meshOrder = new Queue<string>();
        private static long meshBytes;

        internal static string Key(Component component, string kind)
        {
            if (kind == "player") return "player";
            if (kind == "hazard") return "hazard:" + component.GetType().Name;
            var properties = GameAccess.Read(component, kind == "enemy" ? "enemyType" : "itemProperties");
            var name = GameAccess.Scalar(GameAccess.Read(properties, kind == "enemy" ? "enemyName" : "itemId"));
            return string.IsNullOrEmpty(name) ? "" : kind + ":" + name;
        }

        internal static void Warm(bool force = false)
        {
            // The shared player body should be referenced once instead of
            // reading the same skin/GPU maps for every lobby member.
            if (!Prefabs.TryGetValue("player", out var player) || !player)
            {
                var playerType = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
                var playerObject = GameAccess.Read(GameAccess.Singleton("StartOfRound"), "playerPrefab") as GameObject;
                if (playerType != null && playerObject && !playerObject!.scene.IsValid() &&
                    playerObject.GetComponentInChildren(playerType, true) is Component nativePlayer)
                    Add(nativePlayer, "player");
            }
            var config = GameAccess.Read(GameAccess.Singleton("Unity.Netcode.NetworkManager"), "NetworkConfig");
            if (!force && config != null && ReferenceEquals(config, scannedConfig)) return;
            if (!force && config == null && Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 2f;
            // Player/enemy assets are also loaded below scene/network prefabs
            // in a fresh menu, where NetworkConfig.PlayerPrefab is often null.
            foreach (var hazard in new[] { "Turret", "Landmine" })
            {
                var type = GameAccess.Type(hazard);
                if (type == null) continue;
                var prefab = Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault(component => component && !component.gameObject.scene.IsValid());
                if (prefab) Prefabs["hazard:" + hazard] = prefab!;
            }
            foreach (var kind in new[] { "enemy", "item" })
            {
                var type = GameAccess.Type(kind == "enemy" ? "EnemyAI" : "GrabbableObject");
                if (type == null) continue;
                foreach (var component in Resources.FindObjectsOfTypeAll(type).OfType<Component>())
                    if (component && !component.gameObject.scene.IsValid()) Add(component, kind);
            }
            var entries = GameAccess.Read(GameAccess.Read(config, "Prefabs"), "Prefabs") as IEnumerable;
            if (entries == null) return;
            foreach (var entry in entries)
            {
                var prefab = GameAccess.Read(entry, "Prefab") as GameObject;
                if (!prefab) continue;
                foreach (var kind in new[] { "enemy", "item" })
                {
                    var type = GameAccess.Type(kind == "enemy" ? "EnemyAI" : "GrabbableObject");
                    if (type == null) continue;
                    var component = prefab!.GetComponentInChildren(type, true);
                    if (!component) continue;
                    Add(component, kind);
                }
            }
            scannedConfig = config;
        }

        private static void Add(Component component, string kind)
        {
            var key = Key(component, kind);
            if (key.Length == 0 || Prefabs.TryGetValue(key, out var existing) && existing) return;
            Prefabs[key] = component;
            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
                if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                    Renderers[key + "\n" + EntityTracker.RelativePath(component.transform, renderer.transform)] = renderer;
            foreach (var animator in component.GetComponentsInChildren<Animator>(true))
            {
                Animators[key + "\n" + EntityTracker.RelativePath(component.transform, animator.transform)] = animator;
                AnimationAssetRegistry.Remember(animator, kind);
            }
        }

        internal static bool Reference(EntityTracker.Entry owner, Renderer renderer, GeometrySnapshot geometry)
        {
            if (owner.Kind != "enemy" && owner.Kind != "item" && owner.Kind != "player") return false;
            Warm();
            var key = Key(owner.Component, owner.Kind);
            var path = EntityTracker.RelativePath(owner.Component.transform, renderer.transform);
            if (path.Length == 0 || !Renderers.TryGetValue(key + "\n" + path, out var native) || !native ||
                Mesh(renderer) != Mesh(native)) return false;
            geometry.PrefabKey = key; geometry.PrefabRendererPath = path;
            geometry.MeshName = Mesh(native)!.name; geometry.IsBoundsProxy = false;
            return true;
        }

        internal static bool NativeMaterials(Renderer renderer, GeometrySnapshot geometry)
        {
            if (!Renderers.TryGetValue(geometry.PrefabKey + "\n" + geometry.PrefabRendererPath, out var native) || !native) return false;
            var current = renderer.sharedMaterials; var source = native.sharedMaterials;
            return current.Length == source.Length && !current.Where((material, index) => material != source[index]).Any();
        }

        internal static void Restore(WorldSnapshot world, ReplaySession session)
        {
            Warm();
            var actors = session.Frames.SelectMany(frame => frame.Entities).Where(actor => actor.Kind == "enemy")
                .GroupBy(actor => actor.Id).ToDictionary(group => group.Key, group => group.First());
            foreach (var geometry in world.Geometry)
            {
                var recordedMaterials = geometry.PrefabKey.Length != 0 && geometry.MaterialIds.Count != 0;
                var key = geometry.PrefabKey;
                var path = geometry.PrefabRendererPath;
                // Repair legacy giant/thumper skins from the matching installed
                // prefab, including bone scales and original material maps.
                if (key.Length == 0 && actors.TryGetValue(geometry.EntityId, out var actor))
                {
                    key = "enemy:" + actor.Name;
                    if (!Prefabs.TryGetValue(key, out var prefab) || !prefab) continue;
                    var matches = prefab.GetComponentsInChildren<Renderer>(true).Where(renderer =>
                        renderer && renderer.name == geometry.Name && Mesh(renderer) &&
                        (Mesh(renderer)!.name == geometry.MeshName || geometry.IsBoundsProxy)).ToArray();
                    if (matches.Length != 1) continue;
                    path = EntityTracker.RelativePath(prefab.transform, matches[0].transform);
                }
                var asset = key + "\n" + path;
                if (!Renderers.TryGetValue(asset, out var renderer) || !renderer ||
                    !Prefabs.TryGetValue(key, out var root) || !root) continue;
                if (!Meshes.TryGetValue(asset, out var source))
                {
                    source = new GeometrySnapshot();
                    var mesh = Mesh(renderer);
                    if (!mesh || !MeshSnapshotReader.Read(mesh!, source, 1000000, 6000000)) continue;
                    if (renderer is SkinnedMeshRenderer skin && !MeshSnapshotReader.Skin(skin, root.transform, source)) continue;
                    var bytes = Bytes(source);
                    // A single oversized mod asset can be used by this world,
                    // but must not become an unbounded shared cache entry.
                    if (bytes <= 64L * 1024 * 1024)
                    {
                        while (meshBytes + bytes > 64L * 1024 * 1024 && meshOrder.Count != 0)
                        {
                            var oldest = meshOrder.Dequeue();
                            if (Meshes.TryGetValue(oldest, out var evicted)) { meshBytes -= Bytes(evicted); Meshes.Remove(oldest); }
                        }
                        Meshes[asset] = source;
                        meshOrder.Enqueue(asset); meshBytes += bytes;
                    }
                }
                geometry.Vertices = source.Vertices; geometry.Normals = source.Normals; geometry.Uvs = source.Uvs;
                geometry.Uvs1 = source.Uvs1; geometry.Uvs2 = source.Uvs2; geometry.Uvs3 = source.Uvs3; geometry.Tangents = source.Tangents;
                geometry.Triangles = source.Triangles; geometry.SubmeshTriangles = source.SubmeshTriangles;
                geometry.BonePaths = source.BonePaths; geometry.BindPoses = source.BindPoses;
                geometry.BoneIndices = source.BoneIndices; geometry.BoneWeights = source.BoneWeights;
                geometry.RigBones = source.RigBones; geometry.RootBonePath = source.RootBonePath;
                geometry.AnimatorPath = source.AnimatorPath; geometry.AnimatorController = source.AnimatorController;
                geometry.AnimatorAvatar = source.AnimatorAvatar; geometry.IsBoundsProxy = false;
                geometry.PrefabKey = key; geometry.PrefabRendererPath = path;
                if (recordedMaterials) continue;
                geometry.MaterialIds = new List<string>();
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material || !material.shader) { geometry.MaterialIds.Add(""); continue; }
                    var id = "native-prefab:" + material.GetInstanceID();
                    if (!world.Materials.Any(snapshot => snapshot.Id == id))
                    {
                        var color = Color.white;
                        foreach (var property in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                            if (material.HasProperty(property)) { color = material.GetColor(property); break; }
                        world.Materials.Add(new MaterialSnapshot { Id = id, Name = material.name, ShaderName = material.shader.name,
                            Color = new[] { color.r, color.g, color.b, color.a } });
                    }
                    geometry.MaterialIds.Add(id);
                }
            }
        }

        private static Mesh? Mesh(Renderer renderer) => renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
        internal static Animator? ResolveAnimator(string key, string path) => Animators.TryGetValue(key + "\n" + path, out var animator) && animator ? animator : null;
        internal static Component? ResolvePrefab(string key)
        {
            Warm();
            return Prefabs.TryGetValue(key, out var prefab) && prefab ? prefab : null;
        }
        private static long Bytes(GeometrySnapshot geometry) => 4L * (geometry.Vertices.Length + geometry.Normals.Length +
            geometry.Uvs.Length + geometry.Uvs1.Length + geometry.Uvs2.Length + geometry.Uvs3.Length + geometry.Tangents.Length +
            geometry.Triangles.Length + geometry.BoneWeights.Length + geometry.BoneIndices.Length + geometry.BindPoses.Length +
            geometry.SubmeshTriangles.Sum(indices => (long)indices.Length));
    }
}
