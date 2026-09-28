using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // A private HDRP Volume on the replay layer restores the recorded sky, fog and
    // exposure without letting the main-menu Volume color the archived world.
    internal sealed class ReplayEnvironment : IDisposable
    {
        private static readonly HashSet<string> AllowedTypes = new HashSet<string>(StringComparer.Ordinal)
        { "VisualEnvironment", "GradientSky", "HDRISky", "PhysicallyBasedSky", "ProceduralSky", "Fog", "Exposure", "Tonemapping", "ColorAdjustments" };
        private readonly List<Object> owned = new List<Object>();
        private object? fogEnabled;
        private bool recordedFog;
        private object? gammaParameter;
        private readonly GameObject root;

        internal ReplayEnvironment(WorldSnapshot world, Transform parent, int layer)
        {
            root = new GameObject("Replay sky and fog") { layer = layer, hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            var volumeType = GameAccess.Type("UnityEngine.Rendering.Volume");
            var profileType = GameAccess.Type("UnityEngine.Rendering.VolumeProfile");
            if (volumeType == null || profileType == null || !typeof(Component).IsAssignableFrom(volumeType) ||
                !typeof(ScriptableObject).IsAssignableFrom(profileType)) return;
            var profile = ScriptableObject.CreateInstance(profileType);
            owned.Add(profile);
            var environment = world.Environment ?? new EnvironmentSnapshot();
            var components = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var snapshot in environment.Components)
            {
                if (!AllowedTypes.Contains(snapshot.Type)) continue;
                var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + snapshot.Type);
                if (type == null) continue;
                var component = Add(profile, type);
                if (component == null) continue;
                components[snapshot.Type] = component;
                foreach (var parameter in snapshot.Parameters) Apply(component, parameter);
            }
            Cubemap? cube = null;
            if (environment.SkyFaces.Count == 6) cube = RestoreSky(environment.SkyFaces);
            if (cube != null && components.TryGetValue("HDRISky", out var hdri))
            {
                owned.Add(cube);
                SetParameter(hdri, "hdriSky", cube);
            }
            var skyType = components.TryGetValue("VisualEnvironment", out var visual)
                ? Convert.ToInt32(GameAccess.Read(GameAccess.Read(visual, "skyType"), "value") ?? 0) : 0;
            var usableSky = skyType == 3 && components.ContainsKey("GradientSky") ||
                skyType == 4 && components.ContainsKey("PhysicallyBasedSky") ||
                skyType == 2 && components.ContainsKey("ProceduralSky") ||
                skyType == 1 && cube != null;
            if (!usableSky)
            {
                var visualType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.VisualEnvironment");
                var gradientType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.GradientSky");
                if (visual == null && visualType != null) visual = Add(profile, visualType);
                if (gradientType != null)
                {
                    var gradient = components.TryGetValue("GradientSky", out var existing) ? existing : Add(profile, gradientType);
                    if (gradient != null)
                    {
                        var rgba = environment.AmbientSkyColor;
                        var baseColor = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                        SetParameter(gradient, "top", baseColor * 1.15f);
                        SetParameter(gradient, "middle", baseColor * 0.9f);
                        SetParameter(gradient, "bottom", baseColor * 0.55f);
                        SetParameter(gradient, "gradientDiffusion", 1.4f);
                        if (visual != null) SetParameter(visual, "skyType", 3);
                    }
                }
            }
            if (components.TryGetValue("Fog", out var fog))
            {
                fogEnabled = GameAccess.Read(fog, "enabled");
                recordedFog = GameAccess.Read(fogEnabled, "value") is bool enabled && enabled;
                // The source Fog commonly uses SkyColor with a neutral white tint.
                // Reconstructed 8-bit sky faces lose HDR scattering color, which
                // leaves the replay haze pale even when the scene's local fog has a
                // strong recorded hue. Tint the sky fog from the dominant nonwhite
                // local volume, preserving the original density and sky variation.
                var colorSource = world.LocalFogs
                    .Where(local => local.Albedo != null && local.Albedo.Length == 4 &&
                        (Math.Abs(local.Albedo[0] - 1f) > .08f || Math.Abs(local.Albedo[1] - 1f) > .08f || Math.Abs(local.Albedo[2] - 1f) > .08f))
                    .OrderByDescending(local => (double)local.Size.X * local.Size.Y * local.Size.Z)
                    .FirstOrDefault();
                if (colorSource != null)
                    SetParameter(fog, "tint", new Color(colorSource.Albedo[0], colorSource.Albedo[1], colorSource.Albedo[2], 1f));
            }
            var volume = root.AddComponent(volumeType);
            SetMember(volume, "isGlobal", true);
            SetMember(volume, "priority", 100f);
            SetMember(volume, "weight", 1f);
            SetMember(volume, "sharedProfile", profile);

            var gammaType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.LiftGammaGain");
            if (gammaType != null)
            {
                var gammaObject = new GameObject("Replay gamma") { layer = layer, hideFlags = HideFlags.DontSave };
                gammaObject.transform.SetParent(root.transform, false);
                var gammaProfile = ScriptableObject.CreateInstance(profileType);
                owned.Add(gammaProfile);
                var gammaComponent = Add(gammaProfile, gammaType);
                gammaParameter = GameAccess.Read(gammaComponent, "gamma");
                var gammaVolume = gammaObject.AddComponent(volumeType);
                SetMember(gammaVolume, "isGlobal", true);
                SetMember(gammaVolume, "priority", 1000f);
                SetMember(gammaVolume, "weight", 1f);
                SetMember(gammaVolume, "sharedProfile", gammaProfile);
            }
        }

        internal void SetIndoor(bool indoor)
        {
            // The captured global height fog belongs to the exterior. Generated
            // rooms can sit hundreds of metres below its base height, making it
            // far denser there than in the actual game. Recorded local indoor fog
            // remains active independently in ReplayViewer.
            if (fogEnabled != null) SetValue(fogEnabled, recordedFog && !indoor);
        }

        internal void SetGamma(float gamma)
        {
            // HDRP's neutral gamma wheel is (1,1,1,0): RGB selects hue, W
            // adjusts the midtone power. Keep the wheel color neutral.
            if (gammaParameter != null) SetValue(gammaParameter, new Vector4(1f, 1f, 1f, Mathf.Clamp(gamma - 1f, -.5f, 1f)));
        }

        private static object? Add(Object profile, Type type)
        {
            try { return profile.GetType().GetMethod("Add", new[] { typeof(Type), typeof(bool) })?.Invoke(profile, new object[] { type, true }); }
            catch { return null; }
        }

        private static void Apply(object component, EnvironmentParameterSnapshot snapshot)
        {
            if (snapshot.Values == null) return;
            var values = snapshot.Values;
            object? value = null;
            switch (snapshot.Kind)
            {
                case "float" when values.Length == 1: value = values[0]; break;
                case "int" when values.Length == 1: value = (int)values[0]; break;
                case "bool" when values.Length == 1: value = values[0] > .5f; break;
                case "color" when values.Length == 4: value = new Color(values[0], values[1], values[2], values[3]); break;
                case "vector2" when values.Length == 2: value = new Vector2(values[0], values[1]); break;
                case "vector3" when values.Length == 3: value = new Vector3(values[0], values[1], values[2]); break;
                case "vector4" when values.Length == 4: value = new Vector4(values[0], values[1], values[2], values[3]); break;
                case "enum":
                    var field = component.GetType().GetField(snapshot.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var parameter = field?.GetValue(component);
                    var property = parameter?.GetType().GetProperty("value");
                    if (property?.PropertyType.IsEnum == true)
                        try { value = Enum.Parse(property.PropertyType, snapshot.Text); } catch { }
                    break;
            }
            if (value != null) SetParameter(component, snapshot.Name, value);
        }

        private static void SetParameter(object component, string name, object value)
        {
            try
            {
                var parameter = component.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(component);
                if (parameter != null) SetValue(parameter, value);
            }
            catch { }
        }

        private static void SetValue(object parameter, object value)
        {
            try
            {
                parameter.GetType().GetProperty("value")?.SetValue(parameter, value, null);
                parameter.GetType().GetProperty("overrideState")?.SetValue(parameter, true, null);
            }
            catch { }
        }

        private static void SetMember(object component, string name, object value)
        {
            try
            {
                var type = component.GetType();
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) field.SetValue(component, value);
                else type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(component, value, null);
            }
            catch { }
        }

        private static Cubemap? RestoreSky(IReadOnlyList<TextureSnapshot> faces)
        {
            if (faces.Count != 6 || faces[0].Width < 1) return null;
            var cube = new Cubemap(faces[0].Width, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            try
            {
                for (var i = 0; i < 6; i++)
                {
                    var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    try
                    {
                        if (!ImageConversion.LoadImage(image, faces[i].Png, false) || image.width != cube.width || image.height != cube.height)
                            throw new InvalidOperationException("Invalid recorded sky face.");
                        cube.SetPixels(image.GetPixels(), (CubemapFace)i);
                    }
                    finally { Object.Destroy(image); }
                }
                cube.Apply(false, false);
                return cube;
            }
            catch { Object.Destroy(cube); return null; }
        }

        public void Dispose()
        {
            if (root) { root.SetActive(false); Object.Destroy(root); }
            foreach (var asset in owned)
            {
                if (asset is ScriptableObject profile && GameAccess.Read(profile, "components") is IEnumerable components)
                    foreach (var component in components) if (component is Object instance && instance) Object.Destroy(instance);
                if (asset) Object.Destroy(asset);
            }
            owned.Clear();
        }
    }
}
