using System.IO.Compression;
using System.Text;
using LCReplay.Core;
using Newtonsoft.Json;

var suite = new (string Name, Action Run)[]
{
    ("Round trip captures frames, world, events and metadata", RoundTrip),
    ("Every truncated byte boundary recovers only complete records", Truncation),
    ("Reject malformed schemas, sizes, collections, numbers and JSON", Validation),
    ("Bounded writer drains every accepted record", QueueCompletion),
    ("Invalid writer input exposes Error and leaves readable prefix", WriterFailure),
    ("Timeline seeks and applies discrete lifecycle boundaries", Timeline),
    ("Quaternion interpolation follows the shortest normalized arc", Quaternion),
    ("Moving door renderers interpolate local transforms and discrete visibility", RendererPoses),
    ("Textured multi-material skinned geometry survives recording and reading", AppearanceRoundTrip),
    ("Legacy recordings without appearance fields retain their defaults", LegacyAppearance),
    ("Texture headers, dimensions and cumulative byte budgets are bounded", TextureValidation),
    ("Appearance references, mesh arrays and skeletal influences reject invalid data", AppearanceValidation),
    ("Equal-time event and world records retain their original order", StableRecordOrder),
    ("Whole-recording timeline handles gaps, overlaps, boundary seeks and invalid parts", RecordingTimeline),
    ("Duration indexing recovers truncation and honors read limits and cancellation", DurationIndex),
    ("One physical replay file streams bounded playback windows with carried world state", SingleFileWindows),
    ("Static mesh references survive round trips and reject missing, cyclic or duplicated payloads", MeshReferences),
    ("Moving ship anchors interpolate and captured lights survive round trips", MovingEnvironment),
    ("Sky, fog, rooms and shader properties round trip with bounded validation", EnvironmentAndMaterials),
    ("Instanced vegetation, particle emitters and local fog round trip with bounded validation", ProceduralVisuals),
};
int failures = 0;
foreach (var test in suite)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"{suite.Length - failures}/{suite.Length} test groups passed.");
return failures == 0 ? 0 : 1;

static void RoundTrip()
{
    WithTemp(dir =>
    {
        string file = Path.Combine(dir, "roundtrip.lcr");
        var header = Header(); header.Metadata["unicode"] = "리썰 컴퍼니"; header.Warnings.Add("local perspective");
        using (var writer = new ReplayWriter(file, header))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = new WorldSnapshot
            {
                Scene = "Facility", Geometry = new() { new GeometrySnapshot
                {
                    Id = "mesh", Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 }
                } }
            } }), "world accepted");
            Check(writer.TryWrite(Frame(0, Entity("player", 1))), "frame accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = 0.5, Event = new ReplayEvent
            { Time = 0.5, Name = "door", Category = "interaction", Data = new() { ["open"] = "true" } } }), "event accepted");
            Check(writer.TryWrite(Frame(1, Entity("player", 3))), "second frame accepted");
            writer.Dispose(); Check(writer.Error == null, "writer error: " + writer.Error);
            Check(!writer.TryWrite(Frame(2)), "disposed writer refuses data");
        }
        var session = ReplayReader.Read(file);
        Check(session.IsComplete && session.Frames.Count == 2 && session.Events.Count == 1 && session.Worlds.Count == 1, "record counts");
        Check(session.Header.Metadata["unicode"] == "리썰 컴퍼니", "UTF-8 survives");
        Check(session.Warnings.Contains("local perspective"), "header warnings survive");
        Near(session.Duration, 1); Near(session.Worlds[0].World!.Geometry[0].Vertices[3], 1);
        Expect<IOException>(() => { using var duplicate = new ReplayWriter(file, Header()); });
    });
}

static void MovingEnvironment()
{
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "moving-environment.lcr");
        var world = new WorldSnapshot
        {
            CaptureSetId = "capture-1", Layer = "exterior",
            Geometry = new() { new GeometrySnapshot { Id = "ship", AnchorId = "ship-elevator",
                Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 } } },
            Lights = new() { new LightSnapshot { Id = "lamp", AnchorId = "ship-elevator", Type = "Point",
                Position = new Vec3(0, 2, 0), Intensity = 80, Range = 12, Shadows = true } }
        };
        var first = new ReplayFrame { Time = 0, Anchors = new() { new AnchorPose { Id = "ship-elevator", Position = new Vec3(0, 100, 0) } } };
        var last = new ReplayFrame { Time = 1, Anchors = new() { new AnchorPose { Id = "ship-elevator", Position = new Vec3(0, 0, 0) } } };
        using (var writer = new ReplayWriter(file, Header()))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = world }), "moving world accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = 0, Frame = first }), "first anchor accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = 1, Frame = last }), "last anchor accepted");
        }
        var session = ReplayReader.Read(file);
        Check(session.Worlds[0].World!.Geometry[0].AnchorId == "ship-elevator", "geometry retains ship parent");
        Check(session.Worlds[0].World!.Lights[0].Shadows, "light retains shadow flag");
        Near(ReplayTimeline.Sample(session, 0.5).Anchors[0].Position.Y, 50);
        var malformed = new ReplayFrame { Time = 0, Anchors = new() { new AnchorPose { Id = "ship-elevator" }, new AnchorPose { Id = "ship-elevator" } } };
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "frame", Frame = malformed });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
    });
}

static void EnvironmentAndMaterials()
{
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "environment.lcr");
        var world = new WorldSnapshot
        {
            CaptureSetId = "capture-1", Layer = "exterior",
            Rooms = new() { new RoomSnapshot { Id = "r1", Center = new Vec3(2, 3, 4), Size = new Vec3(8, 5, 6) } },
            Environment = new EnvironmentSnapshot { AmbientSkyColor = new[] { .1f, .2f, .3f, 1f }, Components = new()
            {
                new EnvironmentComponentSnapshot { Type = "VisualEnvironment", Parameters = new()
                { new EnvironmentParameterSnapshot { Name = "skyType", Kind = "int", Values = new[] { 3f } } } },
                new EnvironmentComponentSnapshot { Type = "Fog", Parameters = new()
                { new EnvironmentParameterSnapshot { Name = "enabled", Kind = "bool", Values = new[] { 1f } },
                  new EnvironmentParameterSnapshot { Name = "meanFreePath", Kind = "float", Values = new[] { 80f } } } }
            } },
            Materials = new() { new MaterialSnapshot { Id = "m1", ShaderName = "HDRP/Lit", RenderQueue = 2450,
                Keywords = new() { "_ALPHATEST_ON" }, Properties = new()
                { new MaterialPropertySnapshot { Name = "_Smoothness", Kind = "float", Values = new[] { .4f } } } } }
        };
        using (var writer = new ReplayWriter(file, Header()))
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = world }), "environment accepted");
        var result = ReplayReader.Read(file).Worlds.Single().World!;
        Check(result.CaptureSetId == "capture-1" && result.Layer == "exterior" &&
            result.Rooms.Single().Center.Z == 4 && result.Environment!.Components.Count == 2,
            "room/environment preserved");
        Check(result.Materials.Single().Properties.Single().Values[0] == .4f && result.Materials.Single().RenderQueue == 2450,
            "shader parameters preserved");
        world.Rooms.Add(new RoomSnapshot { Id = "r1", Size = Vec3.One });
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        world.Rooms.RemoveAt(1);
        world.Materials[0].Properties[0].Values[0] = float.NaN;
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
    });
}

