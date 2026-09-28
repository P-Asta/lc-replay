using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    // Per-world deduplication keeps PNGs out of per-frame samples. No assets are fetched or distributed with the plugin.
    internal sealed class AppearanceCapture
    {
        private const int MaxTextureBytes = 32 * 1024 * 1024;
        private const int MaxIndividualTextureBytes = 5 * 1024 * 1024;
        private readonly WorldSnapshot world;
        private readonly Dictionary<int, string> materials = new Dictionary<int, string>();
        private readonly Dictionary<int, string> textures = new Dictionary<int, string>();
        private int textureBytes;
        internal int OmittedTextures { get; private set; }
        internal AppearanceCapture(WorldSnapshot world) { this.world = world; }

        internal void Capture(Renderer renderer, GeometrySnapshot geometry) =>
            Capture(renderer.sharedMaterials, renderer is SkinnedMeshRenderer, renderer.name, geometry);

        internal void Capture(Material material, string name, GeometrySnapshot geometry) =>
            Capture(new[] { material }, false, name, geometry);

        internal void CaptureTerrain(Terrain terrain, GeometrySnapshot geometry)
        {
            // Unity Terrain often has no MeshRenderer and its material template may
            // contain no assigned maps. Preserve a terrain layer rather than drawing
            // the reconstructed height field with a flat placeholder color.
            var data = terrain.terrainData;
            if (terrain.materialTemplate && TextureProperty(terrain.materialTemplate) != null)
            {
                Capture(terrain.materialTemplate, terrain.name, geometry);
                return;
            }
            var layers = data.terrainLayers;
            var layer = layers == null ? null : layers.FirstOrDefault(item => item && item.diffuseTexture);
            if (layer && world.Materials.Count < 1024)
            {
                var texture = CaptureTexture(layer!.diffuseTexture, 2048);
                if (texture.Length != 0)
                {
                    var tile = layer.tileSize;
                    var offset = layer.tileOffset;
                    var material = new MaterialSnapshot
                    {
                        Id = "terrain-layer" + terrain.GetInstanceID(),
                        Name = "Terrain layer " + layer.name,
                        ShaderName = "HDRP/Lit",
                        TextureId = texture,
                        TextureScaleOffset = new[]
                        {
                            data.size.x / Mathf.Max(.01f, Mathf.Abs(tile.x)),
                            data.size.z / Mathf.Max(.01f, Mathf.Abs(tile.y)),
                            offset.x / Mathf.Max(.01f, Mathf.Abs(tile.x)),
                            offset.y / Mathf.Max(.01f, Mathf.Abs(tile.y))
                        }
                    };
                    world.Materials.Add(material);
                    geometry.MaterialIds.Add(material.Id);
                    return;
                }
            }
            if (terrain.materialTemplate) Capture(terrain.materialTemplate, terrain.name, geometry);
        }

        private void Capture(Material[] sourceMaterials, bool skinned, string rendererName, GeometrySnapshot geometry)
        {
            foreach (var material in sourceMaterials)
            {
                if (geometry.MaterialIds.Count >= 16) break;
                if (!material) { geometry.MaterialIds.Add(""); continue; }
                int key = material.GetInstanceID();
                if (materials.TryGetValue(key, out var known)) { geometry.MaterialIds.Add(known); continue; }
                if (world.Materials.Count >= 1024) { geometry.MaterialIds.Add(""); continue; }
                var snapshot = new MaterialSnapshot { Id = "m" + key, Name = GameAccess.Scalar(material.name) ?? "",
                    ShaderName = material.shader ? GameAccess.Scalar(material.shader.name) ?? "" : "" };
                var ground = RenderVisibilityPolicy.IsGroundSurface(rendererName) || RenderVisibilityPolicy.IsGroundSurface(material.name);
                var property = TextureProperty(material);
                var color = Color.white;
                foreach (var candidate in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                    if (material.HasProperty(candidate)) { color = material.GetColor(candidate); break; }
                snapshot.Color = new[] { Finite(color.r, 1), Finite(color.g, 1), Finite(color.b, 1), Finite(color.a, 1) };
                if (property != null)
                {
                    var texture = material.GetTexture(property);
                    if (texture) snapshot.TextureId = CaptureTexture(texture,
                        ground ? 2048 : skinned || IsNaturalSurface(rendererName, material.name) ? 1024 : 512);
                    var scale = material.GetTextureScale(property); var offset = material.GetTextureOffset(property);
                    snapshot.TextureScaleOffset = new[] { Finite(scale.x, 1), Finite(scale.y, 1), Finite(offset.x, 0), Finite(offset.y, 0) };
                }
                snapshot.AlphaClip = material.IsKeywordEnabled("_ALPHATEST_ON") ||
                    (material.HasProperty("_AlphaCutoffEnable") && material.GetFloat("_AlphaCutoffEnable") > 0);
                snapshot.Transparent = !snapshot.AlphaClip && ((material.HasProperty("_SurfaceType") && material.GetFloat("_SurfaceType") > 0)
                    || (material.HasProperty("_Mode") && material.GetFloat("_Mode") >= 2) || material.renderQueue >= 3000);
                snapshot.Cutoff = material.HasProperty("_AlphaCutoff") ? Mathf.Clamp01(material.GetFloat("_AlphaCutoff")) :
                    material.HasProperty("_Cutoff") ? Mathf.Clamp01(material.GetFloat("_Cutoff")) : 0.5f;
                if (!GameAccess.Finite(snapshot.Cutoff)) snapshot.Cutoff = 0.5f;
                snapshot.RenderQueue = Mathf.Clamp(material.renderQueue, -1, 5000);
                snapshot.Keywords = material.shaderKeywords.Where(keyword => keyword.Length <= 128).Take(64).ToList();
                CaptureProperties(material, snapshot, ground);
                world.Materials.Add(snapshot); materials[key] = snapshot.Id; geometry.MaterialIds.Add(snapshot.Id);
            }
        }

        private void CaptureProperties(Material material, MaterialSnapshot snapshot, bool ground)
        {
            var shader = material.shader;
            if (!shader) return;
            // HDRP/Lit has more properties than the bounded replay format allows.
            // Shader declaration order puts many surface maps after internal render
            // state. Losing the mask/normal maps leaves the shader's smooth defaults.
            var propertyIndices = Enumerable.Range(0, Math.Min(shader.GetPropertyCount(), 256))
                .OrderByDescending(index => PropertyPriority(shader, index))
                .ToArray();
            foreach (var i in propertyIndices)
            {
                if (snapshot.Properties.Count >= 96) break;
                try
                {
                    var name = shader.GetPropertyName(i);
                    if (name.Length == 0 || name.Length > 128) continue;
                    var type = shader.GetPropertyType(i);
                    var entry = new MaterialPropertySnapshot { Name = name };
                    switch (type)
                    {
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            var number = material.GetFloat(name);
                            if (!GameAccess.Finite(number)) continue;
                            entry.Kind = "float"; entry.Values = new[] { number }; break;
                        case ShaderPropertyType.Color:
                            var color = material.GetColor(name);
                            if (!FiniteColor(color)) continue;
                            entry.Kind = "color"; entry.Values = new[] { color.r, color.g, color.b, color.a }; break;
                        case ShaderPropertyType.Vector:
                            var vector = material.GetVector(name);
                            if (!GameAccess.Finite(new Vector3(vector.x, vector.y, vector.z)) || !GameAccess.Finite(vector.w)) continue;
                            entry.Kind = "vector"; entry.Values = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                        case ShaderPropertyType.Texture:
                            var texture = material.GetTexture(name);
                            if (!texture) continue;
                            var colorMap = name.IndexOf("basecolor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0;
                            var surfaceMap = name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("detail", StringComparison.OrdinalIgnoreCase) >= 0;
                            entry.Kind = "texture"; entry.TextureId = CaptureTexture(texture,
                                ground ? colorMap ? 2048 : 1024 :
                                IsNaturalSurface(material.name, name) || colorMap || surfaceMap ? 512 : 256);
                            var scale = material.GetTextureScale(name); var offset = material.GetTextureOffset(name);
                            entry.TextureScaleOffset = new[] { Finite(scale.x, 1), Finite(scale.y, 1), Finite(offset.x, 0), Finite(offset.y, 0) };
                            break;
                        default: continue;
                    }
                    snapshot.Properties.Add(entry);
                }
                catch { /* A custom shader may not expose one property for CPU readback. */ }
            }
        }

        private static int PropertyPriority(Shader shader, int index)
        {
            var name = shader.GetPropertyName(index).ToLowerInvariant();
            if (shader.GetPropertyType(index) == ShaderPropertyType.Texture) return 5;
            if (name.Contains("emiss") || name.Contains("smooth") || name.Contains("rough") ||
                name.Contains("gloss") || name.Contains("specular") || name.Contains("occlusion") ||
                name.Contains("metal") || name.Contains("normal") || name.Contains("bump") || name.Contains("mask") ||
                name.Contains("basecolor") || name.Contains("detail") || name.Contains("remap") ||
                name.Contains("uv") || name.Contains("alpha") || name.Contains("blend") ||
                name.Contains("cutoff") || name.Contains("surface") || name.Contains("materialid")) return 4;
            if (shader.GetPropertyType(index) == ShaderPropertyType.Color) return 3;
            return 1;
        }

        private static bool FiniteColor(Color color) => GameAccess.Finite(color.r) && GameAccess.Finite(color.g) &&
            GameAccess.Finite(color.b) && GameAccess.Finite(color.a);

        private string CaptureTexture(Texture source, int requestedEdge)
        {
            int key = source.GetInstanceID();
            if (textures.TryGetValue(key, out var known)) return known;
            textures[key] = "";
            if (world.Textures.Count >= 256 || textureBytes >= MaxTextureBytes || source.width < 1 || source.height < 1)
            { OmittedTextures++; return ""; }
            var old = RenderTexture.active;
            bool oldSrgbWrite = GL.sRGBWrite;
            var linear = !source.isDataSRGB;
            try
            {
                // Keep a high-resolution base map for close spectator views. If a
                // complex PNG would exceed either bound, fall back by one mip-sized
                // step instead of losing the material texture altogether.
                for (var edge = Math.Min(2048, Math.Max(256, requestedEdge)); edge >= 256; edge /= 2)
                {
                    RenderTexture? temporary = null;
                    Texture2D? copy = null;
                    try
                    {
                        float ratio = Math.Min(1f, (float)edge / Math.Max(source.width, source.height));
                        int width = Math.Max(1, Mathf.RoundToInt(source.width * ratio));
                        int height = Math.Max(1, Mathf.RoundToInt(source.height * ratio));
                        temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                        GL.sRGBWrite = !linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
                        Graphics.Blit(source, temporary);
                        RenderTexture.active = temporary;
                        copy = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
                        copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        copy.Apply(false, false);
                        var png = ImageConversion.EncodeToPNG(copy);
                        if (png == null || png.Length == 0 || png.Length > MaxIndividualTextureBytes ||
                            textureBytes + png.Length > MaxTextureBytes) continue;
                        var snapshot = new TextureSnapshot { Id = "t" + key, Width = width, Height = height, Linear = linear, Png = png };
                        world.Textures.Add(snapshot); textureBytes += png.Length; textures[key] = snapshot.Id;
                        return snapshot.Id;
                    }
                    finally
                    {
                        RenderTexture.active = old;
                        if (temporary) RenderTexture.ReleaseTemporary(temporary);
                        if (copy) Object.Destroy(copy);
                    }
                }
            }
            catch { /* Keep the remaining appearance even if one GPU texture cannot be read. */ }
            finally { RenderTexture.active = old; GL.sRGBWrite = oldSrgbWrite; }
            OmittedTextures++;
            return "";
        }

        private static string? TextureProperty(Material material)
        {
            foreach (var name in new[] { "_BaseColorMap", "_UnlitColorMap", "_MainTex", "_BaseMap", "_BaseColorTexture" })
                if (material.HasProperty(name) && material.GetTexture(name)) return name;
            foreach (var name in material.GetTexturePropertyNames())
            {
                var lower = name.ToLowerInvariant();
                if ((lower.Contains("albedo") || lower.Contains("diffuse") || lower.Contains("basecolor")) && material.GetTexture(name)) return name;
            }
            return null;
        }
        private static bool IsNaturalSurface(string rendererName, string materialName)
        {
            foreach (var text in new[] { rendererName, materialName })
            {
                var name = text.ToLowerInvariant();
                if (name.Contains("terrain") || name.Contains("ground") || name.Contains("rock") ||
                    name.Contains("stone") || name.Contains("tree") || name.Contains("leaf") ||
                    name.Contains("leaves") || name.Contains("bush") || name.Contains("flower") ||
                    name.Contains("grass") || name.Contains("forest")) return true;
            }
            return false;
        }
        private static float Finite(float value, float fallback) => GameAccess.Finite(value) ? value : fallback;
    }
}
