using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace LCReplay.Plugin.Capture
{
    // Reads rendering buffers only. It never changes readability flags or the game's mesh assets.
    internal static class MeshSnapshotReader
    {
        internal static bool Read(Mesh mesh, GeometrySnapshot target, int vertexBudget, int indexBudget, int maxSubmeshes = 16)
        {
            try
            {
                if (!mesh || mesh.vertexCount < 3 || mesh.vertexCount > vertexBudget || mesh.subMeshCount > maxSubmeshes) return false;
                ulong count = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) count += mesh.GetIndexCount(s);
                if (count > (ulong)Math.Max(0, indexBudget)) return false;
                var streams = new Dictionary<int, byte[]>();
                var vertices = mesh.isReadable ? Flatten(mesh.vertices) : Attribute(mesh, VertexAttribute.Position, 3, streams);
                if (vertices.Length != mesh.vertexCount * 3 || vertices.Any(value => !GameAccess.Finite(value))) return false;
                var normals = mesh.isReadable ? Flatten(mesh.normals) : Attribute(mesh, VertexAttribute.Normal, 3, streams);
                var uv = mesh.isReadable ? Flatten(mesh.uv) : Attribute(mesh, VertexAttribute.TexCoord0, 2, streams);
                target.Uvs1 = Attribute(mesh, VertexAttribute.TexCoord1, 2, streams);
                target.Uvs2 = Attribute(mesh, VertexAttribute.TexCoord2, 2, streams);
                target.Uvs3 = Attribute(mesh, VertexAttribute.TexCoord3, 2, streams);
                target.Tangents = Attribute(mesh, VertexAttribute.Tangent, 4, streams);
                var submeshes = new List<int[]>();
                byte[]? indexData = null;
                if (!mesh.isReadable)
                {
                    using (var buffer = mesh.GetIndexBuffer())
                    {
                        if (buffer == null || (long)buffer.count * buffer.stride > 32L * 1024 * 1024) return false;
                        indexData = new byte[buffer.count * buffer.stride]; buffer.GetData(indexData);
                    }
                }
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) { submeshes.Add(Array.Empty<int>()); continue; }
                    int[] triangles;
                    if (mesh.isReadable) triangles = mesh.GetTriangles(s);
                    else
                    {
                        var descriptor = mesh.GetSubMesh(s);
                        triangles = new int[descriptor.indexCount];
                        int width = mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2;
                        for (int i = 0; i < triangles.Length; i++)
                        {
                            int offset = (descriptor.indexStart + i) * width;
                            if (offset < 0 || offset + width > indexData!.Length) return false;
                            triangles[i] = checked((width == 4 ? (int)BitConverter.ToUInt32(indexData, offset) : BitConverter.ToUInt16(indexData, offset)) + descriptor.baseVertex);
                        }
                    }
                    if (triangles.Length % 3 != 0 || triangles.Any(index => index < 0 || index >= mesh.vertexCount)) return false;
                    submeshes.Add(triangles);
                }
                if (submeshes.Sum(indices => indices.Length) == 0) return false;
                target.MeshName = GameAccess.Scalar(mesh.name) ?? "";
                target.Vertices = vertices;
                target.Normals = normals.Length == vertices.Length && normals.All(GameAccess.Finite) ? normals : Array.Empty<float>();
                target.Uvs = uv.Length == mesh.vertexCount * 2 && uv.All(GameAccess.Finite) ? uv : Array.Empty<float>();
                target.SubmeshTriangles = submeshes;
                target.Triangles = submeshes.SelectMany(indices => indices).ToArray();
                target.IsBoundsProxy = false;
                return true;
            }
            catch { return false; }
        }

        // One context per world capture. Combined GPU buffers are read only once, while
        // each renderer contributes only its material/submesh range and referenced vertices.
        internal sealed class StaticBatchReader
        {
            private const long MaxCacheBytes = 64L * 1024 * 1024;
            private readonly Dictionary<int, GeometrySnapshot> cache = new Dictionary<int, GeometrySnapshot>();
            private readonly HashSet<int> unavailable = new HashSet<int>();
            private long cacheBytes;

            internal bool Read(MeshRenderer renderer, Mesh mesh, GeometrySnapshot target, int vertexBudget, int indexBudget)
            {
                try
                {
                    if (!renderer || !mesh || vertexBudget < 3 || indexBudget < 3) return false;
                    int key = mesh.GetInstanceID();
                    if (unavailable.Contains(key)) return false;
                    if (!cache.TryGetValue(key, out var source))
                    {
                        long indices = 0;
                        if (mesh.vertexCount > 1000000 || mesh.subMeshCount > 8192) { unavailable.Add(key); return false; }
                        for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
                        long bytes = 96L * mesh.vertexCount + 8L * indices + 64L * mesh.subMeshCount;
                        if (indices > 6000000 || bytes + cacheBytes > MaxCacheBytes) { unavailable.Add(key); return false; }
                        source = new GeometrySnapshot();
                        if (!MeshSnapshotReader.Read(mesh, source, 1000000, 6000000, 8192)) { unavailable.Add(key); return false; }
                        source.Triangles = Array.Empty<int>();
                        cache.Add(key, source); cacheBytes += bytes;
                    }
                    int first = renderer.subMeshStartIndex;
                    int count = renderer.sharedMaterials.Length;
                    if (first < 0 || first >= source.SubmeshTriangles.Count || count < 1 || count > 16) return false;
                    count = Math.Min(count, source.SubmeshTriangles.Count - first);
                    long totalIndices = 0;
                    for (int i = first; i < first + count; i++) totalIndices += source.SubmeshTriangles[i].Length;
                    if (totalIndices < 3 || totalIndices > indexBudget) return false;

                    // Unowned static geometry is stored directly in world space. A hierarchy
                    // with rotated nonuniform scales can contain shear that position/rotation/
                    // lossyScale cannot reproduce. Owned geometry still follows its entity pose.
                    bool worldSpace = target.EntityId.Length == 0 && target.AnchorId.Length == 0;
                    var conversion = worldSpace ? renderer.localToWorldMatrix :
                        renderer.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                    for (int i = 0; i < 16; i++) if (!GameAccess.Finite(conversion[i])) return false;
                    var normalConversion = conversion.inverse.transpose;
                    var remap = new Dictionary<int, int>();
                    var vertices = new List<float>();
                    var normals = new List<float>();
                    var uvs = new List<float>();
                    var uv1 = new List<float>(); var uv2 = new List<float>(); var uv3 = new List<float>();
                    var tangents = new List<float>();
                    var submeshes = new List<int[]>();
                    var bounds = new Bounds();
                    bool hasNormals = source.Normals.Length == source.Vertices.Length;
                    bool hasUvs = source.Uvs.Length == source.Vertices.Length / 3 * 2;
                    for (int s = first; s < first + count; s++)
                    {
                        var input = source.SubmeshTriangles[s];
                        var output = new int[input.Length];
                        for (int i = 0; i < input.Length; i++)
                        {
                            int old = input[i];
                            if (!remap.TryGetValue(old, out var index))
                            {
                                if (remap.Count >= vertexBudget) return false;
                                index = remap.Count; remap.Add(old, index);
                                var position = conversion.MultiplyPoint3x4(new Vector3(source.Vertices[old * 3], source.Vertices[old * 3 + 1], source.Vertices[old * 3 + 2]));
                                if (!GameAccess.Finite(position)) return false;
                                vertices.Add(position.x); vertices.Add(position.y); vertices.Add(position.z);
                                if (index == 0) bounds = new Bounds(position, Vector3.zero); else bounds.Encapsulate(position);
                                if (hasNormals)
                                {
                                    var normal = normalConversion.MultiplyVector(new Vector3(source.Normals[old * 3], source.Normals[old * 3 + 1], source.Normals[old * 3 + 2])).normalized;
                                    if (!GameAccess.Finite(normal)) return false;
                                    normals.Add(normal.x); normals.Add(normal.y); normals.Add(normal.z);
                                }
                                if (hasUvs) { uvs.Add(source.Uvs[old * 2]); uvs.Add(source.Uvs[old * 2 + 1]); }
                                CopyUv(source.Uvs1, uv1, old); CopyUv(source.Uvs2, uv2, old); CopyUv(source.Uvs3, uv3, old);
                                if (source.Tangents.Length == source.Vertices.Length / 3 * 4)
                                {
                                    var tangent = conversion.MultiplyVector(new Vector3(source.Tangents[old * 4],
                                        source.Tangents[old * 4 + 1], source.Tangents[old * 4 + 2])).normalized;
                                    tangents.Add(tangent.x); tangents.Add(tangent.y); tangents.Add(tangent.z);
                                    tangents.Add(source.Tangents[old * 4 + 3] * (conversion.determinant < 0f ? -1f : 1f));
                                }
                            }
                            output[i] = index;
                        }
                        submeshes.Add(output);
                    }
                    if (vertices.Count < 9 || !GameAccess.Finite(bounds.center) || !GameAccess.Finite(bounds.size)) return false;
                    target.MeshName = source.MeshName + " [batch slice " + first + "]";
                    if (worldSpace) { target.Position = new Vec3(0, 0, 0); target.Rotation = Quat.Identity; target.Scale = Vec3.One; }
                    target.Vertices = vertices.ToArray(); target.Normals = normals.ToArray(); target.Uvs = uvs.ToArray();
                    target.Uvs1 = uv1.ToArray(); target.Uvs2 = uv2.ToArray(); target.Uvs3 = uv3.ToArray(); target.Tangents = tangents.ToArray();
                    target.SubmeshTriangles = submeshes; target.Triangles = submeshes.SelectMany(indices => indices).ToArray();
                    target.BoundsCenter = GameAccess.Vec(bounds.center); target.BoundsSize = GameAccess.Vec(bounds.size);
                    target.IsBoundsProxy = false;
                    return true;
                }
                catch { return false; }
            }
            private static void CopyUv(float[] values, List<float> target, int vertex)
            {
                if (values.Length < vertex * 2 + 2) return;
                target.Add(values[vertex * 2]); target.Add(values[vertex * 2 + 1]);
            }
        }

        internal static bool Skin(SkinnedMeshRenderer renderer, Transform entityRoot, GeometrySnapshot target)
        {
            var mesh = renderer.sharedMesh;
            if (!mesh || !entityRoot || target.IsBoundsProxy) return false;
            try
            {
                var bones = renderer.bones;
                var bind = mesh.bindposes;
                if (bones.Length == 0 || bones.Length > 512 || bind.Length != bones.Length ||
                    bones.Any(bone => !bone || (bone != entityRoot && !bone.IsChildOf(entityRoot)))) return false;
                var paths = bones.Select(bone => EntityTracker.RelativePath(entityRoot, bone)).ToList();
                if (paths.Any(path => path.Length > 4096)) return false;
                var weights = mesh.boneWeights;
                if (weights.Length != mesh.vertexCount) return false;
                var indices = new int[weights.Length * 4];
                var values = new float[indices.Length];
                for (int i = 0; i < weights.Length; i++)
                {
                    var w = weights[i];
                    indices[i * 4] = w.boneIndex0; indices[i * 4 + 1] = w.boneIndex1;
                    indices[i * 4 + 2] = w.boneIndex2; indices[i * 4 + 3] = w.boneIndex3;
                    values[i * 4] = w.weight0; values[i * 4 + 1] = w.weight1;
                    values[i * 4 + 2] = w.weight2; values[i * 4 + 3] = w.weight3;
                }
                if (indices.Any(index => index < 0 || index >= bones.Length) || values.Any(value => !GameAccess.Finite(value) || value < 0 || value > 1)) return false;
                var matrices = new float[bind.Length * 16];
                for (int i = 0; i < bind.Length; i++) for (int j = 0; j < 16; j++) matrices[i * 16 + j] = bind[i][j];
                if (matrices.Any(value => !GameAccess.Finite(value))) return false;
                target.BonePaths = paths; target.BindPoses = matrices; target.BoneIndices = indices; target.BoneWeights = values;
                var rootBone = renderer.rootBone;
                if (rootBone && (rootBone == entityRoot || rootBone.IsChildOf(entityRoot)))
                    target.RootBonePath = EntityTracker.RelativePath(entityRoot, rootBone);
                var hierarchy = new HashSet<Transform>();
                foreach (var bone in bones)
                    for (var node = bone; node && node != entityRoot && node.IsChildOf(entityRoot); node = node.parent)
                        hierarchy.Add(node);
                var animator = renderer.GetComponentInParent<Animator>();
                var externalHumanoidAnimator = animator != null && animator && animator.isHuman && animator.transform != entityRoot &&
                    !animator.transform.IsChildOf(entityRoot) ? animator : null;
                if (animator != null && animator && animator.transform != entityRoot && !animator.transform.IsChildOf(entityRoot)) animator = null;
                var boneAnimator = entityRoot.GetComponentsInChildren<Animator>(true)
                    .Where(candidate => candidate && candidate.runtimeAnimatorController)
                    .Select(candidate => new { Animator = candidate,
                        Matches = bones.Count(bone => bone &&
                            (bone == candidate.transform || bone.IsChildOf(candidate.transform))),
                        Depth = EntityTracker.RelativePath(entityRoot, candidate.transform).Length })
                    .Where(candidate => candidate.Matches > 0)
                    .OrderByDescending(candidate => candidate.Matches)
                    .ThenByDescending(candidate => candidate.Depth)
                    .Select(candidate => candidate.Animator).FirstOrDefault();
                if (boneAnimator) animator = boneAnimator;
                if (!animator) animator = entityRoot.GetComponentInChildren<Animator>(true);
                if (animator != null && animator && (animator.transform == entityRoot || animator.transform.IsChildOf(entityRoot)))
                    for (var node = animator.transform; node && node != entityRoot; node = node.parent)
                        hierarchy.Add(node);
                if (hierarchy.Count > 512) return false;
                target.RigBones = hierarchy.OrderBy(node => EntityTracker.RelativePath(entityRoot, node), System.StringComparer.Ordinal)
                    .Select(node => new BonePose { Path = EntityTracker.RelativePath(entityRoot, node),
                        Position = GameAccess.Vec(node.localPosition), Rotation = GameAccess.Rot(node.localRotation),
                        Scale = GameAccess.Vec(node.localScale) }).ToList();
                if (animator != null && animator && (animator.transform == entityRoot || animator.transform.IsChildOf(entityRoot)))
                {
                    target.AnimatorPath = EntityTracker.RelativePath(entityRoot, animator.transform);
                    target.AnimatorController = animator.runtimeAnimatorController ? animator.runtimeAnimatorController.name : "";
                    target.AnimatorAvatar = animator.avatar ? animator.avatar.name : "";
                    AnimationAssetRegistry.Remember(animator);
                }
                else if (externalHumanoidAnimator != null && externalHumanoidAnimator &&
                    externalHumanoidAnimator.runtimeAnimatorController && externalHumanoidAnimator.avatar)
                {
                    // A player rig can be nested below an Animator owned by a
                    // parent prefab. A humanoid Avatar can drive the copied
                    // rig from its replay root without reproducing that parent.
                    target.AnimatorController = externalHumanoidAnimator.runtimeAnimatorController.name;
                    target.AnimatorAvatar = externalHumanoidAnimator.avatar.name;
                    AnimationAssetRegistry.Remember(externalHumanoidAnimator);
                }
                return true;
            }
            catch { return false; }
        }

        private static float[] Attribute(Mesh mesh, VertexAttribute attribute, int dimensions, Dictionary<int, byte[]> streams)
        {
            if (!mesh.HasVertexAttribute(attribute) || mesh.GetVertexAttributeDimension(attribute) < dimensions) return Array.Empty<float>();
            int stream = mesh.GetVertexAttributeStream(attribute), stride = mesh.GetVertexBufferStride(stream);
            int offset = mesh.GetVertexAttributeOffset(attribute);
            var format = mesh.GetVertexAttributeFormat(attribute);
            int size = ComponentSize(format);
            if (size == 0 || stride <= 0 || (long)stride * mesh.vertexCount > 64L * 1024 * 1024) return Array.Empty<float>();
            if (!streams.TryGetValue(stream, out var bytes))
            {
                using (var buffer = mesh.GetVertexBuffer(stream))
                {
                    if (buffer == null) return Array.Empty<float>();
                    bytes = new byte[checked(stride * mesh.vertexCount)]; buffer.GetData(bytes); streams[stream] = bytes;
                }
            }
            var output = new float[mesh.vertexCount * dimensions];
            for (int vertex = 0; vertex < mesh.vertexCount; vertex++)
                for (int d = 0; d < dimensions; d++) output[vertex * dimensions + d] = Decode(bytes, vertex * stride + offset + d * size, format);
            if (output.Any(value => !GameAccess.Finite(value))) return Array.Empty<float>();
            return output;
        }

        private static int ComponentSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                case VertexAttributeFormat.UNorm8: case VertexAttributeFormat.SNorm8: case VertexAttributeFormat.UInt8: case VertexAttributeFormat.SInt8: return 1;
                default: return 0;
            }
        }
        private static float Decode(byte[] data, int offset, VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(data, offset);
                case VertexAttributeFormat.Float16:
                    int half = BitConverter.ToUInt16(data, offset), exponent = (half >> 10) & 31, mantissa = half & 1023;
                    double value = exponent == 0 ? mantissa * Math.Pow(2, -24) : exponent == 31 ? (mantissa == 0 ? double.PositiveInfinity : double.NaN) : (1 + mantissa / 1024.0) * Math.Pow(2, exponent - 15);
                    return (float)((half & 32768) == 0 ? value : -value);
                case VertexAttributeFormat.UNorm8: return data[offset] / 255f;
                case VertexAttributeFormat.SNorm8: return Math.Max(-1, (sbyte)data[offset] / 127f);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(data, offset) / 65535f;
                case VertexAttributeFormat.SNorm16: return Math.Max(-1, BitConverter.ToInt16(data, offset) / 32767f);
                case VertexAttributeFormat.UInt8: return data[offset];
                case VertexAttributeFormat.SInt8: return (sbyte)data[offset];
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(data, offset);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(data, offset);
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(data, offset);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(data, offset);
                default: return 0;
            }
        }
        private static float[] Flatten(Vector3[] values) => values.SelectMany(value => new[] { value.x, value.y, value.z }).ToArray();
        private static float[] Flatten(Vector2[] values) => values.SelectMany(value => new[] { value.x, value.y }).ToArray();
    }
}