static void Truncation()
{
    WithTemp(dir =>
    {
        string file = Path.Combine(dir, "source.lcr"), partial = Path.Combine(dir, "partial.lcr");
        using (var writer = new ReplayWriter(file, Header())) { writer.TryWrite(Frame(0, Entity("p", 0))); writer.TryWrite(Frame(1, Entity("p", 1))); }
        var bytes = File.ReadAllBytes(file);
        var boundaries = new List<int> { 8 };
        for (int offset = 8; offset < bytes.Length;) { offset += 8 + BitConverter.ToInt32(bytes, offset); boundaries.Add(offset); }
        for (int length = 0; length <= bytes.Length; length++)
        {
            File.WriteAllBytes(partial, bytes.Take(length).ToArray());
            if (length < boundaries[1]) { Expect<InvalidDataException>(() => ReplayReader.Read(partial)); continue; }
            var session = ReplayReader.Read(partial);
            int completeFrames = (length >= boundaries[2] ? 1 : 0) + (length >= boundaries[3] ? 1 : 0);
            Check(session.Frames.Count == completeFrames, "prefix frame count at byte " + length);
            Check(session.IsComplete == (length == bytes.Length), "end marker at byte " + length);
            if (!session.IsComplete) Check(session.Warnings.Count > 0, "recovery warning");
        }
    });
}

static void Validation()
{
    WithTemp(dir =>
    {
        string file = Path.Combine(dir, "bad.lcr");
        var header = new ReplayRecord { Kind = "header", Header = Header() };
        Raw(file, header, Frame(1, Entity("p", 0)));
        Expect<InvalidDataException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxFileBytes = 8 }));
        Expect<InvalidDataException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxUncompressedRecordBytes = 16 }));
        Expect<InvalidDataException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxTotalUncompressedBytes = 16 }));
        Expect<ArgumentOutOfRangeException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxFrames = 0 }));
        header.Header!.SchemaVersion = 999; Raw(file, header); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        header.Header.SchemaVersion = 1;
        Raw(file, Frame(0)); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, header); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, Frame(1, Entity("p", 0), Entity("p", 1))); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, Frame(1, Entity("p", float.NaN))); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        var renderedEntity = Entity("door", 0);
        renderedEntity.Renderers.Add(new RenderPose { Id = "panel" });
        renderedEntity.Renderers.Add(new RenderPose { Id = "panel" });
        Raw(file, header, Frame(0, renderedEntity)); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        renderedEntity.Renderers[1].Id = "panel-other";
        Raw(file, header, Frame(0, renderedEntity));
        Expect<InvalidDataException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxRenderersPerEntity = 1 }));
        renderedEntity.Renderers[1].Position = new Vec3(float.PositiveInfinity, 0, 0);
        Raw(file, header, Frame(0, renderedEntity)); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, Frame(double.PositiveInfinity)); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, Frame(2), Frame(1)); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        var mismatch = Frame(1); mismatch.Frame!.Time = 2; Raw(file, header, mismatch); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, new ReplayRecord { Kind = "mystery" }); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, new ReplayRecord { Kind = "end" }, Frame(1)); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header, Frame(0), Frame(1)); Expect<InvalidDataException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxFrames = 1 }));
        Raw(file, header, Frame(0, Entity("p", 0)), Frame(1, Entity("p", 1)));
        Expect<InvalidDataException>(() => ReplayReader.Read(file, new ReplayReadLimits { MaxTotalEntitySnapshots = 1 }));
        var mesh = new ReplayRecord { Kind = "world", World = new WorldSnapshot { Geometry = new() { new GeometrySnapshot
        { Id = "mesh", Vertices = new float[] { 0, 0, 0 }, Triangles = new[] { 0, 1, 2 } } } } };
        Raw(file, header, mesh); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header); var data = File.ReadAllBytes(file);
        BitConverter.GetBytes(int.MaxValue).CopyTo(data, 8); File.WriteAllBytes(file, data); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header); data = File.ReadAllBytes(file); int expanded = BitConverter.ToInt32(data, 12);
        BitConverter.GetBytes(expanded - 1).CopyTo(data, 12); File.WriteAllBytes(file, data); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, header); data = File.ReadAllBytes(file); BitConverter.GetBytes(expanded + 1).CopyTo(data, 12);
        File.WriteAllBytes(file, data); Expect<InvalidDataException>(() => ReplayReader.Read(file));
        using (var output = File.Create(file))
        {
            output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            RawPayload(output, "{\"Kind\":\"header\",\"Header\":{\"$type\":\"System.IO.FileInfo, System.Private.CoreLib\",\"SessionId\":\"safe\"}}");
        }
        Check(ReplayReader.Read(file).Header.SessionId == "safe", "type metadata is inert");
        using (var output = File.Create(file)) { output.Write(Encoding.ASCII.GetBytes("LCREPL01")); RawPayload(output, "{}{}"); }
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
    });
}

static void QueueCompletion()
{
    WithTemp(dir =>
    {
        string file = Path.Combine(dir, "queue.lcr"); int accepted = 0, refused = 0;
        var writer = new ReplayWriter(file, Header(), 1);
        for (int i = 0; i < 2000; i++) { if (writer.TryWrite(Frame(i))) accepted++; else refused++; }
        writer.Dispose(); writer.Dispose();
        Check(writer.Error == null, "queue writer error"); Check(accepted > 0 && refused > 0, "bounded queue applies backpressure");
        var session = ReplayReader.Read(file);
        Check(session.Frames.Count == accepted && session.IsComplete, "all accepted records drain exactly once");
    });
}

static void WriterFailure()
{
    WithTemp(dir =>
    {
        string file = Path.Combine(dir, "failure.lcr");
        var writer = new ReplayWriter(file, Header());
        Check(writer.TryWrite(Frame(0, Entity("p", 1))), "valid record accepted");
        Check(writer.TryWrite(Frame(1, Entity("p", float.PositiveInfinity))), "invalid input reaches background validation");
        writer.Dispose();
        Check(writer.Error is InvalidDataException, "background failure exposed");
        var session = ReplayReader.Read(file);
        Check(!session.IsComplete && session.Frames.Count == 1, "valid prefix survives validation failure");
        Check(!writer.TryWrite(Frame(2)), "failed writer rejects subsequent data");
    });
}

