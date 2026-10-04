using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Playables;

namespace LCReplay.Plugin.Playback
{
    // In a live session, copies inert render components from an already loaded
    // matching moon. Menu playback loads the installed scene additively and
    // disables its behaviours after scene activation, before Update/Start.
    internal sealed class SceneAssetReplay : IDisposable
    {
        private static int pendingSceneOperations;
        internal static bool HasPendingLoads => pendingSceneOperations != 0;
        private readonly int replayLayer;
        private readonly Action changed;
        private readonly HashSet<string> rendererPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> hiddenRendererPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> terrainPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<GeometrySnapshot> legacyTerrains = new List<GeometrySnapshot>();
        private readonly HashSet<string> restoredTerrains = new HashSet<string>(StringComparer.Ordinal);
        internal bool HasNativeTerrain(string geometryId) => restoredTerrains.Contains(geometryId);
        private readonly Dictionary<Renderer, string> renderers = new Dictionary<Renderer, string>();
        private readonly Dictionary<Terrain, string> terrains = new Dictionary<Terrain, string>();
        private readonly HashSet<string> pendingScenes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> clonedRendererPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> clonedTerrainPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<GameObject> liveCopies = new List<GameObject>();
        private readonly Transform? liveCloneRoot;
        private int liveSourceHandle;
        private readonly List<Animator> suns = new List<Animator>();
        private readonly List<Light> sunLights = new List<Light>();
        private readonly List<Light> lightTemplates = new List<Light>();
        private readonly List<GameObject> reflectionCopies = new List<GameObject>();
        private readonly HashSet<int> copiedReflectionIds = new HashSet<int>();
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

        internal SceneAssetReplay(int replayLayer, Action changed, Transform? liveCloneRoot = null)
        { this.replayLayer = replayLayer; this.changed = changed; this.liveCloneRoot = liveCloneRoot; }

        internal void SetWorld(WorldSnapshot world)
        {
            legacyTerrains.Clear(); restoredTerrains.Clear();
            var assetScene = world.AssetScene;
            var assetBuildIndex = world.AssetBuildIndex;
            if (assetScene.Length == 0 && world.LevelId >= 0)
            {
                var candidates = world.Geometry.Where(g => g.EntityId.Length == 0 &&
                    g.Id.StartsWith("terrain", StringComparison.Ordinal) && g.Vertices.Length >= 12).ToArray();
                var levelType = GameAccess.Type("SelectableLevel");
                if (candidates.Length != 0 && levelType != null)
                {
                    var levels = Resources.FindObjectsOfTypeAll(levelType).Where(level =>
                        GameAccess.Read(level, "levelID") is int id && id == world.LevelId).ToArray();
                    var names = levels.Select(level => GameAccess.Read(level, "sceneName") as string)
                        .Where(name => !string.IsNullOrEmpty(name)).Distinct().ToArray();
                    if (names.Length == 1)
                        for (var i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                            if (Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i)) == names[0])
                            { assetScene = names[0]!; assetBuildIndex = i; legacyTerrains.AddRange(candidates); break; }
                }
            }
            rendererPaths.Clear(); terrainPaths.Clear();
            hiddenRendererPaths.Clear();
            if (assetScene.Length == 0 || world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count + legacyTerrains.Count == 0)
            {
                sceneName = "";
                loading = null;
                ClearLiveCopies();
                foreach (var renderer in renderers.Keys) if (renderer) renderer.enabled = false;
                foreach (var terrain in terrains.Keys) if (terrain) terrain.enabled = false;
                ReplayIsolation.Unregister(scene);
                Unload(scene);
                renderers.Clear(); terrains.Clear(); ClearSun(); scene = default;
                return;
            }
            if (legacyTerrains.Count == 0 && world.AssetGameVersion != Application.version || assetBuildIndex < 0)
                throw new InvalidDataException("This replay's moon scenery requires Lethal Company version " + world.AssetGameVersion + ".");
            var scenePath = SceneUtility.GetScenePathByBuildIndex(assetBuildIndex);
            if (!string.Equals(Path.GetFileNameWithoutExtension(scenePath), assetScene, StringComparison.Ordinal))
                throw new InvalidDataException("The recorded moon scene " + world.AssetScene + " is unavailable in this game build.");
            foreach (var path in world.AssetRendererPaths) rendererPaths.Add(path);
            foreach (var path in world.AssetTerrainPaths) terrainPaths.Add(path);
            if (liveCloneRoot)
            {
                // Loading a build-index scene while Netcode is listening makes it
                // part of the scenes synchronized to joining clients. Use only
                // inert render components copied from the already loaded moon.
                if (sceneName != assetScene) ClearLiveCopies();
                sceneName = assetScene;
                var sourceWorld = new WorldSnapshot { AssetScene = assetScene, AssetBuildIndex = assetBuildIndex };
                CopyLiveScene(sourceWorld);
                ApplyVisibility();
                return;
            }
            if (sceneName == assetScene)
            { foreach (var entry in terrains) MatchLegacyTerrain(entry.Key, entry.Value); ApplyVisibility(); return; }
            foreach (var renderer in renderers.Keys) if (renderer) renderer.enabled = false;
            foreach (var terrain in terrains.Keys) if (terrain) terrain.enabled = false;
            ReplayIsolation.Unregister(scene);
            Unload(scene);
            renderers.Clear(); terrains.Clear(); ClearSun(); scene = default;
            sceneName = assetScene;
            if (pendingScenes.Contains(sceneName)) return;
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded += OnSceneLoaded;
            pendingScenes.Add(sceneName);
            loading = SceneManager.LoadSceneAsync(assetBuildIndex, LoadSceneMode.Additive);
            if (loading == null)
            {
                pendingScenes.Remove(sceneName);
                if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
                sceneName = "";
                throw new InvalidOperationException("Recorded moon scene could not be loaded from the installed game.");
            }
            Track(loading);
        }

