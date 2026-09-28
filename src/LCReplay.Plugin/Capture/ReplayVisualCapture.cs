using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    // Sparse visual changes: no repeated static mesh or spray texture in frames.
    internal sealed class ReplayVisualCapture
    {
        private readonly Dictionary<string, TrackedRenderer> renderers = new Dictionary<string, TrackedRenderer>(StringComparer.Ordinal);
        private readonly Dictionary<int, SprayState> decals = new Dictionary<int, SprayState>();
        private double nextScan;

        internal void Reset()
        {
            renderers.Clear();
            decals.Clear();
            nextScan = 0;
        }

        internal void Track(Renderer renderer, string id, bool asset, bool cullerManaged)
        {
            if (!renderer || renderers.Count >= 8192 || renderers.ContainsKey(id)) return;
            renderers.Add(id, new TrackedRenderer(renderer, asset, cullerManaged,
                renderer.gameObject.activeInHierarchy && (cullerManaged || renderer.enabled)));
        }

        internal IEnumerable<ReplayEvent> Scan(double now, string captureSetId)
        {
            if (captureSetId.Length == 0 || now < nextScan) yield break;
            nextScan = now + .2;
            foreach (var pair in renderers)
            {
                var tracked = pair.Value;
                var renderer = tracked.Renderer;
                var visible = renderer && renderer.gameObject.activeInHierarchy &&
                    (tracked.CullerManaged || renderer.enabled);
                if (visible == tracked.Visible) continue;
                tracked.Visible = visible;
                yield return new ReplayEvent { Time = now, Category = "visual", Name = "renderer",
                    EntityId = pair.Key, Data = new Dictionary<string, string>
                    {
                        ["set"] = captureSetId, ["asset"] = tracked.Asset ? "true" : "false",
                        ["visible"] = visible ? "true" : "false"
                    } };
            }
            var list = GameAccess.Read(GameAccess.Type("SprayPaintItem"), "sprayPaintDecals") as IList;
            if (list == null) yield break;
            var anchors = WorldCapture.MovingAnchors(GameAccess.Singleton("StartOfRound")).ToArray();
            var count = Math.Min(list.Count, 1000);
            for (var index = 0; index < count; index++)
            {
                if (!(list[index] is GameObject obj)) continue;
                if (!obj)
                {
                    if (decals.TryGetValue(index, out var vanished) && vanished.Active)
                    {
                        decals[index] = new SprayState(false, Vector3.zero, Quaternion.identity, 0, "");
                        yield return new ReplayEvent { Time = now, Category = "visual", Name = "spray",
                            EntityId = index.ToString(CultureInfo.InvariantCulture), Data = new Dictionary<string, string>
                            { ["set"] = captureSetId, ["visible"] = "false" } };
                    }
                    continue;
                }
                var projector = GameAccess.Type("UnityEngine.Rendering.HighDefinition.DecalProjector");
                var component = projector == null ? null : obj.GetComponent(projector);
                var active = obj && obj.activeInHierarchy && component is Behaviour behaviour && behaviour.enabled;
                var anchor = active ? anchors.FirstOrDefault(pair => pair.Value && obj.transform.IsChildOf(pair.Value)) : default;
                var position = active && anchor.Value ? anchor.Value.InverseTransformPoint(obj.transform.position) :
                    active ? obj.transform.position : Vector3.zero;
                var rotation = active && anchor.Value ? Quaternion.Inverse(anchor.Value.rotation) * obj.transform.rotation :
                    active ? obj.transform.rotation : Quaternion.identity;
                var sourceMaterial = GameAccess.Read(component, "material") as Material;
                var state = new SprayState(active, position, rotation,
                    sourceMaterial ? sourceMaterial!.GetInstanceID() : 0, anchor.Value ? anchor.Key : "");
                var existed = decals.TryGetValue(index, out var previous);
                if (existed && previous.SameAs(state)) continue;
                decals[index] = state;
                if (!active)
                {
                    if (existed && previous.Active)
                        yield return new ReplayEvent { Time = now, Category = "visual", Name = "spray",
                            EntityId = index.ToString(CultureInfo.InvariantCulture), Data = new Dictionary<string, string>
                            { ["set"] = captureSetId, ["visible"] = "false" } };
                    continue;
                }
                if (!GameAccess.Finite(position) || !GameAccess.Finite(rotation)) continue;
                var material = sourceMaterial;
                var size = GameAccess.Read(component, "size") is Vector3 extent ? extent : Vector3.one;
                var pivot = GameAccess.Read(component, "pivot") is Vector3 offset ? offset : Vector3.zero;
                var color = Color.white;
                if (material != null && material)
                    foreach (var property in new[] { "_BaseColor", "_Color" })
                        if (material!.HasProperty(property)) { color = material.GetColor(property); break; }
                yield return new ReplayEvent { Time = now, Category = "visual", Name = "spray",
                    EntityId = index.ToString(CultureInfo.InvariantCulture), Data = new Dictionary<string, string>
                    {
                        ["set"] = captureSetId, ["visible"] = "true",
                        ["anchor"] = anchor.Value ? anchor.Key : "",
                        ["material"] = material != null && material ? material!.name : "",
                        ["x"] = F(position.x), ["y"] = F(position.y), ["z"] = F(position.z),
                        ["qx"] = F(rotation.x), ["qy"] = F(rotation.y), ["qz"] = F(rotation.z), ["qw"] = F(rotation.w),
                        ["sx"] = F(size.x), ["sy"] = F(size.y), ["sz"] = F(size.z),
                        ["px"] = F(pivot.x), ["py"] = F(pivot.y), ["pz"] = F(pivot.z),
                        ["r"] = F(color.r), ["g"] = F(color.g), ["b"] = F(color.b), ["a"] = F(color.a)
                    } };
            }
        }

        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private readonly struct SprayState
        {
            internal readonly bool Active;
            private readonly Vector3 position;
            private readonly Quaternion rotation;
            private readonly int material;
            private readonly string anchor;
            internal SprayState(bool active, Vector3 position, Quaternion rotation, int material, string anchor)
            { Active = active; this.position = position; this.rotation = rotation; this.material = material; this.anchor = anchor; }
            internal bool SameAs(SprayState other) => Active == other.Active &&
                (!Active || material == other.material && anchor == other.anchor &&
                 (position - other.position).sqrMagnitude < .000025f &&
                 Mathf.Abs(Quaternion.Dot(rotation, other.rotation)) > .99999f);
        }

        private sealed class TrackedRenderer
        {
            internal readonly Renderer Renderer;
            internal readonly bool Asset, CullerManaged;
            internal bool Visible;
            internal TrackedRenderer(Renderer renderer, bool asset, bool cullerManaged, bool visible)
            { Renderer = renderer; Asset = asset; CullerManaged = cullerManaged; Visible = visible; }
        }
    }
}