static void Timeline()
{
    var a = Entity("a", 0); a.State["health"] = "100";
    a.Bones.Add(new BonePose { Path = "head", Position = new Vec3(0, 1, 0) });
    var b = Entity("a", 10); b.State["health"] = "20";
    b.Bones.Add(new BonePose { Path = "head", Position = new Vec3(0, 3, 0) });
    var session = new ReplaySession { Duration = 4, Frames = new()
    {
        Frame(0, a, Entity("despawn", 5)).Frame!, Frame(1, b, Entity("spawn", 8)).Frame!,
        Frame(2).Frame!, Frame(3, Entity("a", 50)).Frame!, Frame(4, Entity("a", 60)).Frame!
    } };
    var half = ReplayTimeline.Sample(session, 0.5);
    Near(half.Entities[0].Position.X, 5); Near(half.Entities[0].Bones[0].Position.Y, 2);
    Check(half.Entities[0].State["health"] == "100", "discrete state preserved from left");
    Check(half.Entities.Any(e => e.Id == "despawn") && half.Entities.All(e => e.Id != "spawn"), "lifecycle before boundary");
    var exact = ReplayTimeline.Sample(session, 1);
    Check(exact.Entities.Any(e => e.Id == "spawn") && exact.Entities.All(e => e.Id != "despawn"), "lifecycle at boundary");
    Check(exact.Entities[0].State["health"] == "20", "state changes exactly at boundary");
    Check(ReplayTimeline.Sample(session, 2.5).Entities.Count == 0, "no interpolation across respawn gap");
    Near(ReplayTimeline.Sample(session, 3).Entities[0].Position.X, 50);
    Near(ReplayTimeline.Sample(session, 3.5).Entities[0].Position.X, 55);
    Near(ReplayTimeline.Sample(session, -1).Entities[0].Position.X, 0);
    Near(ReplayTimeline.Sample(session, 999).Entities[0].Position.X, 60);
    half.Entities[0].State["health"] = "0"; half.Entities[0].Bones[0].Path = "changed";
    Check(a.State["health"] == "100" && a.Bones[0].Path == "head", "sample cannot mutate recording");
    Check(ReplayTimeline.Sample(new ReplaySession(), 2).Entities.Count == 0, "empty timeline");
    Expect<ArgumentOutOfRangeException>(() => ReplayTimeline.Sample(session, double.NaN));
    var duplicates = new ReplaySession { Frames = new() { Frame(0, Entity("x", 0)).Frame!, Frame(0, Entity("x", 2)).Frame!, Frame(1, Entity("x", 4)).Frame! } };
    Near(ReplayTimeline.Sample(duplicates, 0).Entities[0].Position.X, 2);
    var inactive = Entity("x", 100); inactive.Active = false;
    var activation = new ReplaySession { Frames = new() { Frame(0, inactive).Frame!, Frame(1, Entity("x", 0)).Frame! } };
    Check(!ReplayTimeline.Sample(activation, 0.5).Entities[0].Active, "activation waits for boundary");
    Near(ReplayTimeline.Sample(activation, 0.5).Entities[0].Position.X, 100);
}

static void Quaternion()
{
    var a = Entity("p", 0); var b = Entity("p", 0); b.Rotation = new Quat(0, 0, 0, -1);
    var session = new ReplaySession { Frames = new() { Frame(0, a).Frame!, Frame(1, b).Frame! } };
    var q = ReplayTimeline.Sample(session, 0.5).Entities[0].Rotation;
    Near(q.W, 1); Near(q.Y, 0);
    b.Rotation = new Quat(0, 1, 0, 0);
    q = ReplayTimeline.Sample(session, 0.5).Entities[0].Rotation;
    Near(Math.Abs(q.Y), Math.Sqrt(0.5)); Near(q.W, Math.Sqrt(0.5));
    a.Rotation = new Quat(0, 0, 0, 2); b.Rotation = new Quat(0, -0.2f, 0, -1.99f);
    q = ReplayTimeline.Sample(session, 0.5).Entities[0].Rotation;
    Near(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W, 1); Check(q.Y > 0 && q.W > 0, "short arc sign");
}

static void RendererPoses()
{
    WithTemp(dir =>
    {
        var doorClosed = Entity("door", 0);
        doorClosed.Renderers.Add(new RenderPose { Id = "door-panel", Position = new Vec3(1, 0, 0) });
        var doorOpen = Entity("door", 0);
        doorOpen.Renderers.Add(new RenderPose
        {
            Id = "door-panel", Position = new Vec3(3, 0, 0), Scale = new Vec3(2, 2, 2),
            Rotation = new Quat(0, (float)Math.Sqrt(0.5), 0, (float)Math.Sqrt(0.5))
        });
        var hidden = Entity("door", 0);
        hidden.Renderers.Add(new RenderPose { Id = "door-panel", Active = false, Position = new Vec3(999, 0, 0) });
        string path = Path.Combine(dir, "door.lcr");
        using (var writer = new ReplayWriter(path, Header()))
        {
            Check(writer.TryWrite(Frame(0, doorClosed)), "closed door accepted");
            Check(writer.TryWrite(Frame(1, doorOpen)), "open door accepted");
            Check(writer.TryWrite(Frame(2, hidden)), "hidden door accepted");
            writer.Dispose(); Check(writer.Error == null, "renderer round trip writes successfully");
        }
        var session = ReplayReader.Read(path);
        var halfway = ReplayTimeline.Sample(session, 0.5).Entities[0].Renderers[0];
        Near(halfway.Position.X, 2); Near(halfway.Scale.X, 1.5);
        Near(halfway.Rotation.Y, Math.Sin(Math.PI / 8)); Near(halfway.Rotation.W, Math.Cos(Math.PI / 8));
        Check(halfway.Id == "door-panel" && halfway.Active, "renderer identity and active state survive round trip");
        var beforeHide = ReplayTimeline.Sample(session, 1.5).Entities[0].Renderers[0];
        Check(beforeHide.Active, "renderer visibility holds before exact boundary");
        Near(beforeHide.Position.X, 3);
        var atHide = ReplayTimeline.Sample(session, 2).Entities[0].Renderers[0];
        Check(!atHide.Active, "renderer visibility changes at exact boundary");
        Near(atHide.Position.X, 999);
        halfway.Id = "mutated";
        Check(session.Frames[0].Entities[0].Renderers[0].Id == "door-panel", "sample renderer cannot mutate stored pose");
        var omitted = Entity("door", 0);
        session.Frames.Add(Frame(3, omitted).Frame!);
        Check(ReplayTimeline.Sample(session, 2.5).Entities[0].Renderers.Count == 1, "renderer omission applies at boundary");
        Check(ReplayTimeline.Sample(session, 3).Entities[0].Renderers.Count == 0, "renderer omitted at next frame");
    });
}

