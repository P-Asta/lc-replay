using System;
using System.Buffers;
using System.Threading.Tasks;
using Unity.Collections;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    // Per-world deduplication keeps PNGs out of per-frame samples. No assets are fetched or distributed with the plugin.
    internal sealed class AppearanceCapture : IDisposable
    {
        private const int MaxTextureBytes = 32 * 1024 * 1024;
        private const int MaxIndividualTextureBytes = 5 * 1024 * 1024;
        // Property blocks replace shader uniforms, not HDRP's material-derived
        // draw state. Baking these overrides into a Material would incorrectly
        // change its passes, blend factors, depth writes or face culling.
        private static readonly HashSet<string> HdrpRenderStateProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "_SurfaceType", "_BlendMode", "_SrcBlend", "_DstBlend", "_AlphaSrcBlend", "_AlphaDstBlend",
            "_ZWrite", "_TransparentZWrite", "_ZTestDepthEqualForOpaque", "_ZTestGBuffer", "_ZTestTransparent",
            "_Cull", "_CullMode", "_CullModeForward", "_DoubleSidedEnable", "_AlphaCutoffEnable",
            "_ReceivesSSR", "_ReceivesSSRTransparent", "_MaterialID", "_ExcludeFromTUAndAA",
            "_TransparentSortPriority", "_TransparentBackfaceEnable", "_TransparentDepthPrepassEnable", "_TransparentDepthPostpassEnable",
            "_DistortionEnable", "_DistortionOnly", "_DistortionDepthTest", "_ZTestModeDistortion",
            "_DistortionSrcBlend", "_DistortionDstBlend", "_DistortionBlurSrcBlend", "_DistortionBlurDstBlend", "_DistortionBlurBlendMode"
        };
        private readonly WorldSnapshot world;
        private readonly Dictionary<int, string> materials = new Dictionary<int, string>();
        private readonly Dictionary<string, string> overriddenMaterials = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> textures = new Dictionary<int, string>();
        private int textureBytes;
        internal int OmittedTextures { get; private set; }
        internal AppearanceCapture(WorldSnapshot world) { this.world = world; }

        internal void Capture(Renderer renderer, GeometrySnapshot geometry) =>
            Capture(renderer.sharedMaterials, renderer is SkinnedMeshRenderer, renderer.name, geometry, renderer);

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

        private void Capture(Material[] sourceMaterials, bool skinned, string rendererName, GeometrySnapshot geometry, Renderer? renderer = null)
        {
            MaterialPropertyBlock? rendererBlock = null, materialBlock = null;
            if (renderer && renderer!.HasPropertyBlock())
            {
                rendererBlock = new MaterialPropertyBlock(); materialBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(rendererBlock);
            }
            for (var slot = 0; slot < sourceMaterials.Length; slot++)
            {
                var material = sourceMaterials[slot];
                if (geometry.MaterialIds.Count >= 16) break;
                if (!material) { geometry.MaterialIds.Add(""); continue; }
                MaterialPropertyBlock? block = null;
                if (materialBlock != null)
                {
                    materialBlock.Clear(); renderer!.GetPropertyBlock(materialBlock, slot);
                    // Unity uses the per-material block in preference to the
                    // entire renderer block; the two blocks are not merged.
                    block = !materialBlock.isEmpty ? materialBlock : rendererBlock;
                    if (block!.isEmpty) block = null;
                }
                int key = material.GetInstanceID();
                var overrideId = block == null ? null : "m" + key + ":r" + renderer!.GetInstanceID() + ":s" + slot;
                if (overrideId == null && materials.TryGetValue(key, out var known)) { geometry.MaterialIds.Add(known); continue; }
                if (overrideId != null && overriddenMaterials.TryGetValue(overrideId, out known)) { geometry.MaterialIds.Add(known); continue; }
                if (world.Materials.Count >= 1024) { geometry.MaterialIds.Add(""); continue; }
                var snapshot = new MaterialSnapshot { Id = overrideId ?? "m" + key, Name = GameAccess.Scalar(material.name) ?? "",
                    ShaderName = material.shader ? GameAccess.Scalar(material.shader.name) ?? "" : "" };
                var ground = RenderVisibilityPolicy.IsGroundSurface(rendererName) || RenderVisibilityPolicy.IsGroundSurface(material.name);
                var architecture = RenderVisibilityPolicy.IsArchitectureSurface(rendererName) ||
                    RenderVisibilityPolicy.IsArchitectureSurface(material.name);
                var property = TextureProperty(material, block);
                var color = Color.white;
                foreach (var candidate in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                    if (material.HasProperty(candidate)) { color = ReadColor(material, block, candidate); break; }
                snapshot.Color = new[] { Finite(color.r, 1), Finite(color.g, 1), Finite(color.b, 1), Finite(color.a, 1) };
                if (property != null)
                {
                    var texture = ReadTexture(material, block, property);
                    if (texture) snapshot.TextureId = CaptureTexture(texture!,
                        ground ? 2048 : architecture || skinned || IsNaturalSurface(rendererName, material.name) ? 1024 : 512);
                    snapshot.TextureScaleOffset = TextureScaleOffset(material, block, property);
                }
                snapshot.AlphaClip = material.IsKeywordEnabled("_ALPHATEST_ON") ||
                    (material.HasProperty("_AlphaCutoffEnable") && material.GetFloat("_AlphaCutoffEnable") > 0);
                snapshot.Transparent = !snapshot.AlphaClip && ((material.HasProperty("_SurfaceType") && material.GetFloat("_SurfaceType") > 0)
                    || (material.HasProperty("_Mode") && material.GetFloat("_Mode") >= 2) || material.renderQueue >= 3000);
                snapshot.Cutoff = material.HasProperty("_AlphaCutoff") ? Mathf.Clamp01(ReadFloat(material, block, "_AlphaCutoff")) :
                    material.HasProperty("_Cutoff") ? Mathf.Clamp01(ReadFloat(material, block, "_Cutoff")) : 0.5f;
                if (!GameAccess.Finite(snapshot.Cutoff)) snapshot.Cutoff = 0.5f;
                snapshot.RenderQueue = Mathf.Clamp(material.renderQueue, -1, 5000);
                var keywords = material.shaderKeywords;
                snapshot.Keywords = keywords.Where(keyword => keyword.Length <= 128).Take(64).ToList();
                snapshot.KeywordsComplete = snapshot.Keywords.Count == keywords.Length;
                CaptureProperties(material, snapshot, ground, architecture, block);
                world.Materials.Add(snapshot);
                if (overrideId == null) materials[key] = snapshot.Id;
                else overriddenMaterials[overrideId] = snapshot.Id;
                geometry.MaterialIds.Add(snapshot.Id);
            }
        }

        private void CaptureProperties(Material material, MaterialSnapshot snapshot, bool ground, bool architecture, MaterialPropertyBlock? block = null)
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
                            var number = ReadFloat(material, block, name);
                            if (!GameAccess.Finite(number)) continue;
                            entry.Kind = "float"; entry.Values = new[] { number }; break;
                        case ShaderPropertyType.Color:
                            var color = ReadColor(material, block, name);
                            if (!FiniteColor(color)) continue;
                            entry.Kind = "color"; entry.Values = new[] { color.r, color.g, color.b, color.a }; break;
                        case ShaderPropertyType.Vector:
                            var vector = block?.HasProperty(name) == true ? block.GetVector(name) : material.GetVector(name);
                            if (!GameAccess.Finite(new Vector3(vector.x, vector.y, vector.z)) || !GameAccess.Finite(vector.w)) continue;
                            entry.Kind = "vector"; entry.Values = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                        case ShaderPropertyType.Texture:
                            var texture = ReadTexture(material, block, name);
                            if (!texture)
                            {
                                if (block?.HasProperty(name) != true) continue;
                                entry.Kind = "texture"; entry.Values = new[] { 0f };
                                break;
                            }
                            var colorMap = name.IndexOf("basecolor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0;
                            var surfaceMap = name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("detail", StringComparison.OrdinalIgnoreCase) >= 0;
                            entry.Kind = "texture"; entry.TextureId = CaptureTexture(texture!,
                                ground ? colorMap ? 2048 : 1024 :
                                architecture ? colorMap || surfaceMap ? 1024 : 512 :
                                IsNaturalSurface(material.name, name) || colorMap || surfaceMap ? 512 : 256);
                            entry.TextureScaleOffset = TextureScaleOffset(material, block, name);
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

        private sealed class TextureWork
        {
            internal Texture Source = null!;
            internal string Id = "", Name = "";
            internal int Edge, Width, Height;
            internal bool Linear, Requested, Failed;
            internal int FilterMode;
            internal AsyncGPUReadbackRequest Request;
            internal Task<byte[]>? Png;
        }
        private readonly Queue<TextureWork> textureWork = new Queue<TextureWork>();
        private TextureWork? activeTexture;
        private string CaptureTexture(Texture source, int requestedEdge)
        {
            var key = source.GetInstanceID();
            if (textures.TryGetValue(key, out var known)) return known;
            if (textures.Count >= 256 || textureBytes >= MaxTextureBytes || source.width < 1 || source.height < 1)
            { textures[key] = ""; OmittedTextures++; return ""; }
            var id = "t" + key; textures[key] = id;
            textureWork.Enqueue(new TextureWork { Source = source, Id = id, Name = GameAccess.Scalar(source.name) ?? "",
                Edge = Math.Min(2048, Math.Max(256, requestedEdge)), Linear = !source.isDataSRGB,
                FilterMode = (int)source.filterMode });
            return id;
        }
        // One in-flight readback/encode keeps raw texture memory bounded. Capture
        // only registers references; finalization resumes here on later frames.
        internal bool StepTextures()
        {
            if (activeTexture == null)
            {
                if (textureWork.Count == 0) return true;
                activeTexture = textureWork.Dequeue();
            }
            var work = activeTexture;
            try
            {
                if (work.Png != null)
                {
                    if (!work.Png.IsCompleted) return false;
                    var png = work.Png.GetAwaiter().GetResult();
                    if (png.Length == 0 || png.Length > MaxIndividualTextureBytes || png.Length > MaxTextureBytes - textureBytes)
                    {
                        if (work.Edge > 256) { work.Edge /= 2; work.Requested = false; work.Png = null; return false; }
                        DropTexture(work); return false;
                    }
                    world.Textures.Add(new TextureSnapshot { Id = work.Id, Name = work.Name, Width = work.Width,
                        Height = work.Height, Linear = work.Linear, FilterMode = work.FilterMode, Png = png });
                    textureBytes += png.Length; activeTexture = null; return false;
                }
                if (work.Requested)
                {
                    if (!work.Request.done) return false;
                    if (work.Failed || work.Request.hasError) DropTexture(work);
                    return false;
                }
                if (!work.Source || textureBytes >= MaxTextureBytes) { DropTexture(work); return false; }
                var ratio = Math.Min(1f, (float)work.Edge / Math.Max(work.Source.width, work.Source.height));
                work.Width = Math.Max(1, Mathf.RoundToInt(work.Source.width * ratio));
                work.Height = Math.Max(1, Mathf.RoundToInt(work.Source.height * ratio));
                var oldSrgb = GL.sRGBWrite;
                var target = RenderTexture.GetTemporary(work.Width, work.Height, 0, RenderTextureFormat.ARGB32,
                    work.Linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                var handedOff = false;
                try
                {
                    GL.sRGBWrite = !work.Linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
                    Graphics.Blit(work.Source, target);
                    if (SystemInfo.supportsAsyncGPUReadback)
                    {
                        // The callback owns the surface until the GPU is finished,
                        // including cancellation of this world capture job.
                        work.Request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
                        {
                            try
                            {
                                if (request.hasError) work.Failed = true;
                                else EncodePixels(work, request.GetData<byte>());
                            }
                            catch { work.Failed = true; }
                            finally { RenderTexture.ReleaseTemporary(target); }
                        });
                        handedOff = true; work.Requested = true;
                    }
                    else
                    {
                        // Older GPU fallback still moves PNG compression off the
                        // game thread. Only its readback remains indivisible.
                        var oldActive = RenderTexture.active;
                        var copy = new Texture2D(work.Width, work.Height, TextureFormat.RGBA32, false, work.Linear);
                        try
                        {
                            RenderTexture.active = target;
                            copy.ReadPixels(new Rect(0, 0, work.Width, work.Height), 0, 0, false);
                            EncodePixels(work, copy.GetRawTextureData<byte>());
                        }
                        finally { RenderTexture.active = oldActive; Object.Destroy(copy); }
                    }
                }
                finally { GL.sRGBWrite = oldSrgb; if (!handedOff) RenderTexture.ReleaseTemporary(target); }
            }
            catch { DropTexture(work); }
            return false;
        }
        private static void EncodePixels(TextureWork work, NativeArray<byte> raw)
        {
            // Readback memory lasts only one frame. Copy it in the completion
            // callback even if world maintenance will resume several frames later.
            var pixels = ArrayPool<byte>.Shared.Rent(raw.Length);
            try { NativeArray<byte>.Copy(raw, 0, pixels, 0, raw.Length); }
            catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
            work.Png = Task.Run(() => { try { return ReplayPng.EncodeRgba(pixels, work.Width, work.Height); }
                finally { ArrayPool<byte>.Shared.Return(pixels); } });
            work.Png.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        private void DropTexture(TextureWork work)
        {
            foreach (var material in world.Materials)
            {
                if (material.TextureId == work.Id) material.TextureId = "";
                foreach (var property in material.Properties)
                    if (property.TextureId == work.Id) property.TextureId = "";
            }
            OmittedTextures++; activeTexture = null;
        }
        public void Dispose()
        {
            // No WaitAllRequests/Task.Wait on the game frame. Callbacks release
            // submitted GPU surfaces; encoder finally blocks return pooled bytes.
            textureWork.Clear(); activeTexture = null;
        }
        internal void FinishAvailable()
        {
            // Stop without waiting on GPU/CPU work. Completed geometry and
            // textures remain usable; unfinished maps retain material colors.
            if (activeTexture?.Png?.Status == TaskStatus.RanToCompletion)
            {
                var work = activeTexture; var png = work.Png.Result;
                if (png.Length > 0 && png.Length <= MaxIndividualTextureBytes && png.Length <= MaxTextureBytes - textureBytes)
                    world.Textures.Add(new TextureSnapshot { Id = work.Id, Name = work.Name, Width = work.Width,
                        Height = work.Height, Linear = work.Linear, FilterMode = work.FilterMode, Png = png });
            }
            var complete = new HashSet<string>(world.Textures.Select(texture => texture.Id), StringComparer.Ordinal);
            foreach (var material in world.Materials)
            {
                if (!complete.Contains(material.TextureId)) material.TextureId = "";
                foreach (var property in material.Properties)
                    if (!complete.Contains(property.TextureId)) property.TextureId = "";
            }
            Dispose();
        }

        private static float ReadFloat(Material material, MaterialPropertyBlock? block, string name)
        {
            if (block?.HasProperty(name) != true) return material.GetFloat(name);
            var fixedState = material.shader && material.shader.name.StartsWith("HDRP/", StringComparison.Ordinal) &&
                (HdrpRenderStateProperties.Contains(name) || name.StartsWith("_Stencil", StringComparison.Ordinal));
            return fixedState ? material.GetFloat(name) : block.GetFloat(name);
        }
        private static Color ReadColor(Material material, MaterialPropertyBlock? block, string name) =>
            block?.HasProperty(name) == true ? block.GetColor(name) : material.GetColor(name);
        private static Texture? ReadTexture(Material material, MaterialPropertyBlock? block, string name) =>
            block?.HasProperty(name) == true ? block.GetTexture(name) : material.GetTexture(name);
        private static float[] TextureScaleOffset(Material material, MaterialPropertyBlock? block, string name)
        {
            if (block?.HasProperty(name + "_ST") == true)
            {
                var value = block.GetVector(name + "_ST");
                return new[] { Finite(value.x, 1), Finite(value.y, 1), Finite(value.z, 0), Finite(value.w, 0) };
            }
            var scale = material.GetTextureScale(name); var offset = material.GetTextureOffset(name);
            return new[] { Finite(scale.x, 1), Finite(scale.y, 1), Finite(offset.x, 0), Finite(offset.y, 0) };
        }
        private static string? TextureProperty(Material material, MaterialPropertyBlock? block = null)
        {
            foreach (var name in new[] { "_BaseColorMap", "_UnlitColorMap", "_MainTex", "_BaseMap", "_BaseColorTexture" })
                if (material.HasProperty(name) && ReadTexture(material, block, name)) return name;
            foreach (var name in material.GetTexturePropertyNames())
            {
                var lower = name.ToLowerInvariant();
                if ((lower.Contains("albedo") || lower.Contains("diffuse") || lower.Contains("basecolor")) && ReadTexture(material, block, name)) return name;
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
