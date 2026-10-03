using System.Collections.Generic;
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
        private readonly Vector3[] outlineNormals, outlinePositions;
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
            outlinePositions = new Vector3[vertices.Length];
        }

        internal void Bake(Mesh target, Transform space)
        {
            var inverse = space.worldToLocalMatrix;
            for (var i = 0; i < bones.Length; i++) matrices[i] = inverse * bones[i].localToWorldMatrix * bind[i];
            for (var i = 0; i < vertices.Length; i++)
            {
                var weight = weights[i];
                positions[i] = Point(vertices[i], weight);
                if (directions.Length != 0) directions[i] = Direction(normals[i], weight).normalized;
                if (tangentOutput.Length != 0)
                {
                    var t = tangents[i]; var direction = Direction(new Vector3(t.x, t.y, t.z), weight).normalized;
                    tangentOutput[i] = new Vector4(direction.x, direction.y, direction.z, t.w);
                }
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
            var inverse = space.worldToLocalMatrix;
            var view = camera.transform;
            var pixelScale = camera.orthographic ? 0f : 2f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * .0025f;
            for (var i = 0; i < positions.Length; i++)
            {
                var point = localToWorld.MultiplyPoint3x4(positions[i]);
                var depth = Vector3.Dot(point - view.position, view.forward);
                var width = Mathf.Clamp(camera.orthographic ? camera.orthographicSize * .005f : depth * pixelScale, .001f, .014f);
                var normal = localToWorld.MultiplyVector(Direction(outlineNormals[i], weights[i])).normalized;
                outlinePositions[i] = positions[i] + inverse.MultiplyVector(normal * width);
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

        private Vector3 Point(Vector3 point, BoneWeight w) =>
            matrices[w.boneIndex0].MultiplyPoint3x4(point) * w.weight0 + matrices[w.boneIndex1].MultiplyPoint3x4(point) * w.weight1 +
            matrices[w.boneIndex2].MultiplyPoint3x4(point) * w.weight2 + matrices[w.boneIndex3].MultiplyPoint3x4(point) * w.weight3;
        private Vector3 Direction(Vector3 direction, BoneWeight w) =>
            matrices[w.boneIndex0].MultiplyVector(direction) * w.weight0 + matrices[w.boneIndex1].MultiplyVector(direction) * w.weight1 +
            matrices[w.boneIndex2].MultiplyVector(direction) * w.weight2 + matrices[w.boneIndex3].MultiplyVector(direction) * w.weight3;
    }
}
