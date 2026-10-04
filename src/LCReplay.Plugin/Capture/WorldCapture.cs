using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    internal static class WorldCapture
    {
        internal sealed class CaptureJob : IDisposable
        {
            internal WorldSnapshot Snapshot = new WorldSnapshot();
            internal readonly HashSet<string> Captured = new HashSet<string>(StringComparer.Ordinal);
            internal int Deferred;
            internal string Summary = "";
            private IEnumerator<bool>? steps;
            internal AppearanceCapture? Appearance;
            internal void Start(IEnumerable<bool> sequence) => steps = sequence.GetEnumerator();
            internal bool Step(double milliseconds) => StepBounded(milliseconds, 64);
            internal bool StepBounded(double milliseconds, int maxSteps)
            {
                if (steps == null)
                {
                    if (Appearance?.StepTextures() == false) return false;
                    Summary = Summary.Replace("{texture-count}", Snapshot.Textures.Count.ToString())
                        .Replace("{omitted-texture-count}", (Appearance?.OmittedTextures ?? 0).ToString());
                    return true;
                }
                var clock = Stopwatch.StartNew();
                do
                {
                    if (steps.MoveNext()) continue;
                    steps.Dispose(); steps = null;
                    return false; // Texture readback/encoding finishes on later frames.
                } while (--maxSteps > 0 && clock.Elapsed.TotalMilliseconds < milliseconds);
                return false;
            }
            public void Dispose() { steps?.Dispose(); steps = null; Appearance?.Dispose(); Appearance = null; }
            internal WorldSnapshot? FinishAvailable()
            {
                if (steps != null) return null;
                Appearance?.FinishAvailable();
                return Snapshot;
            }
        }
        internal static IEnumerable<KeyValuePair<string, Transform>> MovingAnchors(object? round)
        {
            var elevator = GameAccess.Read(round, "elevatorTransform") as Transform;
            if (elevator) yield return new KeyValuePair<string, Transform>("ship-elevator", elevator!);
            var animator = GameAccess.Read(round, "shipAnimator") as Animator;
            if (animator && animator!.transform != elevator)
                yield return new KeyValuePair<string, Transform>("ship-visual", animator.transform);
            var network = GameAccess.Read(round, "shipAnimatorObject") as Component;
            if (network && network!.transform != elevator && (!animator || network.transform != animator!.transform))
                yield return new KeyValuePair<string, Transform>("ship-network", network.transform);
        }

        private static KeyValuePair<string, Transform>? ClosestAnchor(Transform transform, KeyValuePair<string, Transform>[] anchors)
        {
            KeyValuePair<string, Transform>? closest = null;
            var depth = int.MaxValue;
            foreach (var anchor in anchors)
            {
                if (!anchor.Value || !transform.IsChildOf(anchor.Value)) continue;
                var distance = 0;
                for (var cursor = transform; cursor != anchor.Value && cursor; cursor = cursor.parent) distance++;
                if (distance < depth) { closest = anchor; depth = distance; }
            }
            return closest;
        }
        // Scene-load callbacks do not cover procedural objects created later in the same scene.
        public static int TopologySignature()
        {
            var signature = 17;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>(true))
            {
                if (!renderer || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded
                    || renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer) continue;
                unchecked { signature += renderer.GetInstanceID() * 397 ^ (renderer.enabled && renderer.gameObject.activeInHierarchy ? 1 : 0); }
            }
            foreach (var terrain in Terrain.activeTerrains)
                if (terrain) unchecked { signature += terrain.GetInstanceID() * 31; }
            var tile = GameAccess.Read(GameAccess.Read(GameAccess.Singleton("StartOfRound"), "occlusionCuller"), "currentTile") as Object;
            if (tile) unchecked { signature += tile!.GetInstanceID() * 67; }
            return signature;
        }

        // Render-only geometry: no prefabs, scripts, colliders or serialized Unity objects.
        public static CaptureJob Begin(EntityTracker tracker, int maxObjects, int maxVertices, string layer, string captureSetId,
            ISet<string> alreadyCaptured, bool includeContext, MeshSnapshotReader.StaticBatchReader staticBatches,
            SceneAssetCapture? sceneAssets = null, ISet<string>? onlyEntityIds = null, bool onlySceneAssets = false)
        {
            if (layer != "exterior" && layer != "interior") throw new ArgumentException("Unknown capture layer.", nameof(layer));
            var job = new CaptureJob { Snapshot = new WorldSnapshot { Scene = SceneManager.GetActiveScene().name, Layer = layer, CaptureSetId = captureSetId } };
            job.Start(CaptureSteps(job, tracker, maxObjects, maxVertices, layer, alreadyCaptured, includeContext, staticBatches,
                sceneAssets, onlyEntityIds, onlySceneAssets));
            return job;
        }

        private static IEnumerable<bool> CaptureSteps(CaptureJob job, EntityTracker tracker, int maxObjects, int maxVertices,
            string layer, ISet<string> alreadyCaptured, bool includeContext, MeshSnapshotReader.StaticBatchReader staticBatches,
            SceneAssetCapture? sceneAssets, ISet<string>? onlyEntityIds, bool onlySceneAssets)
        {
            var captured = job.Captured;
            var deferred = 0;
            var world = job.Snapshot;
            var rendererAssetPaths = new HashSet<string>(StringComparer.Ordinal);
            var terrainAssetPaths = new HashSet<string>(StringComparer.Ordinal);
            if (layer == "exterior")
            {
                var round = GameAccess.Singleton("StartOfRound");
                var manager = GameAccess.Singleton("RoundManager");
                world.MapSeed = SafeInt(GameAccess.Read(round, "randomMapSeed"));
                world.LevelId = SafeInt(GameAccess.Read(round, "currentLevelID"), -1);
                world.DungeonFlow = SafeInt(GameAccess.Read(manager, "currentDungeonType"), -1);
                world.DungeonSeed = SafeInt(GameAccess.Read(GameAccess.Read(GameAccess.Read(manager, "dungeonGenerator"), "Generator"), "Seed"));
            }
            if (onlySceneAssets)
            {
                var mask = CaptureVisibility.GameplayMask();
                foreach (var renderer in sceneAssets?.RendererCandidates ?? Enumerable.Empty<Renderer>())
                {
                    yield return true;
                    if (!renderer || !renderer.gameObject.activeInHierarchy || !renderer.enabled ||
                        CaptureVisibility.IsDebugRenderer(renderer) ||
                        !CaptureVisibility.VisibleLayer(renderer, mask) ||
                        !sceneAssets!.TryRenderer(renderer, out var path) ||
                        world.AssetRendererPaths.Count >= 4096) continue;
                    AttachAssetScene(world, sceneAssets);
                    world.AssetRendererPaths.Add(path);
                    captured.Add("g" + renderer.GetInstanceID());
                    var preGenerationVisibility = tracker.Visibility;
                    tracker.Visual.Track(renderer, path, true, preGenerationVisibility.CullerManaged.Contains(renderer) ||
                        preGenerationVisibility.NaturalLods.ContainsKey(renderer) || preGenerationVisibility.TileLods.Contains(renderer));
                }
                foreach (var terrain in sceneAssets?.TerrainCandidates ?? Enumerable.Empty<Terrain>())
                {
                    yield return true;
                    if (!terrain || !terrain.enabled || !terrain.gameObject.activeInHierarchy ||
                        (mask & (1 << terrain.gameObject.layer)) == 0 ||
                        !sceneAssets!.TryTerrain(terrain, out var path) ||
                        world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count >= 4096) continue;
                    AttachAssetScene(world, sceneAssets);
                    world.AssetTerrainPaths.Add(path);
                    captured.Add("terrain" + terrain.GetInstanceID());
                }
                job.Summary = $"installed moon scene: {world.AssetRendererPaths.Count} renderer and {world.AssetTerrainPaths.Count} terrain references indexed during generation";
                yield break;
            }
            var appearance = new AppearanceCapture(world);
            job.Appearance = appearance;
            var visibility = tracker.Visibility;
            var anchors = MovingAnchors(GameAccess.Singleton("StartOfRound")).ToArray();
            var sharedMeshes = new Dictionary<int, GeometrySnapshot>();
            var viewpoints = tracker.Entries.Where(entry => entry.Kind == "player" && entry.Component &&
                GameAccess.Bool(entry.Component, "isPlayerControlled")).Select(entry => entry.Component.transform.position).ToArray();
            var owners = new Dictionary<Transform, EntityTracker.Entry>();
            foreach (var entry in tracker.Entries)
                if (entry.Component && IsVisualEntity(entry.Kind) && !owners.ContainsKey(entry.Component.transform))
                    owners.Add(entry.Component.transform, entry);
            var usedVertices = 0;
            var usedIndices = 0;
            var boxes = 0;
            var skinned = 0;
            var bakedActors = 0;
            var invisible = 0;
            var debug = 0;
            var lods = 0;
            var reused = 0;
            var interiors = 0;
            // Leave room below the reader's 96 MiB expanded-record limit for
            // higher-resolution PNGs, material parameters and environment data.
            // Reserve the last MiB for terrain.
            long estimatedMeshBytes = 0;
            const long meshJsonBudget = 45L * 1024 * 1024;
            int cameraMask = CaptureVisibility.GameplayMask();
            var skipped = 0;
            var tileType = GameAccess.Type("DunGen.Tile");
            var shipDoorAnimator = GameAccess.Find("HangarShipDoor")
                .Select(door => GameAccess.Read(door, "shipDoorsAnimator") as Animator)
                .FirstOrDefault(animator => animator);
            var tileBounds = CaptureVisibility.TileVolumes();
            var orderedRenderers = new List<RendererOrder>();
            // This iterator yields between renderers. Discovery may add an entry
            // before its next MoveNext, so freeze the selected owners first.
            var selectedEntries = onlyEntityIds == null ? Array.Empty<EntityTracker.Entry>() :
                tracker.Entries.Where(entry => onlyEntityIds.Contains(entry.Id) && entry.Component).ToArray();
            var candidates = onlyEntityIds == null ? Object.FindObjectsOfType<Renderer>(true).AsEnumerable() :
                selectedEntries.SelectMany(entry => entry.Component.GetComponentsInChildren<Renderer>(true)).Distinct();
            foreach (var candidate in candidates)
            {
                yield return true;
                if (!candidate) continue;
                var distance = DistanceToViewpoints(candidate, viewpoints);
                var size = candidate.bounds.size;
                var nearby = viewpoints.Length != 0 && Math.Max(size.x, Math.Max(size.y, size.z)) <= 60f && distance <= 400f;
                var essentialGround = RenderVisibilityPolicy.IsGroundSurface(candidate.name) ||
                    RenderVisibilityPolicy.IsRockSurface(candidate.name);
                orderedRenderers.Add(new RendererOrder(candidate,
                    candidate is SkinnedMeshRenderer ? 0 : essentialGround ? 1 : nearby ? 2 :
                    visibility.Rooms.ContainsKey(candidate) ? 4 : 3,
                    distance));
            }
            // Sort cached scalar keys, never renderer.bounds inside the comparer.
            orderedRenderers.Sort((left, right) =>
            {
                var comparison = left.Priority.CompareTo(right.Priority);
                if (comparison != 0) return comparison;
                comparison = left.Distance.CompareTo(right.Distance);
                return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
            });
            foreach (var ordered in orderedRenderers)
            {
                var renderer = ordered.Renderer;
                // One render object is the atomic unit. The recorder resumes this
                // iterator on later frames, keeping large scene scans off one frame.
                yield return true;
                if (!renderer || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded || renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer) continue;
                var rendererId = "g" + renderer.GetInstanceID();
                if (alreadyCaptured.Contains(rendererId)) continue;
                if (CaptureVisibility.IsDebugRenderer(renderer)) { debug++; continue; }
                if (!GameAccess.Finite(renderer.transform.position)
                    || !GameAccess.Finite(renderer.transform.rotation) || !GameAccess.Finite(renderer.transform.lossyScale)) continue;
                EntityTracker.Entry? owner = null;
                for (var parent = renderer.transform; parent; parent = parent.parent)
                    if (owners.TryGetValue(parent, out owner)) break;
                if (onlyEntityIds != null && (owner == null || !onlyEntityIds.Contains(owner.Id))) continue;
                var shipDoorRenderer = owner == null && shipDoorAnimator &&
                    renderer.transform.IsChildOf(shipDoorAnimator!.transform);
                var tile = tileType == null ? null : renderer.GetComponentInParent(tileType);
                var roomId = shipDoorRenderer ? "" : visibility.Rooms.TryGetValue(renderer, out var managedRoom) && managedRoom.Length != 0 ? managedRoom :
                    tile ? "r" + tile!.GetInstanceID() : SpatialRoom(renderer.bounds.center, renderer.name, tileBounds);
                var interior = roomId.Length != 0;
                if (layer == "interior" && (!interior || owner != null)) continue;
                if (layer == "exterior" && interior && owner == null) continue;
                var movingSceneRenderer = owner == null && (shipDoorRenderer || interior && IsMovingSceneRenderer(renderer, tile));
                if (layer == "exterior" && includeContext && sceneAssets != null && owner == null && !movingSceneRenderer &&
                    sceneAssets.TryRenderer(renderer, out var assetPath) &&
                    CaptureVisibility.VisibleLayer(renderer, cameraMask, visibility.CullerManaged.Contains(renderer)) &&
                    visibility.Enabled(renderer) && renderer.gameObject.activeInHierarchy &&
                    SceneAssetPaths.IsValidReference(assetPath) &&
                    world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count < 4096 &&
                    rendererAssetPaths.Add(assetPath))
                {
                    AttachAssetScene(world, sceneAssets);
                    world.AssetRendererPaths.Add(assetPath);
                    captured.Add(rendererId);
                    tracker.Visual.Track(renderer, assetPath, true, visibility.CullerManaged.Contains(renderer) ||
                        visibility.NaturalLods.ContainsKey(renderer) || visibility.TileLods.Contains(renderer));
                    continue;
                }
                if (world.Geometry.Count >= maxObjects) { skipped++; deferred++; continue; }
                if (owner?.Kind != "player" && visibility.OtherLods.Contains(renderer)) { lods++; continue; }
                // Trigger/placement volumes have enabled renderers on layers excluded by the gameplay camera.
                // Flattening every layer onto the replay layer would turn these invisible volumes into solid walls.
                if (owner?.Kind != "player" && !CaptureVisibility.VisibleLayer(renderer, cameraMask,
                    owner != null || visibility.CullerManaged.Contains(renderer) || visibility.NaturalLods.ContainsKey(renderer) ||
                    visibility.TileLods.Contains(renderer)))
                { invisible++; continue; }
                // Keep disabled entity meshes available for later per-frame visibility changes.
                if (owner == null && !movingSceneRenderer && (!visibility.Enabled(renderer) || !renderer.gameObject.activeInHierarchy)) continue;
                if (owner != null && !EntityTracker.IsCapturedRenderer(owner, renderer)) continue;
                if (owner?.Kind == "player" && !GameAccess.Bool(owner.Component, "isPlayerControlled") && !GameAccess.Bool(owner.Component, "isPlayerDead")) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter ? filter.sharedMesh : (renderer as SkinnedMeshRenderer)?.sharedMesh;
                if (mesh is null || !mesh) continue;
                var batchRenderer = renderer is MeshRenderer candidate && (candidate.isPartOfStaticBatch || candidate.subMeshStartIndex > 0) ? candidate : null;
                if (!GameAccess.Finite(mesh.bounds.center) || !GameAccess.Finite(mesh.bounds.size)) { skipped++; continue; }
                var transform = renderer.transform;
                var geometry = new GeometrySnapshot { Id = "g" + renderer.GetInstanceID(), Name = GameAccess.Scalar(renderer.name) ?? "",
                    EntityId = owner?.Id ?? "", Position = GameAccess.Vec(transform.position), Rotation = GameAccess.Rot(transform.rotation),
                    Scale = GameAccess.Vec(transform.lossyScale), BoundsCenter = GameAccess.Vec(mesh.bounds.center),
                    BoundsSize = GameAccess.Vec(mesh.bounds.size), Color = ReadColor(renderer), IsBoundsProxy = true,
                    ShadowCastingMode = renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On &&
                        renderer.sharedMaterials.All(material => !material || material.renderQueue < 3000)
                        ? -1 : (int)renderer.shadowCastingMode,
                    ReceiveShadows = renderer.receiveShadows };
                if (movingSceneRenderer)
                {
                    geometry.IsMovingSceneRenderer = true;
                    geometry.Active = renderer.gameObject.activeInHierarchy && visibility.Enabled(renderer);
                }
                if (owner == null && visibility.NaturalLods.TryGetValue(renderer, out var naturalLod))
                {
                    geometry.LodGroupId = naturalLod.Id;
                    geometry.LodLevel = naturalLod.Level;
                    geometry.LodCenter = GameAccess.Vec(naturalLod.Center);
                    geometry.LodSwitchDistance = naturalLod.Distance;
                }
                if (interior)
                {
                    geometry.IsInterior = true;
                    geometry.RoomId = roomId;
                }
                if (owner != null)
                {
                    // A sparse renderer stream uses this as its initial state.
                    // Disabled wrecks and alternate forms must not appear until
                    // a later frame happens to publish their first tombstone.
                    geometry.Active = renderer.gameObject.activeInHierarchy && visibility.Enabled(renderer);
                    var root = owner.Component.transform;
                    geometry.Position = GameAccess.Vec(root.InverseTransformPoint(transform.position));
                    geometry.Rotation = GameAccess.Rot(Quaternion.Inverse(root.rotation) * transform.rotation);
                    var scale = root.lossyScale;
                    geometry.Scale = GameAccess.Vec(new Vector3(Div(transform.lossyScale.x, scale.x), Div(transform.lossyScale.y, scale.y), Div(transform.lossyScale.z, scale.z)));
                    if ((renderer.name == "RightFang" || renderer.name == "LeftFang") &&
                        GameAccess.Read(owner.Component, "spiderNormalMesh") is SkinnedMeshRenderer spiderBody)
                    {
                        var bone = spiderBody.bones.Where(candidate => candidate && transform.IsChildOf(candidate))
                            .OrderByDescending(candidate => EntityTracker.RelativePath(root, candidate).Length)
                            .FirstOrDefault();
                        if (bone)
                        {
                            geometry.AttachedBonePath = EntityTracker.RelativePath(root, bone);
                            geometry.Position = GameAccess.Vec(bone.InverseTransformPoint(transform.position));
                            geometry.Rotation = GameAccess.Rot(Quaternion.Inverse(bone.rotation) * transform.rotation);
                            var boneScale = bone.lossyScale;
                            geometry.Scale = GameAccess.Vec(new Vector3(Div(transform.lossyScale.x, boneScale.x),
                                Div(transform.lossyScale.y, boneScale.y), Div(transform.lossyScale.z, boneScale.z)));
                        }
                    }
                }
                else if (ClosestAnchor(transform, anchors) is KeyValuePair<string, Transform> anchor)
                {
                    var root = anchor.Value;
                    geometry.AnchorId = anchor.Key;
                    geometry.Position = GameAccess.Vec(root.InverseTransformPoint(transform.position));
                    geometry.Rotation = GameAccess.Rot(Quaternion.Inverse(root.rotation) * transform.rotation);
                    var scale = root.lossyScale;
                    geometry.Scale = GameAccess.Vec(new Vector3(Div(transform.lossyScale.x, scale.x), Div(transform.lossyScale.y, scale.y), Div(transform.lossyScale.z, scale.z)));
                }
                if (owner != null && PrefabAssetRegistry.Reference(owner, renderer, geometry))
                {
                    // Native actor assets already contain the exact skin/maps.
                    // No GPU readback, PNG encoding or repeated mesh export.
                    reused++;
                    if (!PrefabAssetRegistry.NativeMaterials(renderer, geometry))
                        try { appearance.Capture(renderer, geometry); } catch { }
                }
                else if (batchRenderer == null && renderer is MeshRenderer && sharedMeshes.TryGetValue(mesh.GetInstanceID(), out var source))
                {
                    geometry.MeshSourceId = source.Id; geometry.MeshName = source.MeshName;
                    geometry.IsBoundsProxy = false;
                    var bytes = MeshJsonBytes(geometry);
                    if (estimatedMeshBytes + bytes > meshJsonBudget - 1024 * 1024) { skipped++; deferred++; continue; }
                    estimatedMeshBytes += bytes; reused++;
                    try { appearance.Capture(renderer, geometry); } catch { }
                }
                else if (batchRenderer != null ? staticBatches.Read(batchRenderer, mesh, geometry, maxVertices - usedVertices, maxVertices * 6 - usedIndices)
                    : MeshSnapshotReader.Read(mesh, geometry, maxVertices - usedVertices, maxVertices * 6 - usedIndices))
                {
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        if (owner != null && MeshSnapshotReader.Skin(skin, owner.Component.transform, geometry)) skinned++;
                        else
                        {
                            geometry.BonePaths.Clear(); geometry.BindPoses = Array.Empty<float>();
                            geometry.BoneIndices = Array.Empty<int>(); geometry.BoneWeights = Array.Empty<float>();
                            geometry.RigBones.Clear(); geometry.AnimatorPath = "";
                            geometry.AnimatorController = ""; geometry.AnimatorAvatar = "";
                            var baked = new Mesh { name = mesh.name + " (baked pose)" };
                            try
                            {
                                skin.BakeMesh(baked, false);
                                if (MeshSnapshotReader.Read(baked, geometry, maxVertices - usedVertices, maxVertices * 6 - usedIndices)) bakedActors++;
                            }
                            catch { }
                            finally { Object.Destroy(baked); }
                        }
                    }
                    if (geometry.SubmeshTriangles.Count == 1) geometry.SubmeshTriangles.Clear();
                    var bytes = MeshJsonBytes(geometry);
                    if (estimatedMeshBytes + bytes > meshJsonBudget - 1024 * 1024) { skipped++; deferred++; continue; }
                    estimatedMeshBytes += bytes;
                    usedVertices += geometry.Vertices.Length / 3;
                    usedIndices += geometry.Triangles.Length;
                    try { appearance.Capture(renderer, geometry); } catch { /* Retain mesh if one shader cannot be read. */ }
                    if (batchRenderer == null && renderer is MeshRenderer) sharedMeshes[mesh.GetInstanceID()] = geometry;
                }
                else if (renderer is SkinnedMeshRenderer fallbackSkin && mesh.vertexCount <= maxVertices - usedVertices)
                {
                    var baked = new Mesh { name = mesh.name + " (baked pose)" };
                    try
                    {
                        fallbackSkin.BakeMesh(baked, false);
                        if (MeshSnapshotReader.Read(baked, geometry, maxVertices - usedVertices, maxVertices * 6 - usedIndices))
                        {
                            if (geometry.SubmeshTriangles.Count == 1) geometry.SubmeshTriangles.Clear();
                            var bytes = MeshJsonBytes(geometry);
                            if (estimatedMeshBytes + bytes > meshJsonBudget - 1024 * 1024) { skipped++; deferred++; continue; }
                            estimatedMeshBytes += bytes;
                            bakedActors++; usedVertices += geometry.Vertices.Length / 3; usedIndices += geometry.Triangles.Length;
                            appearance.Capture(renderer, geometry);
                        }
                    }
                    catch { }
                    finally { Object.Destroy(baked); }
                }
                if (geometry.IsBoundsProxy)
                {
                    var bytes = MeshJsonBytes(geometry);
                    if (estimatedMeshBytes + bytes > meshJsonBudget - 1024 * 1024) { skipped++; deferred++; continue; }
                    estimatedMeshBytes += bytes; boxes++;
                }
                if (geometry.IsInterior && !geometry.IsBoundsProxy) interiors++;
                world.Geometry.Add(geometry);
                captured.Add(rendererId);
                if (owner != null && !geometry.IsBoundsProxy && geometry.AttachedBonePath.Length == 0)
                    tracker.TrackEntityRenderer(renderer, owner, geometry);
                if (movingSceneRenderer)
                    tracker.TrackMovingSceneRenderer(renderer, anchors.FirstOrDefault(anchor => anchor.Key == geometry.AnchorId).Value, geometry);
                else if (owner == null && !geometry.IsBoundsProxy)
                    tracker.Visual.Track(renderer, rendererId, false, visibility.CullerManaged.Contains(renderer) ||
                        visibility.NaturalLods.ContainsKey(renderer) || visibility.TileLods.Contains(renderer));
            }
            if (onlyEntityIds != null)
            {
                job.Summary = $"late entities: {world.Geometry.Count} render objects, {skinned} animated skins, {bakedActors} baked skins";
                yield break;
            }
            // Unity Terrain has no MeshFilter. Sample its height field at a useful
            // spectator resolution, yielding between rows to protect gameplay frames.
            foreach (var terrain in layer == "exterior" ? Terrain.activeTerrains : Array.Empty<Terrain>())
            {
                yield return true;
                if (!terrain || !terrain.terrainData) continue;
                var terrainId = "terrain" + terrain.GetInstanceID();
                if (alreadyCaptured.Contains(terrainId)) continue;
                if (includeContext && sceneAssets != null && sceneAssets.TryTerrain(terrain, out var assetTerrainPath) &&
                    terrain.enabled && terrain.gameObject.activeInHierarchy &&
                    (cameraMask & (1 << terrain.gameObject.layer)) != 0 &&
                    SceneAssetPaths.IsValidReference(assetTerrainPath) &&
                    world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count < 4096 &&
                    terrainAssetPaths.Add(assetTerrainPath))
                {
                    AttachAssetScene(world, sceneAssets);
                    world.AssetTerrainPaths.Add(assetTerrainPath);
                    captured.Add(terrainId);
                    continue;
                }
                if (world.Geometry.Count >= maxObjects) { skipped++; deferred++; continue; }
                var data = terrain.terrainData;
                var grid = Math.Min(128, Math.Max(32, data.heightmapResolution - 1));
                while (grid >= 32 && (usedVertices + (grid + 1) * (grid + 1) > maxVertices ||
                    usedIndices + grid * grid * 6 > maxVertices * 6)) grid /= 2;
                if (grid < 32) { skipped++; deferred++; continue; }
                var vertices = new float[(grid + 1) * (grid + 1) * 3];
                var normals = new float[vertices.Length];
                var uvs = new float[(grid + 1) * (grid + 1) * 2];
                var triangles = new int[grid * grid * 6];
                for (var z = 0; z <= grid; z++)
                {
                    yield return true;
                    for (var x = 0; x <= grid; x++)
                    {
                        var u = (float)x / grid;
                        var v = (float)z / grid;
                        var i = z * (grid + 1) + x;
                        vertices[i * 3] = data.size.x * u;
                        vertices[i * 3 + 1] = data.GetInterpolatedHeight(u, v);
                        vertices[i * 3 + 2] = data.size.z * v;
                        var normal = data.GetInterpolatedNormal(u, v);
                        normals[i * 3] = normal.x; normals[i * 3 + 1] = normal.y; normals[i * 3 + 2] = normal.z;
                        uvs[i * 2] = u; uvs[i * 2 + 1] = v;
                    }
                }
                for (var z = 0; z < grid; z++)
                    for (var x = 0; x < grid; x++)
                    { var i = z * (grid + 1) + x; var t = (z * grid + x) * 6;
                        triangles[t] = i; triangles[t + 1] = i + grid + 1; triangles[t + 2] = i + 1;
                        triangles[t + 3] = i + 1; triangles[t + 4] = i + grid + 1; triangles[t + 5] = i + grid + 2; }
                if (!vertices.All(GameAccess.Finite) || !normals.All(GameAccess.Finite) ||
                    !GameAccess.Finite(terrain.transform.position)
                    || !GameAccess.Finite(terrain.transform.rotation) || !GameAccess.Finite(terrain.transform.lossyScale)) { skipped++; continue; }
                var geometry = new GeometrySnapshot { Id = "terrain" + terrain.GetInstanceID(), Name = terrain.name,
                    Position = GameAccess.Vec(terrain.transform.position), Rotation = GameAccess.Rot(terrain.transform.rotation), Scale = GameAccess.Vec(terrain.transform.lossyScale),
                    Vertices = vertices, Normals = normals, Uvs = uvs, Triangles = triangles,
                    Color = new[] { 0.25f, 0.3f, 0.26f, 1f } };
                var bytes = MeshJsonBytes(geometry);
                if (estimatedMeshBytes + bytes > meshJsonBudget) { skipped++; deferred++; continue; }
                try { appearance.CaptureTerrain(terrain, geometry); } catch { }
                estimatedMeshBytes += bytes;
                world.Geometry.Add(geometry);
                captured.Add(terrainId);
                usedVertices += (grid + 1) * (grid + 1);
                usedIndices += triangles.Length;
            }
            // SpawnGrassOnMesh draws blades directly with Graphics.DrawMeshInstanced;
            // no Renderer component exists for those visible blades.
            if (layer == "exterior" && includeContext)
                foreach (var grass in GameAccess.Find("SpawnGrassOnMesh"))
                {
                    yield return true;
                    if (!grass || !(grass is Behaviour behaviour) || !behaviour.isActiveAndEnabled ||
                        !(GameAccess.Read(grass, "mesh") is Mesh mesh) || !mesh ||
                        !(GameAccess.Read(grass, "material") is Material material) || !material ||
                        !(GameAccess.Read(grass, "spawnedGrass") is bool spawned && spawned) ||
                        !(GameAccess.Read(grass, "Batches") is IEnumerable batches)) continue;
                    var id = "grass" + grass.GetInstanceID();
                    if (alreadyCaptured.Contains(id) || world.Geometry.Count >= maxObjects) continue;
                    var matrices = new List<float>();
                    foreach (var batch in batches)
                    {
                        if (!(batch is IEnumerable instances)) continue;
                        foreach (var item in instances)
                        {
                            if (!(item is Matrix4x4 matrix)) continue;
                            var position = matrix.GetColumn(3);
                            if (!GameAccess.Finite(new Vector3(position.x, position.y, position.z))) continue;
                            for (var i = 0; i < 16; i++) matrices.Add(matrix[i]);
                            if (matrices.Count >= 8192 * 16) break;
                        }
                        if (matrices.Count >= 8192 * 16) break;
                    }
                    if (matrices.Count == 0) continue;
                    var geometry = new GeometrySnapshot { Id = id, Name = "Instanced grass " + grass.name,
                        Instances = matrices.ToArray(), Position = GameAccess.Vec(Vector3.zero),
                        BoundsCenter = GameAccess.Vec(mesh.bounds.center), BoundsSize = GameAccess.Vec(mesh.bounds.size),
                        Color = new[] { 0.34f, 0.45f, 0.24f, 1f } };
                    if (!MeshSnapshotReader.Read(mesh, geometry, maxVertices - usedVertices, maxVertices * 6 - usedIndices)) continue;
                    if (geometry.SubmeshTriangles.Count == 1) geometry.SubmeshTriangles.Clear();
                    var bytes = MeshJsonBytes(geometry);
                    if (estimatedMeshBytes + bytes > meshJsonBudget) { deferred++; continue; }
                    try { appearance.Capture(material, grass.name, geometry); } catch { }
                    estimatedMeshBytes += bytes;
                    usedVertices += geometry.Vertices.Length / 3; usedIndices += geometry.Triangles.Length;
                    world.Geometry.Add(geometry); captured.Add(id);
                }
            if (includeContext)
                foreach (var system in Object.FindObjectsOfType<ParticleSystem>(true))
                {
                    yield return true;
                    if (world.ParticleEmitters.Count >= 128) break;
                    if (!system || !system.isPlaying || !system.gameObject.activeInHierarchy ||
                        !system.gameObject.scene.IsValid() || !system.gameObject.scene.isLoaded) continue;
                    var renderer = system.GetComponent<ParticleSystemRenderer>();
                    if (!renderer || !visibility.Enabled(renderer) || CaptureVisibility.IsDebugRenderer(renderer) ||
                        !CaptureVisibility.VisibleLayer(renderer, cameraMask, visibility.CullerManaged.Contains(renderer))) continue;
                    var main = system.main; var emission = system.emission;
                    if (!main.loop || !emission.enabled) continue;
                    EntityTracker.Entry? owner = null;
                    for (var parent = system.transform; parent; parent = parent.parent)
                        if (owners.TryGetValue(parent, out owner)) break;
                    // Turret firing is sampled from its actual per-frame particles, not a forever-looping baseline.
                    var turretType = GameAccess.Type("Turret");
                    if (turretType != null && system.GetComponentInParent(turretType)) continue;
                    var tile = tileType == null ? null : system.GetComponentInParent(tileType);
                    var roomId = visibility.Rooms.TryGetValue(renderer, out var managedRoom) && managedRoom.Length != 0 ? managedRoom :
                        tile ? "r" + tile!.GetInstanceID() : SpatialRoom(renderer.bounds.center, renderer.name, tileBounds);
                    var interior = roomId.Length != 0;
                    if (layer == "interior" && (!interior || owner != null)) continue;
                    if (layer == "exterior" && interior && owner == null) continue;
                    var position = owner == null ? system.transform.position : owner.Component.transform.InverseTransformPoint(system.transform.position);
                    var rotation = owner == null ? system.transform.rotation : Quaternion.Inverse(owner.Component.transform.rotation) * system.transform.rotation;
                    if (!GameAccess.Finite(position) || !GameAccess.Finite(rotation)) continue;
                    var color = main.startColor.color;
                    var shape = system.shape;
                    var snapshot = new ParticleEmitterSnapshot { Id = "p" + system.GetInstanceID(), Name = system.name,
                        EntityId = owner?.Id ?? "", IsInterior = interior,
                        RoomId = roomId,
                        Position = GameAccess.Vec(position), Rotation = GameAccess.Rot(rotation),
                        Color = new[] { Finite(color.r, 1), Finite(color.g, 1), Finite(color.b, 1), Finite(color.a, 1) },
                        Rate = Mathf.Clamp(emission.rateOverTime.constant, 0, 1000),
                        Lifetime = Mathf.Clamp(main.startLifetime.constant, 0.05f, 120),
                        Speed = Mathf.Clamp(main.startSpeed.constant, 0, 100),
                        Size = Mathf.Clamp(main.startSize.constant, 0.005f, 100),
                        Radius = shape.enabled ? Mathf.Clamp(shape.radius, 0, 100) : 0 };
                    var appearanceGeometry = new GeometrySnapshot();
                    try { appearance.Capture(renderer, appearanceGeometry); } catch { }
                    snapshot.MaterialId = appearanceGeometry.MaterialIds.Count != 0 ? appearanceGeometry.MaterialIds[0] : "";
                    snapshot.Style = ParticleCapture.Style(system, renderer);
                    if (owner != null)
                    {
                        var sourceScale = system.transform.lossyScale;
                        var ownerScale = owner.Component.transform.lossyScale;
                        snapshot.Style.Scale = new Vec3(Div(sourceScale.x, ownerScale.x),
                            Div(sourceScale.y, ownerScale.y), Div(sourceScale.z, ownerScale.z));
                    }
                    world.ParticleEmitters.Add(snapshot);
                }
            if (includeContext || onlyEntityIds != null)
            {
                CaptureLights(world, anchors, owners, layer == "interior", onlyEntityIds);
                if (layer == "interior" && includeContext) CaptureEmissiveRooms(world);
                CaptureEmissiveItems(world, tracker);
            }
            var localFogMaskBytes = 0;
            if (includeContext)
                foreach (var fog in GameAccess.Find("UnityEngine.Rendering.HighDefinition.LocalVolumetricFog"))
                {
                    yield return true;
                    if (world.LocalFogs.Count >= 128) break;
                    if (!(fog is Behaviour behaviour) || !behaviour.isActiveAndEnabled ||
                        !GameAccess.Finite(fog.transform.position) || !GameAccess.Finite(fog.transform.rotation)) continue;
                    var parameters = GameAccess.Read(fog, "parameters");
                    if (parameters == null || !(GameAccess.Read(parameters, "size") is Vector3 size) ||
                        !GameAccess.Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0 ||
                        size.x > 10000 || size.y > 10000 || size.z > 10000) continue;
                    var tile = tileType == null ? null : fog.GetComponentInParent(tileType);
                    var fogRoom = tile == null ? tileBounds.FirstOrDefault(room => room.Value.Contains(fog.transform.position)).Key : "";
                    var indoor = tile != null || !string.IsNullOrEmpty(fogRoom) || fog.name.IndexOf("indoor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        fog.name.IndexOf("factory", StringComparison.OrdinalIgnoreCase) >= 0;
                    var albedo = GameAccess.Read(parameters, "albedo") is Color color ? color : Color.white;
                    var snapshot = new LocalFogSnapshot
                    {
                        Id = "f" + fog.GetInstanceID(), Name = fog.name, IsInterior = indoor,
                        RoomId = tile ? "r" + tile!.GetInstanceID() : fogRoom ?? "",
                        Position = GameAccess.Vec(fog.transform.position), Rotation = GameAccess.Rot(fog.transform.rotation),
                        Size = GameAccess.Vec(size),
                        PositiveFade = ReadFogFade(parameters, "positiveFade"),
                        NegativeFade = ReadFogFade(parameters, "negativeFade"),
                        Albedo = new[] { Mathf.Clamp01(Finite(albedo.r, 1)), Mathf.Clamp01(Finite(albedo.g, 1)), Mathf.Clamp01(Finite(albedo.b, 1)), 1f },
                        MeanFreePath = Mathf.Clamp(ReadFogFloat(parameters, "meanFreePath", 10), .05f, 1000000),
                        Anisotropy = Mathf.Clamp(ReadFogFloat(parameters, "anisotropy", 0), -1, 1),
                        DistanceFadeStart = Mathf.Clamp(ReadFogFloat(parameters, "distanceFadeStart", 10000), 0, 1000000),
                        DistanceFadeEnd = Mathf.Clamp(ReadFogFloat(parameters, "distanceFadeEnd", 10000), 0, 1000000),
                        Priority = Mathf.Clamp(ReadFogInt(parameters, "priority"), -10000, 10000),
                        BlendingMode = Mathf.Clamp(ReadFogInt(parameters, "blendingMode"), 0, 8),
                        FalloffMode = Mathf.Clamp(ReadFogInt(parameters, "falloffMode"), 0, 8),
                        MaskMode = Mathf.Clamp(ReadFogInt(parameters, "maskMode"), 0, 1),
                        TextureTiling = ReadFogVector(parameters, "textureTiling", Vector3.one),
                        TextureScrollingSpeed = ReadFogVector(parameters, "textureScrollingSpeed", Vector3.zero),
                        InvertFade = GameAccess.Read(parameters, "invertFade") is bool invert && invert
                    };
                    snapshot.DistanceFadeEnd = Mathf.Max(snapshot.DistanceFadeStart, snapshot.DistanceFadeEnd);
                    if (snapshot.MaskMode == 0 && GameAccess.Read(parameters, "volumeMask") is Texture3D mask &&
                        mask.width > 0 && mask.height > 0 && mask.depth > 0 &&
                        mask.width <= 64 && mask.height <= 64 && mask.depth <= 64)
                    {
                        var byteCount = mask.width * mask.height * mask.depth * 4;
                        if (localFogMaskBytes + byteCount <= 4 * 1024 * 1024)
                            try
                            {
                                var pixels = mask.GetPixels32(0);
                                if (pixels.Length == mask.width * mask.height * mask.depth)
                                {
                                    var rgba = new byte[byteCount];
                                    for (var index = 0; index < pixels.Length; index++)
                                    {
                                        rgba[index * 4] = pixels[index].r;
                                        rgba[index * 4 + 1] = pixels[index].g;
                                        rgba[index * 4 + 2] = pixels[index].b;
                                        rgba[index * 4 + 3] = pixels[index].a;
                                    }
                                    snapshot.MaskWidth = mask.width; snapshot.MaskHeight = mask.height; snapshot.MaskDepth = mask.depth;
                                    snapshot.MaskRgba = rgba;
                                    localFogMaskBytes += byteCount;
                                }
                            }
                            catch { /* A GPU-only mask retains its recorded color and density without texture detail. */ }
                    }
                    world.LocalFogs.Add(snapshot);
                }
            foreach (var group in (layer == "interior" && includeContext ? tileBounds : Array.Empty<KeyValuePair<string, Bounds>>()).GroupBy(tile => tile.Key))
            {
                var bounds = group.First().Value;
                world.Rooms.Add(new RoomSnapshot { Id = group.Key, Center = GameAccess.Vec(bounds.center),
                    Size = GameAccess.Vec(bounds.size), AdditionalVolumes = group.Skip(1).Select(volume =>
                        new RoomVolumeSnapshot { Center = GameAccess.Vec(volume.Value.center), Size = GameAccess.Vec(volume.Value.size) }).ToList() });
            }
            if (layer == "exterior" && includeContext) world.Environment = EnvironmentCapture.Capture();
            job.Deferred = deferred;
            job.Summary = $"{layer}: {world.Geometry.Count} render objects, {usedVertices} vertices, {{texture-count}} textures, {world.Materials.Count} materials, "
                + $"{world.AssetRendererPaths.Count + world.AssetTerrainPaths.Count} installed-scene surfaces, "
                + $"{skinned} animated skins, {bakedActors} baked skins, {boxes} bounds proxies, {invisible} camera-hidden renderers excluded, "
                + $"{debug} debug renderers excluded, {lods} duplicate LODs excluded, {reused} shared meshes, {interiors} interior renderers, "
                + $"{world.Lights.Count} lights, {world.ParticleEmitters.Count} particle emitters, {world.LocalFogs.Count} local fogs, {world.Rooms.Count} rooms, {world.Environment?.Components.Count ?? 0} environment components, "
                + $"{skipped} objects and {{omitted-texture-count}} textures omitted by budget/readback";
        }

        private static void AttachAssetScene(WorldSnapshot world, SceneAssetCapture sceneAssets)
        {
            world.AssetScene = sceneAssets.SceneName;
            world.AssetBuildIndex = sceneAssets.BuildIndex;
            world.AssetGameVersion = Application.version;
        }

        private static int SafeInt(object? value, int fallback = 0)
        {
            try { return value == null ? fallback : Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static void CaptureLights(WorldSnapshot world, KeyValuePair<string, Transform>[] anchors,
            Dictionary<Transform, EntityTracker.Entry> owners, bool interiorLayer, ISet<string>? onlyEntityIds)
        {
            var rooms = new Dictionary<string, Bounds>();
            foreach (var geometry in world.Geometry)
            {
                if (!geometry.IsInterior || geometry.EntityId.Length != 0 ||
                    geometry.RoomId.Length == 0 || geometry.IsBoundsProxy) continue;
                // Captured room bounds are an approximate indoor marker for nearby lights.
                var bounds = new Bounds(new Vector3(geometry.Position.X, geometry.Position.Y, geometry.Position.Z),
                    new Vector3(geometry.BoundsSize.X, geometry.BoundsSize.Y, geometry.BoundsSize.Z));
                if (rooms.TryGetValue(geometry.RoomId, out var previous)) { previous.Encapsulate(bounds); rooms[geometry.RoomId] = previous; }
                else rooms[geometry.RoomId] = bounds;
            }
            var lightCandidates = onlyEntityIds == null ? Object.FindObjectsOfType<Light>(true).AsEnumerable() :
                owners.Values.Where(entry => onlyEntityIds.Contains(entry.Id) && entry.Component)
                    .SelectMany(entry => entry.Component.GetComponentsInChildren<Light>(true)).Distinct();
            var hdLightType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
            foreach (var light in lightCandidates)
            {
                if (world.Lights.Count >= 480) break;
                if (!light || !light.gameObject.activeInHierarchy || !light.gameObject.scene.IsValid() || !light.gameObject.scene.isLoaded ||
                    (light.type != LightType.Directional && light.type != LightType.Point && light.type != LightType.Spot &&
                     light.type != LightType.Rectangle && light.type != LightType.Disc) ||
                    !GameAccess.Finite(light.transform.position) || !GameAccess.Finite(light.transform.rotation) ||
                    !GameAccess.Finite(light.intensity) || !GameAccess.Finite(light.range)) continue;
                EntityTracker.Entry? owner = null;
                for (var parent = light.transform; parent; parent = parent.parent)
                    if (owners.TryGetValue(parent, out owner)) break;
                // The spectator supplies the game's camera-local night vision.
                // Capturing it as an actor/world fixture duplicates the light
                // and loses its large HDRP radius, producing bright wall spots.
                if (owner?.Kind == "player" && (GameAccess.Read(owner.Component, "nightVision") as Light == light ||
                    GameAccess.Read(owner.Component, "nightVisionRadar") as Light == light)) continue;
                if (onlyEntityIds != null && (owner == null || !onlyEntityIds.Contains(owner.Id))) continue;
                var position = light.transform.position;
                var rotation = light.transform.rotation;
                var anchor = owner == null ? ClosestAnchor(light.transform, anchors) : null;
                if (owner != null)
                {
                    position = owner.Component.transform.InverseTransformPoint(position);
                    rotation = Quaternion.Inverse(owner.Component.transform.rotation) * rotation;
                }
                else if (anchor.HasValue)
                {
                    position = anchor.Value.Value.InverseTransformPoint(position);
                    rotation = Quaternion.Inverse(anchor.Value.Value.rotation) * rotation;
                }
                var color = light.color;
                var hdLight = hdLightType == null ? null : light.GetComponent(hdLightType);
                var lightDimmer = GameAccess.Read(hdLight, "lightDimmer") is float sourceLightDimmer && GameAccess.Finite(sourceLightDimmer)
                    ? Mathf.Clamp(sourceLightDimmer, 0, 16) : 1f;
                var shadowDimmer = GameAccess.Read(hdLight, "shadowDimmer") is float sourceShadowDimmer && GameAccess.Finite(sourceShadowDimmer)
                    ? Mathf.Clamp(sourceShadowDimmer, 0, 16) : 1f;
                var tileType = GameAccess.Type("DunGen.Tile");
                var indoor = light.type != LightType.Directional &&
                    ((tileType != null && light.GetComponentInParent(tileType) != null) ||
                     rooms.Values.Any(room => room.SqrDistance(light.transform.position) < 16f));
                if (onlyEntityIds == null && indoor != interiorLayer) continue;
                // Tile culling can disable a room light for the recorder's
                // camera. The replay spectator needs the fixture when nearby.
                if (!light.enabled && light.type != LightType.Directional && (tileType == null || !light.GetComponentInParent(tileType))) continue;
                world.Lights.Add(new LightSnapshot { Id = "l" + light.GetInstanceID(), Name = light.name, AnchorId = anchor?.Key ?? "",
                    EntityId = owner?.Id ?? "", IsInterior = indoor,
                    Type = light.type == LightType.Rectangle || light.type == LightType.Disc ? "Point" : light.type.ToString(),
                    Position = GameAccess.Vec(position), Rotation = GameAccess.Rot(rotation),
                    Color = new[] { color.r, color.g, color.b, color.a },
                    UseColorTemperature = light.useColorTemperature,
                    ColorTemperature = GameAccess.Finite(light.colorTemperature)
                        ? Mathf.Clamp(light.colorTemperature, 1000f, 20000f) : 6500f,
                    Intensity = Mathf.Clamp(light.intensity, 0, 1000000),
                    LightDimmer = lightDimmer,
                    Range = Mathf.Clamp(light.type == LightType.Rectangle || light.type == LightType.Disc ?
                        Mathf.Max(6f, light.range) : light.range, 0, 100000),
                    SpotAngle = Mathf.Clamp(light.spotAngle, 0, 180),
                    Shadows = light.shadows != LightShadows.None && light.shadowStrength > .01f && shadowDimmer > .01f,
                    ShadowStrength = Mathf.Clamp01(light.shadowStrength),
                    ShadowDimmer = shadowDimmer,
                    BakeType = GameAccess.Read(light, "lightmapBakeType")?.ToString() ??
                        GameAccess.Read(GameAccess.Read(light, "bakingOutput"), "lightmapBakeType")?.ToString() ?? "" });
            }
        }
        private static void CaptureEmissiveItems(WorldSnapshot world, EntityTracker tracker)
        {
            var itemIds = tracker.Entries.Where(entry => entry.Kind == "item" && entry.Component.GetType().Name != "HauntedMaskItem").Select(entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal);
            var materials = world.Materials.ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);
            var strongest = new Dictionary<string, (GeometrySnapshot Geometry, Color Glow, float Strength)>(StringComparer.Ordinal);
            foreach (var geometry in world.Geometry)
            {
                if (!itemIds.Contains(geometry.EntityId) || geometry.IsBoundsProxy) continue;
                foreach (var id in geometry.MaterialIds)
                {
                    if (!materials.TryGetValue(id, out var material)) continue;
                    var emission = material.Properties.FirstOrDefault(property => property.Name == "_EmissiveColor" &&
                        property.Values.Length >= 3);
                    if (emission == null) continue;
                    var values = emission.Values;
                    var strength = Mathf.Max(values[0], Mathf.Max(values[1], values[2]));
                    if (strength < 1f || !GameAccess.Finite(strength)) continue;
                    if (!strongest.TryGetValue(geometry.EntityId, out var previous) || strength > previous.Strength)
                        strongest[geometry.EntityId] = (geometry,
                            new Color(values[0] / strength, values[1] / strength, values[2] / strength, 1f), strength);
                }
            }
            foreach (var pair in strongest)
            {
                if (world.Lights.Count >= 512) break;
                if (world.Lights.Any(light => light.EntityId == pair.Key)) continue;
                var source = pair.Value.Geometry;
                var color = pair.Value.Glow;
                world.Lights.Add(new LightSnapshot { Id = "item-emission:" + pair.Key,
                    EntityId = pair.Key, IsInterior = source.IsInterior, Type = "Point",
                    Position = source.Position, Color = new[] { color.r, color.g, color.b, 1f },
                    Intensity = Mathf.Clamp(pair.Value.Strength * 8f, 8f, 80f), Range = 4f,
                    Shadows = false });
            }
        }
        private static void CaptureEmissiveRooms(WorldSnapshot world)
        {
            var materials = world.Materials.ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);
            var strongest = new Dictionary<string, (GeometrySnapshot Geometry, Color Glow, float Strength)>(StringComparer.Ordinal);
            foreach (var geometry in world.Geometry)
            {
                if (!geometry.IsInterior || geometry.RoomId.Length == 0 || geometry.IsBoundsProxy ||
                    geometry.EntityId.Length != 0 || !geometry.Active) continue;
                foreach (var id in geometry.MaterialIds)
                {
                    if (!materials.TryGetValue(id, out var material)) continue;
                    var emission = material.Properties.FirstOrDefault(property =>
                        (property.Name == "_EmissiveColor" || property.Name == "_EmissionColor") &&
                        property.Values.Length >= 3);
                    if (emission == null) continue;
                    var values = emission.Values;
                    var strength = Mathf.Max(values[0], Mathf.Max(values[1], values[2]));
                    if (strength < .5f || !GameAccess.Finite(strength)) continue;
                    if (!strongest.TryGetValue(geometry.RoomId, out var previous) || strength > previous.Strength)
                        strongest[geometry.RoomId] = (geometry,
                            new Color(values[0] / strength, values[1] / strength, values[2] / strength, 1), strength);
                }
            }
            foreach (var pair in strongest.OrderByDescending(item => item.Value.Strength).Take(32))
            {
                if (world.Lights.Count >= 512) break;
                var source = pair.Value.Geometry;
                var center = new Vector3(source.Position.X, source.Position.Y, source.Position.Z);
                if (world.Lights.Any(light => light.IsInterior &&
                    (new Vector3(light.Position.X, light.Position.Y, light.Position.Z) - center).sqrMagnitude < 64f)) continue;
                var glow = pair.Value.Glow;
                world.Lights.Add(new LightSnapshot { Id = "emissive:" + pair.Key, IsInterior = true,
                    Type = "Point", Position = GameAccess.Vec(center), Color = new[] { glow.r, glow.g, glow.b, 1f },
                    Intensity = Mathf.Clamp(pair.Value.Strength * 35f, 20f, 150f),
                    Range = 12f, Shadows = false });
            }
        }
        private static float Div(float a, float b) => Math.Abs(b) < 0.00001f ? 1 : a / b;
        private static float Finite(float value, float fallback) => GameAccess.Finite(value) ? value : fallback;
        private static float ReadFogFloat(object parameters, string name, float fallback) =>
            GameAccess.Read(parameters, name) is float value && GameAccess.Finite(value) ? value : fallback;
        private static Vec3 ReadFogFade(object parameters, string name)
        {
            var vector = GameAccess.Read(parameters, name) is Vector3 value && GameAccess.Finite(value) ? value : Vector3.one * .1f;
            return GameAccess.Vec(new Vector3(Mathf.Clamp01(vector.x), Mathf.Clamp01(vector.y), Mathf.Clamp01(vector.z)));
        }
        private static Vec3 ReadFogVector(object parameters, string name, Vector3 fallback)
        {
            var value = GameAccess.Read(parameters, name) is Vector3 vector && GameAccess.Finite(vector) ? vector : fallback;
            return GameAccess.Vec(value);
        }
        private static int ReadFogInt(object parameters, string name)
        {
            try { return Convert.ToInt32(GameAccess.Read(parameters, name)); }
            catch { return 0; }
        }
        private static float DistanceToViewpoints(Renderer renderer, Vector3[] viewpoints)
        {
            if (!renderer || viewpoints.Length == 0) return 0;
            var bounds = renderer.bounds; float distance = float.MaxValue;
            foreach (var point in viewpoints) distance = Math.Min(distance, bounds.SqrDistance(point));
            return distance;
        }
        private readonly struct RendererOrder
        {
            internal readonly Renderer Renderer;
            internal readonly int Priority;
            internal readonly float Distance;
            internal readonly int Id;
            internal RendererOrder(Renderer renderer, int priority, float distance)
            { Renderer = renderer; Priority = priority; Distance = distance; Id = renderer.GetInstanceID(); }
        }
        private static long MeshJsonBytes(GeometrySnapshot geometry) => 1024L +
            6L * (geometry.Name.Length + geometry.MeshName.Length + geometry.EntityId.Length + geometry.RoomId.Length + geometry.MeshSourceId.Length + geometry.BonePaths.Sum(path => path.Length)) +
            16L * (geometry.Vertices.LongLength + geometry.Normals.LongLength + geometry.Uvs.LongLength + geometry.Uvs1.LongLength +
                geometry.Uvs2.LongLength + geometry.Uvs3.LongLength + geometry.Tangents.LongLength + geometry.Instances.LongLength + geometry.BindPoses.LongLength + geometry.BoneWeights.LongLength) +
            8L * (geometry.Triangles.LongLength + geometry.BoneIndices.LongLength + geometry.SubmeshTriangles.Sum(indices => indices.LongLength));
        private static bool IsVisualEntity(string kind) => kind != "round" && kind != "time" && kind != "terminal";

        private static string SpatialRoom(Vector3 position, string rendererName, KeyValuePair<string, Bounds>[] rooms)
        {
            // A generated prop or spawned hazard need not be parented to its
            // DunGen.Tile. Use the tile's world bounds as the fallback only for
            // compact objects, so terrain and exterior foliage stay outdoors.
            if (RenderVisibilityPolicy.IsGroundSurface(rendererName) ||
                RenderVisibilityPolicy.IsVegetation(rendererName) ||
                RenderVisibilityPolicy.IsRockSurface(rendererName)) return "";
            foreach (var room in rooms)
            {
                var bounds = room.Value;
                bounds.Expand(0.5f);
                if (bounds.Contains(position)) return room.Key;
            }
            return "";
        }

        // Mansion furniture is usually part of a generated Tile, rather than an
        // AnimatedObjectTrigger child. Its mesh therefore has no entity owner.
        private static bool IsMovingSceneRenderer(Renderer renderer, Component? tile)
        {
            var name = renderer.name;
            if (name.IndexOf("shelf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("bookcase", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("bookshelf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("cabinet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("drawer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (var parent = renderer.transform; parent && (!tile || parent != tile!.transform); parent = parent.parent)
                if (parent.GetComponent<Animator>() || parent.GetComponent<Animation>() ||
                    parent.GetComponent("AnimatedObjectTrigger") || parent.GetComponent("DoorLock")) return true;
            return false;
        }
        private static float[] ReadColor(Renderer renderer)
        {
            var color = new Color(0.4f, 0.45f, 0.5f, 1);
            try
            {
                var material = renderer.sharedMaterial;
                if (material)
                    foreach (var property in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                        if (material.HasProperty(property)) { color = material.GetColor(property); break; }
            }
            catch { }
            return new[] { GameAccess.Finite(color.r) ? color.r : 0.4f, GameAccess.Finite(color.g) ? color.g : 0.45f,
                GameAccess.Finite(color.b) ? color.b : 0.5f, 1f };
        }
    }
}
