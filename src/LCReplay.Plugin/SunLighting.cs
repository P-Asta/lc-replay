using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin
{
    // Two sunlight states, sampled once per second. No lightmap or texture data.
    internal sealed class SunLighting
    {
        internal double Time;
        internal string Id = "", Name = "";
        internal float Intensity, Dimmer, ShadowDimmer;
        internal Color Color;
        internal Quaternion Rotation;

        internal static SunLighting Capture(Light light, double time)
        {
            var hdType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
            var hd = hdType == null ? null : light.GetComponent(hdType);
            return new SunLighting { Time = time, Id = "l" + light.GetInstanceID(), Name = light.name,
                Intensity = light.intensity, Color = light.color, Rotation = light.transform.rotation,
                Dimmer = GameAccess.Read(hd, "lightDimmer") is float dimmer ? dimmer : 1f,
                ShadowDimmer = GameAccess.Read(hd, "shadowDimmer") is float shadow ? shadow : 1f };
        }
        internal bool Changed(SunLighting previous) => Id != previous.Id ||
            Mathf.Abs(Intensity - previous.Intensity) > Math.Max(.001f, Math.Abs(previous.Intensity) * .001f) ||
            Mathf.Abs(Dimmer - previous.Dimmer) > .001f || Mathf.Abs(ShadowDimmer - previous.ShadowDimmer) > .001f ||
            Quaternion.Angle(Rotation, previous.Rotation) > .05f || ((Vector4)Color - (Vector4)previous.Color).sqrMagnitude > .000001f;

        private static string Numbers(params float[] values) => string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        internal ReplayEvent Event(string role) => new ReplayEvent { Time = Time, Category = "lighting", Name = "sun", EntityId = role,
            Data = new Dictionary<string, string> { ["id"] = Id, ["name"] = Name,
                ["values"] = Numbers(Intensity, Dimmer, ShadowDimmer, Color.r, Color.g, Color.b, Color.a,
                    Rotation.x, Rotation.y, Rotation.z, Rotation.w) } };
        internal static SunLighting? Read(ReplayEvent evt)
        {
            if (!evt.Data.TryGetValue("values", out var text) || text.Length > 512) return null;
            var fields = text.Split(',');
            if (fields.Length != 11) return null;
            var values = new float[11];
            for (var i = 0; i < fields.Length; i++)
                if (!float.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !GameAccess.Finite(values[i])) return null;
            if (values[0] < 0 || values[0] > 1000000 || values[1] < 0 || values[1] > 16 || values[2] < 0 || values[2] > 16) return null;
            var rotation = new Quaternion(values[7], values[8], values[9], values[10]);
            if (rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w < .1f) return null;
            return new SunLighting { Time = evt.Time, Id = evt.Data.GetValueOrDefault("id", ""), Name = evt.Data.GetValueOrDefault("name", ""),
                Intensity = values[0], Dimmer = values[1], ShadowDimmer = values[2],
                Color = new Color(values[3], values[4], values[5], values[6]), Rotation = rotation.normalized };
        }
        internal void Apply(Light light, SunLighting? next = null, float blend = 0f)
        {
            if (!light) return;
            next = next ?? this;
            light.color = UnityEngine.Color.Lerp(Color, next.Color, blend);
            light.transform.rotation = Quaternion.Slerp(Rotation, next.Rotation, blend);
            var intensity = Mathf.Lerp(Intensity, next.Intensity, blend);
            var hdType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalLightData");
            var hd = hdType == null ? null : light.GetComponent(hdType);
            if (hd != null)
            {
                var type = hd.GetType();
                type.GetProperty("intensity")?.SetValue(hd, intensity);
                type.GetProperty("lightDimmer")?.SetValue(hd, Mathf.Lerp(Dimmer, next.Dimmer, blend));
                type.GetProperty("shadowDimmer")?.SetValue(hd, Mathf.Lerp(ShadowDimmer, next.ShadowDimmer, blend));
            }
            light.intensity = intensity;
        }
    }
}
