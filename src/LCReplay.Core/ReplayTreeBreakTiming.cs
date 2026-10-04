using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace LCReplay.Core
{
    // Older world captures postponed visibility scans until mesh export ended.
    // A positional break sound may date an independently confirmed tree removal.
    public static class ReplayTreeBreakTiming
    {
        public static IEnumerable<ReplayEvent> Correct(WorldSnapshot world, IEnumerable<ReplayEvent> source)
        {
            var events = source.ToArray();
            var sounds = events.Where(e => e.Category == "sound" && e.Name == "play" &&
                e.Data.TryGetValue("clip", out var clip) && (clip == "BreakTree1" || clip == "BreakTree2") &&
                (!e.Data.TryGetValue("anchor", out var anchor) || anchor.Length == 0)).ToArray();
            if (sounds.Length == 0) return events;
            var geometry = world.Geometry.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
            var bases = new Dictionary<string, Vector3>();
            foreach (var evt in events)
            {
                if (evt.Category != "visual" || evt.Name != "renderer" ||
                    !evt.Data.TryGetValue("set", out var set) || set != world.CaptureSetId ||
                    !evt.Data.TryGetValue("visible", out var visible) || visible != "false" ||
                    !geometry.TryGetValue(evt.EntityId, out var tree) || tree.EntityId.Length != 0 ||
                    tree.Name.IndexOf("tree", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!bases.ContainsKey(tree.Id) && TryBase(tree, geometry, out var point)) bases.Add(tree.Id, point);
            }
            return events.Select(evt =>
            {
                if (evt.Category != "visual" || evt.Name != "renderer" ||
                    !evt.Data.TryGetValue("visible", out var visible) || visible != "false" ||
                    !evt.Data.TryGetValue("set", out var set) || set != world.CaptureSetId ||
                    !bases.TryGetValue(evt.EntityId, out var point)) return evt;
                var when = evt.Time;
                foreach (var sound in sounds)
                {
                    if (sound.Time >= when || sound.Time < 0 || evt.Time - sound.Time > 120 ||
                        !Number(sound, "x", out var x) || !Number(sound, "y", out var y) || !Number(sound, "z", out var z)) continue;
                    // Native tree debris/sound is emitted one metre above the
                    // destroyed root. Leafless LOD roots have small mesh offsets.
                    var dx = x - point.X; var dz = z - point.Z;
                    if (dx * dx + dz * dz > .75f * .75f || Math.Abs(y - point.Y - 1f) > .75f) continue;
                    when = sound.Time;
                }
                if (when == evt.Time) return evt;
                return new ReplayEvent { Time = when, Category = evt.Category, Name = evt.Name,
                    EntityId = evt.EntityId, Data = new Dictionary<string, string>(evt.Data) };
            }).ToArray();
        }
        private static bool Number(ReplayEvent evt, string key, out float value)
        {
            value = 0;
            return evt.Data.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }
        private static bool TryBase(GeometrySnapshot tree, Dictionary<string, GeometrySnapshot> all, out Vector3 point)
        {
            point = default;
            var mesh = tree;
            for (var depth = 0; depth < 16 && mesh.MeshSourceId.Length != 0 && all.TryGetValue(mesh.MeshSourceId, out var next); depth++) mesh = next;
            var vertices = mesh.Vertices;
            if (vertices.Length < 9 || vertices.Length % 3 != 0) return false;
            var rotation = new Quaternion(tree.Rotation.X, tree.Rotation.Y, tree.Rotation.Z, tree.Rotation.W);
            var offset = new Vector3(tree.Position.X, tree.Position.Y, tree.Position.Z);
            Vector3 Vertex(int i) => Vector3.Transform(new Vector3(vertices[i] * tree.Scale.X,
                vertices[i + 1] * tree.Scale.Y, vertices[i + 2] * tree.Scale.Z), rotation) + offset;
            var low = float.MaxValue; var high = float.MinValue;
            for (var i = 0; i < vertices.Length; i += 3) { var y = Vertex(i).Y; low = Math.Min(low, y); high = Math.Max(high, y); }
            if (high - low < 2) return false;
            var sum = Vector3.Zero; var count = 0;
            for (var i = 0; i < vertices.Length; i += 3)
            { var vertex = Vertex(i); if (vertex.Y <= low + .3f) { sum += vertex; count++; } }
            if (count == 0) return false;
            point = new Vector3(sum.X / count, low, sum.Z / count);
            return true;
        }
    }
}
