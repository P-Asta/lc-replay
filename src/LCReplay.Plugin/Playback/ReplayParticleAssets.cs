using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    /// <summary>Copies installed visual-only particle templates while their hierarchy is inactive.</summary>
    internal sealed class ReplayParticleAssets : IDisposable
    {
        private readonly Transform staging;
        private readonly int layer;
        private readonly ParticleSystem[] sources;
        private readonly Dictionary<string, ParticleSystem?> templates = new Dictionary<string, ParticleSystem?>(StringComparer.Ordinal);
        private readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);

        internal ReplayParticleAssets(Transform root, int replayLayer)
        {
            layer = replayLayer;
            var obj = new GameObject("Inactive replay effect staging");
            obj.SetActive(false);
            obj.transform.SetParent(root, false);
            staging = obj.transform;
            sources = Resources.FindObjectsOfTypeAll<ParticleSystem>().Where(source => source &&
                !source.transform.IsChildOf(root)).ToArray();
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                if (mesh && !meshes.ContainsKey(mesh.name)) meshes.Add(mesh.name, mesh);
        }

        internal ParticleSystem Create(Transform parent, ParticleStyleSnapshot? style, string legacyName,
            string materialId, ReplayAppearance appearance, Material? fallback, bool sampled, out bool native)
        {
            var source = Find(style, legacyName);
            ParticleSystem effect;
            native = source;
            if (source)
            {
                // The staging parent is inactive before Instantiate. No cloned gameplay
                // behaviour gets Awake/OnEnable; remove it before activating the effect.
                var obj = Object.Instantiate(source!.gameObject, staging, false);
                obj.SetActive(false);
                for (var index = obj.transform.childCount - 1; index >= 0; index--)
                    Object.DestroyImmediate(obj.transform.GetChild(index).gameObject);
                foreach (var component in obj.GetComponents<Component>())
                    if (component && !(component is Transform) && !(component is ParticleSystem) && !(component is ParticleSystemRenderer))
                        Object.DestroyImmediate(component);
                effect = obj.GetComponent<ParticleSystem>();
                effect.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var collision = effect.collision; collision.enabled = false;
                var trigger = effect.trigger; trigger.enabled = false;
                var subEmitters = effect.subEmitters; subEmitters.enabled = false;
                var lights = effect.lights; lights.enabled = false;
                var external = effect.externalForces; external.enabled = false;
            }
            else
            {
                var obj = new GameObject("Recorded effect " + legacyName);
                obj.SetActive(false);
                obj.transform.SetParent(staging, false);
                effect = obj.AddComponent<ParticleSystem>();
            }
            effect.gameObject.name = "Recorded effect " + legacyName;
            effect.gameObject.layer = layer;
            effect.gameObject.hideFlags = HideFlags.DontSave;
            effect.transform.SetParent(parent, false);
            var renderer = effect.GetComponent<ParticleSystemRenderer>();
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (style != null)
            {
                renderer.renderMode = (ParticleSystemRenderMode)style.RenderMode;
                renderer.alignment = (ParticleSystemRenderSpace)style.Alignment;
                renderer.lengthScale = style.LengthScale;
                renderer.velocityScale = style.VelocityScale;
                renderer.cameraVelocityScale = style.CameraVelocityScale;
                renderer.pivot = Vec(style.Pivot);
                if (style.VertexStreams.Length != 0)
                    renderer.SetActiveVertexStreams(style.VertexStreams.Select(stream => (ParticleSystemVertexStream)stream).ToList());
                if (renderer.renderMode == ParticleSystemRenderMode.Mesh && !renderer.mesh && meshes.TryGetValue(style.MeshName, out var mesh))
                    renderer.mesh = mesh;
                effect.transform.localScale = Vec(style.Scale);
            }
            var material = appearance.ResolveParticleMaterial(materialId, style?.MaterialName ?? "", style?.ShaderName ?? "");
            if (material) renderer.sharedMaterial = material;
            else if (!native || !renderer.sharedMaterial) renderer.sharedMaterial = fallback;
            // A mesh effect with no installed mesh must not turn into opaque squares.
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh && !renderer.mesh) renderer.enabled = false;
            var main = effect.main;
            if (main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            {
                var recordedSpace = source ? source!.main.customSimulationSpace : null;
                var spaceObject = new GameObject("Replay particle simulation space");
                spaceObject.transform.SetParent(effect.transform, false);
                if (recordedSpace && source)
                {
                    spaceObject.transform.localPosition = source!.transform.InverseTransformPoint(recordedSpace!.position);
                    spaceObject.transform.localRotation = Quaternion.Inverse(source.transform.rotation) * recordedSpace.rotation;
                    var a = recordedSpace.lossyScale; var b = source.transform.lossyScale;
                    spaceObject.transform.localScale = new Vector3(Div(a.x, b.x), Div(a.y, b.y), Div(a.z, b.z));
                }
                main.customSimulationSpace = spaceObject.transform;
            }
            main.playOnAwake = false;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.stopAction = ParticleSystemStopAction.None;
            effect.useAutoRandomSeed = false;
            if (style != null) effect.randomSeed = style.RandomSeed;
            if (sampled || (!native && style?.Simulate == true))
            {
                main.loop = false;
                main.maxParticles = 256;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Local;
                main.simulationSpeed = 0;
                // Samples already contain evaluated color/size/rotation; applying
                // lifetime curves again would attenuate alpha and stretch twice.
                var color = effect.colorOverLifetime; color.enabled = false;
                var colorSpeed = effect.colorBySpeed; colorSpeed.enabled = false;
                var size = effect.sizeOverLifetime; size.enabled = false;
                var sizeSpeed = effect.sizeBySpeed; sizeSpeed.enabled = false;
                var rotation = effect.rotationOverLifetime; rotation.enabled = false;
                var rotationSpeed = effect.rotationBySpeed; rotationSpeed.enabled = false;
                var emission = effect.emission; emission.enabled = false;
                var shape = effect.shape; shape.enabled = false;
                // World-space samples include source scale; avoid applying it twice.
                effect.transform.localScale = Vector3.one;
            }
            effect.gameObject.SetActive(true);
            return effect;
        }

        private ParticleSystem? Find(ParticleStyleSnapshot? style, string legacyName)
        {
            var key = (style?.Name ?? legacyName) + "\n" + style?.ParentName + "\n" + style?.MaterialName + "\n" + style?.ShaderName;
            if (templates.TryGetValue(key, out var cached)) return cached;
            ParticleSystem? best = null;
            var bestScore = 0;
            foreach (var source in sources)
            {
                if (!source || source.transform.childCount > 64) continue;
                var renderer = source.GetComponent<ParticleSystemRenderer>();
                if (!renderer) continue;
                var material = renderer.sharedMaterial;
                var sameName = source.name == (style?.Name ?? legacyName);
                var sameMaterial = style != null && style.MaterialName.Length != 0 && material &&
                    Normalize(material.name) == Normalize(style.MaterialName) && material.shader && material.shader.name == style.ShaderName;
                if (style != null && style.MaterialName.Length != 0 && !sameMaterial) continue;
                if (!sameName && !sameMaterial) continue;
                var score = (sameName ? 8 : 0) + (sameMaterial ? 16 : 0) +
                    (style != null && source.transform.parent && source.transform.parent.name == style.ParentName ? 4 : 0);
                if (score <= bestScore) continue;
                best = source; bestScore = score;
            }
            templates.Add(key, best);
            return best;
        }

        private static string Normalize(string name) => name.Replace(" (Instance)", "");
        private static Vector3 Vec(Vec3 value) => new Vector3(value.X, value.Y, value.Z);
        private static float Div(float a, float b) => Math.Abs(b) < .00001f ? 1 : a / b;

        public void Dispose()
        {
            templates.Clear(); meshes.Clear();
            if (staging) Object.Destroy(staging.gameObject);
        }
    }
}
