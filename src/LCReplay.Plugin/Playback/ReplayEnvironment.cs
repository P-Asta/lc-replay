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
    // A private HDRP Volume on the replay layer restores the blended settings
    // of the game camera without letting the main-menu Volume color the world.
    internal sealed class ReplayEnvironment : IDisposable
    {
        private static readonly HashSet<string> AllowedTypes = new HashSet<string>(StringComparer.Ordinal)
        { "VisualEnvironment", "GradientSky", "HDRISky", "PhysicallyBasedSky", "ProceduralSky", "Fog", "Exposure", "Tonemapping", "ColorAdjustments",
          "HDShadowSettings", "ContactShadows", "ScreenSpaceAmbientOcclusion", "ScreenSpaceReflection", "Bloom", "PaniniProjection", "FilmGrain",
          "ColorCurves", "SplitToning", "LiftGammaGain", "VolumetricClouds", "CloudLayer" };
        private readonly List<Object> owned = new List<Object>();
        private readonly Dictionary<string, object> components = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, EnvironmentComponentSnapshot> baseline =
            new Dictionary<string, EnvironmentComponentSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, EnvironmentComponentSnapshot> applied =
            new Dictionary<string, EnvironmentComponentSnapshot>(StringComparer.Ordinal);
        private Object? profile;
        private Cubemap? restoredSky;
        private float[] ambientSkyColor = { .15f, .20f, .28f, 1f };
        private float requestedGamma = 1f;
        private object? fogEnabled;
        private bool recordedFog;
        private bool showFog = true;
        private bool indoor;
        private bool preLandingOrbit;
        private object? gammaParameter;
        private readonly GameObject root;
        private readonly bool worldOnlyPostFx;

        internal ReplayEnvironment(WorldSnapshot world, Transform parent, int layer, bool worldOnlyPostFx = false)
        {
            this.worldOnlyPostFx = worldOnlyPostFx;
            root = new GameObject("Replay sky and fog") { layer = layer, hideFlags = HideFlags.DontSave };
            root.transform.SetParent(parent, false);
            var volumeType = GameAccess.Type("UnityEngine.Rendering.Volume");
            var profileType = GameAccess.Type("UnityEngine.Rendering.VolumeProfile");
            if (volumeType == null || profileType == null || !typeof(Component).IsAssignableFrom(volumeType) ||
                !typeof(ScriptableObject).IsAssignableFrom(profileType)) return;
            profile = ScriptableObject.CreateInstance(profileType);
            owned.Add(profile);
            var environment = world.Environment ?? new EnvironmentSnapshot();
            ambientSkyColor = environment.AmbientSkyColor;
            RestoreCustomPasses(environment, layer);
            foreach (var snapshot in environment.Components)
            {
                if (!AllowedTypes.Contains(snapshot.Type)) continue;
                var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + snapshot.Type);
                if (type == null) continue;
                var component = Add(profile, type);
                if (component == null) continue;
                components[snapshot.Type] = component;
                baseline[snapshot.Type] = snapshot;
                applied[snapshot.Type] = snapshot;
                foreach (var parameter in snapshot.Parameters) Apply(component, parameter);
            }
            Cubemap? cube = null;
            if (environment.SkyFaces.Count == 6) cube = RestoreSky(environment.SkyFaces, environment.SkyFaceEncoding);
            if (cube != null && components.TryGetValue("HDRISky", out var hdri))
            {
                owned.Add(cube);
                restoredSky = cube;
                SetParameter(hdri, "hdriSky", cube);
            }
            else if (cube != null) { Object.Destroy(cube); cube = null; }
            EnsureSky();
            if (components.TryGetValue("Fog", out var fog))
            {
                fogEnabled = GameAccess.Read(fog, "enabled");
                recordedFog = GameAccess.Read(fogEnabled, "value") is bool enabled && enabled;
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

        private void EnsureSky()
        {
            components.TryGetValue("VisualEnvironment", out var visual);
            var skyType = visual != null
                ? Convert.ToInt32(GameAccess.Read(GameAccess.Read(visual, "skyType"), "value") ?? 0) : 0;
            var hdrSkyActive = restoredSky != null && components.TryGetValue("HDRISky", out var hdri) &&
                !(GameAccess.Read(hdri, "active") is bool active && !active);
            var usableSky = skyType == 3 && components.ContainsKey("GradientSky") ||
                skyType == 4 && components.ContainsKey("PhysicallyBasedSky") ||
                skyType == 2 && components.ContainsKey("ProceduralSky") ||
                skyType == 1 && hdrSkyActive;
            if (!usableSky)
            {
                var visualType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.VisualEnvironment");
                var gradientType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.GradientSky");
                if (visual == null && visualType != null)
                {
                    visual = Add(profile!, visualType);
                    if (visual != null) components["VisualEnvironment"] = visual;
                }
                if (gradientType != null)
                {
                    var gradient = components.TryGetValue("GradientSky", out var existing) ? existing : Add(profile!, gradientType);
                    if (gradient != null)
                    {
                        components["GradientSky"] = gradient;
                        var rgba = ambientSkyColor;
                        var baseColor = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                        SetParameter(gradient, "top", baseColor * 1.15f);
                        SetParameter(gradient, "middle", baseColor * 0.9f);
                        SetParameter(gradient, "bottom", baseColor * 0.55f);
                        SetParameter(gradient, "gradientDiffusion", 1.4f);
                        SetMember(gradient, "active", true);
                        if (visual != null)
                        { SetParameter(visual, "skyType", 3); SetMember(visual, "active", true); }
                    }
                }
            }
        }

        internal void SetIndoor(bool indoor, bool preLandingOrbit = false)
        {
            this.indoor = indoor;
            this.preLandingOrbit = preLandingOrbit;
            // The captured global height fog belongs to the exterior. Generated
            // rooms can sit hundreds of metres below its base height, making it
            // far denser there than in the actual game. Before the ship departs,
            // the same moon fog also washes out an exterior free camera in orbit.
            // Recorded local indoor fog remains active independently in ReplayViewer.
            if (fogEnabled != null) SetValue(fogEnabled, showFog && recordedFog && !indoor && !preLandingOrbit);
        }

        internal void SetFogEnabled(bool enabled)
        {
            showFog = enabled;
            SetIndoor(indoor, preLandingOrbit);
        }

        internal void SetGamma(float gamma)
        {
            requestedGamma = gamma;
            // Preserve the game's gamma wheel, then add the replay viewer's
            // optional adjustment to W without changing the recorded hue.
            var source = components.TryGetValue("LiftGammaGain", out var lift)
                ? GameAccess.Read(GameAccess.Read(lift, "gamma"), "value") : null;
            var baseGamma = source is Vector4 wheel ? wheel : new Vector4(1f, 1f, 1f, 0f);
            if (gammaParameter != null)
                SetValue(gammaParameter, new Vector4(baseGamma.x, baseGamma.y, baseGamma.z,
                    baseGamma.w + Mathf.Clamp(gamma - 1f, -.5f, 1f)));
        }

        internal void SyncPostProcess(string typeName, EnvironmentComponentSnapshot? change)
        {
            if (!AllowedTypes.Contains(typeName) || profile == null) return;
            // Older files blended HUD status volumes with world volumes. Their
            // changing color/blur filters cannot be separated after recording;
            // retain the initial grading instead of replaying those status events.
            if (!worldOnlyPostFx && (typeName == "Exposure" || typeName == "ColorAdjustments" ||
                typeName == "ColorCurves" || typeName == "SplitToning" || typeName == "LiftGammaGain" || typeName == "Bloom")) return;
            var target = change;
            if (target == null && !baseline.TryGetValue(typeName, out target))
            {
                if (!components.ContainsKey(typeName)) return;
                target = new EnvironmentComponentSnapshot { Type = typeName };
            }
            if (applied.TryGetValue(typeName, out var previous) && ReferenceEquals(previous, target)) return;
            if (!components.TryGetValue(typeName, out var component))
            {
                var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + typeName);
                component = type == null ? null : Add(profile, type);
                if (component == null) return;
                components[typeName] = component;
            }
            foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (GameAccess.Read(field.GetValue(component), "overrideState") is bool)
                    SetMember(field.GetValue(component)!, "overrideState", false);
            foreach (var parameter in target.Parameters) Apply(component, parameter);
            SetMember(component, "active", target.Parameters.Count != 0);
            applied[typeName] = target;
            if (typeName == "HDRISky" && restoredSky != null) SetParameter(component, "hdriSky", restoredSky);
            if (typeName == "HDRISky" || typeName == "VisualEnvironment" || typeName == "GradientSky") EnsureSky();
            if (typeName == "Fog")
            {
                fogEnabled = GameAccess.Read(component, "enabled");
                recordedFog = target.Parameters.Count != 0 && GameAccess.Read(fogEnabled, "value") is bool enabled && enabled;
                SetIndoor(indoor, preLandingOrbit);
            }
            if (typeName == "LiftGammaGain") SetGamma(requestedGamma);
        }

        private static object? Add(Object profile, Type type)
        {
            try { return profile.GetType().GetMethod("Add", new[] { typeof(Type), typeof(bool) })?.Invoke(profile, new object[] { type, true }); }
            catch { return null; }
        }

        private void RestoreCustomPasses(EnvironmentSnapshot environment, int layer)
        {
            var passes = environment.CustomPasses;
            if (!environment.CustomPassCaptureComplete && passes.Count == 0 &&
                Shader.Find("FullScreen/SpongePosterizeNew"))
                passes = new List<PostProcessPassSnapshot> { LegacyOutline() };
            var volumeType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.CustomPassVolume");
            var passType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.FullScreenCustomPass");
            if (volumeType == null || passType == null || !typeof(Component).IsAssignableFrom(volumeType)) return;
            foreach (var group in passes.GroupBy(pass => pass.InjectionPoint))
            {
                var host = new GameObject("Replay custom post process") { layer = layer, hideFlags = HideFlags.DontSave };
                host.SetActive(false);
                host.transform.SetParent(root.transform, false);
                var volume = host.AddComponent(volumeType);
                SetMember(volume, "isGlobal", true);
                SetOptionalEnum(volume, "injectionPoint", group.Key);
                var list = GameAccess.Read(volume, "customPasses") as IList;
                if (list == null) { Object.Destroy(host); continue; }
                foreach (var snapshot in group)
                {
                    // This game pass reads scene buffers that the detached replay camera
                    // cannot reproduce. Running it here paints large black patches across
                    // otherwise correctly lit ship and facility surfaces.
                    if (snapshot.ShaderName == "FullScreen/SpongePosterizeNew") continue;
                    var shader = Shader.Find(snapshot.ShaderName) ?? Resources.FindObjectsOfTypeAll<Shader>()
                        .FirstOrDefault(item => item && item.name == snapshot.ShaderName);
                    if (!shader || !shader!.isSupported) continue;
                    // Fullscreen shader defaults omit the game's material keywords,
                    // textures and hidden render state. Copy the installed pass
                    // material first, then apply the recorded changing scalars.
                    var sourceMaterial = Resources.FindObjectsOfTypeAll<Material>()
                        .Where(item => item && item.shader == shader &&
                            !item.name.StartsWith("Replay ", StringComparison.Ordinal) &&
                            !item.name.StartsWith("LC Replay", StringComparison.Ordinal))
                        .OrderByDescending(item => snapshot.MaterialName.Length != 0 &&
                            string.Equals(item.name.Replace(" (Instance)", ""),
                                snapshot.MaterialName.Replace(" (Instance)", ""), StringComparison.Ordinal))
                        .FirstOrDefault();
                    var material = sourceMaterial ? new Material(sourceMaterial!) : new Material(shader);
                    material.name = "Replay " + snapshot.Name;
                    material.hideFlags = HideFlags.DontSave;
                    foreach (var property in snapshot.Properties)
                    {
                        if (!material.HasProperty(property.Name)) continue;
                        try
                        {
                            if (property.Kind == "float" && property.Values.Length == 1)
                                material.SetFloat(property.Name, property.Values[0]);
                            else if (property.Kind == "color" && property.Values.Length == 4)
                                material.SetColor(property.Name, new Color(property.Values[0], property.Values[1], property.Values[2], property.Values[3]));
                            else if (property.Kind == "vector4" && property.Values.Length == 4)
                                material.SetVector(property.Name, new Vector4(property.Values[0], property.Values[1], property.Values[2], property.Values[3]));
                        }
                        catch { }
                    }
                    var pass = Activator.CreateInstance(passType, true);
                    if (pass == null) { Object.Destroy(material); continue; }
                    SetMember(pass, "name", snapshot.Name);
                    SetMember(pass, "enabled", true);
                    SetMember(pass, "fullscreenPassMaterial", material);
                    SetMember(pass, "materialPassName", snapshot.MaterialPassName);
                    SetMember(pass, "fetchColorBuffer", snapshot.FetchColorBuffer ?? snapshot.MaterialPassName == "ReadColor");
                    list.Add(pass);
                    owned.Add(material);
                }
                if (list.Count == 0) Object.Destroy(host);
                else host.SetActive(true);
            }
        }

        private static PostProcessPassSnapshot LegacyOutline()
        {
            var pass = new PostProcessPassSnapshot { Name = "LethalSponge", ShaderName = "FullScreen/SpongePosterizeNew",
                InjectionPoint = "BeforeTransparent", MaterialPassName = "ReadColor" };
            foreach (var pair in new[] { ("_OutlineThickness", .001f), ("_DepthThreshold", .4f),
                ("_DepthCurve", .4f), ("_DepthStrength", 6f), ("_ColorThreshold", .47f),
                ("_ColorCurve", 2.94f), ("_ColorStrength", .65f) })
                pass.Properties.Add(new EnvironmentParameterSnapshot { Name = pair.Item1, Kind = "float", Values = new[] { pair.Item2 } });
            return pass;
        }

        private static void SetOptionalEnum(object component, string name, string text)
        {
            try
            {
                var type = component.GetType();
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.FieldType.IsEnum) field.SetValue(component, Enum.Parse(field.FieldType, text));
                else
                {
                    var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property?.PropertyType.IsEnum == true) property.SetValue(component, Enum.Parse(property.PropertyType, text), null);
                }
            }
            catch { }
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
                case "curve" when values.Length == 3 && snapshot.CurveKeys.Length % 7 == 0:
                    value = RestoreCurve(component, snapshot); break;
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

        private static object? RestoreCurve(object component, EnvironmentParameterSnapshot snapshot)
        {
            try
            {
                var parameter = component.GetType().GetField(snapshot.Name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(component);
                var curveType = parameter?.GetType().GetProperty("value")?.PropertyType;
                if (curveType == null || curveType.Name != "TextureCurve") return null;
                var entries = snapshot.CurveKeys;
                var keys = new Keyframe[entries.Length / 7];
                for (var index = 0; index < keys.Length; index++)
                {
                    var offset = index * 7;
                    keys[index] = new Keyframe(entries[offset], entries[offset + 1], entries[offset + 2],
                        entries[offset + 3], entries[offset + 4], entries[offset + 5])
                    { weightedMode = (WeightedMode)(int)entries[offset + 6] };
                }
                var constructor = curveType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(candidate => candidate.GetParameters().Length == 4 &&
                        candidate.GetParameters()[0].ParameterType == typeof(Keyframe[]));
                return constructor?.Invoke(new object[] { keys, snapshot.Values[0], snapshot.Values[2] > .5f,
                    new Vector2(0f, snapshot.Values[1]) });
            }
            catch { return null; }
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

        private static Cubemap? RestoreSky(IReadOnlyList<TextureSnapshot> faces, string encoding)
        {
            if (faces.Count != 6 || faces[0].Width < 1) return null;
            var rgbe = encoding == "rgbe8";
            if (rgbe && !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf)) return null;
            var cube = new Cubemap(faces[0].Width, rgbe ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.DontSave };
            try
            {
                var hasColor = false;
                for (var i = 0; i < 6; i++)
                {
                    var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, rgbe);
                    try
                    {
                        if (!ImageConversion.LoadImage(image, faces[i].Png, false) || image.width != cube.width || image.height != cube.height)
                            throw new InvalidOperationException("Invalid recorded sky face.");
                        Color[] pixels;
                        if (rgbe)
                        {
                            var encoded = image.GetPixels32();
                            pixels = new Color[encoded.Length];
                            for (var pixel = 0; pixel < encoded.Length; pixel++)
                            {
                                var value = encoded[pixel];
                                ReplaySkyRgbe.Decode(value.r, value.g, value.b, value.a,
                                    out var red, out var green, out var blue);
                                pixels[pixel] = new Color(red, green, blue, 1f);
                            }
                        }
                        else pixels = image.GetPixels();
                        foreach (var pixel in pixels)
                            hasColor |= pixel.r > 0 || pixel.g > 0 || pixel.b > 0;
                        cube.SetPixels(pixels, (CubemapFace)i);
                    }
                    finally { Object.Destroy(image); }
                }
                // Old HDR skies were quantized to all-black 8-bit PNGs. They
                // must not suppress the ambient-color sky fallback.
                if (!hasColor) { Object.Destroy(cube); return null; }
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