static void StableRecordOrder()
{
    WithTemp(dir =>
    {
        string path = Path.Combine(dir, "stable.lcr");
        using (var writer = new ReplayWriter(path, Header()))
        {
            for (int i = 0; i < 40; i++)
            {
                double time = i % 3;
                Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = time, Event = new ReplayEvent { Time = time, Name = i.ToString() } }), "event queued");
                Check(writer.TryWrite(new ReplayRecord { Kind = "world", Time = time, World = new WorldSnapshot { Scene = i.ToString() } }), "world queued");
            }
            writer.Dispose(); Check(writer.Error == null, "out-of-order events/worlds accepted");
        }
        var session = ReplayReader.Read(path);
        var expected = Enumerable.Range(0, 40).OrderBy(i => i % 3).Select(i => i.ToString()).ToArray();
        Check(session.Events.Select(item => item.Name).SequenceEqual(expected), "equal-time events preserve written order");
        Check(session.Worlds.Select(item => item.World!.Scene).SequenceEqual(expected), "equal-time worlds preserve written order");
    });
}

static void AppearanceRoundTrip()
{
    WithTemp(dir =>
    {
        var source = AppearanceWorld();
        var player = Entity("crew", 3);
        player.Bones.Add(new BonePose { Path = "hip[0]", Position = new Vec3(0, 1, 0) });
        player.Bones.Add(new BonePose { Path = "hip[0]/head[0]", Position = new Vec3(0, 1.5f, 0) });
        player.Renderers.Add(new RenderPose { Id = "suit", Position = new Vec3(.2f, 0, 0) });
        var path = Path.Combine(dir, "appearance.lcr");
        using (var writer = new ReplayWriter(path, Header()))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = source }), "appearance world accepted");
            Check(writer.TryWrite(Frame(0, player)), "matching skeleton frame accepted");
            writer.Dispose();
            Check(writer.Error == null, "appearance writer failed: " + writer.Error);
        }
        var replay = ReplayReader.Read(path);
        Check(replay.IsComplete && replay.Worlds.Count == 1, "appearance recording completes");
        var world = replay.Worlds[0].World!;
        var texture = world.Textures.Single();
        Check(texture.Id == "suit-albedo" && texture.Width == 1 && texture.Height == 1, "texture identity/dimensions");
        Check(texture.Png.SequenceEqual(source.Textures[0].Png), "PNG bytes survive base64 JSON/gzip round trip exactly");
        var material = world.Materials.Single(m => m.Id == "orange");
        Check(material.TextureId == texture.Id && material.Name == "Crew suit" && material.ShaderName == "HDRP/Lit" && material.AlphaClip, "material identity and cutout mode");
        Check(material.Color.SequenceEqual(new[] { 1f, .35f, .1f, .8f }), "material RGBA tint");
        Check(material.TextureScaleOffset.SequenceEqual(new[] { 2f, 3f, .25f, -.5f }), "texture tiling and offset");
        Near(material.Cutoff, .3);
        var mesh = world.Geometry.Single();
        Check(mesh.MeshName == "Recorded suit mesh" && mesh.EntityId == "crew" && mesh.Id == "suit", "geometry-to-actor link");
        Check(mesh.Vertices.SequenceEqual(source.Geometry[0].Vertices) && mesh.Normals.SequenceEqual(source.Geometry[0].Normals)
            && mesh.Uvs.SequenceEqual(source.Geometry[0].Uvs), "vertex attributes retain their vertex correspondence");
        Check(mesh.SubmeshTriangles.Count == 2 && mesh.SubmeshTriangles[0].SequenceEqual(new[] { 0, 1, 2 })
            && mesh.SubmeshTriangles[1].SequenceEqual(new[] { 2, 1, 3 }), "two submesh index buffers remain distinct");
        Check(mesh.MaterialIds.SequenceEqual(new[] { "orange", "visor" }), "submesh material order survives");
        Check(mesh.BonePaths.SequenceEqual(player.Bones.Select(b => b.Path)), "mesh palette resolves recorded bone paths");
        Check(mesh.BindPoses.SequenceEqual(source.Geometry[0].BindPoses) && mesh.BoneIndices.SequenceEqual(source.Geometry[0].BoneIndices)
            && mesh.BoneWeights.SequenceEqual(source.Geometry[0].BoneWeights), "bind matrices and four influences per vertex survive");
        Check(replay.Frames[0].Entities[0].Renderers[0].Id == mesh.Id, "renderer pose maps to recorded geometry");
    });
}

static void LegacyAppearance()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "legacy-appearance.lcr");
        using (var output = File.Create(path))
        {
            output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            RawPayload(output, JsonConvert.SerializeObject(new ReplayRecord { Kind = "header", Header = Header() }));
            // This deliberately contains none of the newer optional appearance properties.
            RawPayload(output, "{\"Kind\":\"world\",\"World\":{\"Scene\":\"Old facility\",\"Geometry\":[{\"Id\":\"old\",\"Vertices\":[0,0,0,1,0,0,0,1,0],\"Triangles\":[0,1,2]}]}}");
            RawPayload(output, "{\"Kind\":\"frame\",\"Frame\":{\"Entities\":[{\"Id\":\"crew\"}]}}");
            RawPayload(output, "{\"Kind\":\"end\"}");
        }
        var replay = ReplayReader.Read(path);
        var world = replay.Worlds.Single().World!;
        var mesh = world.Geometry.Single();
        Check(replay.IsComplete && world.Textures.Count == 0 && world.Materials.Count == 0, "old worlds default to no appearance assets");
        Check(mesh.Uvs.Length == 0 && mesh.Normals.Length == 0 && mesh.SubmeshTriangles.Count == 0 && mesh.MaterialIds.Count == 0,
            "old mesh attributes stay optional");
        Check(mesh.BonePaths.Count == 0 && mesh.BindPoses.Length == 0 && mesh.BoneIndices.Length == 0 && mesh.BoneWeights.Length == 0,
            "old unskinned geometry remains valid");
        Check(replay.Frames[0].Entities[0].Renderers.Count == 0, "old entities without renderer poses remain valid");
        using (var output = File.Create(path))
        {
            output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            RawPayload(output, JsonConvert.SerializeObject(new ReplayRecord { Kind = "header", Header = Header() }));
            // Explicit null optional properties follow the reader's existing omission policy.
            RawPayload(output, "{\"Kind\":\"world\",\"World\":{\"Textures\":null,\"Materials\":null,\"Geometry\":[{\"Id\":\"old\",\"MeshName\":null,\"Uvs\":null,\"Normals\":null,\"SubmeshTriangles\":null,\"MaterialIds\":null,\"BonePaths\":null,\"BindPoses\":null,\"BoneIndices\":null,\"BoneWeights\":null}]}}");
        }
        var normalized = ReplayReader.Read(path).Worlds.Single().World!;
        var normalizedMesh = normalized.Geometry.Single();
        Check(normalized.Textures.Count == 0 && normalized.Materials.Count == 0 && normalizedMesh.MeshName == ""
            && normalizedMesh.SubmeshTriangles.Count == 0 && normalizedMesh.BoneIndices.Length == 0,
            "explicit null optional appearance fields normalize to initialized defaults");
    });
}

