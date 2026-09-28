using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    internal static class ParticleCapture
    {
        internal static ParticleStyleSnapshot Style(ParticleSystem system, ParticleSystemRenderer renderer)
        {
            var material = renderer.sharedMaterial;
            var streams = new List<ParticleSystemVertexStream>();
            renderer.GetActiveVertexStreams(streams);
            return new ParticleStyleSnapshot
            {
                Id = "p" + system.GetInstanceID(), Name = system.name,
                ParentName = system.transform.parent ? system.transform.parent.name : "",
                MaterialName = material ? material.name : "", ShaderName = material && material.shader ? material.shader.name : "",
                MeshName = renderer.mesh ? renderer.mesh.name : "",
                Position = GameAccess.Vec(system.transform.position), Rotation = GameAccess.Rot(system.transform.rotation),
                Time = Mathf.Clamp(Safe(system.time), 0, 86400), RandomSeed = system.randomSeed,
                RenderMode = (int)renderer.renderMode, Alignment = (int)renderer.alignment,
                VertexStreams = streams.Select(stream => (int)stream).Take(32).ToArray(),
                LengthScale = Safe(renderer.lengthScale), VelocityScale = Safe(renderer.velocityScale),
                CameraVelocityScale = Safe(renderer.cameraVelocityScale),
                Pivot = GameAccess.Vec(renderer.pivot), Scale = GameAccess.Vec(system.transform.lossyScale)
            };
        }

        private static float Safe(float value) => GameAccess.Finite(value) ? Mathf.Clamp(value, -10000, 10000) : 0;
    }
}