        private static void Track(AsyncOperation operation)
        {
            pendingSceneOperations++;
            operation.completed += _ => pendingSceneOperations--;
        }

        private static void Unload(Scene target)
        {
            if (!target.IsValid() || !target.isLoaded) return;
            var operation = SceneManager.UnloadSceneAsync(target);
            if (operation != null) Track(operation);
        }

        private void CopyLiveScene(WorldSnapshot world)
        {
            if (!liveCloneRoot) return;
            var parent = liveCloneRoot!;
            Scene source = default;
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var candidate = SceneManager.GetSceneAt(index);
                if (candidate.IsValid() && candidate.isLoaded && candidate.buildIndex == world.AssetBuildIndex &&
                    candidate.name == world.AssetScene && !ReplayIsolation.IsReplayScene(candidate))
                { source = candidate; break; }
            }
            if (!source.IsValid()) return;
            if (liveSourceHandle != source.handle)
            {
                ClearLiveCopies();
                liveSourceHandle = source.handle;
            }
            var indexLights = lightTemplates.Count == 0;
            foreach (var root in source.GetRootGameObjects())
            {
                if (indexLights) lightTemplates.AddRange(root.GetComponentsInChildren<Light>(true));
                CopyReflectionProbes(root, parent, parent.gameObject.scene);
                foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                    MatchLegacyTerrain(terrain, SceneAssetPaths.For(terrain));
            }
            if (rendererPaths.IsSubsetOf(clonedRendererPaths) && terrainPaths.IsSubsetOf(clonedTerrainPaths)) return;
            var missingRenderers = rendererPaths.Count(path => !clonedRendererPaths.Contains(path));
            var missingTerrains = terrainPaths.Count(path => !clonedTerrainPaths.Contains(path));
            foreach (var root in source.GetRootGameObjects())
            {
                if (missingRenderers != 0) foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!renderer || !renderer.TryGetComponent<MeshFilter>(out var filter) || !filter.sharedMesh) continue;
                    var path = SceneAssetPaths.For(renderer);
                    if (!rendererPaths.Contains(path) || !clonedRendererPaths.Add(path)) continue;
                    var copy = new GameObject("LCReplay scenery " + renderer.name, typeof(MeshFilter), typeof(MeshRenderer));
                    liveCopies.Add(copy);
                    copy.hideFlags = HideFlags.DontSave;
                    copy.layer = replayLayer;
                    SceneManager.MoveGameObjectToScene(copy, parent.gameObject.scene);
                    copy.transform.SetParent(parent, false);
                    copy.transform.position = renderer.transform.position;
                    copy.transform.rotation = renderer.transform.rotation;
                    copy.transform.localScale = renderer.transform.lossyScale;
                    copy.GetComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    var target = copy.GetComponent<MeshRenderer>();
                    target.sharedMaterials = renderer.sharedMaterials;
                    target.shadowCastingMode = renderer.shadowCastingMode;
                    target.receiveShadows = renderer.receiveShadows;
                    target.lightmapIndex = renderer.lightmapIndex;
                    target.lightmapScaleOffset = renderer.lightmapScaleOffset;
                    renderers.Add(target, path);
                    if (--missingRenderers == 0) break;
                }
                if (missingTerrains != 0) foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                {
                    if (!terrain || !terrain.terrainData) continue;
                    var path = SceneAssetPaths.For(terrain);
                    if (!terrainPaths.Contains(path) || !clonedTerrainPaths.Add(path)) continue;
                    var copy = new GameObject("LCReplay terrain " + terrain.name, typeof(Terrain));
                    liveCopies.Add(copy);
                    copy.hideFlags = HideFlags.DontSave;
                    copy.layer = replayLayer;
                    SceneManager.MoveGameObjectToScene(copy, parent.gameObject.scene);
                    copy.transform.SetParent(parent, false);
                    copy.transform.position = terrain.transform.position;
                    copy.transform.rotation = terrain.transform.rotation;
                    copy.transform.localScale = terrain.transform.lossyScale;
                    var target = copy.GetComponent<Terrain>();
                    target.terrainData = terrain.terrainData;
                    target.materialTemplate = terrain.materialTemplate;
                    target.drawInstanced = terrain.drawInstanced;
                    target.heightmapPixelError = terrain.heightmapPixelError;
                    target.basemapDistance = terrain.basemapDistance;
                    terrains.Add(target, path);
                    if (--missingTerrains == 0) break;
                }
                if (missingRenderers == 0 && missingTerrains == 0) break;
            }
        }

        private void ClearLiveCopies()
        {
            ClearLightingCopies();
            foreach (var copy in liveCopies) if (copy) UnityEngine.Object.Destroy(copy);
            liveCopies.Clear();
            clonedRendererPaths.Clear();
            clonedTerrainPaths.Clear();
            liveSourceHandle = 0;
            if (liveCloneRoot) { renderers.Clear(); terrains.Clear(); }
        }

        private void OnSceneLoaded(Scene loaded, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive || !pendingScenes.Remove(loaded.name)) return;
            // Keep this build-index scene out of a host's scene synchronization
            // from the first callback through Unity's asynchronous unload.
            ReplayIsolation.Register(loaded);
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
            var current = !disposed && loaded.name == sceneName;
            foreach (var root in loaded.GetRootGameObjects())
            {
                // Capture author-enabled probe assets before the inert-scene
                // pass disables their HDRP behaviour. Copies never render cubes.
                if (current) CopyReflectionProbes(root, null, loaded);
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
                        case Light light:
                            if (current) lightTemplates.Add(light);
                            light.enabled = false;
                            break;
                        case ReflectionProbe reflection: reflection.enabled = false; break;
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
                                MatchLegacyTerrain(terrain, SceneAssetPaths.For(terrain));
                            }
                            break;
                    }
                }
            }
            if (!current) { Unload(loaded); return; }
            scene = loaded;
            foreach (var sun in suns) sunLights.AddRange(sun.GetComponentsInChildren<Light>(true).Where(light => light.type == LightType.Directional));
            loading = null;
            ApplyVisibility();
            changed();
        }

        private void ClearSun()
        { suns.Clear(); sunLights.Clear(); sunTargets.Clear(); sunTargetSignature = 0; lastSunTime = -1f; ClearLightingCopies(); }

        internal Light? ResolveLightTemplate(LightSnapshot snapshot, Vector3 position, Quaternion rotation)
        {
            Light? match = null;
            foreach (var light in lightTemplates)
            {
                if (!light || light.name != snapshot.Name) continue;
                var typeName = light.type == LightType.Rectangle ? "Rectangle" : light.type == LightType.Disc ? "Disc" : light.type.ToString();
                if (typeName != snapshot.Type) continue;
                if (light.type != LightType.Directional &&
                    ((light.transform.position - position).sqrMagnitude > .0001f || Quaternion.Angle(light.transform.rotation, rotation) > .1f)) continue;
                if (match) return null;
                match = light;
            }
            return match;
        }

        private void CopyReflectionProbes(GameObject root, Transform? parent, Scene destination)
        {
            foreach (var source in root.GetComponentsInChildren<ReflectionProbe>(true))
            {
                if (!source || copiedReflectionIds.Contains(source.GetInstanceID())) continue;
                var copy = ReplayReflectionProbe.Copy(source, parent, destination, replayLayer);
                if (!copy) continue;
                copiedReflectionIds.Add(source.GetInstanceID());
                reflectionCopies.Add(copy!);
            }
        }

        private void ClearLightingCopies()
        {
            lightTemplates.Clear(); copiedReflectionIds.Clear();
            foreach (var copy in reflectionCopies) if (copy) { copy.SetActive(false); UnityEngine.Object.Destroy(copy); }
            reflectionCopies.Clear();
        }

        private void MatchLegacyTerrain(Terrain terrain, string path)
        {
            if (!terrain || !terrain.terrainData) return;
            var data = terrain.terrainData;
            foreach (var geometry in legacyTerrains)
            {
                if (geometry.Name != terrain.name ||
                    Vector3.Distance(terrain.transform.position, new Vector3(geometry.Position.X, geometry.Position.Y, geometry.Position.Z)) > .05f ||
                    Quaternion.Angle(terrain.transform.rotation, new Quaternion(geometry.Rotation.X, geometry.Rotation.Y, geometry.Rotation.Z, geometry.Rotation.W)) > .05f ||
                    Vector3.Distance(terrain.transform.lossyScale, new Vector3(geometry.Scale.X, geometry.Scale.Y, geometry.Scale.Z)) > .001f) continue;
                var vertices = geometry.Vertices;
                var count = vertices.Length / 3;
                if (Math.Abs(vertices[(count - 1) * 3] - data.size.x) > .05f ||
                    Math.Abs(vertices[(count - 1) * 3 + 2] - data.size.z) > .05f) continue;
                var matches = true;
                for (var sample = 0; sample <= 8; sample++)
                {
                    var offset = (count - 1) * sample / 8 * 3;
                    var height = data.GetInterpolatedHeight(vertices[offset] / data.size.x, vertices[offset + 2] / data.size.z);
                    if (Math.Abs(height - vertices[offset + 1]) > .05f) { matches = false; break; }
                }
                if (!matches) continue;
                // The coarse legacy height field proves the installed terrain
                // matches. Use its full splat layers, holes and height map.
                restoredTerrains.Add(geometry.Id);
                terrainPaths.Add(path);
            }
        }

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
            foreach (var copy in reflectionCopies)
                if (copy && copy.activeSelf != (!suspended && !indoor)) copy.SetActive(!suspended && !indoor);
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
            ClearLiveCopies();
            ReplayIsolation.Unregister(scene);
            if (pendingScenes.Count == 0) SceneManager.sceneLoaded -= OnSceneLoaded;
            Unload(scene);
            rendererPaths.Clear(); hiddenRendererPaths.Clear(); terrainPaths.Clear(); renderers.Clear(); terrains.Clear();
            ClearSun();
        }
    }
}
