using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Structure ambience is derived from installed assets, not recorded audio.
    internal sealed class NativeStructureAudio : IDisposable
    {
        private readonly List<(AudioSource Source, float Pitch)> sources = new List<(AudioSource, float)>();

        internal NativeStructureAudio(WorldSnapshot world, Dictionary<string, GameObject> geometryObjects, Transform root)
        {
            var geometry = world.Geometry.Where(g => g.EntityId.Length == 0 && g.MeshName.Length != 0 && geometryObjects.ContainsKey(g.Id))
                .GroupBy(g => (g.Name, g.MeshName)).ToDictionary(g => g.Key, g => g.ToArray());
            var emitted = new HashSet<(string, int)>();
            // Legacy files contain no emitter identity. Rebuild only the
            // explicit BreakerBox hum from its own uniquely named native mesh.
            // A mesh/name match cannot authorize arbitrary prefab audio.
            var breakerType = GameAccess.Type("BreakerBox");
            if (breakerType == null) return;
            var meshOwners = Resources.FindObjectsOfTypeAll<MeshRenderer>()
                .Where(renderer => renderer && !renderer.gameObject.scene.IsValid() && renderer.GetComponent<MeshFilter>()?.sharedMesh)
                .GroupBy(renderer => (renderer.name, renderer.GetComponent<MeshFilter>().sharedMesh.name))
                .ToDictionary(group => group.Key, group => group.Select(renderer => renderer.GetComponent<MeshFilter>().sharedMesh).Distinct().Count());
            foreach (var breaker in Resources.FindObjectsOfTypeAll(breakerType).OfType<Component>())
            {
                if (!breaker || breaker.gameObject.scene.IsValid()) continue;
                var native = GameAccess.Read(breaker, "breakerBoxHum") as AudioSource;
                if (!native || !native!.enabled || native.gameObject.scene.IsValid() || !native.loop || !native.playOnAwake || !native.clip || !ActivePrefabPath(native.transform)) continue;
                foreach (var renderer in breaker.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (!mesh || !meshOwners.TryGetValue((renderer.name, mesh!.name), out var distinctMeshes) || distinctMeshes != 1 ||
                        !geometry.TryGetValue((renderer.name, mesh!.name), out var instances)) continue;
                    foreach (var instance in instances)
                    {
                        if (!instance.Active || sources.Count >= 128 || !emitted.Add((instance.Id, native.GetInstanceID()))) continue;
                        var parent = geometryObjects[instance.Id].transform;
                        var copy = NativeEffectCopy.Audio(native, parent, "structure " + native.clip.name);
                        copy.transform.localPosition = renderer.transform.InverseTransformPoint(native.transform.position);
                        sources.Add((copy, native.pitch));
                    }
                    // One attachment per physical structure, even if it has several renderers.
                    break;
                }
            }
        }

        private static bool ActivePrefabPath(Transform node)
        {
            for (var parent = node; parent; parent = parent.parent) if (!parent.gameObject.activeSelf) return false;
            return true;
        }

        internal static bool IsPassive(AudioSource source, Transform scope)
        {
            if (!source || !source.loop || !source.playOnAwake || !source.clip) return false;
            // Serialized playOnAwake/loop describes configuration, not an action.
            // Sources referenced by gameplay controllers require their own state.
            foreach (var component in scope.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!component) continue;
                for (var type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (field.FieldType == typeof(AudioSource) && field.GetValue(component) as AudioSource == source &&
                            !(type.Name == "BreakerBox" && field.Name == "breakerBoxHum")) return false;
                        else if (field.FieldType == typeof(AudioSource[]) && field.GetValue(component) is AudioSource[] list && list.Contains(source)) return false;
            }
            for (var parent = source.transform; parent; parent = parent.parent)
                foreach (var component in parent.GetComponents<MonoBehaviour>())
                    if (component && (component.GetType().Name == "Turret" || component.GetType().Name == "Landmine" ||
                        component.GetType().Name == "HauntedMaskItem" || component.GetType().Name == "PlayerControllerB" ||
                        IsType(component, "GrabbableObject") || IsType(component, "EnemyAI"))) return false;
            return true;
        }

        private static bool IsType(Component component, string name) => GameAccess.Type(name)?.IsAssignableFrom(component.GetType()) == true;
        internal void Sync(bool playing, float speed, Vector3 listener)
        {
            foreach (var entry in sources)
            {
                var source = entry.Source; if (!source) continue;
                var audible = source.gameObject.activeInHierarchy && (source.transform.position - listener).sqrMagnitude < source.maxDistance * source.maxDistance;
                source.mute = !playing || speed > 3 || !audible;
                source.pitch = Mathf.Clamp(entry.Pitch * speed, .1f, 3);
                if (playing && audible) { source.UnPause(); if (!source.isPlaying) source.Play(); }
                else if (source.isPlaying) source.Pause();
            }
        }

        public void Dispose()
        {
            foreach (var entry in sources) if (entry.Source) { entry.Source.Stop(); Object.Destroy(entry.Source.gameObject); }
            sources.Clear();
        }
    }
}
