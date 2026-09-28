using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin.Capture
{
    // Snapshot the built-in moon scene before its procedural content is created.
    // Only these stable components may be referenced instead of embedded later.
    internal sealed class SceneAssetCapture
    {
        private readonly Dictionary<int, string> renderers = new Dictionary<int, string>();
        private readonly Dictionary<int, string> terrains = new Dictionary<int, string>();
        internal string SceneName { get; }
        internal int BuildIndex { get; }

        private SceneAssetCapture(Scene scene)
        {
            SceneName = scene.name;
            BuildIndex = scene.buildIndex;
            var rendererCandidates = new List<KeyValuePair<int, string>>();
            var terrainCandidates = new List<KeyValuePair<int, string>>();
            var rendererPathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var terrainPathCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    if (renderer && SceneAssetPaths.IsStable(renderer))
                    {
                        var path = SceneAssetPaths.For(renderer);
                        if (!SceneAssetPaths.IsValidReference(path)) continue;
                        rendererCandidates.Add(new KeyValuePair<int, string>(renderer.GetInstanceID(), path));
                        rendererPathCounts[path] = rendererPathCounts.TryGetValue(path, out var count) ? count + 1 : 1;
                    }
                foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                    if (terrain)
                    {
                        var path = SceneAssetPaths.For(terrain);
                        if (!SceneAssetPaths.IsValidReference(path)) continue;
                        terrainCandidates.Add(new KeyValuePair<int, string>(terrain.GetInstanceID(), path));
                        terrainPathCounts[path] = terrainPathCounts.TryGetValue(path, out var count) ? count + 1 : 1;
                    }
            }
            foreach (var candidate in rendererCandidates)
                if (rendererPathCounts[candidate.Value] == 1) renderers[candidate.Key] = candidate.Value;
            foreach (var candidate in terrainCandidates)
                if (terrainPathCounts[candidate.Value] == 1) terrains[candidate.Key] = candidate.Value;
        }

        internal static SceneAssetCapture? TryCreate(Scene scene, string? expectedName)
        {
            if (string.IsNullOrEmpty(expectedName) || !SceneAssetPaths.IsSupportedScene(scene, expectedName)) return null;
            return new SceneAssetCapture(scene);
        }

        internal bool TryRenderer(Renderer renderer, out string path) =>
            renderers.TryGetValue(renderer.GetInstanceID(), out path!);

        internal bool TryTerrain(Terrain terrain, out string path) =>
            terrains.TryGetValue(terrain.GetInstanceID(), out path!);
    }
}
