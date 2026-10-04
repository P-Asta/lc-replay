using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    // Bake the same bone/bind-pose matrices used by the game's skin renderer.
    // Unity's BakeMesh can shift detached skins with offset renderer transforms.
    internal sealed class NativePlayerSkinBake
    {
        private readonly Transform[] bones;
        private readonly Matrix4x4[] bind, matrices;
        private readonly BoneWeight[] weights;
        private readonly Vector3[] vertices, normals, positions, directions;
        private readonly Vector3[] outlineNormals, outlineDirections, outlinePositions;
        private readonly Vector4[] tangents, tangentOutput;

        internal NativePlayerSkinBake(SkinnedMeshRenderer source)
        {
            var mesh = source.sharedMesh;
            bones = source.bones; bind = mesh.bindposes; weights = mesh.boneWeights;
            vertices = mesh.vertices; normals = mesh.normals; tangents = mesh.tangents;
            matrices = new Matrix4x4[bones.Length]; positions = new Vector3[vertices.Length];
            directions = normals.Length == vertices.Length ? new Vector3[vertices.Length] : new Vector3[0];
            tangentOutput = tangents.Length == vertices.Length ? new Vector4[vertices.Length] : new Vector4[0];
            outlineNormals = SmoothOutlineNormals(vertices, normals);
            outlineDirections = new Vector3[vertices.Length];
            outlinePositions = new Vector3[vertices.Length];
        }

        internal void Bake(Mesh target, Transform space)
        {
            var inverse = space.worldToLocalMatrix;
            for (var i = 0; i < bones.Length; i++) matrices[i] = inverse * bones[i].localToWorldMatrix * bind[i];
            for (var i = 0; i < vertices.Length; i++)
            {
                // All channels use the same linear skinning transform. Blend
                // its matrix once, instead of transforming every channel by
                // four bones separately (including zero-weight influences).
                var matrix = SkinMatrix(weights[i]);
                positions[i] = matrix.MultiplyPoint3x4(vertices[i]);
                if (directions.Length != 0) directions[i] = matrix.MultiplyVector(normals[i]).normalized;
                if (tangentOutput.Length != 0)
                {
                    var t = tangents[i]; var direction = matrix.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                    tangentOutput[i] = new Vector4(direction.x, direction.y, direction.z, t.w);
                }
                outlineDirections[i] = matrix.MultiplyVector(outlineNormals[i]);
            }
            target.vertices = positions;
            if (directions.Length != 0) target.normals = directions;
            if (tangentOutput.Length != 0) target.tangents = tangentOutput;
            target.RecalculateBounds();
        }

        // A scaled copy expands limbs away from the body center and changes the
        // silhouette when crouching. Offset along the same animated surface
        // instead, with a visible multi-pixel silhouette and a modest world-space limit.
        internal void BakeOutline(Mesh target, Transform space, Camera camera)
        {
            var localToWorld = space.localToWorldMatrix;
            var view = camera.transform;
            // Unity transform/camera properties cross into native code. Cache them
            // once per outline, rather than twice for every skinned vertex.
            var viewPosition = view.position;
            var viewForward = view.forward;
            var orthographic = camera.orthographic;
            var orthographicWidth = orthographic ? camera.orthographicSize * .005f : 0f;
            var pixelScale = orthographic ? 0f : 2f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * .0025f;
            var depthX = localToWorld.m00 * viewForward.x + localToWorld.m10 * viewForward.y + localToWorld.m20 * viewForward.z;
            var depthY = localToWorld.m01 * viewForward.x + localToWorld.m11 * viewForward.y + localToWorld.m21 * viewForward.z;
            var depthZ = localToWorld.m02 * viewForward.x + localToWorld.m12 * viewForward.y + localToWorld.m22 * viewForward.z;
            var depthOffset = (localToWorld.m03 - viewPosition.x) * viewForward.x +
                (localToWorld.m13 - viewPosition.y) * viewForward.y + (localToWorld.m23 - viewPosition.z) * viewForward.z;
            for (var i = 0; i < positions.Length; i++)
            {
                var position = positions[i];
                var depth = position.x * depthX + position.y * depthY + position.z * depthZ + depthOffset;
                var width = Mathf.Clamp(orthographic ? orthographicWidth : depth * pixelScale, .001f, .014f);
                var direction = outlineDirections[i];
                var worldDirection = localToWorld.MultiplyVector(direction);
                var length = worldDirection.magnitude;
                // inverse(M) * normalize(M * d) = d / |M * d|.
                // This preserves the original non-uniform-scale behavior and
                // avoids another transform for every outline vertex.
                var offset = length > .00001f ? width / length : 0f;
                outlinePositions[i] = new Vector3(position.x + direction.x * offset,
                    position.y + direction.y * offset, position.z + direction.z * offset);
            }
            target.vertices = outlinePositions;
            target.RecalculateBounds();
        }

        private static Vector3[] SmoothOutlineNormals(Vector3[] vertices, Vector3[] normals)
        {
            var sums = new Dictionary<Vector3, Vector3>();
            for (var i = 0; i < vertices.Length; i++)
            {
                var normal = normals.Length == vertices.Length ? normals[i] : Vector3.zero;
                sums.TryGetValue(vertices[i], out var sum);
                sums[vertices[i]] = sum + normal;
            }
            var result = new Vector3[vertices.Length];
            for (var i = 0; i < result.Length; i++) result[i] = sums[vertices[i]].normalized;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Matrix4x4 SkinMatrix(BoneWeight weight)
        {
            var matrix = matrices[weight.boneIndex0];
            if (weight.weight0 != 1f)
            {
                var value = weight.weight0;
                matrix.m00 *= value; matrix.m01 *= value; matrix.m02 *= value; matrix.m03 *= value;
                matrix.m10 *= value; matrix.m11 *= value; matrix.m12 *= value; matrix.m13 *= value;
                matrix.m20 *= value; matrix.m21 *= value; matrix.m22 *= value; matrix.m23 *= value;
            }
            if (weight.weight1 != 0f) Add(ref matrix, matrices[weight.boneIndex1], weight.weight1);
            if (weight.weight2 != 0f) Add(ref matrix, matrices[weight.boneIndex2], weight.weight2);
            if (weight.weight3 != 0f) Add(ref matrix, matrices[weight.boneIndex3], weight.weight3);
            return matrix;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Add(ref Matrix4x4 result, Matrix4x4 matrix, float weight)
        {
            result.m00 += matrix.m00 * weight; result.m01 += matrix.m01 * weight;
            result.m02 += matrix.m02 * weight; result.m03 += matrix.m03 * weight;
            result.m10 += matrix.m10 * weight; result.m11 += matrix.m11 * weight;
            result.m12 += matrix.m12 * weight; result.m13 += matrix.m13 * weight;
            result.m20 += matrix.m20 * weight; result.m21 += matrix.m21 * weight;
            result.m22 += matrix.m22 * weight; result.m23 += matrix.m23 * weight;
        }
    }
}
