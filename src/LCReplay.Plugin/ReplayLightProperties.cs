using System;
using System.Collections.Generic;
using System.Reflection;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin
{
    // Only bounded visual settings are archived. Component references, callbacks,
    // light-layer masks and realtime baking controls never cross this boundary.
    internal static class ReplayLightProperties
    {
        private static readonly Dictionary<string, (float Min, float Max)> Floats =
            new Dictionary<string, (float, float)>(StringComparer.Ordinal)
            {
                ["shapeRadius"] = (0, 10000), ["shapeWidth"] = (0, 10000), ["shapeHeight"] = (0, 10000),
                ["aspectRatio"] = (.01f, 1000), ["innerSpotPercent"] = (0, 100),
                ["volumetricDimmer"] = (0, 16), ["volumetricShadowDimmer"] = (0, 16),
                ["fadeDistance"] = (0, 100000), ["volumetricFadeDistance"] = (0, 100000),
                ["maxSmoothness"] = (0, 1), ["angularDiameter"] = (0, 90),
                ["normalBias"] = (0, 64), ["slopeBias"] = (0, 64), ["shadowNearPlane"] = (0, 1000)
            };
        private static readonly HashSet<string> Bools = new HashSet<string>(StringComparer.Ordinal)
            { "applyRangeAttenuation", "affectDiffuse", "affectSpecular", "enableSpotReflector", "interactsWithSky" };
        private static readonly HashSet<string> Enums = new HashSet<string>(StringComparer.Ordinal)
            { "type", "spotLightShape", "areaLightShape" };

        internal static List<EnvironmentParameterSnapshot> Capture(Component? data)
        {
            var result = new List<EnvironmentParameterSnapshot>();
            if (!data) return result;
            foreach (var entry in Floats)
                if (GameAccess.Read(data, entry.Key) is float value && GameAccess.Finite(value))
                    result.Add(new EnvironmentParameterSnapshot { Name = entry.Key, Kind = "float",
                        Values = new[] { Mathf.Clamp(value, entry.Value.Min, entry.Value.Max) } });
            foreach (var name in Bools)
                if (GameAccess.Read(data, name) is bool value)
                    result.Add(new EnvironmentParameterSnapshot { Name = name, Kind = "bool", Values = new[] { value ? 1f : 0f } });
            foreach (var name in Enums)
            {
                var value = GameAccess.Read(data, name);
                if (value != null && value.GetType().IsEnum)
                    result.Add(new EnvironmentParameterSnapshot { Name = name, Kind = "enum", Text = value.ToString()! });
            }
            return result;
        }

        internal static void Apply(Component? data, IReadOnlyList<EnvironmentParameterSnapshot> parameters, Light light, float intensity)
        {
            if (!data) { light.intensity = intensity; return; }
            // Set shapes before intensity units; a box spot accepts Lux, while a
            // cone/point stores Unity's already-converted intensity in Candela.
            // HDRP represents rectangle/tube lights as Unity Point lights plus
            // its own Area type, so the Unity enum alone loses their shape.
            foreach (var parameter in parameters)
                if (parameter.Name == "type" && parameter.Kind == "enum") SetEnum(data!, "type", parameter.Text);
            foreach (var parameter in parameters)
                if (parameter.Name != "type" && parameter.Kind == "enum" && Enums.Contains(parameter.Name)) SetEnum(data!, parameter.Name, parameter.Text);
            foreach (var parameter in parameters)
            {
                if (parameter.Values.Length != 1) continue;
                var value = parameter.Values[0];
                if (!GameAccess.Finite(value)) continue;
                if (parameter.Kind == "float" && Floats.TryGetValue(parameter.Name, out var range))
                    Set(data!, parameter.Name, Mathf.Clamp(value, range.Min, range.Max));
                else if (parameter.Kind == "bool" && Bools.Contains(parameter.Name)) Set(data!, parameter.Name, value != 0);
            }
            var box = light.type == LightType.Spot && GameAccess.Read(data, "spotLightShape")?.ToString() == "Box";
            var area = GameAccess.Read(data, "type")?.ToString() == "Area";
            var unit = light.type == LightType.Directional || box ? "Lux" :
                area || light.type == LightType.Rectangle || light.type == LightType.Disc ? "Nits" : "Candela";
            SetEnum(data!, "lightUnit", unit);
            Set(data!, "intensity", intensity);
            light.intensity = intensity;
        }

        private static void Set(Component data, string name, object value)
        {
            try
            {
                var property = data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.CanWrite == true) property.SetValue(data, value, null);
                else data.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(data, value);
            }
            catch { /* Unsupported properties retain this pipeline's defaults. */ }
        }

        private static void SetEnum(Component data, string name, string value)
        {
            var property = data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite != true || !property.PropertyType.IsEnum || !Enum.IsDefined(property.PropertyType, value)) return;
            Set(data, name, Enum.Parse(property.PropertyType, value));
        }
    }
}