static void TextureValidation()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "texture-validation.lcr");
        void Reject(string name, Action<WorldSnapshot> change, ReplayReadLimits? limits = null)
            => RejectAppearance(path, name, change, limits);
        Reject("empty texture id", world => world.Textures[0].Id = "");
        Reject("duplicate texture id", world => world.Textures.Add(world.Textures[0]));
        Reject("null texture entry", world => world.Textures.Add(null!));
        Reject("null texture collection", world => world.Textures = null!);
        Reject("null PNG", world => world.Textures[0].Png = null!);
        Reject("truncated IHDR", world => world.Textures[0].Png = world.Textures[0].Png.Take(32).ToArray());
        Reject("invalid PNG signature", world => world.Textures[0].Png[0] = 0);
        Reject("invalid first PNG chunk", world => world.Textures[0].Png[12] = (byte)'J');
        Reject("invalid IHDR length", world => world.Textures[0].Png[11] = 12);
        Reject("declared width differs from IHDR", world => world.Textures[0].Width = 2);
        Reject("declared height differs from IHDR", world => world.Textures[0].Height = 2);
        Reject("zero dimension", world => world.Textures[0].Width = 0);
        Reject("negative dimension", world => world.Textures[0].Height = -1);
        Reject("oversized decoded dimensions", world =>
        {
            world.Textures[0].Width = 2049;
            WritePngUInt32(world.Textures[0].Png, 16, 2049);
        });
        Reject("PNG width allocation bomb", world => WritePngUInt32(world.Textures[0].Png, 16, uint.MaxValue));
        Reject("PNG height allocation bomb", world => WritePngUInt32(world.Textures[0].Png, 20, uint.MaxValue));
        Reject("maximum signed dimensions remain rejected", world =>
        {
            world.Textures[0].Width = int.MaxValue;
            world.Textures[0].Height = int.MaxValue;
            WritePngUInt32(world.Textures[0].Png, 16, int.MaxValue);
            WritePngUInt32(world.Textures[0].Png, 20, int.MaxValue);
        });
        Reject("per-world texture count", world => world.Textures.Add(Texture("other")), new ReplayReadLimits { MaxTexturesPerWorld = 1 });
        var pngBytes = Texture("fixture").Png.Length;
        Reject("one PNG over byte budget", _ => { }, new ReplayReadLimits { MaxTextureBytesPerWorld = pngBytes - 1 });
        Reject("cumulative PNG byte budget", world => world.Textures.Add(Texture("other")), new ReplayReadLimits { MaxTextureBytesPerWorld = pngBytes * 2 - 1 });
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = AppearanceWorld() });
        Check(ReplayReader.Read(path, new ReplayReadLimits { MaxTextureBytesPerWorld = pngBytes, MaxTextureDimension = 1 }).Worlds.Count == 1,
            "exact encoded byte and dimension boundaries are accepted");
        foreach (var limits in new[]
        {
            new ReplayReadLimits { MaxTexturesPerWorld = 0 }, new ReplayReadLimits { MaxTextureDimension = 0 },
            new ReplayReadLimits { MaxTextureBytesPerWorld = 0 }, new ReplayReadLimits { MaxMaterialsPerWorld = 0 },
            new ReplayReadLimits { MaxMaterialSlotsPerGeometry = 0 }
        }) Expect<ArgumentOutOfRangeException>(() => ReplayReader.Read(path, limits));
    });
}

static void AppearanceValidation()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "appearance-validation.lcr");
        void Reject(string name, Action<WorldSnapshot> change, ReplayReadLimits? limits = null)
            => RejectAppearance(path, name, change, limits);
        Reject("duplicate material id", world => world.Materials.Add(world.Materials[0]));
        Reject("missing material texture", world => world.Materials[0].TextureId = "missing");
        Reject("missing geometry material", world => world.Geometry[0].MaterialIds[0] = "missing");
        Reject("null material", world => world.Materials[0] = null!);
        Reject("null material list", world => world.Materials = null!);
        Reject("material count", _ => { }, new ReplayReadLimits { MaxMaterialsPerWorld = 1 });
        Reject("RGB without alpha", world => world.Materials[0].Color = new float[3]);
        Reject("non-finite tint", world => world.Materials[0].Color[2] = float.NaN);
        Reject("invalid tiling array", world => world.Materials[0].TextureScaleOffset = new float[3]);
        Reject("infinite UV offset", world => world.Materials[0].TextureScaleOffset[3] = float.PositiveInfinity);
        Reject("negative cutoff", world => world.Materials[0].Cutoff = -.1f);
        Reject("cutoff above one", world => world.Materials[0].Cutoff = 1.1f);
        Reject("non-finite cutoff", world => world.Materials[0].Cutoff = float.NaN);
        Reject("UV vertex count mismatch", world => world.Geometry[0].Uvs = new float[6]);
        Reject("non-finite UV", world => world.Geometry[0].Uvs[2] = float.NegativeInfinity);
        Reject("normal vertex count mismatch", world => world.Geometry[0].Normals = new float[9]);
        Reject("non-finite normal", world => world.Geometry[0].Normals[0] = float.NaN);
        Reject("submesh references absent vertex", world => world.Geometry[0].SubmeshTriangles[0][0] = 4);
        Reject("negative submesh index", world => world.Geometry[0].SubmeshTriangles[0][0] = -1);
        Reject("incomplete triangle", world => world.Geometry[0].SubmeshTriangles[0] = new[] { 0, 1 });
        Reject("null submesh", world => world.Geometry[0].SubmeshTriangles[0] = null!);
        Reject("submesh count budget", _ => { }, new ReplayReadLimits { MaxMaterialSlotsPerGeometry = 1 });
        Reject("cumulative submesh index budget", _ => { }, new ReplayReadLimits { MaxTriangleIndicesPerGeometry = 3 });
        Reject("material slot budget without extra submeshes", world => world.Geometry[0].SubmeshTriangles.Clear(), new ReplayReadLimits { MaxMaterialSlotsPerGeometry = 1 });
        Reject("null bone palette", world => world.Geometry[0].BonePaths = null!);
        Reject("skeleton palette budget", _ => { }, new ReplayReadLimits { MaxBonesPerEntity = 1 });
        Reject("excessive bone path", world => world.Geometry[0].BonePaths[0] = new string('b', 4097));
        Reject("excessive bone hierarchy", world => world.Geometry[0].BonePaths[0] = string.Join("/", Enumerable.Repeat("bone", 129)));
        Reject("bind pose matrix count mismatch", world => world.Geometry[0].BindPoses = new float[16]);
        Reject("non-finite bind matrix", world => world.Geometry[0].BindPoses[4] = float.NaN);
        Reject("bone influence index count mismatch", world => world.Geometry[0].BoneIndices = new int[12]);
        Reject("bone influence weight count mismatch", world => world.Geometry[0].BoneWeights = new float[12]);
        Reject("null bone indices", world => world.Geometry[0].BoneIndices = null!);
        Reject("null bone weights", world => world.Geometry[0].BoneWeights = null!);
        Reject("bone index outside palette", world => world.Geometry[0].BoneIndices[0] = 2);
        Reject("negative bone index", world => world.Geometry[0].BoneIndices[0] = -1);
        Reject("negative bone weight", world => world.Geometry[0].BoneWeights[0] = -.1f);
        Reject("bone weight above one", world => world.Geometry[0].BoneWeights[0] = 1.1f);
        Reject("non-finite bone weight", world => world.Geometry[0].BoneWeights[0] = float.PositiveInfinity);
        Reject("influences without a skeleton", world => { world.Geometry[0].BonePaths.Clear(); world.Geometry[0].BindPoses = Array.Empty<float>(); });
    });
}

