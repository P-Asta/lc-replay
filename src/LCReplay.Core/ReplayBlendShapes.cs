using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LCReplay.Core
{
    // Optional visual outputs captured from installed enemy skins. Ordinary
    // entity state remains discrete; these bounded numeric channels can blend.
    internal static class ReplayBlendShapes
    {
        internal const string Prefix = "$blendshape:";

        internal sealed class Cache
        {
            private Dictionary<string, float[]?>? values;
            internal float[]? Parse(string text)
            {
                if (values != null && values.TryGetValue(text, out var existing)) return existing;
                var parsed = ParseWeights(text);
                if (values == null) values = new Dictionary<string, float[]?>(StringComparer.Ordinal);
                if (values.Count >= 256) values.Clear();
                values[text] = parsed;
                return parsed;
            }
        }

        internal static Dictionary<string, string>? Interpolate(EntitySnapshot left, EntitySnapshot right,
            double amount, Cache? cache)
        {
            if (!left.Active || !right.Active || left.Kind != right.Kind || amount <= 0 || amount >= 1) return null;
            Dictionary<string, string>? result = null;
            var renderers = 0; var weights = 0; var checkedForms = false;
            foreach (var pair in left.State)
            {
                if (!pair.Key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                if (++renderers > 32) break;
                if (!right.State.TryGetValue(pair.Key, out var next) || pair.Value == next) continue;
                if (!checkedForms)
                {
                    // A visible form changes on its recorded frame boundary.
                    // Avoid blending expressions across a renderer activation
                    // change when an entity itself remains active.
                    if (!SameForms(left, right)) return null;
                    checkedForms = true;
                }
                var before = cache == null ? ParseWeights(pair.Value) : cache.Parse(pair.Value);
                var after = cache == null ? ParseWeights(next) : cache.Parse(next);
                if (before == null || after == null || before.Length != after.Length) continue;
                weights += before.Length;
                if (weights > 128) break;
                var text = new StringBuilder(before.Length * 6);
                for (var index = 0; index < before.Length; index++)
                {
                    if (index != 0) text.Append(',');
                    var value = (float)(before[index] + ((double)after[index] - before[index]) * amount);
                    text.Append(value.ToString("R", CultureInfo.InvariantCulture));
                }
                if (result == null) result = new Dictionary<string, string>(left.State, StringComparer.Ordinal);
                result[pair.Key] = text.ToString();
            }
            return result;
        }

        private static bool SameForms(EntitySnapshot left, EntitySnapshot right)
        {
            if (left.Renderers.Count != right.Renderers.Count) return false;
            for (var index = 0; index < left.Renderers.Count; index++)
            {
                var before = left.Renderers[index];
                if (before.Id == right.Renderers[index].Id)
                { if (before.Active != right.Renderers[index].Active) return false; continue; }
                var found = false;
                foreach (var after in right.Renderers)
                    if (before.Id == after.Id)
                    { if (before.Active != after.Active) return false; found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        private static float[]? ParseWeights(string text)
        {
            if (text.Length == 0 || text.Length > 4096) return null;
            var parts = text.Split(new[] { ',' }, 129);
            if (parts.Length > 128) return null;
            var values = new float[parts.Length];
            for (var index = 0; index < parts.Length; index++)
                if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                    float.IsNaN(values[index]) || float.IsInfinity(values[index])) return null;
            return values;
        }
    }
}
