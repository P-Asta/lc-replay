using System;
using System.Collections.Generic;

namespace LCReplay.Core
{
    public sealed class ReplayHeader
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public string UnityVersion { get; set; } = "";
        public string RecorderVersion { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string Perspective { get; set; } = "";
        public int SampleRate { get; set; } = 10;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public List<string> Capabilities { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class ReplayRecord
    {
        public string Kind { get; set; } = "";
        public double Time { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public double? SourceTime { get; set; }
        public ReplayHeader? Header { get; set; }
        public ReplayFrame? Frame { get; set; }
        public ReplayEvent? Event { get; set; }
        public WorldSnapshot? World { get; set; }
    }

    public sealed class ReplayFrame
    {
        public double Time { get; set; }
        public List<EntitySnapshot> Entities { get; set; } = new List<EntitySnapshot>();
        public List<AnchorPose> Anchors { get; set; } = new List<AnchorPose>();
        public List<RenderPose> SceneRenderers { get; set; } = new List<RenderPose>();
        public List<ParticlePose> Particles { get; set; } = new List<ParticlePose>();
        public List<ParticleStyleSnapshot> ParticleStyles { get; set; } = new List<ParticleStyleSnapshot>();
        public List<LinePose> Lines { get; set; } = new List<LinePose>();
        public Dictionary<string, string> State { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>One observed particle from a short-lived Unity ParticleSystem.</summary>
    public sealed class ParticlePose
    {
        public string EmitterId { get; set; } = "";
        public Vec3 Position { get; set; }
        public Vec3 Velocity { get; set; }
        public Vec3 Size3D { get; set; }
        public Vec3 Rotation3D { get; set; }
        public float Lifetime { get; set; } = 1f;
        public float RemainingLifetime { get; set; } = 1f;
        public uint RandomSeed { get; set; }
        public float Size { get; set; }
        public float Rotation { get; set; }
        public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public bool IsInterior { get; set; }
    }

    /// <summary>A bounded world-space LineRenderer sample (lasers, ropes and beams).</summary>
    public sealed class LinePose
    {
        public string Id { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public string ShaderName { get; set; } = "";
        public int TextureMode { get; set; }
        public int Alignment { get; set; }
        public float[] Positions { get; set; } = System.Array.Empty<float>();
        public float[] StartColor { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public float[] EndColor { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public float StartWidth { get; set; }
        public float EndWidth { get; set; }
        public bool IsInterior { get; set; }
    }

    /// <summary>World-space pose of a moving environment root, such as the ship elevator.</summary>
    public sealed class AnchorPose
    {
        public string Id { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
    }

    public sealed class EntitySnapshot
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public Vec3 Position { get; set; }
        public Vec3 Scale { get; set; } = Vec3.One;
        public Quat Rotation { get; set; } = Quat.Identity;
        public bool Active { get; set; } = true;
        public Quat? ViewRotation { get; set; }
        public Vec3? ViewPosition { get; set; }
        public bool ShouldSerializeViewRotation() => ViewRotation.HasValue;
        public bool ShouldSerializeViewPosition() => ViewPosition.HasValue;
        // Item poses can be reconstructed from sparse item/pose events. Keep
        // ordinary poses for held or otherwise unpredictable items.
        public bool PoseFromItemEvents { get; set; }
        public bool ShouldSerializePoseFromItemEvents() => PoseFromItemEvents;
        public bool ShouldSerializePosition() => !PoseFromItemEvents;
        public bool ShouldSerializeRotation() => !PoseFromItemEvents;
        public bool ShouldSerializeScale() => !PoseFromItemEvents;
        public Dictionary<string, string> State { get; set; } = new Dictionary<string, string>();
        public List<BonePose> Bones { get; set; } = new List<BonePose>();
        public List<RenderPose> Renderers { get; set; } = new List<RenderPose>();
    }

    public struct Vec3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 One => new Vec3(1, 1, 1);
    }

    public struct Quat
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static Quat Identity => new Quat(0, 0, 0, 1);
    }

    public sealed class BonePose
    {
        public string Path { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
    }

    /// <summary>A captured renderer transform relative to its entity root; Id matches GeometrySnapshot.Id.</summary>
    public sealed class RenderPose
    {
        public string Id { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
        public bool Active { get; set; } = true;
    }

    public sealed class ReplayEvent
    {
        public double Time { get; set; }
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string EntityId { get; set; } = "";
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();
        // One short, reusable motion track learned during the clip's first
        // occurrence. It lets a fresh menu process replay an actor whose game
        // AnimatorController has not yet been loaded.
        public AnimationTrackSnapshot? AnimationTrack { get; set; }
        public ItemMotionSnapshot? ItemMotion { get; set; }
        // Full blended HDRP component only when its effective values changed.
        public EnvironmentComponentSnapshot? PostProcess { get; set; }
    }

    // A resting item needs one pose; a dropped item needs its start and floor
    // target plus a small sampled copy of the game's shared fall curve.
    public sealed class ItemMotionSnapshot
    {
        public string Mode { get; set; } = "";
        public string AnchorId { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
        public Vec3 Target { get; set; }
        public Quat TargetRotation { get; set; } = Quat.Identity;
        public float FallTime { get; set; }
        public float FallRate { get; set; }
        public float[] Curve { get; set; } = new float[0];
        public bool ShouldSerializeAnchorId() => AnchorId.Length != 0;
        public bool ShouldSerializeTarget() => Mode == "fall";
        public bool ShouldSerializeTargetRotation() => Mode == "fall";
        public bool ShouldSerializeFallTime() => Mode == "fall";
        public bool ShouldSerializeFallRate() => Mode == "fall";
        public bool ShouldSerializeCurve() => Mode == "fall";
    }

    public sealed class AnimationTrackSnapshot
    {
        public string Clip { get; set; } = "";
        // Identifies the Animator on actors with independent body/prop rigs.
        // Empty in older recordings, which used one Animator per actor.
        public string AnimatorPath { get; set; } = "";
        // Full Animator state path distinguishes a shared clip used by different states.
        public int StateHash { get; set; }
        public int Layer { get; set; }
        public bool Looping { get; set; }
        public List<string> BonePaths { get; set; } = new List<string>();
        public float[] Phases { get; set; } = new float[0];
        // Sample-major local transform arrays; scale comes from RigBones.
        public float[] Positions { get; set; } = new float[0];
        public float[] Rotations { get; set; } = new float[0];
    }

    public sealed class WorldSnapshot
    {
        public string Scene { get; set; } = "";
        public string CaptureSetId { get; set; } = "";
        public string Layer { get; set; } = "";
        // Observation time of this world payload; ReplayRecord.Time may be
        // earlier so generated scenery is available when the round begins.
        public double CaptureCompletedAt { get; set; }
        public bool ShouldSerializeCaptureCompletedAt() => CaptureCompletedAt > 0;
        // Built-in scene renderers can be loaded from the installed matching game.
        // Procedural and moving objects remain embedded below.
        public string AssetScene { get; set; } = "";
        public string AssetGameVersion { get; set; } = "";
        public int AssetBuildIndex { get; set; } = -1;
        public int MapSeed { get; set; }
        public int LevelId { get; set; } = -1;
        public int DungeonSeed { get; set; }
        public int DungeonFlow { get; set; } = -1;
        public List<string> AssetRendererPaths { get; set; } = new List<string>();
        public List<string> AssetTerrainPaths { get; set; } = new List<string>();
        public List<GeometrySnapshot> Geometry { get; set; } = new List<GeometrySnapshot>();
        public List<TextureSnapshot> Textures { get; set; } = new List<TextureSnapshot>();
        public List<MaterialSnapshot> Materials { get; set; } = new List<MaterialSnapshot>();
        public List<LightSnapshot> Lights { get; set; } = new List<LightSnapshot>();
        public List<ParticleEmitterSnapshot> ParticleEmitters { get; set; } = new List<ParticleEmitterSnapshot>();
        public List<LocalFogSnapshot> LocalFogs { get; set; } = new List<LocalFogSnapshot>();
        public List<RoomSnapshot> Rooms { get; set; } = new List<RoomSnapshot>();
        public EnvironmentSnapshot? Environment { get; set; }
    }

    public sealed class RoomSnapshot
    {
        public string Id { get; set; } = "";
        public Vec3 Center { get; set; }
        public Vec3 Size { get; set; }
        public List<RoomVolumeSnapshot> AdditionalVolumes { get; set; } = new List<RoomVolumeSnapshot>();
    }

    public sealed class RoomVolumeSnapshot
    {
        public Vec3 Center { get; set; }
        public Vec3 Size { get; set; }
    }

    public sealed class EnvironmentSnapshot
    {
        public float[] AmbientSkyColor { get; set; } = new[] { 0.15f, 0.20f, 0.28f, 1f };
        public List<EnvironmentComponentSnapshot> Components { get; set; } = new List<EnvironmentComponentSnapshot>();
        public List<TextureSnapshot> SkyFaces { get; set; } = new List<TextureSnapshot>();
        public string SkyFaceEncoding { get; set; } = "";
        public bool ShouldSerializeSkyFaceEncoding() => SkyFaceEncoding.Length != 0;
        public List<PostProcessPassSnapshot> CustomPasses { get; set; } = new List<PostProcessPassSnapshot>();
        public bool CustomPassCaptureComplete { get; set; }
        public bool ShouldSerializeCustomPasses() => CustomPasses.Count != 0;
        public bool ShouldSerializeCustomPassCaptureComplete() => CustomPassCaptureComplete;
    }

    public sealed class PostProcessPassSnapshot
    {
        public string Name { get; set; } = "";
        public string ShaderName { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public string InjectionPoint { get; set; } = "";
        public string MaterialPassName { get; set; } = "";
        public bool? FetchColorBuffer { get; set; }
        public List<EnvironmentParameterSnapshot> Properties { get; set; } = new List<EnvironmentParameterSnapshot>();
        public bool ShouldSerializeMaterialName() => MaterialName.Length != 0;
        public bool ShouldSerializeFetchColorBuffer() => FetchColorBuffer.HasValue;
    }

    public sealed class EnvironmentComponentSnapshot
    {
        public string Type { get; set; } = "";
        public List<EnvironmentParameterSnapshot> Parameters { get; set; } = new List<EnvironmentParameterSnapshot>();
    }

    public sealed class EnvironmentParameterSnapshot
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public float[] Values { get; set; } = new float[0];
        public string Text { get; set; } = "";
        // TextureCurve: up to 32 keyframes, seven floats per key.
        public float[] CurveKeys { get; set; } = new float[0];
        public bool ShouldSerializeText() => Text.Length != 0;
        public bool ShouldSerializeCurveKeys() => CurveKeys.Length != 0;
    }

    public sealed class LightSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool ShouldSerializeName() => Name.Length != 0;
        public string AnchorId { get; set; } = "";
        public string EntityId { get; set; } = "";
        public bool IsInterior { get; set; }
        public string Type { get; set; } = "Point";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public bool UseColorTemperature { get; set; }
        public float ColorTemperature { get; set; } = 6500f;
        public float Intensity { get; set; } = 1f;
        public float LightDimmer { get; set; } = 1f;
        public float Range { get; set; } = 10f;
        public float SpotAngle { get; set; } = 30f;
        public bool Shadows { get; set; }
        public float ShadowStrength { get; set; } = 1f;
        public float ShadowDimmer { get; set; } = 1f;
        // Older recordings did not distinguish realtime from baked fixtures.
        public string BakeType { get; set; } = "";
        public List<EnvironmentParameterSnapshot> LightPipelineParameters { get; set; } = new List<EnvironmentParameterSnapshot>();
        public bool ShouldSerializeLightPipelineParameters() => LightPipelineParameters.Count != 0;
        public bool ShouldSerializeShadowStrength() => ShadowStrength != 1f;
        public bool ShouldSerializeLightDimmer() => LightDimmer != 1f;
        public bool ShouldSerializeShadowDimmer() => ShadowDimmer != 1f;
        public bool ShouldSerializeBakeType() => BakeType.Length != 0;
    }

    public sealed class ParticleEmitterSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string MaterialId { get; set; } = "";
        public ParticleStyleSnapshot? Style { get; set; }
        public bool IsInterior { get; set; }
        public string RoomId { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public float Rate { get; set; } = 10f;
        public float Lifetime { get; set; } = 1f;
        public float Speed { get; set; }
        public float Size { get; set; } = 0.1f;
        public float Radius { get; set; } = 0.1f;
    }

    /// <summary>Identity and renderer state of the original effect; never a generic white billboard.</summary>
    public sealed class ParticleStyleSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ParentName { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public string ShaderName { get; set; } = "";
        public string MeshName { get; set; } = "";
        public bool Simulate { get; set; }
        public bool IsInterior { get; set; }
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public float Time { get; set; }
        public uint RandomSeed { get; set; }
        public int RenderMode { get; set; }
        public int Alignment { get; set; }
        public int[] VertexStreams { get; set; } = System.Array.Empty<int>();
        public float LengthScale { get; set; } = 2f;
        public float VelocityScale { get; set; }
        public float CameraVelocityScale { get; set; }
        public Vec3 Scale { get; set; } = Vec3.One;
        public Vec3 Pivot { get; set; }
    }

    public sealed class LocalFogSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsInterior { get; set; }
        public string RoomId { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Size { get; set; } = new Vec3(1, 1, 1);
        public Vec3 PositiveFade { get; set; } = new Vec3(.1f, .1f, .1f);
        public Vec3 NegativeFade { get; set; } = new Vec3(.1f, .1f, .1f);
        public float[] Albedo { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public float MeanFreePath { get; set; } = 10f;
        public float Anisotropy { get; set; }
        public float DistanceFadeStart { get; set; } = 10000f;
        public float DistanceFadeEnd { get; set; } = 10000f;
        public int Priority { get; set; }
        public int BlendingMode { get; set; }
        public int FalloffMode { get; set; }
        public bool InvertFade { get; set; }
        public int MaskMode { get; set; }
        public Vec3 TextureTiling { get; set; } = Vec3.One;
        public Vec3 TextureScrollingSpeed { get; set; }
        public int MaskWidth { get; set; }
        public int MaskHeight { get; set; }
        public int MaskDepth { get; set; }
        public byte[] MaskRgba { get; set; } = new byte[0];
    }

    public sealed class TextureSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool ShouldSerializeName() => Name.Length != 0;
        public int Width { get; set; }
        public int Height { get; set; }
        // Older captures encoded every PNG through an sRGB target. False retains
        // that interpretation; new normal/mask maps keep their linear channels.
        public bool Linear { get; set; }
        // -1 keeps the legacy replay filtering for recordings without this field.
        public int FilterMode { get; set; } = -1;
        public bool ShouldSerializeFilterMode() => FilterMode >= 0;
        public byte[] Png { get; set; } = new byte[0];
    }

    public sealed class MaterialSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ShaderName { get; set; } = "";
        public float[] Color { get; set; } = new[] { 1f, 1f, 1f, 1f };
        public string TextureId { get; set; } = "";
        public float[] TextureScaleOffset { get; set; } = new[] { 1f, 1f, 0f, 0f };
        public bool AlphaClip { get; set; }
        public bool Transparent { get; set; }
        public float Cutoff { get; set; } = 0.5f;
        public int RenderQueue { get; set; } = -1;
        public List<string> Keywords { get; set; } = new List<string>();
        // Null identifies older captures or native-asset placeholders. False
        // explicitly preserves a keyword list truncated by capture limits.
        public bool? KeywordsComplete { get; set; }
        public bool ShouldSerializeKeywordsComplete() => KeywordsComplete.HasValue;
        public List<MaterialPropertySnapshot> Properties { get; set; } = new List<MaterialPropertySnapshot>();
    }

    public sealed class MaterialPropertySnapshot
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public float[] Values { get; set; } = new float[0];
        public string TextureId { get; set; } = "";
        public float[] TextureScaleOffset { get; set; } = new[] { 1f, 1f, 0f, 0f };
    }

    public sealed class GeometrySnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public Vec3 Position { get; set; }
        public Vec3 Scale { get; set; } = Vec3.One;
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 BoundsCenter { get; set; }
        public Vec3 BoundsSize { get; set; }
        public float[] Vertices { get; set; } = new float[0];
        public int[] Triangles { get; set; } = new int[0];
        public float[] Color { get; set; } = new[] { 0.6f, 0.6f, 0.6f, 1f };
        public bool IsBoundsProxy { get; set; }
        // Preserve the source renderer's shadow behavior. Defaults keep old recordings compatible.
        public int ShadowCastingMode { get; set; } = -1;
        public bool ReceiveShadows { get; set; } = true;
        public bool ShouldSerializeShadowCastingMode() => ShadowCastingMode != -1;
        public bool ShouldSerializeReceiveShadows() => !ReceiveShadows;
        public string EntityId { get; set; } = "";
        public string AnchorId { get; set; } = "";
        public string MeshName { get; set; } = "";
        public string MeshSourceId { get; set; } = "";
        // Installed prefab render assets. Only identifiers are recorded; playback
        // resolves the matching game mesh, skin and materials without running AI.
        public string PrefabKey { get; set; } = "";
        public string PrefabRendererPath { get; set; } = "";
        public bool ShouldSerializePrefabKey() => PrefabKey.Length != 0;
        public bool ShouldSerializePrefabRendererPath() => PrefabRendererPath.Length != 0;
        public bool IsInterior { get; set; }
        public string RoomId { get; set; } = "";
        // Optional pose stream for moving furniture and doors in generated tiles.
        public bool IsMovingSceneRenderer { get; set; }
        public bool Active { get; set; } = true;
        // Optional natural-object LOD pair, selected by the replay camera.
        public string LodGroupId { get; set; } = "";
        public int LodLevel { get; set; }
        public Vec3 LodCenter { get; set; }
        public float LodSwitchDistance { get; set; }
        public float[] Uvs { get; set; } = new float[0];
        public float[] Uvs1 { get; set; } = new float[0];
        public float[] Uvs2 { get; set; } = new float[0];
        public float[] Uvs3 { get; set; } = new float[0];
        public float[] Tangents { get; set; } = new float[0];
        public bool ShouldSerializeUvs1() => Uvs1.Length != 0;
        public bool ShouldSerializeUvs2() => Uvs2.Length != 0;
        public bool ShouldSerializeUvs3() => Uvs3.Length != 0;
        public bool ShouldSerializeTangents() => Tangents.Length != 0;
        public float[] Normals { get; set; } = new float[0];
        // Optional world-space matrices for procedural GPU-instanced geometry.
        // Sixteen column-major floats describe each instance.
        public float[] Instances { get; set; } = new float[0];
        public List<int[]> SubmeshTriangles { get; set; } = new List<int[]>();
        public List<string> MaterialIds { get; set; } = new List<string>();
        public List<string> BonePaths { get; set; } = new List<string>();
        // Optional parent for rigid details attached to a skinned actor bone.
        // Their pose is stored in that bone's local space.
        public string AttachedBonePath { get; set; } = "";
        public string RootBonePath { get; set; } = "";
        public float[] BindPoses { get; set; } = new float[0];
        public int[] BoneIndices { get; set; } = new int[0];
        public float[] BoneWeights { get; set; } = new float[0];
        // One rig pose per captured skin. Runtime animation states can then be
        // replayed without a bone transform stream in every gameplay frame.
        public List<BonePose> RigBones { get; set; } = new List<BonePose>();
        public string AnimatorPath { get; set; } = "";
        public string AnimatorController { get; set; } = "";
        public string AnimatorAvatar { get; set; } = "";
    }

    public sealed class ReplaySession
    {
        public ReplayHeader Header { get; set; } = new ReplayHeader();
        public List<ReplayFrame> Frames { get; set; } = new List<ReplayFrame>();
        public List<ReplayEvent> Events { get; set; } = new List<ReplayEvent>();
        public List<ReplayRecord> Worlds { get; set; } = new List<ReplayRecord>();
        // Playback-only poses at the time a late map snapshot was captured.
        // They let a moving door mesh follow the door's earlier recorded motion.
        [Newtonsoft.Json.JsonIgnore]
        public Dictionary<string, List<EntitySnapshot>> DoorPoseReferences { get; set; } =
            new Dictionary<string, List<EntitySnapshot>>(StringComparer.Ordinal);
        public List<string> Warnings { get; set; } = new List<string>();
        public double Duration { get; set; }
        public bool IsComplete { get; set; }
    }

    /// <summary>Limits apply before allocation/decompression, then again to decoded collections.</summary>
    public sealed class ReplayReadLimits
    {
        public long MaxFileBytes { get; set; } = 1024L * 1024 * 1024;
        public int MaxCompressedRecordBytes { get; set; } = 48 * 1024 * 1024;
        public int MaxUncompressedRecordBytes { get; set; } = 96 * 1024 * 1024;
        public long MaxTotalUncompressedBytes { get; set; } = 512L * 1024 * 1024;
        public int MaxRecords { get; set; } = 500000;
        public int MaxFrames { get; set; } = 432000;
        public int MaxEvents { get; set; } = 100000;
        public int MaxWorlds { get; set; } = 256;
        public int MaxEntitiesPerFrame { get; set; } = 4096;
        public int MaxTotalEntitySnapshots { get; set; } = 1000000;
        public int MaxBonesPerEntity { get; set; } = 512;
        public int MaxRenderersPerEntity { get; set; } = 512;
        public int MaxGeometryPerWorld { get; set; } = 50000;
        public int MaxParticleEmittersPerWorld { get; set; } = 256;
        public int MaxLocalFogsPerWorld { get; set; } = 128;
        public int MaxVerticesPerGeometry { get; set; } = 1000000;
        public int MaxTriangleIndicesPerGeometry { get; set; } = 6000000;
        public int MaxInstancesPerGeometry { get; set; } = 8192;
        public int MaxTexturesPerWorld { get; set; } = 256;
        public int MaxTextureDimension { get; set; } = 2048;
        public int MaxTextureBytesPerWorld { get; set; } = 32 * 1024 * 1024;
        public int MaxMaterialsPerWorld { get; set; } = 8192;
        public int MaxMaterialSlotsPerGeometry { get; set; } = 16;
        public int MaxDictionaryEntries { get; set; } = 1024;
        public int MaxStringLength { get; set; } = 32768;
        public double MaxDurationSeconds { get; set; } = 7 * 24 * 60 * 60;
    }
}
