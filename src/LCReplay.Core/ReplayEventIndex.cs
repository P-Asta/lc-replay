using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace LCReplay.Core
{
    // Small sidecar descriptors let a later window read current state instead
    // of decoding the recording's entire state-change history again.
    internal static class ReplayEventIndex
    {
        internal static string Category(ReplayEvent? evt)
        {
            if (evt == null) return "";
            if (evt.Category == "marker" && evt.Name == "bookmark") return "bookmark";
            if (evt.Category == "visual" && (evt.Name == "renderer" || evt.Name == "spray")) return "visual";
            if (evt.Category == "animation" && (evt.Name == "state" || evt.Name == "parameters" || evt.Name == "track")) return "animation";
            if (evt.Category == "item" && evt.Name == "pose") return "item";
            if (evt.Category == "postfx" && evt.Name == "component") return "postfx";
            if (evt.Category == "lighting" && evt.Name == "sun") return "lighting";
            if (evt.Category == "sound" && (evt.Name == "stop" || evt.Data.GetValueOrDefault("loop") == "true")) return "sound";
            if (evt.Category == "state" && evt.Name == "isPlayerDead" &&
                string.Equals(evt.Data.GetValueOrDefault("to"), "True", StringComparison.OrdinalIgnoreCase)) return "death";
            return "";
        }
        internal static byte Code(string category) => category == "visual" ? (byte)1 : category == "animation" ? (byte)2 :
            category == "item" ? (byte)3 : category == "postfx" ? (byte)4 : category == "sound" ? (byte)5 : category == "death" ? (byte)6 : category == "bookmark" ? (byte)7 : category == "lighting" ? (byte)8 : (byte)0;
        internal static string Name(byte code) => code == 1 ? "visual" : code == 2 ? "animation" : code == 3 ? "item" :
            code == 4 ? "postfx" : code == 5 ? "sound" : code == 6 ? "death" : code == 7 ? "bookmark" : code == 8 ? "lighting" : "";
        internal static string Key(ReplayEvent? evt)
        {
            var category = Category(evt);
            if (evt == null || category.Length == 0 || category == "death" || category == "bookmark") return "";
            if (category == "postfx") return evt.PostProcess?.Type ?? "";
            var tokens = new List<string> { category, evt.Name, evt.EntityId };
            if (category == "animation")
            {
                tokens.Add(evt.AnimationTrack?.AnimatorPath ?? evt.Data.GetValueOrDefault("animatorPath", ""));
                if (evt.Name == "parameters") tokens.AddRange(evt.Data.Keys.Where(key => key.Length > 1 &&
                    (key[0] == 'b' || key[0] == 'f' || key[0] == 'i') && int.TryParse(key.Substring(1), out _)).OrderBy(key => key, StringComparer.Ordinal));
                else
                {
                    tokens.Add(evt.AnimationTrack?.Layer.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? evt.Data.GetValueOrDefault("layer", "0"));
                    if (evt.Name == "track")
                    {
                        tokens.Add(evt.AnimationTrack?.StateHash.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
                        tokens.Add(evt.AnimationTrack?.Clip ?? "");
                    }
                }
            }
            else if (category == "visual") { tokens.Add(evt.Data.GetValueOrDefault("set", "")); tokens.Add(evt.Data.GetValueOrDefault("asset", "false")); }
            else if (category == "sound") tokens = new List<string> { category, "loop", evt.Data.GetValueOrDefault("source", ""), evt.EntityId };
            return JsonConvert.SerializeObject(tokens);
        }
        internal static IEnumerable<string> CarryKeys(string category, string key, ISet<string>? actors = null)
        {
            if (category == "postfx") { yield return "postfx:" + key; yield break; }
            if (key.Length == 0 || category == "death" || category == "bookmark") yield break;
            var tokens = JsonConvert.DeserializeObject<string[]>(key) ?? Array.Empty<string>();
            if (actors != null)
            {
                var owner = (category == "animation" || category == "item") && tokens.Length >= 3 ? tokens[2] :
                    category == "sound" && tokens.Length >= 4 ? tokens[3] : "";
                if (owner.Length != 0 && !actors.Contains(owner)) yield break;
            }
            if (tokens.Length >= 4 && tokens[0] == "animation" && tokens[1] == "parameters")
            {
                var binding = JsonConvert.SerializeObject(tokens.Take(4));
                foreach (var parameter in tokens.Skip(4)) yield return binding + ":" + parameter;
            }
            else yield return key;
        }
    }
}
