using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // The game renders bee electricity with a separate ThunderAndLightning
    // component, not the FlyingBugs graph. Retain only that visual component.
    internal sealed class NativeBeeLightning
    {
        internal readonly Component Bolt;
        internal readonly GameObject Root;
        private readonly Transform[] points;
        private readonly MethodInfo trigger, reset, update, lateUpdate, cleanup;
        private System.Random random = new System.Random(73);
        private int seed = int.MinValue, mode = -1;
        private double previous = -1, nextZap;
        private int updatedFrame = -1;
        internal int Triggers { get; private set; }

        internal NativeBeeLightning(Component prefab, Transform parent, int layer)
        {
            var source = (Component)GameAccess.Read(prefab, "lightningComponent")!;
            var staging = new GameObject("Inactive bee lightning staging"); staging.SetActive(false); staging.transform.SetParent(parent, false);
            Root = Object.Instantiate(source.gameObject, staging.transform, false); Root.SetActive(false);
            foreach (var script in Root.GetComponentsInChildren<MonoBehaviour>(true))
                if (script.GetType().Namespace != "DigitalRuby.ThunderAndLightning") Object.DestroyImmediate(script);
            foreach (var audio in Root.GetComponentsInChildren<AudioSource>(true)) Object.DestroyImmediate(audio);
            foreach (var collider in Root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            Bolt = Root.GetComponent(source.GetType());
            Set("ManualMode", true); Set("AutomaticModeSeconds", 0f); Set("LifeTime", 0f);
            // Extra realtime lights are not needed for the original bolt/glow
            // meshes and could affect the connected game's lighting layers.
            Set("MaximumLightsPerBatch", 0); Set("MultiThreaded", false);
            Set("LightningOriginParticleSystem", null); Set("LightningDestinationParticleSystem", null);
            var count = (GameAccess.Read(prefab, "lightningPoints") as Transform[])?.Length ?? 4;
            points = new Transform[Math.Max(2, count)]; var path = new List<GameObject>();
            for (var i = 0; i < points.Length; i++)
            {
                var point = new GameObject("Replay bee lightning point " + i); point.transform.SetParent(Root.transform, false);
                points[i] = point.transform; path.Add(point);
            }
            Set("LightningPath", path);
            Root.transform.SetParent(parent, false); Root.transform.localPosition = Vector3.zero; Root.transform.localRotation = Quaternion.identity;
            foreach (var node in Root.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = layer;
            trigger = Bolt.GetType().GetMethod("Trigger", new[] { typeof(float) })!;
            reset = Bolt.GetType().GetMethod("Reset", Type.EmptyTypes)!;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            update = Bolt.GetType().GetMethod("Update", flags)!;
            lateUpdate = Bolt.GetType().GetMethod("LateUpdate", flags)!;
            var baseType = Bolt.GetType();
            while (baseType!.Name != "LightningBoltScript") baseType = baseType.BaseType;
            cleanup = baseType.GetMethod("OnDisable", flags)!;
            // Initialize once, including a swarm that stays idle. Drive the
            // native visual update from replay Tick so Unity cannot run Start
            // a second time, or destroy an uninitialized native thread state.
            ((Behaviour)Bolt).enabled = false;
            Root.SetActive(true);
            Bolt.GetType().GetMethod("Start", flags)!.Invoke(Bolt, null);
            // The native script keeps this subscription beyond OnDestroy.
            // A replay object does not need scene notifications.
            var sceneLoaded = baseType.GetMethod("OnSceneLoaded", flags)!;
            SceneManager.sceneLoaded -= (UnityAction<Scene, LoadSceneMode>)Delegate.CreateDelegate(typeof(UnityAction<Scene, LoadSceneMode>), Bolt, sceneLoaded);
            Object.Destroy(staging);
        }

        internal void Sync(EntitySnapshot entity, Vector3 target, double time, bool playing, float speed, Camera? camera)
        {
            var nextMode = entity.State.TryGetValue("beesZappingMode", out var value) && int.TryParse(value, out var parsed) ? parsed :
                entity.State.TryGetValue("currentBehaviourStateIndex", out value) && int.TryParse(value, out parsed) ? parsed : 0;
            var nextSeed = entity.State.TryGetValue("$beeZapSeed", out value) && int.TryParse(value, out parsed) ? parsed : 73;
            var seek = previous < 0 || time < previous - .02 || time - previous > .45;
            if (seek || nextMode != mode || nextSeed != seed)
            {
                cleanup.Invoke(Bolt, null); Root.SetActive(false); Set("AutomaticModeSeconds", 0f); Set("ManualMode", true); reset.Invoke(Bolt, null);
                random = new System.Random(nextSeed); seed = nextSeed; mode = nextMode; nextZap = time;
            }
            var alive = entity.Active && entity.State.GetValueOrDefault("isEnemyDead") != "True";
            var active = alive && playing && mode > 0 && speed <= 3;
            if (!active && Root.activeSelf) cleanup.Invoke(Bolt, null);
            Root.SetActive(active);
            if (camera) Set("Camera", camera);
            if (Root.activeInHierarchy && updatedFrame != Time.frameCount)
            { updatedFrame = Time.frameCount; update.Invoke(Bolt, null); lateUpdate.Invoke(Bolt, null); }
            if (Root.activeInHierarchy && time > previous && time >= nextZap)
            {
                foreach (var point in points) point.position = target + new Vector3((float)random.NextDouble() * 4 - 2, (float)random.NextDouble() * 4 - 2, (float)random.NextDouble() * 4 - 2);
                reset.Invoke(Bolt, null); trigger.Invoke(Bolt, new object[] { .1f / Mathf.Max(.25f, speed) }); Triggers++;
                nextZap = time + (mode == 1 ? random.Next(1, 8) * .1 : mode == 2 ? random.Next(1, 7) * .06 : random.Next(1, 5) * .04);
            }
            previous = time;
        }
        private void Set(string name, object? value)
        {
            var field = Bolt.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) field.SetValue(Bolt, value);
            else Bolt.GetType().GetProperty(name)?.SetValue(Bolt, value);
        }
    }
}
