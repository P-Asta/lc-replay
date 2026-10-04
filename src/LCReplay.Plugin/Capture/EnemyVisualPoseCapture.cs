using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    // Capture the result of the installed animation and procedural scripts,
    // without needing a list of enemy types or running their AI in playback.
    internal sealed class EnemyVisualPoseCapture
    {
        private const int MaxBones = 512;
        internal const string BlendShapeStatePrefix = "$blendshape:";
        private const int MaxBlendRenderers = 32, MaxBlendWeights = 128;
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);

        private sealed class Binding
        {
            internal Component Owner = null!;
            internal Node[] Nodes = Array.Empty<Node>();
            internal BlendSkin[] BlendSkins = Array.Empty<BlendSkin>();
            internal int Omitted;
            internal int OmittedBlendWeights;
            internal bool Reported;
        }

        private sealed class BlendSkin
        {
            internal SkinnedMeshRenderer Renderer = null!;
            internal string Key = "";
            internal float[] Values = Array.Empty<float>();
            internal string? Encoded;
        }

        private sealed class Node
        {
            internal Transform Transform = null!;
            internal string Path = "";
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal BonePose? Pose;
        }

        internal void Forget(string id) => bindings.Remove(id);

        internal void Refresh(EntityTracker.Entry entry, CaptureVisibility.SceneVisibility visibility)
        {
            var root = entry.Component.transform;
            var selected = new HashSet<Transform>();
            var blendRenderers = new List<SkinnedMeshRenderer>();
            void Include(Transform node)
            {
                for (var current = node; current && current != root && current.IsChildOf(root); current = current.parent)
                    selected.Add(current);
            }
            // Inactive skins belong to forms which have not appeared yet.
            // Include renderer ancestors too: rigid faces, jaws and complete
            // visual containers can be moved or scaled outside an Animator.
            foreach (var renderer in entry.Component.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) ||
                    CaptureVisibility.IsDebugRenderer(renderer) || visibility.OtherLods.Contains(renderer)) continue;
                Include(renderer.transform);
                if (renderer is SkinnedMeshRenderer skin)
                {
                    foreach (var bone in skin.bones) if (bone) Include(bone);
                    if (skin.sharedMesh && skin.sharedMesh.blendShapeCount != 0) blendRenderers.Add(skin);
                }
            }
            if (!bindings.TryGetValue(entry.Id, out var binding) || binding.Owner != entry.Component)
                bindings[entry.Id] = binding = new Binding { Owner = entry.Component };
            var previous = binding.Nodes.Where(node => node.Transform).ToDictionary(node => node.Transform);
            var nodes = new List<Node>(Math.Min(MaxBones, selected.Count));
            var omitted = 0;
            foreach (var candidate in selected.Select(node => (Transform: node, Path: EntityTracker.RelativePath(root, node)))
                .OrderBy(node => node.Path, StringComparer.Ordinal))
            {
                var transform = candidate.Transform; var path = candidate.Path;
                if (nodes.Count >= MaxBones || path.Length > 4096 || path.Count(character => character == '/') > 127)
                { omitted++; continue; }
                if (!previous.TryGetValue(transform, out var node) || node.Path != path)
                    node = new Node { Transform = transform, Path = path };
                nodes.Add(node);
            }
            binding.Nodes = nodes.ToArray(); binding.Omitted = omitted;
            var priorSkins = binding.BlendSkins.Where(skin => skin.Renderer).ToDictionary(skin => skin.Renderer);
            var blendSkins = new List<BlendSkin>();
            var remaining = MaxBlendWeights; var omittedWeights = 0;
            foreach (var candidate in blendRenderers.Select(skin => (Renderer: skin, Path: EntityTracker.RelativePath(root, skin.transform)))
                .OrderBy(skin => skin.Path, StringComparer.Ordinal))
            {
                var count = candidate.Renderer.sharedMesh.blendShapeCount;
                if (blendSkins.Count >= MaxBlendRenderers || candidate.Path.Length > 4096)
                { omittedWeights += count; continue; }
                var captured = Math.Min(remaining, count);
                remaining -= captured; omittedWeights += count - captured;
                if (captured == 0) continue;
                var key = BlendShapeStatePrefix + candidate.Path;
                if (!priorSkins.TryGetValue(candidate.Renderer, out var skin) || skin.Key != key || skin.Values.Length != captured)
                    skin = new BlendSkin { Renderer = candidate.Renderer, Key = key, Values = new float[captured] };
                blendSkins.Add(skin);
            }
            binding.BlendSkins = blendSkins.ToArray(); binding.OmittedBlendWeights = omittedWeights;
            if ((omitted != 0 || omittedWeights != 0) && !binding.Reported)
            {
                binding.Reported = true;
                Debug.LogWarning("LC Replay: enemy visual pose limit reached for " + entry.Name +
                    " (" + entry.Id + "); " + omitted + " transforms and " + omittedWeights + " blendshape weights omitted.");
            }
        }

        internal void Capture(EntityTracker.Entry entry, EntitySnapshot entity, CaptureVisibility.SceneVisibility visibility)
        {
            if (!bindings.TryGetValue(entry.Id, out var binding) || binding.Owner != entry.Component)
            { Refresh(entry, visibility); binding = bindings[entry.Id]; }
            var omitted = binding.Omitted;
            foreach (var node in binding.Nodes)
            {
                if (!node.Transform) { omitted++; continue; }
                var position = node.Transform.localPosition;
                var rotation = node.Transform.localRotation;
                var scale = node.Transform.localScale;
                if (!GameAccess.Finite(position) || !GameAccess.Finite(rotation) || !GameAccess.Finite(scale))
                { omitted++; continue; }
                // Published frames are immutable. Reuse an unchanged pose
                // object, but never modify one retained by an earlier frame
                // or by the asynchronous writer.
                if (node.Pose == null || !position.Equals(node.Position) ||
                    !rotation.Equals(node.Rotation) || !scale.Equals(node.Scale))
                {
                    node.Position = position; node.Rotation = rotation; node.Scale = scale;
                    node.Pose = new BonePose { Path = node.Path, Position = GameAccess.Vec(position),
                        Rotation = GameAccess.Rot(rotation), Scale = GameAccess.Vec(scale) };
                }
                entity.Bones.Add(node.Pose);
            }
            if (omitted != 0) entity.State["$omittedBones"] = "at least " + omitted;
            var omittedWeights = binding.OmittedBlendWeights;
            foreach (var skin in binding.BlendSkins)
            {
                if (!skin.Renderer || !skin.Renderer.sharedMesh) { omittedWeights += skin.Values.Length; continue; }
                var changed = skin.Encoded == null;
                for (var index = 0; index < skin.Values.Length; index++)
                {
                    var value = index < skin.Renderer.sharedMesh.blendShapeCount ? skin.Renderer.GetBlendShapeWeight(index) : 0f;
                    if (float.IsNaN(value) || float.IsInfinity(value)) { omittedWeights++; value = 0f; }
                    if (skin.Values[index] != value) { skin.Values[index] = value; changed = true; }
                }
                if (changed)
                {
                    var text = new StringBuilder(skin.Values.Length * 6);
                    for (var index = 0; index < skin.Values.Length; index++)
                    { if (index != 0) text.Append(','); text.Append(skin.Values[index].ToString("R", CultureInfo.InvariantCulture)); }
                    skin.Encoded = text.ToString();
                }
                // Include zero weights too: a seek must not retain a later
                // expression when this frame used the undeformed mesh.
                entity.State[skin.Key] = skin.Encoded!;
            }
            if (omittedWeights != 0) entity.State["$omittedBlendShapes"] = "at least " + omittedWeights;
        }
    }
}
