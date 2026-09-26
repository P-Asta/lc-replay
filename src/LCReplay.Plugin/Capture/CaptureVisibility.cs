using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LCReplay.Plugin.Capture
{
    internal static class CaptureVisibility
    {
        internal static int GameplayMask()
        {
            var player = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController")
                ?? GameAccess.Read(GameAccess.Singleton("StartOfRound"), "localPlayerController");
            var camera = GameAccess.Read(player, "gameplayCamera") as Camera;
            if (!camera) camera = Camera.main;
            return camera ? camera!.cullingMask : ~0;
        }

        internal static bool VisibleLayer(Renderer renderer, int mask, bool cullerManaged = false) =>
            (mask & (1 << renderer.gameObject.layer)) != 0 && (cullerManaged || !renderer.forceRenderingOff) &&
            renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly;

        internal static bool IsDebugRenderer(Renderer renderer)
        {
            for (var parent = renderer.transform; parent; parent = parent.parent)
                if (RenderVisibilityPolicy.IsDebugObject(parent.name) || RenderVisibilityPolicy.IsFirstPersonOverlay(parent.name)) return true;
            var materials = renderer.sharedMaterials;
            if (materials.Length == 0) return false;
            foreach (var material in materials)
                if (!material || !RenderVisibilityPolicy.IsDebugMaterial(material.name)) return false;
            return true;
        }

        // The room culler turns Renderer.enabled off solely for the live camera. Read its
        // membership instead of changing it, so recording never alters the running game.
        internal static SceneVisibility Snapshot()
        {
            var result = new SceneVisibility();
            var culler = GameAccess.Read(GameAccess.Singleton("StartOfRound"), "occlusionCuller");
            foreach (var field in new[] { "tileRenderers", "doorRenderers" })
            {
                if (!(GameAccess.Read(culler, field) is IDictionary rooms)) continue;
                foreach (DictionaryEntry room in rooms)
                {
                    var key = room.Key as UnityEngine.Object;
                    var id = key ? "r" + key!.GetInstanceID() : "";
                    if (!(room.Value is IEnumerable renderers)) continue;
                    foreach (var item in renderers)
                        if (item is Renderer renderer && renderer)
                        {
                            if (field == "tileRenderers") result.Rooms[renderer] = id;
                            result.CullerManaged.Add(renderer);
                        }
                }
            }
            if (GameAccess.Read(culler, "OverrideRendererVisibilities") is IDictionary overrides)
                foreach (DictionaryEntry item in overrides)
                    if (item.Key is Renderer renderer && renderer && item.Value is bool visible)
                        result.Overrides[renderer] = visible;
            // Disabling the game's culler restores rooms and clears its membership maps.
            // Tile membership still supplies replay room tags; it must not override enabled.
            if (result.Rooms.Count == 0)
                foreach (var tile in GameAccess.Find("DunGen.Tile"))
                    foreach (var renderer in tile.GetComponentsInChildren<Renderer>(true))
                        if (renderer && renderer.enabled && renderer.gameObject.activeInHierarchy && result.Rooms.Count < 50000)
                            result.Rooms[renderer] = "r" + tile.GetInstanceID();
            foreach (var group in UnityEngine.Object.FindObjectsOfType<LODGroup>(true))
            {
                if (!group || !group.gameObject.scene.IsValid() || !group.gameObject.scene.isLoaded) continue;
                var lods = group.GetLODs();
                if (lods.Length == 0) continue;
                var selected = new HashSet<Renderer>();
                var natural = IsNatural(group, lods) && lods.Length > 1;
                if (natural)
                {
                    var low = lods.Length - 1;
                    while (low > 0 && lods[low].renderers.Length == 0) low--;
                    if (low > 0)
                    {
                        var center = group.transform.TransformPoint(group.localReferencePoint);
                        var camera = Camera.main;
                        var fov = camera ? camera!.fieldOfView : 70f;
                        var transition = Mathf.Max(.05f, lods[0].screenRelativeTransitionHeight);
                        var distance = Mathf.Clamp(group.size / (2f * Mathf.Tan(fov * Mathf.Deg2Rad * .5f) * transition), 20f, 120f);
                        var id = "lod" + group.GetInstanceID();
                        foreach (var renderer in lods[0].renderers)
                            if (renderer) { selected.Add(renderer); result.NaturalLods[renderer] = new LodSelection(id, 0, center, distance); }
                        foreach (var renderer in lods[low].renderers)
                            if (renderer && !selected.Contains(renderer))
                            { selected.Add(renderer); result.NaturalLods[renderer] = new LodSelection(id, 1, center, distance); }
                    }
                }
                foreach (var lod in lods)
                {
                    if (selected.Count == 0)
                        foreach (var renderer in lod.renderers) if (renderer) selected.Add(renderer);
                    foreach (var renderer in lod.renderers)
                        if (renderer && !selected.Contains(renderer)) result.OtherLods.Add(renderer);
                }
            }
            return result;
        }

        private static bool IsNatural(LODGroup group, LOD[] lods)
        {
            const string names = " tree bush flower grass rock stone terrain forest leaf leaves plant foliage pine trunk branch fern shrub moss ";
            if (NaturalName(group.name, names)) return true;
            foreach (var lod in lods)
                foreach (var renderer in lod.renderers)
                    if (renderer && (NaturalName(renderer.name, names) || NaturalName(renderer.transform.parent ? renderer.transform.parent.name : "", names)))
                        return true;
            return false;
        }

        private static bool NaturalName(string name, string words)
        {
            name = name.ToLowerInvariant();
            foreach (var word in words.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.Contains(word)) return true;
            return false;
        }

        internal readonly struct LodSelection
        {
            internal readonly string Id;
            internal readonly int Level;
            internal readonly Vector3 Center;
            internal readonly float Distance;
            internal LodSelection(string id, int level, Vector3 center, float distance)
            { Id = id; Level = level; Center = center; Distance = distance; }
        }

        internal sealed class SceneVisibility
        {
            internal readonly Dictionary<Renderer, string> Rooms = new Dictionary<Renderer, string>();
            internal readonly HashSet<Renderer> CullerManaged = new HashSet<Renderer>();
            internal readonly Dictionary<Renderer, bool> Overrides = new Dictionary<Renderer, bool>();
            internal readonly HashSet<Renderer> OtherLods = new HashSet<Renderer>();
            internal readonly Dictionary<Renderer, LodSelection> NaturalLods = new Dictionary<Renderer, LodSelection>();
            internal bool Enabled(Renderer renderer) => NaturalLods.ContainsKey(renderer) ||
                (Overrides.TryGetValue(renderer, out var visible) ? visible : renderer.enabled || CullerManaged.Contains(renderer));
        }
    }
}