static void ProceduralVisuals()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "procedural.lcr");
        WorldSnapshot World() => new()
        {
            Scene = "Outdoor moon",
            Materials = new() { new MaterialSnapshot { Id = "grass", Name = "Grass" } },
            Geometry = new() { new GeometrySnapshot
            {
                Id = "blades", Name = "Instanced grass", Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                Triangles = new[] { 0, 1, 2 }, MaterialIds = new() { "grass" },
                Instances = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 3, 0, 4, 1 }
            }, new GeometrySnapshot { Id = "tree-near", LodGroupId = "tree1", LodLevel = 0,
                LodCenter = new Vec3(4, 0, 5), LodSwitchDistance = 25 },
                new GeometrySnapshot { Id = "tree-far", LodGroupId = "tree1", LodLevel = 1,
                LodCenter = new Vec3(4, 0, 5), LodSwitchDistance = 25 } },
            ParticleEmitters = new() { new ParticleEmitterSnapshot
            { Id = "p1", Name = "steam", MaterialId = "grass", Position = new Vec3(1, 2, 3),
                Color = new[] { .5f, .6f, .7f, 1f }, Rate = 12, Lifetime = 2, Speed = .4f, Size = .2f, Radius = .3f } },
            LocalFogs = new() { new LocalFogSnapshot { Id = "f1", Name = "fakefog", Position = new Vec3(1, 2, 3),
                Size = new Vec3(10, 5, 10), MeanFreePath = 4, IsInterior = true,
                MaskWidth = 2, MaskHeight = 2, MaskDepth = 2, MaskRgba = Enumerable.Repeat((byte)127, 32).ToArray() } }
        };
        void Write(WorldSnapshot world) => Raw(path, new ReplayRecord { Kind = "header", Header = Header() },
            new ReplayRecord { Kind = "world", World = world });
        void Reject(string name, Action<WorldSnapshot> mutate, ReplayReadLimits? limits = null)
        {
            var world = World(); mutate(world); Write(world);
            try { ReplayReader.Read(path, limits); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid procedural data was accepted: " + name);
        }
        Write(World());
        var decoded = ReplayReader.Read(path).Worlds.Single().World!;
        Check(decoded.Geometry.Single(item => item.Id == "blades").Instances.Length == 16 &&
            decoded.Geometry.Single(item => item.Id == "tree-far").LodLevel == 1 &&
            decoded.ParticleEmitters.Single().Rate == 12 &&
            decoded.LocalFogs.Single().IsInterior && decoded.LocalFogs.Single().MeanFreePath == 4 &&
            decoded.LocalFogs.Single().MaskRgba.Length == 32,
            "instanced grass, natural LOD pair, emitter and local fog survive the file round trip");
        Reject("truncated matrix", world => world.Geometry[0].Instances = new float[15]);
        Reject("non-affine matrix", world => world.Geometry[0].Instances[15] = 0);
        Reject("non-finite matrix", world => world.Geometry[0].Instances[12] = float.NaN);
        Reject("matrix count", world => world.Geometry[0].Instances = world.Geometry[0].Instances.Concat(world.Geometry[0].Instances).ToArray(),
            new ReplayReadLimits { MaxInstancesPerGeometry = 1 });
        Reject("missing emitter material", world => world.ParticleEmitters[0].MaterialId = "missing");
        Reject("invalid emitter rate", world => world.ParticleEmitters[0].Rate = -1);
        Reject("invalid LOD level", world => world.Geometry[1].LodLevel = 3);
        Reject("invalid LOD distance", world => world.Geometry[1].LodSwitchDistance = float.PositiveInfinity);
        Reject("emitter count", world => world.ParticleEmitters.Add(new ParticleEmitterSnapshot { Id = "p2" }),
            new ReplayReadLimits { MaxParticleEmittersPerWorld = 1 });
        Reject("invalid local fog density", world => world.LocalFogs[0].MeanFreePath = -1);
        Reject("invalid local fog fade", world => world.LocalFogs[0].PositiveFade = new Vec3(2, 0, 0));
        Reject("invalid local fog mask", world => world.LocalFogs[0].MaskRgba = new byte[4]);
        Reject("local fog count", world => world.LocalFogs.Add(new LocalFogSnapshot { Id = "f2" }),
            new ReplayReadLimits { MaxLocalFogsPerWorld = 1 });
    });
}

static void RejectAppearance(string path, string name, Action<WorldSnapshot> change, ReplayReadLimits? limits)
{
    var world = AppearanceWorld();
    change(world);
    Raw(path, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
    try { ReplayReader.Read(path, limits); }
    catch (InvalidDataException) { return; }
    throw new Exception("Invalid appearance data was accepted: " + name);
}

static TextureSnapshot Texture(string id) => new()
{
    Id = id, Width = 1, Height = 1,
    // Valid 1x1 grayscale+alpha PNG; IHDR/IDAT/IEND CRCs and zlib payload verified independently.
    Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
};
static void WritePngUInt32(byte[] png, int offset, uint value)
{
    png[offset] = (byte)(value >> 24); png[offset + 1] = (byte)(value >> 16);
    png[offset + 2] = (byte)(value >> 8); png[offset + 3] = (byte)value;
}
static WorldSnapshot AppearanceWorld() => new()
{
    Scene = "Recorded ship", Textures = new() { Texture("suit-albedo") },
    Materials = new()
    {
        new MaterialSnapshot { Id = "orange", Name = "Crew suit", ShaderName = "HDRP/Lit", TextureId = "suit-albedo",
            Color = new[] { 1f, .35f, .1f, .8f }, TextureScaleOffset = new[] { 2f, 3f, .25f, -.5f }, AlphaClip = true, Cutoff = .3f },
        new MaterialSnapshot { Id = "visor", Name = "Visor", Color = new[] { .1f, .1f, .1f, 1f } }
    },
    Geometry = new() { new GeometrySnapshot
    {
        Id = "suit", Name = "Crew visual", EntityId = "crew", MeshName = "Recorded suit mesh",
        Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 },
        Uvs = new float[] { 0, 0, 1, 0, 0, 1, 1, 1 },
        Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
        SubmeshTriangles = new() { new[] { 0, 1, 2 }, new[] { 2, 1, 3 } }, MaterialIds = new() { "orange", "visor" },
        BonePaths = new() { "hip[0]", "hip[0]/head[0]" },
        BindPoses = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, -1, 0, 0, 1, 0, 0, 0, 0, 1 },
        BoneIndices = new[] { 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        BoneWeights = new[] { .75f, .25f, 0, 0, .5f, .5f, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }
    } }
};

