using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    // Capture the blended HDRP environment, not scene Volume objects whose lifetimes end
    // when the host disconnects. Only scalar rendering settings and optional sky pixels
    // enter the replay file; Unity assets and executable components are never serialized.
    internal static class EnvironmentCapture
    {
        private static readonly string[] Types = { "VisualEnvironment", "GradientSky", "HDRISky", "PhysicallyBasedSky",
            "ProceduralSky", "Fog", "Exposure", "Tonemapping", "ColorAdjustments" };

        internal static EnvironmentSnapshot Capture()
        {
            var color = RenderSettings.ambientSkyColor;
            var result = new EnvironmentSnapshot { AmbientSkyColor = new[] { color.r, color.g, color.b, color.a } };
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
                update.Invoke(manager, new[] { stack, camera.transform, mask });
                var get = stack.GetType().GetMethod("GetComponent", new[] { typeof(Type) });
                foreach (var name in Types)
                {
                    var type = GameAccess.Type("UnityEngine.Rendering.HighDefinition." + name);
                    if (type == null) continue;
                    var component = get?.Invoke(stack, new object[] { type });
                    if (component == null) continue;
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

        private static Camera? GameplayCamera()
        {
            var player = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController")
                ?? GameAccess.Read(GameAccess.Singleton("StartOfRound"), "localPlayerController");
            return GameAccess.Read(player, "gameplayCamera") as Camera ?? Camera.main;
        }

        private static EnvironmentComponentSnapshot CaptureComponent(string name, object component)
        {
            var result = new EnvironmentComponentSnapshot { Type = name };
            foreach (var field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Take(80))
            {
                object? parameter;
                try { parameter = field.GetValue(component); } catch { continue; }
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
                        if (!value.GetType().IsEnum) continue;
                        item.Kind = "enum"; item.Text = value.ToString() ?? "";
                        if (item.Text.Length > 80) continue;
                        break;
                }
                result.Parameters.Add(item);
            }
            return result;
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
                    var image = new Texture2D(width, width, TextureFormat.RGBA32, false, false);
                    try
                    {
                        image.SetPixels(pixels); image.Apply(false, false);
                        var png = ImageConversion.EncodeToPNG(image);
                        if (png == null || png.Length == 0) return;
                        bytes += png.Length;
                        if (bytes > 8 * 1024 * 1024) return;
                        faces.Add(new TextureSnapshot { Id = "sky" + i, Width = width, Height = width, Png = png });
                    }
                    finally { Object.Destroy(image); }
                }
                if (faces.Count == 6) result.SkyFaces = faces;
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
