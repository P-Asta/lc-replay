using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Playables;

namespace LCReplay.Plugin.Playback
{
    // Uses the installed game's baked moon scene as render-only scenery. Game
    // behaviours and colliders are disabled before their first Update/Start.
    internal sealed class SceneAssetReplay : IDisposable
    {
        private readonly int replayLayer;
        private readonly Action changed;
        private readonly HashSet<string> rendererPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> hiddenRendererPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> terrainPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<Renderer, string> renderers = new Dictionary<Renderer, string>();
        private readonly Dictionary<Terrain, string> terrains = new Dictionary<Terrain, string>();
        private readonly HashSet<string> pendingScenes = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Animator> suns = new List<Animator>();
        private readonly List<Light> sunLights = new List<Light>();
        private readonly Dictionary<Light, Light> sunTargets = new Dictionary<Light, Light>();
        private int sunTargetSignature;
        private float lastSunTime = -1f;
        private Scene scene;
        private AsyncOperation? loading;
        private string sceneName = "";
        private bool indoor, fogEnabled = true, suspended, disposed;

        internal Scene Scene => scene;
        internal int RenderedCount => renderers.Count(entry => entry.Key && entry.Key.enabled) +
            terrains.Count(entry => entry.Key && entry.Key.enabled);
        internal bool IsLoading => sceneName.Length != 0 && pendingScenes.Contains(sceneName);

        internal SceneAssetReplay(int replayLayer, Action changed)
        { this.replayLayer = replayLayer; this.changed = changed; }

        internal void SetWorld(WorldSnapshot world)
        {
            rendererPaths.Clear(); terrainPaths.Clear();
            hiddenRendererPaths.Clear();
            if (world.AssetScene.Length == 0 || world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count == 0)
            {
                sceneName = "";
                loading = null;
                foreach (var renderer in renderers.Keys) if (renderer) renderer.enabled = false;
                foreach (var terrain in terrains.Keys) if (terrain) terrain.enabled = false;
                ReplayIsolation.Unregister(scene);
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
                renderers.Clear(); terrains.Clear(); ClearSun(); scene = default;
                return;
            }
            if (world.AssetGameVersion != Application.version || world.AssetBuildIndex < 0)
                throw new InvalidDataException("This replay's moon scenery requires Lethal Company version " + world.AssetGameVersion + ".");
            var scenePath = SceneUtility.GetScenePathByBuildIndex(world.AssetBuildIndex);
            if (!string.Equals(Path.GetFileNameWithoutExtension(scenePath), world.AssetScene, StringComparison.Ordinal))
                throw new InvalidDataException("The recorded moon scene " + world.AssetScene + " is unavailable in this game build.");
            foreach (var path in world.AssetRendererPaths) rendererPaths.Add(path);
            foreach (var path in world.AssetTerrainPaths) terrainPaths.Add(path);
            if (sceneName == world.AssetScene)
            { ApplyVisibility(); return; }
            foreach (var renderer in renderers.Keys) if (renderer) renderer.enabled = false;
            foreach (var terrain in terrains.Keys) if (terrain) terrain.enabled = false;
            ReplayIsolation.Unregister(scene);
            if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            renderers.Clear(); terrains.Clear(); ClearSun(); scene = default;
            sceneName = world.AssetScene;
            if (pendingScenes.Contains(sceneName)) return;
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded += OnSceneLoaded;
            pendingScenes.Add(sceneName);
            loading = SceneManager.LoadSceneAsync(world.AssetBuildIndex, LoadSceneMode.Additive);
            if (loading == null)
            {
                pendingScenes.Remove(sceneName);
                if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
                sceneName = "";
                throw new InvalidOperationException("Recorded moon scene could not be loaded from the installed game.");
            }
        }

