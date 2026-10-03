using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin.Capture
{
    // Snapshot the built-in moon scene before its procedural content is created.
    // Only these stable components may be referenced instead of embedded later.
    internal sealed class SceneAssetCapture
    {
        private readonly Dictionary<int, string> renderers = new Dictionary<int, string>();
        private readonly List<Renderer> rendererCandidates = new List<Renderer>();
        private readonly Dictionary<int, string> terrains = new Dictionary<int, string>();
        private readonly List<Terrain> terrainCandidates = new List<Terrain>();
        private Dictionary<string, int>? rendererOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        private Dictionary<string, int>? terrainOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        private HashSet<string>? ambiguousRenderers = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string>? ambiguousTerrains = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<Transform> pending = new Queue<Transform>();
        private Dictionary<Transform, NodePath>? hierarchy = new Dictionary<Transform, NodePath>();
        internal string SceneName { get; }
        internal int BuildIndex { get; }
        internal bool Ready { get; private set; }
        internal IEnumerable<Renderer> RendererCandidates => rendererCandidates;
        internal IEnumerable<Terrain> TerrainCandidates => terrainCandidates;

        private SceneAssetCapture(Scene scene)
        {
            SceneName = scene.name;
            BuildIndex = scene.buildIndex;
            // Freeze membership at load time: procedural children created later
            // must never be mistaken for objects in the installed moon scene.
            // The transform-only walk is done here; paths, renderer and terrain
            // inspection are spread over later frames.
            var walking = new Stack<KeyValuePair<Transform, NodePath>>();
            foreach (var root in scene.GetRootGameObjects())
                walking.Push(new KeyValuePair<Transform, NodePath>(root.transform,
                    new NodePath(null, root.transform.GetSiblingIndex())));
            while (walking.Count != 0)
            {
                var entry = walking.Pop();
                pending.Enqueue(entry.Key);
                hierarchy[entry.Key] = entry.Value;
                for (var i = entry.Key.childCount - 1; i >= 0; i--)
                    walking.Push(new KeyValuePair<Transform, NodePath>(entry.Key.GetChild(i), new NodePath(entry.Key, i)));
            }
            Ready = pending.Count == 0;
            if (Ready) ReleaseIndexWork();
        }

        // Scene loading can happen in one frame. Index a few objects each later
        // frame, while the game generates the level, instead of walking the whole
        // hierarchy inside SceneManager.sceneLoaded.
        internal void Step(double milliseconds) => StepBounded(milliseconds, 64);
        internal void StepBounded(double milliseconds, int maxNodes)
        {
            if (Ready) return;
            var clock = Stopwatch.StartNew();
            do
            {
                var node = pending.Dequeue();
                if (node)
                {
                    var nodeRenderers = node.GetComponents<Renderer>();
                    var nodeTerrains = node.GetComponents<Terrain>();
                    if (nodeRenderers.Length == 0 && nodeTerrains.Length == 0) continue;
                    var path = PathFor(node);
                    foreach (var renderer in nodeRenderers)
                        if (renderer && SceneAssetPaths.IsStable(renderer))
                        {
                            Add(renderer, path, renderers, rendererOwners!, ambiguousRenderers!);
                            rendererCandidates.Add(renderer);
                        }
                    foreach (var terrain in nodeTerrains)
                        if (terrain)
                        {
                            Add(terrain, path, terrains, terrainOwners!, ambiguousTerrains!);
                            terrainCandidates.Add(terrain);
                        }
                }
            } while (pending.Count != 0 && --maxNodes > 0 && clock.Elapsed.TotalMilliseconds < milliseconds);
            Ready = pending.Count == 0;
            if (Ready)
            {
                ReleaseIndexWork();
                pending.TrimExcess();
            }
        }

        private void ReleaseIndexWork()
        {
            hierarchy = null;
            rendererOwners = null;
            terrainOwners = null;
            ambiguousRenderers = null;
            ambiguousTerrains = null;
        }

        private string PathFor(Transform node)
        {
            var indices = new Stack<int>();
            for (Transform? cursor = node; cursor != null && hierarchy!.TryGetValue(cursor, out var location);
                cursor = location.Parent)
                indices.Push(location.SiblingIndex);
            return string.Join("/", indices);
        }

        private readonly struct NodePath
        {
            internal readonly Transform? Parent;
            internal readonly int SiblingIndex;
            internal NodePath(Transform? parent, int siblingIndex)
            { Parent = parent; SiblingIndex = siblingIndex; }
        }

        private static void Add(Component component, string transformPath, Dictionary<int, string> results,
            Dictionary<string, int> owners, HashSet<string> ambiguous)
        {
            var path = SceneAssetPaths.For(component, transformPath);
            if (!SceneAssetPaths.IsValidReference(path) || ambiguous.Contains(path)) return;
            if (owners.TryGetValue(path, out var previous))
            {
                results.Remove(previous);
                owners.Remove(path);
                ambiguous.Add(path);
                return;
            }
            owners[path] = component.GetInstanceID();
            results[component.GetInstanceID()] = path;
        }

        internal static SceneAssetCapture? TryCreate(Scene scene, string? expectedName)
        {
            if (string.IsNullOrEmpty(expectedName) || !SceneAssetPaths.IsSupportedScene(scene, expectedName)) return null;
            return new SceneAssetCapture(scene);
        }

        internal bool TryRenderer(Renderer renderer, out string path)
        {
            path = "";
            return Ready && renderers.TryGetValue(renderer.GetInstanceID(), out path!);
        }

        internal bool TryTerrain(Terrain terrain, out string path)
        {
            path = "";
            return Ready && terrains.TryGetValue(terrain.GetInstanceID(), out path!);
        }
    }
}
