using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        private Scene scene;
        private AsyncOperation? loading;
        private string sceneName = "";
        private bool indoor, disposed;

        internal Scene Scene => scene;
        internal int RenderedCount => renderers.Count(entry => entry.Key && entry.Key.enabled) +
            terrains.Count(entry => entry.Key && entry.Key.enabled);
        internal bool IsLoading => loading != null;

        internal SceneAssetReplay(int replayLayer, Action changed)
        { this.replayLayer = replayLayer; this.changed = changed; }

        internal void SetWorld(WorldSnapshot world)
        {
            rendererPaths.Clear(); terrainPaths.Clear();
            hiddenRendererPaths.Clear();
            if (world.AssetScene.Length == 0 || world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count == 0)
            {
                sceneName = "";
                foreach (var renderer in renderers.Keys) if (renderer) renderer.enabled = false;
                foreach (var terrain in terrains.Keys) if (terrain) terrain.enabled = false;
                ReplayIsolation.Unregister(scene);
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
                renderers.Clear(); terrains.Clear(); scene = default;
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
            renderers.Clear(); terrains.Clear(); scene = default;
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
            ReplayIsolation.Register(scene);
            loading = null;
            ApplyVisibility();
            changed();
        }

        internal void SetIndoor(bool value)
        { indoor = value; ApplyVisibility(); }

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
                    entry.Key.enabled = rendererPaths.Contains(entry.Value);
                    entry.Key.forceRenderingOff = indoor || hiddenRendererPaths.Contains(entry.Value);
                }
            foreach (var entry in terrains)
                if (entry.Key)
                {
                    entry.Key.enabled = terrainPaths.Contains(entry.Value);
                    entry.Key.drawHeightmap = !indoor;
                }
        }

        public void Dispose()
        {
            disposed = true;
            ReplayIsolation.Unregister(scene);
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
            if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            rendererPaths.Clear(); hiddenRendererPaths.Clear(); terrainPaths.Clear(); renderers.Clear(); terrains.Clear();
        }
    }
}
