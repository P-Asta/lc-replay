using System;
using System.Collections.Generic;

namespace LCReplay.Core
{
    // Conservative queue accounting, not a measurement of the process heap.
    // Array payloads dominate world records; no JSON or temporary arrays are made.
    public static class ReplayRecordMemory
    {
        public static long Estimate(ReplayRecord record)
        {
            long bytes = 512;
            if (record.Event is ReplayEvent evt)
            {
                bytes += Map(evt.Data) + Text(evt.EntityId) + Text(evt.Name) + Text(evt.Category);
                if (evt.AnimationTrack is AnimationTrackSnapshot track)
                    bytes += Array(track.Positions) + Array(track.Rotations) + Array(track.Phases) + Strings(track.BonePaths);
                if (evt.ItemMotion is ItemMotionSnapshot motion) bytes += 512 + Array(motion.Curve);
                if (evt.PostProcess is EnvironmentComponentSnapshot component) bytes += Component(component);
            }
            if (record.Frame is ReplayFrame frame)
            {
                bytes += Map(frame.State) + frame.Anchors.Count * 256L + frame.SceneRenderers.Count * 256L + frame.Particles.Count * 512L;
                foreach (var entity in frame.Entities)
                {
                    if (entity == null) continue;
                    bytes += 512 + Text(entity.Id) + Text(entity.Kind) + Text(entity.Name) + Map(entity.State) + entity.Renderers.Count * 256L;
                    foreach (var bone in entity.Bones) if (bone != null) bytes += 256 + Text(bone.Path);
                }
                foreach (var line in frame.Lines) if (line != null) bytes += 1024 + Array(line.Positions);
                bytes += frame.ParticleStyles.Count * 4096L;
            }
            if (record.World is WorldSnapshot world)
            {
                bytes += Strings(world.AssetRendererPaths) + Strings(world.AssetTerrainPaths);
                foreach (var geometry in world.Geometry)
                {
                    if (geometry == null) continue;
                    bytes += 2048 + Array(geometry.Vertices) + Array(geometry.Triangles) + Array(geometry.Normals) + Array(geometry.Tangents) +
                        Array(geometry.Uvs) + Array(geometry.Uvs1) + Array(geometry.Uvs2) + Array(geometry.Uvs3) + Array(geometry.Instances) +
                        Array(geometry.BindPoses) + Array(geometry.BoneIndices) + Array(geometry.BoneWeights) + Strings(geometry.BonePaths);
                    foreach (var triangles in geometry.SubmeshTriangles) bytes += Array(triangles);
                    foreach (var bone in geometry.RigBones) if (bone != null) bytes += 256 + Text(bone.Path);
                }
                foreach (var texture in world.Textures) if (texture != null) bytes += 512 + (texture.Png?.LongLength ?? 0);
                foreach (var material in world.Materials) if (material != null)
                {
                    bytes += 2048;
                    foreach (var property in material.Properties) if (property != null) bytes += 512 + Array(property.Values);
                }
                foreach (var fog in world.LocalFogs) if (fog != null) bytes += 2048 + (fog.MaskRgba?.LongLength ?? 0);
                bytes += world.Lights.Count * 2048L + world.Rooms.Count * 4096L + world.ParticleEmitters.Count * 4096L;
                foreach (var light in world.Lights) if (light != null)
                    foreach (var property in light.LightPipelineParameters) if (property != null)
                        bytes += 512 + Text(property.Name) + Text(property.Kind) + Array(property.Values) + Text(property.Text);
                if (world.Environment is EnvironmentSnapshot environment)
                {
                    foreach (var texture in environment.SkyFaces) if (texture != null) bytes += 512 + (texture.Png?.LongLength ?? 0);
                    foreach (var component in environment.Components) if (component != null) bytes += Component(component);
                    foreach (var pass in environment.CustomPasses) if (pass != null)
                        foreach (var property in pass.Properties) if (property != null) bytes += 512 + Array(property.Values) + Array(property.CurveKeys) + Text(property.Text);
                }
            }
            return bytes;
        }
        private static long Component(EnvironmentComponentSnapshot component)
        {
            long bytes = 512;
            foreach (var property in component.Parameters) if (property != null) bytes += 512 + Array(property.Values) + Array(property.CurveKeys) + Text(property.Text);
            return bytes;
        }
        private static long Array(Array? array) => 32 + (array?.LongLength ?? 0) * 4;
        private static long Text(string? text) => 32 + (text?.Length ?? 0) * 2L;
        private static long Strings(IEnumerable<string> values) { long bytes = 64; foreach (var value in values) bytes += 32 + Text(value); return bytes; }
        private static long Map(IDictionary<string, string> values) { long bytes = 128; foreach (var pair in values) bytes += 64 + Text(pair.Key) + Text(pair.Value); return bytes; }
    }
}
