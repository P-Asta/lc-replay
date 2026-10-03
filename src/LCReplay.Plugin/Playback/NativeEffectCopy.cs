using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    internal static class NativeEffectCopy
    {
        internal static ParticleSystem Particles(ParticleSystem source, Transform parent, int layer)
        {
            var staging = new GameObject("Inactive native particle staging"); staging.SetActive(false);
            staging.transform.SetParent(parent, false);
            var obj = Object.Instantiate(source.gameObject, staging.transform, false); obj.SetActive(false);
            foreach (var component in obj.GetComponentsInChildren<Component>(true)
                .OrderBy(c => c is MonoBehaviour ? 0 : 1).ThenBy(c => c.GetType().Name == "VFXPropertyBinder" ? 1 : 0))
                if (component && !(component is Transform) && !(component is ParticleSystem) && !(component is ParticleSystemRenderer)) Object.DestroyImmediate(component);
            foreach (var node in obj.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = layer;
            foreach (var system in obj.GetComponentsInChildren<ParticleSystem>(true))
            {
                system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = system.main; main.playOnAwake = false; main.stopAction = ParticleSystemStopAction.None;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                var collision = system.collision; collision.enabled = false;
                var trigger = system.trigger; trigger.enabled = false;
                var lights = system.lights; lights.enabled = false;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer) { renderer.enabled = true; renderer.forceRenderingOff = false; renderer.shadowCastingMode = ShadowCastingMode.Off; }
            }
            var result = obj.GetComponent<ParticleSystem>(); obj.transform.SetParent(parent, false);
            obj.SetActive(true); Object.Destroy(staging); return result;
        }

        internal static AudioSource Audio(AudioSource source, Transform parent, string name)
        {
            var obj = new GameObject("Replay native " + name) { hideFlags = HideFlags.DontSave }; obj.transform.SetParent(parent, false);
            var result = obj.AddComponent<AudioSource>(); result.playOnAwake = false;
            result.clip = source.clip; result.loop = source.loop; result.volume = source.volume; result.pitch = source.pitch;
            result.spatialBlend = 1f; result.minDistance = source.minDistance; result.maxDistance = source.maxDistance;
            result.rolloffMode = source.rolloffMode; result.spread = source.spread; result.dopplerLevel = 0;
            if (source.rolloffMode == AudioRolloffMode.Custom)
                result.SetCustomCurve(AudioSourceCurveType.CustomRolloff, source.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            result.SetCustomCurve(AudioSourceCurveType.SpatialBlend, AnimationCurve.Constant(0, 1, 1));
            return result;
        }
    }
}