static void RecordingTimeline()
{
    var origin = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
    ReplayRecordingPart Part(string path, double duration, double seconds) => new()
        { FilePath = path, Duration = duration, StartedUtc = origin.AddSeconds(seconds) };
    var first = Part("part-a.lcr", 10, 0);
    var recording = new ReplayRecordingTimeline(new[] { first, Part("part-b.lcr", 5, 15), Part("part-c.lcr", 4, 17) });
    Near(recording.Duration, 24);
    Near(recording.Parts[0].Offset, 0); Near(recording.Parts[1].Offset, 15); Near(recording.Parts[2].Offset, 20);
    Check(recording.Locate(-2) == 0 && recording.Locate(0) == 0 && recording.Locate(14.999) == 0, "gap retains first part");
    Near(recording.LocalTime(0, -2), 0); Near(recording.LocalTime(0, 14.999), 10);
    Check(recording.Locate(15) == 1 && recording.Locate(19.999) == 1 && recording.Locate(20) == 2, "exact boundary switches part");
    Near(recording.LocalTime(1, 15), 0); Near(recording.LocalTime(2, 22), 2);
    Check(recording.Locate(24) == 2 && recording.Locate(30) == 2, "end remains final part");
    Near(recording.LocalTime(2, 30), 4);
    Check(recording.Locate(3) == 0, "backward seek resolves independently of prior seek");
    Near(recording.LocalTime(0, 3), 3);
    first.Duration = 100;
    Near(recording.Parts[0].Duration, 10); Near(recording.Duration, 24);
    var zero = new ReplayRecordingTimeline(new[] { Part("zero-a.lcr", 0, 0), Part("zero-b.lcr", 0, 0), Part("zero-c.lcr", 3, 0) });
    Near(zero.Duration, 3); Check(zero.Locate(0) == 2, "zero-length parts do not trap the timeline");
    var noDates = new ReplayRecordingTimeline(new[] { new ReplayRecordingPart { FilePath = "undated-a.lcr", Duration = 4 }, new ReplayRecordingPart { FilePath = "undated-b.lcr", Duration = 6 } });
    Near(noDates.Duration, 10); Near(noDates.Parts[1].Offset, 4);
    var oldDate = new ReplayRecordingTimeline(new[] { Part("date-a.lcr", 4, 0), Part("date-b.lcr", 2, -10) });
    Near(oldDate.Parts[1].Offset, 4); Near(oldDate.Duration, 6);
    Expect<ArgumentNullException>(() => new ReplayRecordingTimeline(null!));
    Expect<ArgumentException>(() => new ReplayRecordingTimeline(Array.Empty<ReplayRecordingPart>()));
    Expect<ArgumentException>(() => new ReplayRecordingTimeline(new ReplayRecordingPart[] { null! }));
    Expect<ArgumentException>(() => new ReplayRecordingTimeline(new[] { Part("dup.lcr", 1, 0), Part("DUP.LCR", 1, 1) }));
    foreach (var duration in new[] { -1d, double.NaN, double.PositiveInfinity, 604801d })
        Expect<ArgumentException>(() => new ReplayRecordingTimeline(new[] { Part("invalid.lcr", duration, 0) }));
    foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        Expect<ArgumentOutOfRangeException>(() => recording.Locate(time));
}

static void DurationIndex()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "duration.lcr");
        var header = new ReplayRecord { Kind = "header", Header = Header() };
        Raw(path, header, Frame(2), Frame(7), new ReplayRecord { Kind = "end", Time = 12.5 });
        Near(ReplayReader.ReadDuration(path), 12.5);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Expect<OperationCanceledException>(() => ReplayReader.Read(path, cancellationToken: cancelled.Token));
        Expect<OperationCanceledException>(() => ReplayReader.ReadDuration(path, cancelled.Token));
        Expect<OperationCanceledException>(() => ReplayReader.ReadDuration(Path.Combine(dir, "absent.lcr"), cancelled.Token));
        var complete = File.ReadAllBytes(path);
        var boundaries = new List<int> { 8 };
        for (var offset = 8; offset < complete.Length;) { offset += 8 + BitConverter.ToInt32(complete, offset); boundaries.Add(offset); }
        for (var length = boundaries[1]; length <= complete.Length; length++)
        {
            File.WriteAllBytes(path, complete.Take(length).ToArray());
            var expected = length == complete.Length ? 12.5 : length >= boundaries[3] ? 7 : length >= boundaries[2] ? 2 : 0;
            Near(ReplayReader.ReadDuration(path), expected);
        }
        Raw(path, header, new ReplayRecord { Kind = "event", Time = 9, Event = new ReplayEvent { Time = 9, Name = "last" } });
        Near(ReplayReader.ReadDuration(path), 9);
        Raw(path, header);
        Near(ReplayReader.ReadDuration(path), 0);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("invalid!"));
        Expect<InvalidDataException>(() => ReplayReader.ReadDuration(path));
        Raw(path, new ReplayRecord { Kind = "end", Time = 99 });
        Expect<InvalidDataException>(() => ReplayReader.ReadDuration(path));
        var limits = new ReplayReadLimits();
        foreach (var lengths in new[] { (0, 1), (1, 0), (limits.MaxCompressedRecordBytes + 1, 1), (1, limits.MaxUncompressedRecordBytes + 1) })
        {
            using (var output = File.Create(path))
            using (var binary = new BinaryWriter(output))
            {
                binary.Write(Encoding.ASCII.GetBytes("LCREPL01"));
                RawPayload(output, JsonConvert.SerializeObject(header));
                binary.Write(lengths.Item1); binary.Write(lengths.Item2);
            }
            Expect<InvalidDataException>(() => ReplayReader.ReadDuration(path));
        }
        // Large declared expansion is rejected without allocating or inflating that payload.
        using (var output = File.Create(path))
        using (var binary = new BinaryWriter(output))
        {
            binary.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            RawPayload(output, JsonConvert.SerializeObject(header));
            for (var i = 0; i < 9; i++) { binary.Write(1); binary.Write(limits.MaxUncompressedRecordBytes); binary.Write((byte)0); }
        }
        Expect<InvalidDataException>(() => ReplayReader.ReadDuration(path));
    });
}

