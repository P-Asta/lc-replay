using System;
using System.Collections;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    // Older recordings can contain a spider actor but no body geometry. Reuse
    // the installed game's render assets without instantiating any gameplay AI.
    internal static class NativeSpiderFallback
    {
        internal static int Restore(WorldSnapshot world, ReplayFrame frame)
        {
            var missing = frame.Entities.Where(entity => entity.Kind == "enemy" &&
                (entity.Name.IndexOf("Bunker Spider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 entity.Name.IndexOf("SandSpider", StringComparison.OrdinalIgnoreCase) >= 0) &&
                !world.Geometry.Any(geometry => geometry.EntityId == entity.Id &&
                    geometry.Name == "MeshRenderer" && !geometry.IsBoundsProxy)).Take(4).ToArray();
            if (missing.Length == 0) return 0;
            var source = FindSource();
            if (!source) return 0;
            var body = GameAccess.Read(source, "spiderNormalMesh") as SkinnedMeshRenderer;
            if (!body || !body!.sharedMesh) return 0;
            var count = 0;
            foreach (var actor in missing)
            {
                foreach (var renderer in source!.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer || renderer != body && renderer.name != "RightFang" && renderer.name != "LeftFang" ||
                        world.Geometry.Any(geometry => geometry.EntityId == actor.Id && geometry.Name == renderer.name &&
                            !geometry.IsBoundsProxy)) continue;
                    var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
                        renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (!mesh) continue;
                    var root = source.transform;
                    // Fangs are rigid children of the animated head. The old
                    // fallback stored them under the AI root, so the head and
                    // teeth separated as soon as the spider moved.
                    var attachedBone = renderer == body ? null : body!.bones
                        .Where(bone => bone && renderer.transform.IsChildOf(bone))
                        .OrderByDescending(bone => EntityTracker.RelativePath(root, bone).Length)
                        .FirstOrDefault();
                    var parent = attachedBone ? attachedBone! : root;
                    var scale = parent.lossyScale;
                    var localScale = renderer.transform.lossyScale;
                    var geometry = new GeometrySnapshot
                    {
                        Id = "native-spider:" + actor.Id + ":" + renderer.name,
                        Name = renderer.name, EntityId = actor.Id,
                        AttachedBonePath = attachedBone ? EntityTracker.RelativePath(root, attachedBone!) : "",
                        Position = GameAccess.Vec(parent.InverseTransformPoint(renderer.transform.position)),
                        Rotation = GameAccess.Rot(Quaternion.Inverse(parent.rotation) * renderer.transform.rotation),
                        Scale = GameAccess.Vec(new Vector3(Div(localScale.x, scale.x),
                            Div(localScale.y, scale.y), Div(localScale.z, scale.z))),
                        BoundsCenter = GameAccess.Vec(mesh!.bounds.center), BoundsSize = GameAccess.Vec(mesh.bounds.size)
                    };
                    if (!MeshSnapshotReader.Read(mesh, geometry, 50000, 300000)) continue;
                    if (renderer is SkinnedMeshRenderer skinned &&
                        !MeshSnapshotReader.Skin(skinned, root, geometry)) continue;
                    if (geometry.SubmeshTriangles.Count == 1) geometry.SubmeshTriangles.Clear();
                    foreach (var material in renderer.sharedMaterials.Take(16))
                    {
                        if (!material || !material!.shader) { geometry.MaterialIds.Add(""); continue; }
                        var id = "native-spider-material:" + material.GetInstanceID();
                        if (!world.Materials.Any(snapshot => snapshot.Id == id))
                        {
                            var color = Color.white;
                            foreach (var property in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                                if (material.HasProperty(property)) { color = material.GetColor(property); break; }
                            world.Materials.Add(new MaterialSnapshot { Id = id, Name = material.name,
                                ShaderName = material.shader.name,
                                Color = new[] { color.r, color.g, color.b, color.a } });
                        }
                        geometry.MaterialIds.Add(id);
                    }
                    world.Geometry.Add(geometry);
                    count++;
                }
            }
            return count;
        }

        private static Component? FindSource()
        {
            var config = GameAccess.Read(GameAccess.Singleton("Unity.Netcode.NetworkManager"), "NetworkConfig");
            var entries = GameAccess.Read(GameAccess.Read(config, "Prefabs"), "Prefabs") as IEnumerable;
            var spider = GameAccess.Type("SandSpiderAI");
            if (spider == null || entries == null) return null;
            foreach (var entry in entries)
            {
                var prefab = GameAccess.Read(entry, "Prefab") as GameObject;
                if (!prefab || !prefab!.name.StartsWith("SandSpider", StringComparison.OrdinalIgnoreCase)) continue;
                var component = prefab.GetComponentInChildren(spider, true);
                if (component) return component;
            }
            return null;
        }

        private static float Div(float value, float scale) => Math.Abs(scale) < .00001f ? 1f : value / scale;
    }
}
