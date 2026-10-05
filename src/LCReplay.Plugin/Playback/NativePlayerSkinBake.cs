using System.Collections.Generic;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace LCReplay.Plugin.Playback
{
    // Bake the same bone/bind-pose matrices used by the game's skin renderer.
    // Unity's BakeMesh can shift detached skins with offset renderer transforms.
    internal sealed class NativePlayerSkinBake
    {
        private readonly Transform[] bones;
        private readonly Matrix4x4[] bind, matrices;
        private readonly BoneWeight[] weights;
        private readonly BoneWeight[] matrixWeights;
        private readonly Matrix4x4[] skinMatrices;
        private readonly int[] matrixIndices;
        private readonly int[] weightVertexOffsets, weightVertices, chunkMatrixOffsets;
        private readonly int[] positionRepresentatives, positionVertexOffsets, positionVertices;
        private readonly int[] weightPositionOffsets, weightPositions;
        private readonly bool groupedSkinChunks;
        private readonly Vector3[] vertices, normals, positions, directions;
        private readonly Vector3[] outlineNormals, outlineDirections, outlinePositions;
        private readonly Vector4[] tangents, tangentOutput;
        private readonly Action<int> bakeChunk, bakeOutlineChunk;
        private Matrix4x4 outlineLocalToWorld;
        private float outlineDepthX, outlineDepthY, outlineDepthZ, outlineDepthOffset;
        private float outlineOrthographicWidth, outlinePixelScale;
        private bool outlineOrthographic;
        private bool bakeOutlineTogether;
        private readonly NativeSkinKernel? nativeKernel;
        private static readonly ParallelOptions SkinWorkers = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
        private const int ChunkSize = 2048;

        internal NativePlayerSkinBake(SkinnedMeshRenderer source)
        {
            var mesh = source.sharedMesh;
            bones = source.bones; bind = mesh.bindposes; weights = mesh.boneWeights;
            vertices = mesh.vertices; normals = mesh.normals; tangents = mesh.tangents;
            // UV seams and hard edges duplicate vertices without changing their
            // bone influences. Blend each exact weight set once per pose; the
            // channels still use the original per-vertex skinning arithmetic.
            var weightIndices = new Dictionary<BoneWeight, int>();
            var uniqueWeights = new List<BoneWeight>();
            matrixIndices = new int[weights.Length];
            for (var i = 0; i < weights.Length; i++)
            {
                if (!weightIndices.TryGetValue(weights[i], out var index))
                {
                    index = uniqueWeights.Count;
                    weightIndices.Add(weights[i], index);
                    uniqueWeights.Add(weights[i]);
                }
                matrixIndices[i] = index;
            }
            matrixWeights = uniqueWeights.ToArray();
            skinMatrices = new Matrix4x4[matrixWeights.Length];
            // Keep each weight group and its vertices in the same worker. This
            // also parallelizes matrix blending without a second worker barrier.
            weightVertexOffsets = new int[matrixWeights.Length + 1];
            for (var i = 0; i < matrixIndices.Length; i++) weightVertexOffsets[matrixIndices[i] + 1]++;
            for (var i = 1; i < weightVertexOffsets.Length; i++) weightVertexOffsets[i] += weightVertexOffsets[i - 1];
            weightVertices = new int[matrixIndices.Length];
            var writeOffsets = (int[])weightVertexOffsets.Clone();
            for (var i = 0; i < matrixIndices.Length; i++) weightVertices[writeOffsets[matrixIndices[i]]++] = i;
            // A hard edge or UV seam can have different surface normals and
            // tangents at the same weighted point. Its position and smoothed
            // outline are still identical. Share only those two channels; keep
            // every original normal/tangent and the mesh topology unchanged.
            var pointIndices = new Dictionary<WeightedPoint, int>();
            var pointRepresentatives = new List<int>();
            var vertexPoints = new int[vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var key = new WeightedPoint(matrixIndices[i], vertices[i]);
                if (!pointIndices.TryGetValue(key, out var point))
                {
                    point = pointRepresentatives.Count;
                    pointIndices.Add(key, point);
                    pointRepresentatives.Add(i);
                }
                vertexPoints[i] = point;
            }
            positionRepresentatives = pointRepresentatives.ToArray();
            positionVertexOffsets = new int[positionRepresentatives.Length + 1];
            for (var i = 0; i < vertexPoints.Length; i++) positionVertexOffsets[vertexPoints[i] + 1]++;
            for (var i = 1; i < positionVertexOffsets.Length; i++) positionVertexOffsets[i] += positionVertexOffsets[i - 1];
            positionVertices = new int[vertices.Length];
            writeOffsets = (int[])positionVertexOffsets.Clone();
            for (var i = 0; i < vertexPoints.Length; i++) positionVertices[writeOffsets[vertexPoints[i]]++] = i;
            weightPositionOffsets = new int[matrixWeights.Length + 1];
            for (var i = 0; i < positionRepresentatives.Length; i++) weightPositionOffsets[matrixIndices[positionRepresentatives[i]] + 1]++;
            for (var i = 1; i < weightPositionOffsets.Length; i++) weightPositionOffsets[i] += weightPositionOffsets[i - 1];
            weightPositions = new int[positionRepresentatives.Length];
            writeOffsets = (int[])weightPositionOffsets.Clone();
            for (var i = 0; i < positionRepresentatives.Length; i++) weightPositions[writeOffsets[matrixIndices[positionRepresentatives[i]]]++] = i;
            var chunkCount = Math.Max(1, (vertices.Length + ChunkSize - 1) / ChunkSize);
            chunkMatrixOffsets = new int[chunkCount + 1];
            var totalWork = vertices.Length + matrixWeights.Length * 4;
            var accumulatedWork = 0;
            var largestGroupWork = 0;
            var nextChunk = 1;
            for (var i = 0; i < matrixWeights.Length; i++)
            {
                var groupWork = weightVertexOffsets[i + 1] - weightVertexOffsets[i] + 4;
                accumulatedWork += groupWork;
                largestGroupWork = Math.Max(largestGroupWork, groupWork);
                while (nextChunk < chunkCount && accumulatedWork >= (long)totalWork * nextChunk / chunkCount)
                    chunkMatrixOffsets[nextChunk++] = i + 1;
            }
            while (nextChunk <= chunkCount) chunkMatrixOffsets[nextChunk++] = matrixWeights.Length;
            // A custom skin can put almost every vertex on one bone. Such a
            // group would monopolize one worker; retain vertex chunks for it.
            groupedSkinChunks = largestGroupWork <= 2L * totalWork / chunkCount;
            matrices = new Matrix4x4[bones.Length]; positions = new Vector3[vertices.Length];
            directions = normals.Length == vertices.Length ? new Vector3[vertices.Length] : new Vector3[0];
            tangentOutput = tangents.Length == vertices.Length ? new Vector4[vertices.Length] : new Vector4[0];
            outlineNormals = SmoothOutlineNormals(vertices, normals);
            outlineDirections = new Vector3[vertices.Length];
            outlinePositions = new Vector3[vertices.Length];
            bakeChunk = BakeChunk;
            bakeOutlineChunk = BakeOutlineChunk;
            nativeKernel = NativeSkinKernel.Create(matrices, skinMatrices, matrixWeights, vertices, normals, positions,
                directions, outlineNormals, outlineDirections, outlinePositions, tangents, tangentOutput,
                positionRepresentatives, positionVertexOffsets, positionVertices, weightPositionOffsets, weightPositions);
        }

        internal void Bake(Mesh target, Transform space)
        {
            bakeOutlineTogether = false;
            BakeSkin(target, space);
        }

        internal void BakeWithOutline(Mesh target, Mesh outlineTarget, Transform space, Camera camera)
        {
            PrepareOutline(space, camera);
            bakeOutlineTogether = true;
            try { BakeSkin(target, space); }
            finally { bakeOutlineTogether = false; }
            outlineTarget.SetVertices(outlinePositions, 0, outlinePositions.Length, MeshUpdateFlags.DontRecalculateBounds);
            outlineTarget.RecalculateBounds();
        }

        private void BakeSkin(Mesh target, Transform space)
        {
            var inverse = space.worldToLocalMatrix;
            for (var i = 0; i < bones.Length; i++) matrices[i] = inverse * bones[i].localToWorldMatrix * bind[i];
            // All Unity object access stays on the caller. Worker chunks only
            // transform owned numeric arrays, then join before mesh upload.
            // Native ranges share the same disjoint group partition. Pin once
            // around the worker join, and release before any Unity mesh upload.
            var nativeBegun = (groupedSkinChunks || vertices.Length < ChunkSize * 2 || SkinWorkers.MaxDegreeOfParallelism <= 1) &&
                nativeKernel?.Begin(bakeOutlineTogether, outlineLocalToWorld, outlineDepthX, outlineDepthY,
                    outlineDepthZ, outlineDepthOffset, outlineOrthographic, outlineOrthographicWidth, outlinePixelScale) == true;
            try
            {
                if (vertices.Length >= ChunkSize * 2 && SkinWorkers.MaxDegreeOfParallelism > 1)
                {
                    if (!groupedSkinChunks)
                        for (var i = 0; i < matrixWeights.Length; i++) skinMatrices[i] = SkinMatrix(matrixWeights[i]);
                    Parallel.For(0, chunkMatrixOffsets.Length - 1, SkinWorkers, bakeChunk);
                }
                else BakeWeightRange(0, matrixWeights.Length);
            }
            finally { if (nativeBegun) nativeKernel!.End(); }
            // Bounds are computed once after every channel is uploaded. Keep
            // the normal mesh-user notifications so HDRP observes each pose.
            target.SetVertices(positions, 0, positions.Length, MeshUpdateFlags.DontRecalculateBounds);
            if (directions.Length != 0) target.SetNormals(directions, 0, directions.Length, MeshUpdateFlags.DontRecalculateBounds);
            if (tangentOutput.Length != 0) target.SetTangents(tangentOutput, 0, tangentOutput.Length, MeshUpdateFlags.DontRecalculateBounds);
            target.RecalculateBounds();
        }

        private void BakeChunk(int chunk)
        {
            if (groupedSkinChunks) BakeWeightRange(chunkMatrixOffsets[chunk], chunkMatrixOffsets[chunk + 1]);
            else BakeRange(chunk * ChunkSize, Math.Min(vertices.Length, (chunk + 1) * ChunkSize));
        }

        private void BakeWeightRange(int start, int end)
        {
            if (nativeKernel?.TryBakeRange(start, end) == true) return;
            for (var group = start; group < end; group++)
            {
                skinMatrices[group] = SkinMatrix(matrixWeights[group]);
                ref var matrix = ref skinMatrices[group];
                var pointEnd = weightPositionOffsets[group + 1];
                for (var pointIndex = weightPositionOffsets[group]; pointIndex < pointEnd; pointIndex++)
                {
                    var point = weightPositions[pointIndex];
                    var representative = positionRepresentatives[point];
                    var position = matrix.MultiplyPoint3x4(vertices[representative]);
                    var outlineDirection = matrix.MultiplyVector(outlineNormals[representative]);
                    var outlinePosition = bakeOutlineTogether ? OutlinePosition(position, outlineDirection) : default;
                    var vertexEnd = positionVertexOffsets[point + 1];
                    for (var vertex = positionVertexOffsets[point]; vertex < vertexEnd; vertex++)
                    {
                        var i = positionVertices[vertex];
                        positions[i] = position;
                        outlineDirections[i] = outlineDirection;
                        if (bakeOutlineTogether) outlinePositions[i] = outlinePosition;
                        BakeSurface(i, ref matrix);
                    }
                }
            }
        }

        private void BakeRange(int start, int end)
        {
            for (var i = start; i < end; i++)
                BakeVertex(i, ref skinMatrices[matrixIndices[i]]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BakeVertex(int i, ref Matrix4x4 matrix)
        {
            positions[i] = matrix.MultiplyPoint3x4(vertices[i]);
            outlineDirections[i] = matrix.MultiplyVector(outlineNormals[i]);
            if (bakeOutlineTogether) outlinePositions[i] = OutlinePosition(positions[i], outlineDirections[i]);
            BakeSurface(i, ref matrix);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BakeSurface(int i, ref Matrix4x4 matrix)
        {
            if (directions.Length != 0) directions[i] = matrix.MultiplyVector(normals[i]).normalized;
            if (tangentOutput.Length != 0)
            {
                var t = tangents[i]; var direction = matrix.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                tangentOutput[i] = new Vector4(direction.x, direction.y, direction.z, t.w);
            }
        }

        // A scaled copy expands limbs away from the body center and changes the
        // silhouette when crouching. Offset along the same animated surface
        // instead, with a visible multi-pixel silhouette and a modest world-space limit.
        internal void BakeOutline(Mesh target, Transform space, Camera camera)
        {
            PrepareOutline(space, camera);
            if (positions.Length >= ChunkSize * 2 && SkinWorkers.MaxDegreeOfParallelism > 1)
                Parallel.For(0, (positions.Length + ChunkSize - 1) / ChunkSize, SkinWorkers, bakeOutlineChunk);
            else BakeOutlineRange(0, positions.Length);
            target.SetVertices(outlinePositions, 0, outlinePositions.Length, MeshUpdateFlags.DontRecalculateBounds);
            target.RecalculateBounds();
        }

        private void PrepareOutline(Transform space, Camera camera)
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
            outlineLocalToWorld = localToWorld;
            outlineDepthX = depthX; outlineDepthY = depthY; outlineDepthZ = depthZ; outlineDepthOffset = depthOffset;
            outlineOrthographic = orthographic; outlineOrthographicWidth = orthographicWidth; outlinePixelScale = pixelScale;
        }

        private void BakeOutlineChunk(int chunk) => BakeOutlineRange(chunk * ChunkSize, Math.Min(positions.Length, (chunk + 1) * ChunkSize));

        private void BakeOutlineRange(int start, int end)
        {
            for (var i = start; i < end; i++)
                outlinePositions[i] = OutlinePosition(positions[i], outlineDirections[i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 OutlinePosition(Vector3 position, Vector3 direction)
        {
            var depth = position.x * outlineDepthX + position.y * outlineDepthY + position.z * outlineDepthZ + outlineDepthOffset;
            var width = Mathf.Clamp(outlineOrthographic ? outlineOrthographicWidth : depth * outlinePixelScale, .001f, .014f);
            var worldDirection = outlineLocalToWorld.MultiplyVector(direction);
            var length = worldDirection.magnitude;
            // inverse(M) * normalize(M * d) = d / |M * d|.
            // This preserves the original non-uniform-scale behavior and
            // avoids another transform for every outline vertex.
            var offset = length > .00001f ? width / length : 0f;
            return new Vector3(position.x + direction.x * offset,
                position.y + direction.y * offset, position.z + direction.z * offset);
        }

        private readonly struct WeightedPoint : IEquatable<WeightedPoint>
        {
            private readonly int weight;
            private readonly Vector3 position;
            internal WeightedPoint(int weight, Vector3 position) { this.weight = weight; this.position = position; }
            public bool Equals(WeightedPoint other) => weight == other.weight && position.Equals(other.position);
            public override bool Equals(object? other) => other is WeightedPoint point && Equals(point);
            public override int GetHashCode() => unchecked(weight * 397 ^ position.GetHashCode());
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
