using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Installed VFX Graph retains its mesh, texture and GPU simulation bindings.
    // Only its render component is copied; no enemy AI or live-world binder runs.
    internal sealed class NativeSwarmEffect
    {
        internal readonly VisualEffect Effect;
        internal readonly NativeBeeLightning? Lightning;
        internal Vector3 Target { get; private set; }
        internal Func<string, Vector3?>? ItemPosition { get; set; }
        private readonly Transform owner;
        private readonly bool bees;
        private readonly List<(string Property, Vector3 Offset)> targets = new List<(string, Vector3)>();
        private double previous = -1;

        internal static NativeSwarmEffect? Create(EntitySnapshot entity, Transform parent, int layer)
        {
            var prefab = PrefabAssetRegistry.ResolvePrefab("enemy:" + entity.Name);
            var source = prefab ? prefab!.GetComponentInChildren<VisualEffect>(true) : null;
            if (!source || !source!.visualEffectAsset) return null;
            return new NativeSwarmEffect(prefab!, source, parent, layer);
        }

        private NativeSwarmEffect(Component prefab, VisualEffect source, Transform parent, int layer)
        {
            owner = parent; bees = prefab.GetType().Name == "RedLocustBees";
            var staging = new GameObject("Inactive native swarm staging"); staging.SetActive(false);
            staging.transform.SetParent(parent, false);
            var obj = Object.Instantiate(source.gameObject, staging.transform, false);
            obj.SetActive(false);
            foreach (var binder in source.GetComponents<MonoBehaviour>())
                if (binder.GetType().Name == "VFXPositionBinder" && GameAccess.Read(binder, "Target") is Transform target)
                {
                    var property = GameAccess.Read(binder, "Property")?.ToString() ?? "";
                    if (property.Length != 0) targets.Add((property, prefab.transform.InverseTransformPoint(target.position)));
                }
            foreach (var component in obj.GetComponentsInChildren<Component>(true)
                .OrderBy(c => c is MonoBehaviour ? 0 : 1).ThenBy(c => c.GetType().Name == "VFXPropertyBinder" ? 1 : 0))
                if (component && !(component is Transform) && !(component is VisualEffect) && component.GetType().Name != "VFXRenderer")
                    Object.DestroyImmediate(component);
            Effect = obj.GetComponent<VisualEffect>();
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = prefab.transform.InverseTransformPoint(source.transform.position);
            obj.transform.localRotation = Quaternion.Inverse(prefab.transform.rotation) * source.transform.rotation;
            foreach (var node in obj.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = layer;
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
            { renderer.enabled = true; renderer.forceRenderingOff = false; }
            Effect.enabled = true; Effect.resetSeedOnPlay = false; Effect.startSeed = 73;
            obj.SetActive(true); Object.Destroy(staging);
            UpdateTargets(); Effect.Reinit(); Effect.Play();
            if (bees && GameAccess.Read(prefab, "lightningComponent") is Component lightning && lightning)
                Lightning = new NativeBeeLightning(prefab, parent, layer);
        }

        internal void Sync(EntitySnapshot entity, double time, bool playing, float speed, Vector3 camera, ReplayFrame? frame = null, Camera? replayCamera = null)
        {
            var alive = entity.Active && !Flag(entity, "isEnemyDead") && (bees || (camera - owner.position).sqrMagnitude < 62f * 62f);
            SetBool("Alive", alive);
            var state = entity.State.TryGetValue("currentBehaviourStateIndex", out var value) && int.TryParse(value, out var number) ? number : 0;
            if (bees) UpdateBeeTarget(entity, state, frame);
            else UpdateTargets();
            if (bees)
            {
                var idle = state == 0; var angry = state >= 2;
                SetFloat("NoiseIntensity", idle ? 3 : angry ? 35 : 16);
                SetFloat("NoiseFrequency", state == 1 ? 20 : 35);
                SetFloat("MoveToTargetSpeed", idle ? 155 : angry ? 35 : 13);
                SetFloat("MoveToTargetForce", idle ? 155 : angry ? 35 : 13);
                SetFloat("TargetRadius", idle ? .3f : 1f); SetFloat("TargetStickiness", idle ? 7 : 0);
            }
            else SetFloat("MoveToTargetForce", state == 0 ? 6 : -35);
            if (previous >= 0 && (time < previous - .02 || time - previous > .45))
            { Effect.Reinit(); Effect.Simulate(1f / 60f, 30); }
            Effect.pause = !playing; Effect.playRate = speed;
            Lightning?.Sync(entity, Target, time, playing, speed, replayCamera);
            previous = time;
        }

        private void UpdateBeeTarget(EntitySnapshot entity, int state, ReplayFrame? frame)
        {
            Target = owner.position + Vector3.up * 1.5f;
            var targetMode = entity.State.GetValueOrDefault("$beeTarget", "");
            if (targetMode == "override" && entity.State.TryGetValue("$beeTargetPosition", out var text))
            {
                var values = text.Split(',');
                if (values.Length == 3 && float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) && GameAccess.Finite(new Vector3(x, y, z))) Target = new Vector3(x, y, z);
            }
            else if (state == 0 && targetMode != "self" && frame != null)
            {
                EntitySnapshot? hive = null;
                if (entity.State.TryGetValue("hive", out var id)) hive = frame.Entities.FirstOrDefault(item => item.Id == id && item.Active);
                // Legacy captures lack the hive link. Match only a nearby hive,
                // rather than centering every swarm on another colony's hive.
                if (hive == null && !entity.State.ContainsKey("hive"))
                    hive = frame.Entities.Where(item => item.Active && item.Kind == "item" && item.Name.IndexOf("hive", StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(item => (HivePosition(item) - owner.position).sqrMagnitude).FirstOrDefault();
                if (hive != null && (targetMode == "hive" || (HivePosition(hive) - owner.position).sqrMagnitude < 25f)) Target = HivePosition(hive);
            }
            foreach (var target in targets) if (Effect.HasVector3(target.Property)) Effect.SetVector3(target.Property, Target);
        }
        private Vector3 HivePosition(EntitySnapshot hive) => ItemPosition?.Invoke(hive.Id) ?? ToVector(hive.Position);
        private static Vector3 ToVector(Vec3 value) => new Vector3(value.X, value.Y, value.Z);

        private void UpdateTargets()
        {
            foreach (var target in targets)
                if (Effect.HasVector3(target.Property)) Effect.SetVector3(target.Property, owner.TransformPoint(target.Offset));
        }
        private void SetFloat(string name, float value) { if (Effect.HasFloat(name)) Effect.SetFloat(name, value); }
        private void SetBool(string name, bool value) { if (Effect.HasBool(name)) Effect.SetBool(name, value); }
        private static bool Flag(EntitySnapshot entity, string name) => entity.State.TryGetValue(name, out var value) && string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
    }
}
