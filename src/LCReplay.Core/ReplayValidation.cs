using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace LCReplay.Core
{
    internal static class ReplayValidation
    {
        public static void Limits(ReplayReadLimits l)
        {
            if (l.MaxFileBytes < 8 || l.MaxCompressedRecordBytes <= 0 || l.MaxUncompressedRecordBytes <= 0 ||
                l.MaxTotalUncompressedBytes <= 0 || l.MaxRecords <= 0 || l.MaxFrames <= 0 || l.MaxEvents <= 0 ||
                l.MaxWorlds <= 0 || l.MaxEntitiesPerFrame <= 0 || l.MaxTotalEntitySnapshots <= 0 ||
                l.MaxBonesPerEntity <= 0 || l.MaxRenderersPerEntity <= 0 || l.MaxGeometryPerWorld <= 0 ||
                l.MaxParticleEmittersPerWorld <= 0 || l.MaxLocalFogsPerWorld <= 0 || l.MaxVerticesPerGeometry <= 0 ||
                l.MaxTriangleIndicesPerGeometry <= 0 || l.MaxInstancesPerGeometry <= 0 || l.MaxDictionaryEntries <= 0 || l.MaxStringLength <= 0 ||
                l.MaxTexturesPerWorld <= 0 || l.MaxTextureDimension <= 0 || l.MaxTextureBytesPerWorld <= 0 ||
                l.MaxMaterialsPerWorld <= 0 || l.MaxMaterialSlotsPerGeometry <= 0 ||
                !Finite(l.MaxDurationSeconds) || l.MaxDurationSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(l), "Replay read limits must be positive.");
        }

        public static void Record(ReplayRecord r, ReplayReadLimits l)
        {
            if (r == null) Fail("Null record.");
            String(r.Kind, l);
            Time(r.Time, l);
            switch (r.Kind)
            {
                case "header":
                    if (r.Header == null || r.Frame != null || r.Event != null || r.World != null || r.Time != 0)
                        Fail("Malformed header record.");
                    Header(r.Header!, l);
                    break;
                case "frame":
                    if (r.Frame == null || r.Header != null || r.Event != null || r.World != null)
                        Fail("Malformed frame record.");
                    if (r.Frame!.Time != r.Time) Fail("Frame and record timestamps disagree.");
                    Frame(r.Frame, l);
                    break;
                case "event":
                    if (r.Event == null || r.Header != null || r.Frame != null || r.World != null)
                        Fail("Malformed event record.");
                    if (r.Event!.Time != r.Time) Fail("Event and record timestamps disagree.");
                    String(r.Event.Category, l); String(r.Event.Name, l); String(r.Event.EntityId, l);
                    Dictionary(r.Event.Data, l);
                    break;
                case "world":
                    if (r.World == null || r.Header != null || r.Frame != null || r.Event != null)
                        Fail("Malformed world record.");
                    World(r.World!, l);
                    break;
                case "end":
                    if (r.Header != null || r.Frame != null || r.Event != null || r.World != null)
                        Fail("Malformed end record.");
                    break;
                default: Fail("Unsupported record kind: " + r.Kind); break;
            }
        }

        private static void Header(ReplayHeader h, ReplayReadLimits l)
        {
            if (h.SchemaVersion != 1) Fail("Unsupported replay schema version: " + h.SchemaVersion);
            if (h.SampleRate < 1 || h.SampleRate > 240) Fail("Invalid sample rate.");
            String(h.SessionId, l); String(h.GameVersion, l); String(h.UnityVersion, l);
            String(h.RecorderVersion, l); String(h.StartedUtc, l); String(h.Perspective, l);
            Dictionary(h.Metadata, l);
            Strings(h.Capabilities, l); Strings(h.Warnings, l);
        }

        private static void Frame(ReplayFrame f, ReplayReadLimits l)
        {
            Time(f.Time, l); Dictionary(f.State, l);
            if (f.ParticleStyles == null || f.ParticleStyles.Count > 128) Fail("Particle style count exceeds limit.");
            var particleStyleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var style in f.ParticleStyles!)
            {
                ParticleStyle(style, l);
                if (!particleStyleIds.Add(style.Id)) Fail("Duplicate particle style identifier.");
            }
            if (f.Particles == null || f.Particles.Count > 256) Fail("Short-lived particle count exceeds limit.");
            foreach (var particle in f.Particles!)
            {
                if (particle == null) Fail("Null short-lived particle.");
                String(particle.EmitterId, l);
                if (particle.EmitterId.Length != 0 && !particleStyleIds.Contains(particle.EmitterId)) Fail("Missing particle style.");
                Vector(particle.Position); Floats(particle.Color, 4, false);
                Vector(particle.Velocity); Vector(particle.Size3D); Vector(particle.Rotation3D);
                if (!Finite(particle.Lifetime) || !Finite(particle.RemainingLifetime) || particle.Lifetime <= 0 ||
                    particle.Lifetime > 86400 || particle.RemainingLifetime < 0 || particle.RemainingLifetime > particle.Lifetime)
                    Fail("Invalid particle lifetime.");
                if (!Finite(particle.Size) || particle.Size <= 0 || particle.Size > 100 ||
                    !Finite(particle.Rotation) || Math.Abs(particle.Rotation) > 3600)
                    Fail("Invalid short-lived particle pose.");
            }
            if (f.Lines == null || f.Lines.Count > 128) Fail("Line renderer count exceeds limit.");
            var lineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in f.Lines!)
            {
                if (line == null) Fail("Null line renderer pose.");
                String(line.Id, l);
                String(line.MaterialName, l); String(line.ShaderName, l);
                if (line.TextureMode < 0 || line.TextureMode > 4 || line.Alignment < 0 || line.Alignment > 1)
                    Fail("Invalid line renderer mode.");
                if (line.Id.Length == 0 || !lineIds.Add(line.Id) || line.Positions == null ||
                    line.Positions.Length < 6 || line.Positions.Length > 96 || line.Positions.Length % 3 != 0)
                    Fail("Invalid line renderer geometry.");
                foreach (var value in line.Positions!) if (!Finite(value) || Math.Abs(value) > 1000000) Fail("Invalid line position.");
                Floats(line.StartColor, 4, false); Floats(line.EndColor, 4, false);
                if (!Finite(line.StartWidth) || !Finite(line.EndWidth) ||
                    line.StartWidth < 0 || line.EndWidth < 0 || line.StartWidth > 100 || line.EndWidth > 100)
                    Fail("Invalid line width.");
            }
            if (f.Anchors == null || f.Anchors.Count > 16) Fail("Anchor count exceeds limit.");
            var anchorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var anchor in f.Anchors!)
            {
                if (anchor == null) Fail("Null anchor.");
                String(anchor.Id, l);
                if (anchor.Id.Length == 0 || !anchorIds.Add(anchor.Id)) Fail("Invalid or duplicate anchor identifier.");
                Vector(anchor.Position); Vector(anchor.Scale); Rotation(anchor.Rotation);
            }
            if (f.SceneRenderers == null || f.SceneRenderers.Count > 512) Fail("Moving scene renderer count exceeds limit.");
            var sceneRendererIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in f.SceneRenderers!)
            {
                if (renderer == null) Fail("Null moving scene renderer pose.");
                String(renderer.Id, l);
                if (renderer.Id.Length == 0 || !sceneRendererIds.Add(renderer.Id)) Fail("Invalid or duplicate moving scene renderer identifier.");
                Vector(renderer.Position); Vector(renderer.Scale); Rotation(renderer.Rotation);
            }
            if (f.Entities == null || f.Entities.Count > l.MaxEntitiesPerFrame) Fail("Entity count exceeds limit.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in f.Entities!)
            {
                if (e == null) Fail("Null entity.");
                String(e.Id, l); String(e.Kind, l); String(e.Name, l);
                if (e.Id.Length == 0 || !ids.Add(e.Id)) Fail("Entity identifiers must be nonempty and unique per frame.");
                Vector(e.Position); Vector(e.Scale); Rotation(e.Rotation); Dictionary(e.State, l);
                if (e.Bones == null || e.Bones.Count > l.MaxBonesPerEntity) Fail("Bone count exceeds limit.");
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in e.Bones!)
                {
                    if (b == null) Fail("Null bone pose.");
                    String(b.Path, l);
                    if (!paths.Add(b.Path)) Fail("Duplicate bone path.");
                    Vector(b.Position); Vector(b.Scale); Rotation(b.Rotation);
                }
                if (e.Renderers == null || e.Renderers.Count > l.MaxRenderersPerEntity) Fail("Renderer pose count exceeds limit.");
                var rendererIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var renderer in e.Renderers!)
                {
                    if (renderer == null) Fail("Null renderer pose.");
                    String(renderer.Id, l);
                    if (renderer.Id.Length == 0 || !rendererIds.Add(renderer.Id)) Fail("Renderer identifiers must be nonempty and unique per entity.");
                    Vector(renderer.Position); Vector(renderer.Scale); Rotation(renderer.Rotation);
                }
            }
        }

        private static void World(WorldSnapshot w, ReplayReadLimits l)
        {
            String(w.Scene, l);
            String(w.CaptureSetId, l); String(w.Layer, l);
            String(w.AssetScene, l); String(w.AssetGameVersion, l);
            if (w.AssetRendererPaths == null || w.AssetTerrainPaths == null ||
                w.AssetRendererPaths.Count + w.AssetTerrainPaths.Count > 4096 ||
                (w.AssetScene.Length == 0 && (w.AssetRendererPaths.Count != 0 || w.AssetTerrainPaths.Count != 0)) ||
                (w.AssetScene.Length != 0 && (w.AssetBuildIndex < 0 || w.AssetGameVersion.Length == 0 || w.Layer != "exterior")) ||
                w.LevelId < -1 || w.DungeonFlow < -1) Fail("Invalid scene-asset reference metadata.");
            var assetPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in w.AssetRendererPaths!)
            {
                String(path, l);
                if (path.Length == 0 || path.Length > 256) Fail("Scene renderer path length is invalid: " + path.Length + ".");
                if (!assetPaths.Add("r" + path)) Fail("Duplicate scene renderer path.");
            }
            foreach (var path in w.AssetTerrainPaths!)
            {
                String(path, l);
                if (path.Length == 0 || path.Length > 256) Fail("Scene terrain path length is invalid: " + path.Length + ".");
                if (!assetPaths.Add("t" + path)) Fail("Duplicate scene terrain path.");
            }
            if (w.CaptureSetId.Length > 96 || (w.Layer != "" && w.Layer != "exterior" && w.Layer != "interior") ||
                (w.Layer.Length != 0 && w.CaptureSetId.Length == 0)) Fail("Invalid world capture layer.");
            if (w.Rooms == null || w.Rooms.Count > 4096) Fail("World room count exceeds limit.");
            var roomIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in w.Rooms!)
            {
                if (room == null) Fail("Null room.");
                String(room.Id, l); Vector(room.Center); Vector(room.Size);
                if (room.Id.Length == 0 || !roomIds.Add(room.Id) || room.Size.X <= 0 || room.Size.Y <= 0 || room.Size.Z <= 0 ||
                    room.Size.X > 10000 || room.Size.Y > 10000 || room.Size.Z > 10000) Fail("Invalid room bounds.");
                if (room.AdditionalVolumes == null || room.AdditionalVolumes.Count > 8) Fail("Room volume count exceeds limit.");
                foreach (var volume in room.AdditionalVolumes!)
                {
                    if (volume == null) Fail("Null room volume.");
                    Vector(volume.Center); Vector(volume.Size);
                    if (volume.Size.X <= 0 || volume.Size.Y <= 0 || volume.Size.Z <= 0 ||
                        volume.Size.X > 10000 || volume.Size.Y > 10000 || volume.Size.Z > 10000) Fail("Invalid additional room bounds.");
                }
            }
            if (w.Environment != null)
            {
                Floats(w.Environment.AmbientSkyColor, 4, false);
                if (w.Environment.Components == null || w.Environment.Components.Count > 16) Fail("Environment component count exceeds limit.");
                if (w.Environment.SkyFaces == null || (w.Environment.SkyFaces.Count != 0 && w.Environment.SkyFaces.Count != 6))
                    Fail("Environment cubemap needs six faces.");
                long skyBytes = 0;
                foreach (var face in w.Environment.SkyFaces!)
                {
                    if (face == null || face.Width < 1 || face.Width > 256 || face.Height != face.Width || face.Png == null || face.Png.Length < 33)
                        Fail("Invalid sky face.");
                    skyBytes += face.Png!.Length;
                    if (skyBytes > 8 * 1024 * 1024 || face.Png[0] != 137 || face.Png[1] != 80 || face.Png[2] != 78 || face.Png[3] != 71 ||
                        face.Png[4] != 13 || face.Png[5] != 10 || face.Png[6] != 26 || face.Png[7] != 10 ||
                        PngDimension(face.Png, 16) != face.Width || PngDimension(face.Png, 20) != face.Height)
                        Fail("Invalid sky PNG.");
                }
                var types = new HashSet<string>(StringComparer.Ordinal);
                foreach (var component in w.Environment.Components!)
                {
                    if (component == null) Fail("Null environment component.");
                    String(component.Type, l);
                    if (component.Type.Length == 0 || component.Type.Length > 96 || !types.Add(component.Type) ||
                        component.Parameters == null || component.Parameters.Count > 80) Fail("Invalid environment component.");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var parameter in component.Parameters)
                    {
                        if (parameter == null) Fail("Null environment parameter.");
                        String(parameter.Name, l); String(parameter.Kind, l); String(parameter.Text, l);
                        if (parameter.Name.Length == 0 || parameter.Name.Length > 96 || !names.Add(parameter.Name) ||
                            parameter.Values == null || parameter.Values.Length > 4 ||
                            !new[] { "float", "int", "bool", "color", "vector2", "vector3", "vector4", "enum" }.Contains(parameter.Kind))
                            Fail("Invalid environment parameter.");
                        foreach (var value in parameter.Values) if (!Finite(value)) Fail("Non-finite environment parameter.");
                    }
                }
            }
            if (w.Lights == null || w.Lights.Count > 512) Fail("World light count exceeds limit.");
            var lightIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var light in w.Lights!)
            {
                if (light == null) Fail("Null light.");
                String(light.Id, l); String(light.AnchorId, l); String(light.Type, l);
                if (light.Id.Length == 0 || !lightIds.Add(light.Id) ||
                    (light.Type != "Directional" && light.Type != "Point" && light.Type != "Spot")) Fail("Invalid light identifier or type.");
                Vector(light.Position); Rotation(light.Rotation); Floats(light.Color, 4, false);
                if (!Finite(light.ColorTemperature) || light.ColorTemperature < 1000 || light.ColorTemperature > 20000 ||
                    !Finite(light.Intensity) || light.Intensity < 0 || light.Intensity > 1000000 ||
                    !Finite(light.Range) || light.Range < 0 || light.Range > 100000 ||
                    !Finite(light.SpotAngle) || light.SpotAngle < 0 || light.SpotAngle > 180) Fail("Invalid light parameters.");
            }
            if (w.Textures == null || w.Textures.Count > l.MaxTexturesPerWorld) Fail("World texture count exceeds limit.");
            var textureIds = new HashSet<string>(StringComparer.Ordinal);
            long textureBytes = 0;
            foreach (var t in w.Textures!)
            {
                if (t == null) Fail("Null texture.");
                String(t.Id, l);
                if (t.Id.Length == 0 || !textureIds.Add(t.Id)) Fail("Texture identifiers must be nonempty and unique per world.");
                if (t.Width <= 0 || t.Height <= 0 || t.Width > l.MaxTextureDimension || t.Height > l.MaxTextureDimension)
                    Fail("Invalid or excessive texture dimensions.");
                if (t.Png == null || t.Png.Length < 33) Fail("Invalid PNG texture.");
                textureBytes += t.Png!.Length;
                if (textureBytes > l.MaxTextureBytesPerWorld) Fail("World texture byte budget exceeded.");
                // Check the PNG IHDR before the rendering engine allocates its decoded image.
                var p = t.Png;
                if (p[0] != 137 || p[1] != 80 || p[2] != 78 || p[3] != 71 || p[4] != 13 || p[5] != 10 || p[6] != 26 || p[7] != 10 ||
                    p[8] != 0 || p[9] != 0 || p[10] != 0 || p[11] != 13 || p[12] != 73 || p[13] != 72 || p[14] != 68 || p[15] != 82 ||
                    PngDimension(p, 16) != t.Width || PngDimension(p, 20) != t.Height) Fail("PNG header and texture dimensions disagree.");
            }
            if (w.Materials == null || w.Materials.Count > l.MaxMaterialsPerWorld) Fail("World material count exceeds limit.");
            var materialIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in w.Materials!)
            {
                if (m == null) Fail("Null material.");
                String(m.Id, l); String(m.Name, l); String(m.ShaderName, l); String(m.TextureId, l);
                if (m.Id.Length == 0 || !materialIds.Add(m.Id)) Fail("Material identifiers must be nonempty and unique per world.");
                Floats(m.Color, 4, false); Floats(m.TextureScaleOffset, 4, false);
                if (!Finite(m.Cutoff) || m.Cutoff < 0 || m.Cutoff > 1) Fail("Invalid alpha cutoff.");
                if (m.TextureId.Length != 0 && !textureIds.Contains(m.TextureId)) Fail("Material references missing texture.");
                if (m.RenderQueue < -1 || m.RenderQueue > 5000 || m.Keywords == null || m.Keywords.Count > 64 ||
                    m.Properties == null || m.Properties.Count > 96) Fail("Invalid material properties.");
                foreach (var keyword in m.Keywords) { String(keyword, l); if (keyword.Length > 128) Fail("Material keyword too long."); }
                var propertyNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in m.Properties)
                {
                    if (property == null) Fail("Null material property.");
                    String(property.Name, l); String(property.Kind, l); String(property.TextureId, l);
                    if (property.Name.Length == 0 || property.Name.Length > 128 || !propertyNames.Add(property.Name) ||
                        property.Values == null || property.Values.Length > 4 ||
                        !new[] { "float", "color", "vector", "texture" }.Contains(property.Kind)) Fail("Invalid material property.");
                    foreach (var value in property.Values) if (!Finite(value)) Fail("Non-finite material property.");
                    Floats(property.TextureScaleOffset, 4, false);
                    if (property.TextureId.Length != 0 && !textureIds.Contains(property.TextureId)) Fail("Material property references missing texture.");
                }
            }
            if (w.ParticleEmitters == null || w.ParticleEmitters.Count > l.MaxParticleEmittersPerWorld)
                Fail("World particle emitter count exceeds limit.");
            var emitterIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var emitter in w.ParticleEmitters!)
            {
                if (emitter == null) Fail("Null particle emitter.");
                String(emitter.Id, l); String(emitter.Name, l); String(emitter.EntityId, l);
                String(emitter.MaterialId, l); String(emitter.RoomId, l);
                if (emitter.Style != null) ParticleStyle(emitter.Style, l);
                if (emitter.Id.Length == 0 || !emitterIds.Add(emitter.Id) ||
                    (emitter.MaterialId.Length != 0 && !materialIds.Contains(emitter.MaterialId)))
                    Fail("Invalid particle emitter reference.");
                Vector(emitter.Position); Rotation(emitter.Rotation); Floats(emitter.Color, 4, false);
                if (!Finite(emitter.Rate) || emitter.Rate < 0 || emitter.Rate > 1000 ||
                    !Finite(emitter.Lifetime) || emitter.Lifetime <= 0 || emitter.Lifetime > 120 ||
                    !Finite(emitter.Speed) || emitter.Speed < 0 || emitter.Speed > 100 ||
                    !Finite(emitter.Size) || emitter.Size <= 0 || emitter.Size > 100 ||
                    !Finite(emitter.Radius) || emitter.Radius < 0 || emitter.Radius > 100)
                    Fail("Invalid particle emitter settings.");
            }
            if (w.LocalFogs == null || w.LocalFogs.Count > l.MaxLocalFogsPerWorld)
                Fail("World local fog count exceeds limit.");
            var localFogIds = new HashSet<string>(StringComparer.Ordinal);
            long localMaskBytes = 0;
            foreach (var fog in w.LocalFogs!)
            {
                if (fog == null) Fail("Null local fog.");
                String(fog.Id, l); String(fog.Name, l); String(fog.RoomId, l);
                if (fog.Id.Length == 0 || !localFogIds.Add(fog.Id) || fog.BlendingMode < 0 || fog.BlendingMode > 8 ||
                    fog.FalloffMode < 0 || fog.FalloffMode > 8 || fog.MaskMode < 0 || fog.MaskMode > 1 ||
                    fog.Priority < -10000 || fog.Priority > 10000)
                    Fail("Invalid local fog identifier or mode.");
                Vector(fog.Position); Rotation(fog.Rotation); Vector(fog.Size);
                Vector(fog.PositiveFade); Vector(fog.NegativeFade); Vector(fog.TextureTiling);
                Vector(fog.TextureScrollingSpeed); Floats(fog.Albedo, 4, false);
                if (fog.PositiveFade.X < 0 || fog.PositiveFade.Y < 0 || fog.PositiveFade.Z < 0 ||
                    fog.PositiveFade.X > 1 || fog.PositiveFade.Y > 1 || fog.PositiveFade.Z > 1 ||
                    fog.NegativeFade.X < 0 || fog.NegativeFade.Y < 0 || fog.NegativeFade.Z < 0 ||
                    fog.NegativeFade.X > 1 || fog.NegativeFade.Y > 1 || fog.NegativeFade.Z > 1 ||
                    fog.Size.X <= 0 || fog.Size.Y <= 0 || fog.Size.Z <= 0 ||
                    fog.Size.X > 10000 || fog.Size.Y > 10000 || fog.Size.Z > 10000 ||
                    !Finite(fog.MeanFreePath) || fog.MeanFreePath < .05f || fog.MeanFreePath > 1000000 ||
                    !Finite(fog.Anisotropy) || fog.Anisotropy < -1 || fog.Anisotropy > 1 ||
                    !Finite(fog.DistanceFadeStart) || fog.DistanceFadeStart < 0 ||
                    !Finite(fog.DistanceFadeEnd) || fog.DistanceFadeEnd < fog.DistanceFadeStart || fog.DistanceFadeEnd > 1000000)
                    Fail("Invalid local fog parameters.");
                if (fog.MaskRgba == null || fog.MaskWidth < 0 || fog.MaskHeight < 0 || fog.MaskDepth < 0 ||
                    fog.MaskWidth > 64 || fog.MaskHeight > 64 || fog.MaskDepth > 64 ||
                    (fog.MaskWidth == 0 || fog.MaskHeight == 0 || fog.MaskDepth == 0
                        ? fog.MaskWidth != 0 || fog.MaskHeight != 0 || fog.MaskDepth != 0 || fog.MaskRgba.Length != 0
                        : fog.MaskRgba.Length != fog.MaskWidth * fog.MaskHeight * fog.MaskDepth * 4))
                    Fail("Invalid local fog texture mask.");
                localMaskBytes += fog.MaskRgba.Length;
                if (localMaskBytes > 4 * 1024 * 1024) Fail("World local fog mask byte budget exceeded.");
            }
            if (w.Geometry == null || w.Geometry.Count > l.MaxGeometryPerWorld) Fail("World geometry count exceeds limit.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var meshSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in w.Geometry!)
            {
                if (g == null) Fail("Null geometry.");
                String(g.Id, l); String(g.Name, l); String(g.EntityId, l); String(g.MeshName, l);
                String(g.MeshSourceId, l); String(g.RoomId, l); String(g.AnchorId, l);
                String(g.LodGroupId, l);
                if (g.Id.Length == 0 || !ids.Add(g.Id)) Fail("Geometry identifiers must be nonempty and unique per world.");
                Vector(g.Position); Vector(g.Scale); Rotation(g.Rotation); Vector(g.BoundsCenter); Vector(g.BoundsSize);
                Vector(g.LodCenter);
                if (g.LodLevel < 0 || g.LodLevel > 1 || !Finite(g.LodSwitchDistance) ||
                    g.LodSwitchDistance < 0 || g.LodSwitchDistance > 1000 ||
                    (g.LodGroupId.Length != 0 && (g.LodSwitchDistance <= 0 || g.EntityId.Length != 0 || g.IsInterior)))
                    Fail("Invalid natural LOD metadata.");
                if (g.BoundsSize.X < 0 || g.BoundsSize.Y < 0 || g.BoundsSize.Z < 0) Fail("Negative geometry bounds.");
                if (g.Color == null || g.Color.Length != 4) Fail("Color must contain four components.");
                foreach (var c in g.Color!) if (!Finite(c)) Fail("Non-finite color component.");
                if (g.Vertices == null || g.Vertices.Length % 3 != 0 || g.Vertices.Length / 3 > l.MaxVerticesPerGeometry)
                    Fail("Invalid or excessive vertex count.");
                foreach (var v in g.Vertices!) if (!Finite(v)) Fail("Non-finite mesh vertex.");
                if (g.Triangles == null || g.Triangles.Length % 3 != 0 || g.Triangles.Length > l.MaxTriangleIndicesPerGeometry)
                    Fail("Invalid or excessive triangle count.");
                foreach (var t in g.Triangles!) if (t < 0 || t >= g.Vertices.Length / 3) Fail("Triangle index outside vertex array.");
                var vertices = g.Vertices.Length / 3;
                Floats(g.Uvs, vertices * 2, true); Floats(g.Normals, vertices * 3, true);
                if (g.Instances == null || g.Instances.Length % 16 != 0 || g.Instances.Length / 16 > l.MaxInstancesPerGeometry ||
                    (g.Instances.Length != 0 && (g.IsBoundsProxy || g.MeshSourceId.Length != 0 || g.EntityId.Length != 0 || vertices < 3)))
                    Fail("Invalid instanced geometry.");
                foreach (var value in g.Instances!) if (!Finite(value)) Fail("Non-finite instance matrix.");
                for (var matrix = 0; matrix < g.Instances.Length; matrix += 16)
                    if (Math.Abs(g.Instances[matrix + 3]) > 0.001f || Math.Abs(g.Instances[matrix + 7]) > 0.001f ||
                        Math.Abs(g.Instances[matrix + 11]) > 0.001f || Math.Abs(g.Instances[matrix + 15] - 1f) > 0.001f)
                        Fail("Instance matrix must be affine.");
                if (g.SubmeshTriangles == null || g.SubmeshTriangles.Count > l.MaxMaterialSlotsPerGeometry ||
                    g.MaterialIds == null || g.MaterialIds.Count > l.MaxMaterialSlotsPerGeometry) Fail("Material slot count exceeds limit.");
                long indices = 0;
                foreach (var submesh in g.SubmeshTriangles!)
                {
                    if (submesh == null || submesh.Length % 3 != 0) Fail("Invalid submesh indices.");
                    indices += submesh!.Length;
                    if (indices > l.MaxTriangleIndicesPerGeometry) Fail("Excessive submesh indices.");
                    foreach (var i in submesh) if (i < 0 || i >= vertices) Fail("Submesh index outside vertex array.");
                }
                foreach (var id in g.MaterialIds!)
                {
                    String(id, l);
                    if (id.Length != 0 && !materialIds.Contains(id)) Fail("Geometry references missing material.");
                }
                if (g.BonePaths == null || g.BonePaths.Count > l.MaxBonesPerEntity) Fail("Mesh bone count exceeds limit.");
                String(g.RootBonePath, l);
                if (g.RootBonePath.Length > 4096) Fail("Mesh root bone path exceeds limit.");
                var rootDepth = 1;
                foreach (var c in g.RootBonePath) if (c == '/' && ++rootDepth > 128) Fail("Mesh root bone hierarchy exceeds limit.");
                foreach (var path in g.BonePaths!)
                {
                    String(path, l);
                    if (path.Length > 4096) Fail("Mesh bone path exceeds limit.");
                    var depth = 1;
                    foreach (var c in path) if (c == '/' && ++depth > 128) Fail("Mesh bone hierarchy exceeds limit.");
                }
                Floats(g.BindPoses, g.BonePaths.Count * 16, false);
                if (g.BoneIndices == null || g.BoneWeights == null) Fail("Null bone weights.");
                if (g.BonePaths.Count == 0)
                {
                    if (g.BoneIndices!.Length != 0 || g.BoneWeights!.Length != 0) Fail("Bone weights without skeleton.");
                }
                else
                {
                    if (g.BoneIndices!.Length != vertices * 4) Fail("Invalid bone indices.");
                    Floats(g.BoneWeights!, vertices * 4, false);
                    for (var i = 0; i < g.BoneIndices.Length; i++)
                        if (g.BoneIndices[i] < 0 || g.BoneIndices[i] >= g.BonePaths.Count || g.BoneWeights![i] < 0 || g.BoneWeights[i] > 1)
                            Fail("Invalid bone influence.");
                }
                if (g.MeshSourceId.Length != 0)
                {
                    if (!meshSources.Contains(g.MeshSourceId) || g.IsBoundsProxy || g.Vertices.Length != 0 || g.Triangles.Length != 0 ||
                        g.SubmeshTriangles!.Count != 0 || g.Uvs.Length != 0 || g.Normals.Length != 0 || g.BonePaths.Count != 0)
                        Fail("Mesh instance must reference an earlier static mesh without duplicating arrays.");
                }
                else if (!g.IsBoundsProxy && g.Vertices.Length >= 9 && g.BonePaths.Count == 0) meshSources.Add(g.Id);
            }
        }

        private static void ParticleStyle(ParticleStyleSnapshot style, ReplayReadLimits l)
        {
            if (style == null) Fail("Null particle style.");
            String(style.Id, l); String(style.Name, l); String(style.ParentName, l);
            String(style.MaterialName, l); String(style.ShaderName, l); String(style.MeshName, l);
            if (style.Id.Length == 0 || style.RenderMode < 0 || style.RenderMode > 5 ||
                style.Alignment < 0 || style.Alignment > 5 || style.VertexStreams == null || style.VertexStreams.Length > 32)
                Fail("Invalid particle renderer style.");
            foreach (var stream in style.VertexStreams!) if (stream < 0 || stream > 63) Fail("Invalid particle vertex stream.");
            Vector(style.Scale); Vector(style.Pivot);
            Vector(style.Position); Rotation(style.Rotation);
            if (!Finite(style.Time) || style.Time < 0 || style.Time > 86400) Fail("Invalid particle simulation time.");
            if (!Finite(style.LengthScale) || !Finite(style.VelocityScale) || !Finite(style.CameraVelocityScale) ||
                Math.Abs(style.LengthScale) > 10000 || Math.Abs(style.VelocityScale) > 10000 || Math.Abs(style.CameraVelocityScale) > 10000)
                Fail("Invalid particle stretch settings.");
        }

        private static uint PngDimension(byte[] data, int offset) =>
            ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

        private static void Floats(float[] values, int count, bool allowEmpty)
        {
            if (values == null || (values.Length != count && !(allowEmpty && values.Length == 0))) Fail("Invalid mesh or material array length.");
            foreach (var value in values!) if (!Finite(value)) Fail("Non-finite mesh or material component.");
        }

        private static void Dictionary(Dictionary<string, string> d, ReplayReadLimits l)
        {
            if (d == null || d.Count > l.MaxDictionaryEntries) Fail("Dictionary size exceeds limit.");
            foreach (var p in d!) { String(p.Key, l); String(p.Value, l); }
        }

        private static void Strings(List<string> values, ReplayReadLimits l)
        {
            if (values == null || values.Count > l.MaxDictionaryEntries) Fail("String list exceeds limit.");
            foreach (var value in values!) String(value, l);
        }

        private static void String(string value, ReplayReadLimits l)
        {
            if (value == null || value.Length > l.MaxStringLength) Fail("Null or excessive string.");
        }

        private static void Time(double value, ReplayReadLimits l)
        {
            if (!Finite(value) || value < 0 || value > l.MaxDurationSeconds) Fail("Invalid or excessive replay time.");
        }

        private static void Vector(Vec3 v)
        {
            if (!Finite(v.X) || !Finite(v.Y) || !Finite(v.Z)) Fail("Non-finite vector.");
        }

        private static void Rotation(Quat q)
        {
            double norm = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
            if (!Finite(norm) || norm < 1e-12 || norm > 1e12) Fail("Invalid quaternion.");
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        [DoesNotReturn]
        private static void Fail(string message) => throw new InvalidDataException(message);
    }
}