        private void OnSceneLoaded(Scene loaded, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive || !pendingScenes.Remove(loaded.name)) return;
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
            var current = !disposed && loaded.name == sceneName;
            foreach (var root in loaded.GetRootGameObjects())
            {
                // Isolate the hierarchy in one traversal instead of seven recursive
                // component searches. No work is yielded before scripts are disabled.
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (!component) continue;
                    switch (component)
                    {
                        case MonoBehaviour behaviour: behaviour.enabled = false; break;
                        case Collider collider: collider.enabled = false; break;
                        case Camera camera: camera.enabled = false; break;
                        case Light light: light.enabled = false; break;
                        case AudioSource audio: audio.enabled = false; break;
                        case Animator animator:
                            if (current && animator.runtimeAnimatorController && animator.parameters.Any(parameter =>
                                parameter.name == "timeOfDay" && parameter.type == AnimatorControllerParameterType.Float))
                            {
                                animator.fireEvents = false; animator.applyRootMotion = false;
                                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                                suns.Add(animator);
                            }
                            else animator.enabled = false;
                            break;
                        case Renderer renderer:
                            renderer.enabled = false;
                            if (current)
                            {
                                renderer.gameObject.layer = replayLayer;
                                renderers.Add(renderer, SceneAssetPaths.For(renderer));
                            }
                            break;
                        case Terrain terrain:
                            terrain.enabled = false;
                            if (current)
                            {
                                terrain.gameObject.layer = replayLayer;
                                terrains.Add(terrain, SceneAssetPaths.For(terrain));
                            }
                            break;
                    }
                }
            }
            if (!current) { SceneManager.UnloadSceneAsync(loaded); return; }
            scene = loaded;
            foreach (var sun in suns) sunLights.AddRange(sun.GetComponentsInChildren<Light>(true).Where(light => light.type == LightType.Directional));
            ReplayIsolation.Register(scene);
            loading = null;
            ApplyVisibility();
            changed();
        }

        private void ClearSun()
        { suns.Clear(); sunLights.Clear(); sunTargets.Clear(); sunTargetSignature = 0; lastSunTime = -1f; }

        // Older recordings already contain the game clock. Evaluate the moon's
        // installed sun curves in this isolated scene, never the live round.
        internal void ApplyTimeOfDay(double normalized, IReadOnlyList<KeyValuePair<Light, LightSnapshot>> targets)
        {
            if (suspended || suns.Count == 0 || targets.Count == 0) return;
            var signature = targets[0].Key ? targets[0].Key.GetInstanceID() : 0;
            if (signature != sunTargetSignature)
            {
                sunTargetSignature = signature; sunTargets.Clear(); lastSunTime = -1f;
                foreach (var source in sunLights.Where(light => light))
                {
                    var candidates = targets.Where(pair => pair.Key && !pair.Value.IsInterior && pair.Value.Type == "Directional" &&
                        !sunTargets.ContainsValue(pair.Key)).ToArray();
                    var match = candidates.FirstOrDefault(pair => pair.Value.Name == source.name);
                    if (!match.Key) match = candidates.OrderBy(pair =>
                        (pair.Value.Shadows == (source.shadows != LightShadows.None) ? 0 : 10) +
                        Math.Abs(Math.Log(1 + pair.Value.Intensity) - Math.Log(1 + source.intensity))).FirstOrDefault();
                    if (match.Key) sunTargets[source] = match.Key;
                }
            }
            var value = Mathf.Clamp((float)normalized, 0f, .99f);
            if (value == lastSunTime) return;
            lastSunTime = value;
            foreach (var sun in suns.Where(animator => animator))
            {
                sun.enabled = true; sun.speed = 1f;
                sun.SetFloat("timeOfDay", value);
                sun.Update(0f);
                if (sun.playableGraph.IsValid()) sun.playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            }
            foreach (var pair in sunTargets) if (pair.Key && pair.Value)
            {
                SunLighting.Capture(pair.Key, normalized).Apply(pair.Value);
                pair.Key.enabled = false;
            }
        }

        internal void SetIndoor(bool value)
        { indoor = value; ApplyVisibility(); }

        internal void SetFogEnabled(bool value)
        { fogEnabled = value; ApplyVisibility(); }

        internal void SetSuspended(bool value)
        { if (suspended != value) { suspended = value; ApplyVisibility(); } }

        internal void SetHiddenRendererPaths(IEnumerable<string> paths)
        {
            hiddenRendererPaths.Clear();
            foreach (var path in paths) hiddenRendererPaths.Add(path);
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            foreach (var entry in renderers)
                if (entry.Key)
                {
                    // Later exterior records can introduce more installed surfaces.
                    // Keep the whole scene lookup, not only the first record's subset.
                    entry.Key.enabled = !suspended && rendererPaths.Contains(entry.Value);
                    entry.Key.forceRenderingOff = indoor || hiddenRendererPaths.Contains(entry.Value) ||
                        !fogEnabled && (entry.Key.name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            entry.Key.sharedMaterials.Any(material => material &&
                                material.name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0));
                }
            foreach (var entry in terrains)
                if (entry.Key)
                {
                    entry.Key.enabled = !suspended && terrainPaths.Contains(entry.Value);
                    entry.Key.drawHeightmap = !indoor;
                }
        }

        public void Dispose()
        {
            disposed = true;
            sceneName = "";
            loading = null;
            ReplayIsolation.Unregister(scene);
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
            if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            rendererPaths.Clear(); hiddenRendererPaths.Clear(); terrainPaths.Clear(); renderers.Clear(); terrains.Clear();
            ClearSun();
        }
    }
}
