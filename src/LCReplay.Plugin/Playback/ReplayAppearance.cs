using System;
using System.Collections.Generic;
using System.Reflection;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Only render resources are reconstructed: no gameplay prefabs, behaviours, or network objects.
    internal sealed class ReplayAppearance : IDisposable
    {
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        internal int TextureCount => textures.Count;
        internal int MaterialCount => materials.Count;

        internal ReplayAppearance(WorldSnapshot world, Shader shader)
        {
            var loadedShaders = new Dictionary<string, Shader>(StringComparer.Ordinal);
            var alphaCutoffs = new Dictionary<string, float>(StringComparer.Ordinal);
            var groundMaterials = new HashSet<string>(StringComparer.Ordinal);
            var vegetationMaterials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var geometry in world.Geometry)
            {
                if (RenderVisibilityPolicy.IsGroundSurface(geometry.Name))
                    foreach (var id in geometry.MaterialIds) groundMaterials.Add(id);
                else if (RenderVisibilityPolicy.IsVegetation(geometry.Name))
                    foreach (var id in geometry.MaterialIds) vegetationMaterials.Add(id);
            }
            var groundTextures = new HashSet<string>(StringComparer.Ordinal);
            var vegetationTextures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in world.Materials)
            {
                if (material.AlphaClip && material.TextureId.Length != 0 && !alphaCutoffs.ContainsKey(material.TextureId))
                    alphaCutoffs[material.TextureId] = material.Cutoff;
                if (groundMaterials.Contains(material.Id) || RenderVisibilityPolicy.IsGroundSurface(material.Name))
                {
                    if (material.TextureId.Length != 0) groundTextures.Add(material.TextureId);
                    foreach (var property in material.Properties)
                        if (property.Kind == "texture" && property.TextureId.Length != 0) groundTextures.Add(property.TextureId);
                }
                else if (vegetationMaterials.Contains(material.Id) || RenderVisibilityPolicy.IsVegetation(material.Name))
                {
                    if (material.TextureId.Length != 0) vegetationTextures.Add(material.TextureId);
                    foreach (var property in material.Properties)
                        if (property.Kind == "texture" && property.TextureId.Length != 0) vegetationTextures.Add(property.TextureId);
                }
            }
            foreach (var loaded in Resources.FindObjectsOfTypeAll<Shader>())
                if (loaded && loaded.isSupported && !loadedShaders.ContainsKey(loaded.name)) loadedShaders.Add(loaded.name, loaded);
            foreach (var snapshot in world.Textures)
            {
                Texture2D? texture = null;
                try
                {
                    var ground = groundTextures.Contains(snapshot.Id);
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, !ground, false) { name = "Replay " + snapshot.Id, hideFlags = HideFlags.DontSave };
                    if (!ImageConversion.LoadImage(texture, snapshot.Png, false)) { Object.Destroy(texture); continue; }
                    texture.Apply(!ground, false);
                    if (!ground && alphaCutoffs.TryGetValue(snapshot.Id, out var cutoff)) PreserveAlphaCoverage(texture, cutoff);
                    texture.Apply(false, true);
                    texture.wrapMode = TextureWrapMode.Repeat;
                    texture.filterMode = ground ? FilterMode.Bilinear : FilterMode.Trilinear;
                    texture.anisoLevel = ground || vegetationTextures.Contains(snapshot.Id) ? 8 : 4;
                    texture.mipMapBias = vegetationTextures.Contains(snapshot.Id) && !ground ? -1.5f : -0.15f;
                    textures[snapshot.Id] = texture;
                }
                catch { if (texture) Object.Destroy(texture); }
            }
            foreach (var snapshot in world.Materials)
            {
                var shaderName = snapshot.ShaderName ?? "";
                var validName = !string.IsNullOrWhiteSpace(shaderName) &&
                    !shaderName.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase) &&
                    !shaderName.StartsWith("UI/", StringComparison.OrdinalIgnoreCase);
                var originalShader = !validName ? null : Shader.Find(shaderName);
                if (validName && (originalShader == null || !originalShader.isSupported))
                    loadedShaders.TryGetValue(shaderName, out originalShader);
                var selectedShader = originalShader && originalShader!.isSupported ? originalShader : shader;
                var material = new Material(selectedShader) { name = "Replay " + snapshot.Name, hideFlags = HideFlags.DontSave };
                var rgba = snapshot.Color;
                var color = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                foreach (var property in new[] { "_UnlitColor", "_BaseColor", "_Color" }) if (material.HasProperty(property)) material.SetColor(property, color);
                foreach (var property in snapshot.Properties)
                {
                    if (!material.HasProperty(property.Name)) continue;
                    var value = property.Values;
                    try
                    {
                        switch (property.Kind)
                        {
                            case "float" when value.Length == 1: material.SetFloat(property.Name, value[0]); break;
                            case "color" when value.Length == 4:
                                material.SetColor(property.Name, new Color(value[0], value[1], value[2], value[3])); break;
                            case "vector" when value.Length == 4:
                                material.SetVector(property.Name, new Vector4(value[0], value[1], value[2], value[3])); break;
                            case "texture" when textures.TryGetValue(property.TextureId, out var map):
                                material.SetTexture(property.Name, map);
                                material.SetTextureScale(property.Name, new Vector2(property.TextureScaleOffset[0], property.TextureScaleOffset[1]));
                                material.SetTextureOffset(property.Name, new Vector2(property.TextureScaleOffset[2], property.TextureScaleOffset[3]));
                                break;
                        }
                    }
                    catch { /* Unsupported property combinations fall back to the other recorded values. */ }
                }
                foreach (var keyword in snapshot.Keywords) material.EnableKeyword(keyword);
                if (textures.TryGetValue(snapshot.TextureId, out var texture))
                {
                    var uv = snapshot.TextureScaleOffset;
                    foreach (var property in new[] { "_UnlitColorMap", "_BaseColorMap", "_MainTex", "_BaseMap" })
                    {
                        if (!material.HasProperty(property)) continue;
                        material.SetTexture(property, texture);
                        material.SetTextureScale(property, new Vector2(uv[0], uv[1]));
                        material.SetTextureOffset(property, new Vector2(uv[2], uv[3]));
                    }
                    if (selectedShader.name.Contains("Unlit")) material.EnableKeyword("_UNLIT_COLOR_MAP");
                }
                if (snapshot.AlphaClip)
                {
                    material.EnableKeyword("_ALPHATEST_ON");
                    SetFloat(material, "_AlphaCutoffEnable", 1); SetFloat(material, "_AlphaCutoff", snapshot.Cutoff); SetFloat(material, "_Cutoff", snapshot.Cutoff);
                }
                if (snapshot.Transparent)
                {
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.EnableKeyword("_ALPHABLEND_ON");
                    SetFloat(material, "_SurfaceType", 1); SetFloat(material, "_ZWrite", 0);
                    SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha); SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    SetFloat(material, "_AlphaSrcBlend", (float)BlendMode.One); SetFloat(material, "_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.renderQueue = (int)RenderQueue.Transparent;
                }
                // Retain exterior/interior surfaces from either camera side without introducing solid bounds cubes.
                SetFloat(material, "_CullMode", 0); SetFloat(material, "_Cull", 0); SetFloat(material, "_DoubleSidedEnable", 1);
                material.EnableKeyword("_DOUBLESIDED_ON");
                // HDRP/Lit derives its draw passes and alpha-test variants from the material
                // properties. A new Material(shader) does not initialize those passes merely
                // because _ALPHATEST_ON was enabled, leaving transparent leaf texels opaque.
                ValidateHdrpMaterial(material);
                if (snapshot.RenderQueue >= 0) material.renderQueue = snapshot.RenderQueue;
                materials[snapshot.Id] = material;
            }
        }

        // PNGs do not contain the original importer's coverage-preserving mips.
        // Without this, high-cutoff leaves vanish as soon as the camera moves back.
        private static void PreserveAlphaCoverage(Texture2D texture, float cutoff)
        {
            if (texture.mipmapCount < 2 || cutoff <= 0f || cutoff >= 1f) return;
            var threshold = Mathf.RoundToInt(cutoff * 255f);
            var basePixels = texture.GetPixels32(0);
            var visible = 0;
            foreach (var pixel in basePixels) if (pixel.a >= threshold) visible++;
            if (visible == 0 || visible == basePixels.Length) return;
            var target = (float)visible / basePixels.Length;
            for (var level = 1; level < texture.mipmapCount; level++)
            {
                var pixels = texture.GetPixels32(level);
                if (pixels.Length == 0) continue;
                float bestScale = 1f, bestError = float.MaxValue;
                for (var scale = 1f; scale <= 2.5f; scale += 0.125f)
                {
                    var count = 0;
                    foreach (var pixel in pixels) if (pixel.a * scale >= threshold) count++;
                    var error = Mathf.Abs((float)count / pixels.Length - target);
                    if (error < bestError) { bestError = error; bestScale = scale; }
                }
                if (bestScale <= 1f) continue;
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i].a = (byte)Mathf.Min(255, Mathf.RoundToInt(pixels[i].a * bestScale));
                texture.SetPixels32(pixels, level);
            }
        }

        internal Material[] Resolve(GeometrySnapshot geometry, Material fallback, GeometrySnapshot? source = null)
        {
            int count = Math.Max(1, (source ?? geometry).SubmeshTriangles.Count);
            var result = new Material[count];
            for (int i = 0; i < count; i++)
                result[i] = i < geometry.MaterialIds.Count && materials.TryGetValue(geometry.MaterialIds[i], out var material) ? material : fallback;
            return result;
        }
        internal Material Resolve(string id, Material fallback) => materials.TryGetValue(id, out var material) ? material : fallback;

        private static void SetFloat(Material material, string property, float value) { if (material.HasProperty(property)) material.SetFloat(property, value); }
        private static void ValidateHdrpMaterial(Material material)
        {
            if (!material.shader.name.StartsWith("HDRP/", StringComparison.Ordinal)) return;
            try
            {
                var type = Type.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime");
                var method = type?.GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null);
                method?.Invoke(null, new object[] { material });
            }
            catch { /* Other pipeline versions retain the recorded shader properties. */ }
        }
        public void Dispose()
        {
            foreach (var material in materials.Values) if (material) Object.Destroy(material);
            foreach (var texture in textures.Values) if (texture) Object.Destroy(texture);
            materials.Clear(); textures.Clear();
        }
    }
}
