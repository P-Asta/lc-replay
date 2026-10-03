using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    // Capture the blended HDRP environment, not scene Volume objects whose lifetimes end
    // when the host disconnects. Only scalar rendering settings and optional sky pixels
    // enter the replay file; Unity assets and executable components are never serialized.
    internal static class EnvironmentCapture
    {
        private static readonly string[] Types = { "VisualEnvironment", "GradientSky", "HDRISky", "PhysicallyBasedSky",
            "ProceduralSky", "Fog", "Exposure", "Tonemapping", "ColorAdjustments", "HDShadowSettings",
            "ContactShadows", "ScreenSpaceAmbientOcclusion", "ScreenSpaceReflection", "Bloom", "PaniniProjection",
            "FilmGrain", "ColorCurves", "SplitToning", "LiftGammaGain", "VolumetricClouds", "CloudLayer" };
        private static readonly string[] ChangingTypes = { "VisualEnvironment", "GradientSky", "HDRISky", "PhysicallyBasedSky", "VolumetricClouds", "CloudLayer", "Fog", "HDShadowSettings", "Exposure", "Tonemapping", "ColorAdjustments",
            "ContactShadows", "ScreenSpaceAmbientOcclusion", "ScreenSpaceReflection", "Bloom", "PaniniProjection",
            "FilmGrain", "ColorCurves", "SplitToning", "LiftGammaGain" };

        // Own one independent volume stack for the recorder's lifetime. Updating
        // and reading it are separate work frames; personal filter weights are
        // always restored before returning to the game.
        internal sealed class ChangingCapture : IDisposable
        {
            private object? manager, stack;
            private MethodInfo? update, get;
            private int cursor = -1;
            internal bool Active => cursor >= 0;
            internal List<EnvironmentComponentSnapshot> Step()
            {
                var result = new List<EnvironmentComponentSnapshot>(4);
                try
                {
                    if (cursor < 0)
                    {
                        var camera = GameplayCamera();
                        if (!camera) return result;
                        if (stack == null)
                        {
                            manager = GameAccess.Read(GameAccess.Type("UnityEngine.Rendering.VolumeManager"), "instance");
                            if (manager == null) return result;
                            stack = manager.GetType().GetMethod("CreateStack", BindingFlags.Public | BindingFlags.Instance)?.Invoke(manager, null);
                            update = manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(method => method.Name == "Update" && method.GetParameters().Length == 3);
                            get = stack?.GetType().GetMethod("GetComponent", new[] { typeof(Type) });
                        }
                        if (stack == null || update == null) return result;
                        var dataType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                        var data = dataType == null ? null : camera!.GetComponent(dataType);
                        var mask = GameAccess.Read(data, "volumeLayerMask") ?? (LayerMask)1;
                        using (new PersonalFilterScope()) update.Invoke(manager, new[] { stack, camera!.transform, mask });
                        cursor = 0;
                        return result;
                    }
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (var count = 0; count < 4 && cursor < ChangingTypes.Length; count++)
                    {
                        var name = ChangingTypes[cursor++];
                        var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + name);
                        var component = type == null ? null : get?.Invoke(stack, new object[] { type });
                        result.Add(component == null || GameAccess.Read(component, "active") is bool active && !active
                            ? new EnvironmentComponentSnapshot { Type = name } : CaptureComponent(name, component));
                        if ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 /
                            System.Diagnostics.Stopwatch.Frequency >= .4) break;
                    }
                    if (cursor >= ChangingTypes.Length) cursor = -1;
                }
                catch { Dispose(); result.Clear(); }
                return result;
            }
            public void Dispose()
            {
                if (manager != null && stack != null)
                    try { manager.GetType().GetMethod("DestroyStack")?.Invoke(manager, new[] { stack }); } catch { }
                manager = stack = null; update = get = null; cursor = -1;
            }
        }

        // Observe world lighting changes without baking a local player's status
        // filters into the environment shared by every replay camera.
        internal static List<EnvironmentComponentSnapshot> CaptureChangingPostProcessing()
        {
            var result = new List<EnvironmentComponentSnapshot>();
            object? manager = null, stack = null;
            try
            {
                var managerType = GameAccess.Type("UnityEngine.Rendering.VolumeManager");
                manager = GameAccess.Read(managerType, "instance");
                var camera = GameplayCamera();
                if (manager == null || camera == null) return result;
                stack = manager.GetType().GetMethod("CreateStack", BindingFlags.Public | BindingFlags.Instance)?.Invoke(manager, null);
                var update = manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "Update" && method.GetParameters().Length == 3);
                if (stack == null || update == null) return result;
                var cameraDataType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                var cameraData = cameraDataType == null ? null : camera.GetComponent(cameraDataType);
                var mask = GameAccess.Read(cameraData, "volumeLayerMask") ?? (LayerMask)1;
                using (new PersonalFilterScope()) update.Invoke(manager, new[] { stack, camera.transform, mask });
                var get = stack.GetType().GetMethod("GetComponent", new[] { typeof(Type) });
                foreach (var name in ChangingTypes)
                {
                    var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + name);
                    var component = type == null ? null : get?.Invoke(stack, new object[] { type });
                    result.Add(component == null || GameAccess.Read(component, "active") is bool active && !active
                        ? new EnvironmentComponentSnapshot { Type = name } : CaptureComponent(name, component));
                }
            }
            catch { result.Clear(); }
            finally
            {
                if (manager != null && stack != null)
                    try { manager.GetType().GetMethod("DestroyStack")?.Invoke(manager, new[] { stack }); } catch { }
            }
            return result;
        }

        internal static EnvironmentSnapshot Capture()
        {
            var color = RenderSettings.ambientSkyColor;
            var result = new EnvironmentSnapshot { AmbientSkyColor = new[] { color.r, color.g, color.b, color.a } };
            CaptureCustomPasses(result);
            object? manager = null, stack = null;
            try
            {
                var managerType = GameAccess.Type("UnityEngine.Rendering.VolumeManager");
                manager = GameAccess.Read(managerType, "instance");
                var camera = GameplayCamera();
                if (manager == null || camera == null) return result;
                var create = manager.GetType().GetMethod("CreateStack", BindingFlags.Public | BindingFlags.Instance);
                stack = create?.Invoke(manager, null);
                if (stack == null) return result;
                var update = manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "Update" && method.GetParameters().Length == 3);
                if (update == null) return result;
                var cameraDataType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                var cameraData = cameraDataType == null ? null : camera.GetComponent(cameraDataType);
                var mask = GameAccess.Read(cameraData, "volumeLayerMask") ?? (LayerMask)1;
                using (new PersonalFilterScope()) update.Invoke(manager, new[] { stack, camera.transform, mask });
                var get = stack.GetType().GetMethod("GetComponent", new[] { typeof(Type) });
                foreach (var name in Types)
                {
                    var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + name);
                    if (type == null) continue;
                    var component = get?.Invoke(stack, new object[] { type });
                    if (component == null) continue;
                    if (GameAccess.Read(component, "active") is bool active && !active) continue;
                    var captured = CaptureComponent(name, component);
                    if (captured.Parameters.Count != 0) result.Components.Add(captured);
                    if (name == "HDRISky") CaptureSkyFaces(component, result);
                }
            }
            catch { /* An older render pipeline still gets an ambient-color sky. */ }
            finally
            {
                if (manager != null && stack != null)
                    try { manager.GetType().GetMethod("DestroyStack")?.Invoke(manager, new[] { stack }); } catch { }
            }
            return result;
        }

        private sealed class PersonalFilterScope : IDisposable
        {
            private readonly List<(Component Volume, Action<float> Set, float Value)> saved =
                new List<(Component, Action<float>, float)>();
            internal PersonalFilterScope()
            {
                var hud = GameAccess.Singleton("HUDManager");
                foreach (var name in new[] { "drunknessFilter", "poisonFilter", "insanityScreenFilter",
                    "flashbangScreenFilter", "CadaverBloomFilter", "underwaterScreenFilter" })
                    if (GameAccess.Read(hud, name) is Component volume && volume &&
                        GameAccess.Read(volume, "weight") is float value && value != 0f)
                    {
                        var field = volume.GetType().GetField("weight");
                        var property = volume.GetType().GetProperty("weight");
                        if (field == null && property?.CanWrite != true) continue;
                        Action<float> set = amount =>
                        {
                            if (field != null) field.SetValue(volume, amount);
                            else property!.SetValue(volume, amount);
                        };
                        saved.Add((volume, set, value));
                        set(0f);
                    }
            }
            public void Dispose()
            {
                // This scope never yields a frame. Restore the live player's
                // filters before the game's own camera can render.
                foreach (var entry in saved) if (entry.Volume) entry.Set(entry.Value);
            }
        }

        private static Camera? GameplayCamera()
        {
            var player = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController")
                ?? GameAccess.Read(GameAccess.Singleton("StartOfRound"), "localPlayerController");
            return GameAccess.Read(player, "gameplayCamera") as Camera ?? Camera.main;
        }

        private static void CaptureCustomPasses(EnvironmentSnapshot result)
        {
            var volumeType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.CustomPassVolume");
            if (volumeType == null) return;
            try
            {
                foreach (var source in Resources.FindObjectsOfTypeAll(volumeType).OfType<Component>()
                    .Where(item => item && item.gameObject.activeInHierarchy && item.gameObject.scene.IsValid()))
                {
                    if (result.CustomPasses.Count >= 4) break;
                    if (GameAccess.Read(source, "isGlobal") is bool global && !global) continue;
                    if (!(GameAccess.Read(source, "customPasses") is IEnumerable passes)) continue;
                    foreach (var pass in passes)
                    {
                        if (result.CustomPasses.Count >= 4) break;
                        if (pass == null || pass.GetType().Name != "FullScreenCustomPass" ||
                            GameAccess.Read(pass, "enabled") is bool enabled && !enabled) continue;
                        var material = GameAccess.Read(pass, "fullscreenPassMaterial") as Material;
                        if (!material || !material!.shader) continue;
                        var snapshot = new PostProcessPassSnapshot
                        {
                            Name = GameAccess.Read(pass, "name") as string ?? "",
                            ShaderName = material.shader.name,
                            MaterialName = material.name,
                            InjectionPoint = GameAccess.Read(source, "injectionPoint")?.ToString() ?? "BeforeTransparent",
                            MaterialPassName = GameAccess.Read(pass, "materialPassName") as string ?? "",
                            FetchColorBuffer = GameAccess.Read(pass, "fetchColorBuffer") is bool fetch ? fetch : (bool?)null
                        };
                        var shader = material.shader;
                        for (var index = 0; index < shader.GetPropertyCount() && snapshot.Properties.Count < 32; index++)
                        {
                            var name = shader.GetPropertyName(index);
                            var entry = new EnvironmentParameterSnapshot { Name = name };
                            switch (shader.GetPropertyType(index))
                            {
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    var number = material.GetFloat(name);
                                    if (!GameAccess.Finite(number)) continue;
                                    entry.Kind = "float"; entry.Values = new[] { number }; break;
                                case ShaderPropertyType.Color:
                                    var color = material.GetColor(name);
                                    if (!Finite(color)) continue;
                                    entry.Kind = "color"; entry.Values = new[] { color.r, color.g, color.b, color.a }; break;
                                case ShaderPropertyType.Vector:
                                    var vector = material.GetVector(name);
                                    if (!GameAccess.Finite(new Vector3(vector.x, vector.y, vector.z)) || !GameAccess.Finite(vector.w)) continue;
                                    entry.Kind = "vector4"; entry.Values = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                                default: continue;
                            }
                            snapshot.Properties.Add(entry);
                        }
                        result.CustomPasses.Add(snapshot);
                    }
                }
                result.CustomPassCaptureComplete = true;
            }
            catch { /* An older HDRP build may not expose custom passes. */ }
        }

        private static EnvironmentComponentSnapshot CaptureComponent(string name, object component)
        {
            var result = new EnvironmentComponentSnapshot { Type = name };
            foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Take(80))
            {
                object? parameter;
                try { parameter = field.GetValue(component); } catch { continue; }
                if (GameAccess.Read(parameter, "overrideState") is bool overridden && !overridden) continue;
                var value = GameAccess.Read(parameter, "value");
                if (value == null) continue;
                var item = new EnvironmentParameterSnapshot { Name = field.Name };
                switch (value)
                {
                    case bool flag: item.Kind = "bool"; item.Values = new[] { flag ? 1f : 0f }; break;
                    case int number: item.Kind = "int"; item.Values = new[] { (float)number }; break;
                    case float number when GameAccess.Finite(number): item.Kind = "float"; item.Values = new[] { number }; break;
                    case Color rgba when Finite(rgba): item.Kind = "color"; item.Values = new[] { rgba.r, rgba.g, rgba.b, rgba.a }; break;
                    case Vector2 vector when GameAccess.Finite(new Vector3(vector.x, vector.y, 0)):
                        item.Kind = "vector2"; item.Values = new[] { vector.x, vector.y }; break;
                    case Vector3 vector when GameAccess.Finite(vector): item.Kind = "vector3"; item.Values = new[] { vector.x, vector.y, vector.z }; break;
                    case Vector4 vector when GameAccess.Finite(new Vector3(vector.x, vector.y, vector.z)) && GameAccess.Finite(vector.w):
                        item.Kind = "vector4"; item.Values = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                    default:
                        if (value.GetType().Name == "TextureCurve")
                        {
                            var capturedCurve = false;
                            try { capturedCurve = CaptureCurve(value, item); } catch { }
                            if (capturedCurve) break;
                        }
                        if (!value.GetType().IsEnum) continue;
                        item.Kind = "enum"; item.Text = value.ToString() ?? "";
                        if (item.Text.Length > 80) continue;
                        break;
                }
                result.Parameters.Add(item);
            }
            return result;
        }

        private static bool CaptureCurve(object curve, EnvironmentParameterSnapshot item)
        {
            if (!(GameAccess.Read(curve, "length") is int length) || length < 0 || length > 32) return false;
            var indexer = curve.GetType().GetProperty("Item", new[] { typeof(int) });
            if (indexer == null) return false;
            var zero = GameAccess.Read(curve, "m_ZeroValue") is float z ? z : 0f;
            var range = GameAccess.Read(curve, "m_Range") is float r ? r : 1f;
            var loop = GameAccess.Read(curve, "m_Loop") is bool l && l;
            if (!GameAccess.Finite(zero) || !GameAccess.Finite(range) || range <= 0) return false;
            var keys = new float[length * 7];
            for (var index = 0; index < length; index++)
            {
                if (!(indexer.GetValue(curve, new object[] { index }) is Keyframe key)) return false;
                var offset = index * 7;
                keys[offset] = key.time; keys[offset + 1] = key.value;
                keys[offset + 2] = key.inTangent; keys[offset + 3] = key.outTangent;
                keys[offset + 4] = key.inWeight; keys[offset + 5] = key.outWeight;
                keys[offset + 6] = (int)key.weightedMode;
                for (var part = 0; part < 7; part++) if (!GameAccess.Finite(keys[offset + part])) return false;
            }
            item.Kind = "curve";
            item.Values = new[] { zero, range, loop ? 1f : 0f };
            item.CurveKeys = keys;
            return true;
        }

        private static void CaptureSkyFaces(object component, EnvironmentSnapshot result)
        {
            var sky = GameAccess.Read(GameAccess.Read(component, "hdriSky"), "value") as Cubemap;
            if (!sky) return;
            var mip = 0;
            while ((sky!.width >> mip) > 128 && mip + 1 < sky.mipmapCount) mip++;
            var width = Math.Max(1, sky.width >> mip);
            if (width > 256) return;
            var faces = new List<TextureSnapshot>();
            var bytes = 0;
            try
            {
                for (var i = 0; i < 6; i++)
                {
                    var pixels = ReadFace(sky, (CubemapFace)i, mip, width);
                    var image = new Texture2D(width, width, TextureFormat.RGBA32, false, true);
                    try
                    {
                        var encoded = new Color32[pixels.Length];
                        for (var pixel = 0; pixel < pixels.Length; pixel++)
                        {
                            ReplaySkyRgbe.Encode(pixels[pixel].r, pixels[pixel].g, pixels[pixel].b,
                                out var r, out var g, out var b, out var exponent);
                            encoded[pixel] = new Color32(r, g, b, exponent);
                        }
                        image.SetPixels32(encoded); image.Apply(false, false);
                        var png = ImageConversion.EncodeToPNG(image);
                        if (png == null || png.Length == 0) return;
                        bytes += png.Length;
                        if (bytes > 8 * 1024 * 1024) return;
                        faces.Add(new TextureSnapshot { Id = "sky" + i, Width = width, Height = width, Png = png });
                    }
                    finally { Object.Destroy(image); }
                }
                if (faces.Count == 6)
                { result.SkyFaces = faces; result.SkyFaceEncoding = "rgbe8"; }
            }
            catch { /* Non-readable imported cubemap; use the recorded ambient sky fallback. */ }
        }

        private static Color[] ReadFace(Cubemap sky, CubemapFace face, int mip, int width)
        {
            try { return sky.GetPixels(face, mip); }
            catch
            {
                // Imported HDRI cubemaps are commonly GPU-only. Copy one GPU face at a
                // small mip into a 2D surface before the one-time PNG readback.
                Texture2D? gpu = null, cpu = null;
                RenderTexture? target = null;
                var previous = RenderTexture.active;
                try
                {
                    gpu = new Texture2D(width, width, sky.format, false, true);
                    Graphics.CopyTexture(sky, (int)face, mip, gpu, 0, 0);
                    target = RenderTexture.GetTemporary(width, width, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                    Graphics.Blit(gpu, target);
                    RenderTexture.active = target;
                    cpu = new Texture2D(width, width, TextureFormat.RGBAHalf, false, true);
                    cpu.ReadPixels(new Rect(0, 0, width, width), 0, 0, false);
                    cpu.Apply(false, false);
                    return cpu.GetPixels();
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (target) RenderTexture.ReleaseTemporary(target);
                    if (gpu) Object.Destroy(gpu);
                    if (cpu) Object.Destroy(cpu);
                }
            }
        }

        private static bool Finite(Color color) => GameAccess.Finite(color.r) && GameAccess.Finite(color.g) &&
            GameAccess.Finite(color.b) && GameAccess.Finite(color.a);
    }
}