static void SingleFileWindows()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "3.lcr");
        var header = Header(); header.Metadata["singleFile"] = "true";
        var records = new List<ReplayRecord>
        {
            new() { Kind = "header", Header = header },
            new() { Kind = "world", Time = 0, World = new WorldSnapshot { CaptureSetId = "first", Layer = "exterior" } }
        };
        for (var i = 0; i < 32; i++)
        {
            if (i == 12) records.Add(new ReplayRecord { Kind = "world", Time = i,
                World = new WorldSnapshot { CaptureSetId = "second", Layer = "exterior" } });
            var frame = Frame(i, Entity("player", i));
            frame.Frame!.Entities[0].State["detail"] = new string('x', 512);
            records.Add(frame);
            if (i == 19) records.Add(new ReplayRecord { Kind = "event", Time = i,
                Event = new ReplayEvent { Time = i, Name = "marker" } });
        }
        records.Add(new ReplayRecord { Kind = "end", Time = 31 });
        Raw(path, records.ToArray());
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048);
        var indexedPath = Path.Combine(dir, "2.lcr");
        using (var writer = new ReplayWriter(indexedPath, header, 64, indexed: true))
        {
            foreach (var record in records.Skip(1).Take(records.Count - 2))
                Check(writer.TryWrite(record), "indexed writer accepted the day record");
            writer.Dispose();
            Check(writer.Error == null, "indexed writer completed without error");
        }
        Check(File.Exists(Path.ChangeExtension(indexedPath, ".lci")), "single-file index sidecar was persisted");
        var indexed = ReplayReader.IndexSingleFile(indexedPath, windowExpandedBytes: 2048);
        Check(indexed.IsComplete && indexed.Windows.Count == index.Windows.Count && indexed.Duration == index.Duration,
            "sidecar and recovered scans agree on record/window boundaries");
        Check(index.IsComplete && index.Windows.Count >= 3 && index.Duration == 31, "one day indexes into playback windows");
        var timeline = new ReplayRecordingTimeline(index.Windows.Select(window => new ReplayRecordingPart
            { FilePath = path, Duration = window.Duration, Window = window }));
        Near(timeline.Duration, 31);
        Check(timeline.Parts.All(part => part.FilePath == path), "virtual playback windows share one physical file");
        foreach (var window in index.Windows)
        {
            var session = ReplayReader.ReadWindow(window);
            Check(session.Frames.Count > 0 && session.Worlds.Count > 0, "each window has frames and a carried world");
            Check(session.Worlds[0].Time == 0, "carried world is available at the window start");
            Check(ReplayTimeline.Sample(session, 0).Entities.Single().Id == "player", "boundary frame is playable");
            if (window.Start > 12) Check(session.Worlds[0].World!.CaptureSetId == "second", "latest world replaces earlier set");
        }
        Check(index.Windows.Select(window => ReplayReader.ReadWindow(window)).SelectMany(session => session.Events).Any(item => item.Name == "marker"),
            "events survive the virtual window boundary");
    });
}

static void MeshReferences()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "instances.lcr");
        WorldSnapshot World() => new()
        {
            Materials = new() { new MaterialSnapshot { Id = "instance-color", Color = new[] { 0f, 1f, 0f, 1f } } },
            Geometry = new()
            {
                new GeometrySnapshot { Id = "source", RoomId = "room-a", Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 } },
                new GeometrySnapshot { Id = "copy", MeshSourceId = "source", RoomId = "room-b", Position = new Vec3(5, 2, 1), MaterialIds = new() { "instance-color" } }
            }
        };
        using (var writer = new ReplayWriter(path, Header()))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = World() }), "shared mesh world accepted");
            writer.Dispose(); Check(writer.Error == null, "shared mesh writer failure");
        }
        var copy = ReplayReader.Read(path).Worlds.Single().World!.Geometry[1];
        Check(copy.MeshSourceId == "source" && copy.RoomId == "room-b" && copy.MaterialIds.Single() == "instance-color", "instance source, room and material survive");
        Near(copy.Position.X, 5); Check(copy.Vertices.Length == 0, "instance retains no duplicate vertex payload");
        void Reject(Action<WorldSnapshot> change)
        {
            var world = World(); change(world);
            Raw(path, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
            Expect<InvalidDataException>(() => ReplayReader.Read(path));
        }
        Reject(w => w.Geometry[1].MeshSourceId = "missing");
        Reject(w => w.Geometry[1].MeshSourceId = "copy");
        Reject(w => w.Geometry.Reverse());
        Reject(w => w.Geometry[0].MeshSourceId = "copy");
        Reject(w => w.Geometry.Add(new GeometrySnapshot { Id = "chained", MeshSourceId = "copy" }));
        Reject(w => w.Geometry[1].Vertices = w.Geometry[0].Vertices.ToArray());
        Reject(w => w.Geometry[1].Triangles = new[] { 0, 1, 2 });
        Reject(w => w.Geometry[1].SubmeshTriangles.Add(Array.Empty<int>()));
        Reject(w => w.Geometry[1].Uvs = new[] { 0f, 1f });
        Reject(w => w.Geometry[1].Normals = new[] { 0f, 0f, 1f });
        Reject(w => w.Geometry[1].BonePaths.Add("hip"));
        Reject(w => w.Geometry[1].BindPoses = new float[16]);
        Reject(w => w.Geometry[1].BoneIndices = new[] { 0 });
        Reject(w => w.Geometry[1].BoneWeights = new[] { 1f });
        Reject(w => w.Geometry[1].IsBoundsProxy = true);
        Reject(w => w.Geometry[0].IsBoundsProxy = true);
        Reject(w => w.Geometry[0].Vertices = Array.Empty<float>());
        Reject(w =>
        {
            var skin = AppearanceWorld().Geometry.Single(); skin.Id = "source"; skin.MaterialIds.Clear();
            w.Geometry[0] = skin;
        });
    });
}

static ReplayHeader Header() => new() { SessionId = "test", GameVersion = "test", StartedUtc = "2026-09-23T00:00:00Z" };
static EntitySnapshot Entity(string id, float x) => new() { Id = id, Kind = "player", Position = new Vec3(x, 0, 0) };
static ReplayRecord Frame(double time, params EntitySnapshot[] entities) => new()
{ Kind = "frame", Time = time, Frame = new ReplayFrame { Time = time, Entities = entities.ToList() } };
static void Raw(string path, params ReplayRecord[] records)
{
    using var output = File.Create(path); output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
    foreach (var record in records) RawPayload(output, JsonConvert.SerializeObject(record));
}
static void RawPayload(Stream output, string json)
{
    byte[] raw = Encoding.UTF8.GetBytes(json); using var compressed = new MemoryStream();
    using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(raw);
    using var writer = new BinaryWriter(output, Encoding.UTF8, true);
    writer.Write((int)compressed.Length); writer.Write(raw.Length); writer.Write(compressed.ToArray());
}
static void WithTemp(Action<string> action)
{
    string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string dir = Path.GetFullPath(Path.Combine(tempRoot, "LCReplayTests-" + Guid.NewGuid().ToString("N")));
    if (!dir.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test temporary path.");
    Directory.CreateDirectory(dir);
    try { action(dir); } finally { Directory.Delete(dir, true); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double value, double expected) { if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value - expected) > 0.00001) throw new Exception($"Expected {expected}, got {value}"); }
static void Expect<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
