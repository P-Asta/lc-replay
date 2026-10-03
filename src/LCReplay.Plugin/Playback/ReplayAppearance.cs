using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly Dictionary<string, MaterialSnapshot> particleMaterials = new Dictionary<string, MaterialSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> installedMaterials = new Dictionary<string, Material>(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> installedInstances = new Dictionary<string, Material>(StringComparer.Ordinal);
        private readonly Dictionary<string, Material> particleSourceCopies = new Dictionary<string, Material>(StringComparer.Ordinal);
        private static readonly string[] MainMapNames = { "_BaseColorMap", "_UnlitColorMap", "_MainTex", "_BaseMap", "_BaseColorTexture" };
        private static readonly MethodInfo? HdrpValidator = Type.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime")?
            .GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null);
        internal int TextureCount => textures.Count;
        internal int MaterialCount => materials.Count;

        internal ReplayAppearance() { }

        internal IEnumerable<float> BuildSteps(WorldSnapshot world, Shader shader)
        {
            IndexInstalledMaterials();
            var recordedTextures = world.Textures.ToDictionary(texture => texture.Id, StringComparer.Ordinal);
            var namedTextures = Resources.FindObjectsOfTypeAll<Texture>().Where(texture => texture &&
                !texture.name.StartsWith("Replay ", StringComparison.Ordinal)).GroupBy(texture => texture.name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            Texture? RecordedMap(Texture? native, string id)
            {
                if (!recordedTextures.TryGetValue(id, out var captured) || captured.Name.Length == 0) return native;
                if (native && native!.name == captured.Name) return native;
                return namedTextures.TryGetValue(captured.Name, out var installed) ? installed : null;
            }
            var originals = new Dictionary<string, Material>(StringComparer.Ordinal);
            var requiredTextures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in world.Materials)
            {
                var original = installedInstances.TryGetValue(snapshot.Id, out var exactNative) && exactNative
                    ? exactNative : FindInstalledMaterial(snapshot.Name, snapshot.ShaderName);
                if (original) originals[snapshot.Id] = original!;
                if (snapshot.TextureId.Length != 0 && !RecordedMap(MainMap(original), snapshot.TextureId)) requiredTextures.Add(snapshot.TextureId);
                foreach (var property in snapshot.Properties)
                    if (property.Kind == "texture" && property.TextureId.Length != 0 &&
                        !RecordedMap(GetMap(original, property.Name), property.TextureId)) requiredTextures.Add(property.TextureId);
            }
            Dictionary<string, Shader>? loadedShaders = null;
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
            var particleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var emitter in world.ParticleEmitters) particleIds.Add(emitter.MaterialId);
            foreach (var material in world.Materials)
            {
                if (particleIds.Contains(material.Id))
                {
                    particleMaterials[material.Id] = material;
                }
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
            var completed = 0;
            var total = Math.Max(1, world.Textures.Count + world.Materials.Count);
            yield return .02f;
            foreach (var snapshot in world.Textures)
            {
                // A prepared map may already own this decoded texture. Keep its
                // Unity object when a later world pass adds native actor assets.
                if (textures.ContainsKey(snapshot.Id)) { completed++; continue; }
                // A loaded game material already owns the full-resolution maps and
                // importer state. Do not decode/upload a PNG that no renderer needs.
                if (!requiredTextures.Contains(snapshot.Id)) { completed++; continue; }
                Texture2D? texture = null;
                try
                {
                    var ground = groundTextures.Contains(snapshot.Id);
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, !ground, snapshot.Linear) { name = "Replay " + snapshot.Id, hideFlags = HideFlags.DontSave };
                    if (!ImageConversion.LoadImage(texture, snapshot.Png, false)) { Object.Destroy(texture); continue; }
                    if (!ground && alphaCutoffs.TryGetValue(snapshot.Id, out var cutoff)) PreserveAlphaCoverage(texture, cutoff);
                    texture.Apply(false, true);
                    texture.wrapMode = TextureWrapMode.Repeat;
                    texture.filterMode = snapshot.FilterMode >= 0 ? (FilterMode)snapshot.FilterMode :
                        ground ? FilterMode.Bilinear : FilterMode.Trilinear;
                    texture.anisoLevel = ground || vegetationTextures.Contains(snapshot.Id) ? 8 : 4;
                    texture.mipMapBias = vegetationTextures.Contains(snapshot.Id) && !ground ? -1.5f : -0.15f;
                    textures[snapshot.Id] = texture;
                }
                catch { if (texture) Object.Destroy(texture); }
                yield return .02f + .98f * ++completed / total;
            }
            foreach (var snapshot in world.Materials)
            {
                if (materials.ContainsKey(snapshot.Id)) { completed++; continue; }
                originals.TryGetValue(snapshot.Id, out var original);
                var shaderName = snapshot.ShaderName ?? "";
                var validName = !string.IsNullOrWhiteSpace(shaderName) &&
                    !shaderName.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase) &&
                    !shaderName.StartsWith("UI/", StringComparison.OrdinalIgnoreCase);
                var originalShader = original ? original.shader : !validName ? null : Shader.Find(shaderName);
                if (validName && (originalShader == null || !originalShader.isSupported))
                {
                    if (loadedShaders == null)
                    {
                        loadedShaders = new Dictionary<string, Shader>(StringComparer.Ordinal);
                        foreach (var loaded in Resources.FindObjectsOfTypeAll<Shader>())
                            if (loaded && loaded.isSupported && !loadedShaders.ContainsKey(loaded.name))
                                loadedShaders.Add(loaded.name, loaded);
                    }
                    loadedShaders.TryGetValue(shaderName, out originalShader);
                }
                var selectedShader = originalShader && originalShader!.isSupported ? originalShader : shader;
                // Copy the installed material, including its normal/mask maps,
                // texture import settings and shader passes. Recorded scalar/color
                // properties below still restore changes made during the game.
                var material = original ? new Material(original) : new Material(selectedShader);
                material.name = "Replay " + snapshot.Name; material.hideFlags = HideFlags.DontSave;
                var rgba = snapshot.Color;
                var color = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                // Capture stores the first primary tint in this order. Custom
                // shaders can expose several unrelated colors; leave the other
                // native tints intact unless they have their own recorded value.
                foreach (var property in new[] { "_BaseColor", "_UnlitColor", "_Color" })
                    if (material.HasProperty(property)) { material.SetColor(property, color); break; }
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
                            case "texture":
                                var nativeMap = RecordedMap(GetMap(original, property.Name), property.TextureId);
                                if (nativeMap) material.SetTexture(property.Name, nativeMap);
                                else if (textures.TryGetValue(property.TextureId, out var map)) material.SetTexture(property.Name, map);
                                material.SetTextureScale(property.Name, new Vector2(property.TextureScaleOffset[0], property.TextureScaleOffset[1]));
                                material.SetTextureOffset(property.Name, new Vector2(property.TextureScaleOffset[2], property.TextureScaleOffset[3]));
                                break;
                        }
                    }
                    catch { /* Unsupported property combinations fall back to the other recorded values. */ }
                }
                foreach (var keyword in snapshot.Keywords) material.EnableKeyword(keyword);
                var mainTexture = RecordedMap(MainMap(original), snapshot.TextureId);
                if (!mainTexture && textures.TryGetValue(snapshot.TextureId, out var decoded)) mainTexture = decoded;
                if (mainTexture && mainTexture != MainMap(original))
                {
                    var uv = snapshot.TextureScaleOffset;
                    foreach (var property in new[] { "_UnlitColorMap", "_BaseColorMap", "_MainTex", "_BaseMap" })
                    {
                        if (!material.HasProperty(property)) continue;
                        material.SetTexture(property, mainTexture);
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
                if (snapshot.Transparent && !original)
                {
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    SetFloat(material, "_SurfaceType", 1); SetFloat(material, "_ZWrite", 0);
                    // Additive/premultiplied particles must retain their recorded
                    // blend factors rather than becoming ordinary opaque billboards.
                    if (!HasProperty(snapshot, "_SrcBlend") && !HasProperty(snapshot, "_DstBlend"))
                    {
                        material.EnableKeyword("_ALPHABLEND_ON");
                        SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha); SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                        SetFloat(material, "_AlphaSrcBlend", (float)BlendMode.One); SetFloat(material, "_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    }
                    material.renderQueue = (int)RenderQueue.Transparent;
                }
                if (!original && !originalShader)
                {
                    SetFloat(material, "_CullMode", 0); SetFloat(material, "_Cull", 0); SetFloat(material, "_DoubleSidedEnable", 1);
                    material.EnableKeyword("_DOUBLESIDED_ON");
                }
                // HDRP/Lit derives its draw passes and alpha-test variants from the material
                // properties. A new Material(shader) does not initialize those passes merely
                // because _ALPHATEST_ON was enabled, leaving transparent leaf texels opaque.
                ValidateHdrpMaterial(material);
                if (snapshot.RenderQueue >= 0) material.renderQueue = snapshot.RenderQueue;
                materials[snapshot.Id] = material;
                yield return .02f + .98f * ++completed / total;
            }
            yield return 1f;
        }

        private void IndexInstalledMaterials()
        {
            installedMaterials.Clear();
            installedInstances.Clear();
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (!material || !material.shader || !material.shader.isSupported ||
                    material.name.StartsWith("Replay ", StringComparison.Ordinal) ||
                    material.name.StartsWith("LC Replay", StringComparison.Ordinal)) continue;
                installedInstances["native-prefab:" + material.GetInstanceID()] = material;
                var exact = MaterialKey(material.name, material.shader.name);
                if (!installedMaterials.ContainsKey(exact)) installedMaterials.Add(exact, material);
                var shared = MaterialKey(SharedName(material.name), material.shader.name);
                if (!installedMaterials.TryGetValue(shared, out var existing) ||
                    existing.name.EndsWith(" (Instance)", StringComparison.Ordinal)) installedMaterials[shared] = material;
            }
        }

        private Material? FindInstalledMaterial(string name, string shaderName)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(shaderName)) return null;
            if (installedMaterials.TryGetValue(MaterialKey(name, shaderName), out var exact) && exact) return exact;
            return installedMaterials.TryGetValue(MaterialKey(SharedName(name), shaderName), out var shared) && shared ? shared : null;
        }

        private static string MaterialKey(string name, string shaderName) => shaderName + "\n" + name;
        private static string SharedName(string name) => name.EndsWith(" (Instance)", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - " (Instance)".Length) : name;
        private static Texture? GetMap(Material? material, string name) =>
            material && material!.HasProperty(name) ? material.GetTexture(name) : null;
        private static Texture? MainMap(Material? material)
        {
            if (!material) return null;
            foreach (var name in MainMapNames)
            {
                var map = GetMap(material, name);
                if (map) return map;
            }
            foreach (var name in material!.GetTexturePropertyNames())
                if (name.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("basecolor", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var map = material.GetTexture(name);
                    if (map) return map;
                }
            return null;
        }
        private static bool HasProperty(MaterialSnapshot snapshot, string name)
        {
            foreach (var property in snapshot.Properties) if (property.Name == name) return true;
            return false;
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

        internal Texture2D? ResolveParticleMap(string materialId)
        {
            if (materials.TryGetValue(materialId, out var material) && MainMap(material) is Texture2D native) return native;
            if (!particleMaterials.TryGetValue(materialId, out var snapshot)) return null;
            return textures.TryGetValue(snapshot.TextureId, out var primary) ? primary : null;
        }

        // Ownership remains here; callers may share this material but must not
        // destroy it or replace its atlas/blending with a generic particle shader.
        internal Material? ResolveParticleMaterial(string materialId, string materialName, string shaderName)
        {
            if (materialId.Length != 0 && materials.TryGetValue(materialId, out var recorded))
            {
                var expectedShader = shaderName;
                if (expectedShader.Length == 0 && particleMaterials.TryGetValue(materialId, out var snapshot))
                    expectedShader = snapshot.ShaderName;
                // An opaque geometry fallback is not a usable particle material.
                // Let the caller choose its alpha-safe fallback if the source
                // particle shader is absent from this installed game version.
                if (expectedShader.Length != 0 && recorded.shader.name == expectedShader) return recorded;
            }
            var key = MaterialKey(materialName, shaderName);
            if (particleSourceCopies.TryGetValue(key, out var known)) return known;
            var original = FindInstalledMaterial(materialName, shaderName);
            if (!original) return null;
            var copy = new Material(original!) { name = "Replay " + original!.name, hideFlags = HideFlags.DontSave };
            particleSourceCopies.Add(key, copy);
            return copy;
        }

        private static void SetFloat(Material material, string property, float value) { if (material.HasProperty(property)) material.SetFloat(property, value); }
        internal static void ValidateHdrpMaterial(Material material)
        {
            if (!material.shader.name.StartsWith("HDRP/", StringComparison.Ordinal)) return;
            try
            {
                HdrpValidator?.Invoke(null, new object[] { material });
            }
            catch { /* Other pipeline versions retain the recorded shader properties. */ }
        }
        public void Dispose()
        {
            foreach (var material in materials.Values) if (material) Object.Destroy(material);
            foreach (var material in particleSourceCopies.Values) if (material) Object.Destroy(material);
            foreach (var texture in textures.Values) if (texture) Object.Destroy(texture);
            materials.Clear(); textures.Clear();
            particleMaterials.Clear(); particleSourceCopies.Clear(); installedMaterials.Clear(); installedInstances.Clear();
        }
    }
}
