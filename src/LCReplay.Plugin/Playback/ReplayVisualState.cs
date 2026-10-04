using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Replays sparse world visibility and spray-decal events deterministically
    // through forward playback, seeks, and bounded single-file windows.
    internal sealed class ReplayVisualState : IDisposable
    {
        private readonly Transform root;
        private readonly int layer;
        private readonly SceneAssetReplay? assetScene;
        private readonly Dictionary<string, GameObject> geometry;
        private readonly Dictionary<string, Transform> anchors;
        private readonly Dictionary<string, bool> staticBaselines = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly HashSet<string> hiddenAssets = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> spray = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        private readonly ReplayEvent[] events;
        private readonly Dictionary<string, List<ReplayEvent>> rendererEvents = new Dictionary<string, List<ReplayEvent>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ReplayEvent>> assetEvents = new Dictionary<string, List<ReplayEvent>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ReplayEvent>> sprayEvents = new Dictionary<string, List<ReplayEvent>>(StringComparer.Ordinal);
        private readonly string captureSet;
        private readonly Type? projectorType;
        private int cursor;
        private double lastTime = -1;
        private Texture2D? fallbackTexture;
        private Material? fallbackMaterial;

        internal ReplayVisualState(WorldSnapshot world, IEnumerable<ReplayEvent> source,
            Dictionary<string, GameObject> geometry, Dictionary<string, Transform> anchors,
            SceneAssetReplay? assetScene, Transform root, int layer)
        {
            this.geometry = geometry;
            this.anchors = anchors;
            this.assetScene = assetScene;
            this.root = root;
            this.layer = layer;
            captureSet = world.CaptureSetId;
            foreach (var item in world.Geometry)
                if (item.EntityId.Length == 0 && !item.IsMovingSceneRenderer)
                    staticBaselines[item.Id] = item.Active;
            events = ReplayTreeBreakTiming.Correct(world, source).Where(value => value.Category == "visual" && value.Data != null)
                .OrderBy(value => value.Time).ToArray();
            foreach (var evt in events)
            {
                if (!evt.Data.TryGetValue("set", out var set) || set != captureSet) continue;
                var index = evt.Name == "spray" ? sprayEvents :
                    evt.Name == "renderer" && evt.Data.TryGetValue("asset", out var asset) && asset == "true" ? assetEvents :
                    evt.Name == "renderer" ? rendererEvents : null;
                if (index == null) continue;
                if (!index.TryGetValue(evt.EntityId, out var history)) index[evt.EntityId] = history = new List<ReplayEvent>();
                history.Add(evt);
            }
            projectorType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.DecalProjector");
        }

        internal void Sync(double time)
        {
            if (time < lastTime || time - lastTime > 5)
            {
                RestoreAt(time);
                lastTime = time;
                return;
            }
            lastTime = time;
            var assetsChanged = false;
            while (cursor < events.Length && events[cursor].Time <= time)
            {
                var evt = events[cursor++];
                var data = evt.Data;
                if (!data.TryGetValue("set", out var set) || set != captureSet) continue;
                var visible = data.TryGetValue("visible", out var value) && value == "true";
                if (evt.Name == "renderer")
                {
                    if (data.TryGetValue("asset", out var asset) && asset == "true")
                    {
                        if (visible) hiddenAssets.Remove(evt.EntityId);
                        else hiddenAssets.Add(evt.EntityId);
                        assetsChanged = true;
                    }
                    else if (staticBaselines.ContainsKey(evt.EntityId) &&
                        geometry.TryGetValue(evt.EntityId, out var obj) && obj) obj.SetActive(visible);
                }
                else if (evt.Name == "spray") ApplySpray(evt, visible);
            }
            if (assetsChanged) assetScene?.SetHiddenRendererPaths(hiddenAssets);
        }

        private void RestoreAt(double time)
        {
            cursor = UpperBound(events, time);
            foreach (var pair in rendererEvents)
            {
                if (!staticBaselines.TryGetValue(pair.Key, out var baseline) ||
                    !geometry.TryGetValue(pair.Key, out var obj) || !obj) continue;
                var evt = Latest(pair.Value, time);
                var visible = evt == null ? baseline : evt.Data.TryGetValue("visible", out var value) && value == "true";
                if (obj.activeSelf != visible) obj.SetActive(visible);
            }
            hiddenAssets.Clear();
            foreach (var pair in assetEvents)
            {
                var evt = Latest(pair.Value, time);
                if (evt != null && (!evt.Data.TryGetValue("visible", out var value) || value != "true"))
                    hiddenAssets.Add(pair.Key);
            }
            assetScene?.SetHiddenRendererPaths(hiddenAssets);
            foreach (var obj in spray.Values) if (obj && obj.activeSelf) obj.SetActive(false);
            foreach (var pair in sprayEvents)
            {
                var evt = Latest(pair.Value, time);
                if (evt != null && evt.Data.TryGetValue("visible", out var value) && value == "true")
                    ApplySpray(evt, true);
            }
        }

        private static ReplayEvent? Latest(List<ReplayEvent> history, double time)
        {
            var low = 0;
            var high = history.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (history[mid].Time <= time) low = mid + 1;
                else high = mid;
            }
            return low == 0 ? null : history[low - 1];
        }

        private static int UpperBound(ReplayEvent[] history, double time)
        {
            var low = 0;
            var high = history.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (history[mid].Time <= time) low = mid + 1;
                else high = mid;
            }
            return low;
        }

        private void ApplySpray(ReplayEvent evt, bool visible)
        {
            if (!visible)
            {
                if (spray.TryGetValue(evt.EntityId, out var prior) && prior) prior.SetActive(false);
                return;
            }
            if (projectorType == null || !typeof(Behaviour).IsAssignableFrom(projectorType) ||
                !TryFloat(evt.Data, "x", out var x) || !TryFloat(evt.Data, "y", out var y) ||
                !TryFloat(evt.Data, "z", out var z) || !TryFloat(evt.Data, "qx", out var qx) ||
                !TryFloat(evt.Data, "qy", out var qy) || !TryFloat(evt.Data, "qz", out var qz) ||
                !TryFloat(evt.Data, "qw", out var qw)) return;
            if (!spray.TryGetValue(evt.EntityId, out var obj) || !obj)
            {
                obj = new GameObject("Recorded spray " + evt.EntityId) { layer = layer,
                    hideFlags = HideFlags.DontSave };
                obj.SetActive(false);
                obj.transform.SetParent(root, false);
                obj.AddComponent(projectorType);
                spray[evt.EntityId] = obj;
            }
            var position = new Vector3(x, y, z);
            var rotation = new Quaternion(qx, qy, qz, qw);
            evt.Data.TryGetValue("anchor", out var anchorId);
            var parent = anchorId != null && anchors.TryGetValue(anchorId, out var anchor) && anchor ? anchor : root;
            if (obj.transform.parent != parent) obj.transform.SetParent(parent, false);
            if (parent == root) obj.transform.SetPositionAndRotation(position, rotation);
            else { obj.transform.localPosition = position; obj.transform.localRotation = rotation; }
            var projector = obj.GetComponent(projectorType);
            if (!projector) return;
            var size = new Vector3(Read(evt.Data, "sx", 1), Read(evt.Data, "sy", 1), Read(evt.Data, "sz", 1));
            var pivot = new Vector3(Read(evt.Data, "px", 0), Read(evt.Data, "py", 0), Read(evt.Data, "pz", 0));
            Set(projector, "size", size);
            Set(projector, "pivot", pivot);
            Set(projector, "fadeFactor", 1f);
            Set(projector, "drawDistance", 1000f);
            var mask = projectorType.GetProperty("decalLayerMask", BindingFlags.Instance | BindingFlags.Public);
            if (mask != null) Set(projector, "decalLayerMask", Enum.ToObject(mask.PropertyType, 1));
            evt.Data.TryGetValue("material", out var materialName);
            var color = new Color(Read(evt.Data, "r", 1), Read(evt.Data, "g", 1),
                Read(evt.Data, "b", 1), Read(evt.Data, "a", 1));
            var material = ResolveMaterial(materialName ?? "", color);
            if (material) Set(projector, "material", material!);
            ((Behaviour)projector).enabled = true;
            obj.SetActive(true);
        }

        private Material? ResolveMaterial(string name, Color color)
        {
            if (materials.TryGetValue(name, out var known)) return known;
            // The installed game's spray materials are the best match and keep
            // their original projected alpha and HDRP decal shader passes.
            var original = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(item =>
                item && item.name == name && item.shader && item.shader.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0);
            if (original)
            {
                var copy = new Material(original!) { hideFlags = HideFlags.DontSave };
                materials[name] = copy;
                return copy;
            }
            if (!fallbackMaterial)
            {
                var template = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(item =>
                    item && item.shader && item.shader.name == "HDRP/Decal");
                var shader = template ? template!.shader : Shader.Find("HDRP/Decal");
                if (!shader) return null;
                fallbackMaterial = template ? new Material(template!) : new Material(shader);
                fallbackMaterial.name = "Replay spray fallback";
                fallbackMaterial.hideFlags = HideFlags.DontSave;
                fallbackTexture = new Texture2D(64, 64, TextureFormat.RGBA32, false, false)
                { hideFlags = HideFlags.DontSave };
                var pixels = new Color[64 * 64];
                for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
                {
                    var dx = (x - 31.5f) / 31.5f; var dy = (y - 31.5f) / 31.5f;
                    pixels[y * 64 + x] = new Color(1, 1, 1, Mathf.Clamp01((1f - Mathf.Sqrt(dx * dx + dy * dy)) * 8f));
                }
                fallbackTexture.SetPixels(pixels); fallbackTexture.Apply(false, true);
                if (fallbackMaterial.HasProperty("_BaseColorMap")) fallbackMaterial.SetTexture("_BaseColorMap", fallbackTexture);
                if (fallbackMaterial.HasProperty("_AffectBaseColor")) fallbackMaterial.SetFloat("_AffectBaseColor", 1);
                ReplayAppearance.ValidateHdrpMaterial(fallbackMaterial);
            }
            var key = "fallback:" + name + ":" + color.ToString();
            if (materials.TryGetValue(key, out known)) return known;
            var material = new Material(fallbackMaterial!) { hideFlags = HideFlags.DontSave };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            ReplayAppearance.ValidateHdrpMaterial(material);
            materials[key] = material;
            return material;
        }

        private static bool TryFloat(Dictionary<string, string> data, string key, out float result) =>
            float.TryParse(data.TryGetValue(key, out var value) ? value : null,
                NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            !float.IsNaN(result) && !float.IsInfinity(result);
        private static float Read(Dictionary<string, string> data, string key, float fallback) =>
            TryFloat(data, key, out var value) ? value : fallback;
        private static void Set(Component component, string name, object value)
        {
            try { component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.SetValue(component, value, null); }
            catch { /* HDRP property layouts can vary between game builds. */ }
        }

        public void Dispose()
        {
            foreach (var obj in spray.Values) if (obj) Object.Destroy(obj);
            foreach (var material in materials.Values) if (material) Object.Destroy(material);
            if (fallbackMaterial) Object.Destroy(fallbackMaterial);
            if (fallbackTexture) Object.Destroy(fallbackTexture);
            spray.Clear(); materials.Clear();
        }
    }
}
