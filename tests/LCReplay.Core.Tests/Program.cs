using System.IO.Compression;
using System.Text;
using LCReplay.Core;
using Newtonsoft.Json;

if (args.Length == 2 && args[0] == "--inspect-deaths")
{
    var index = ReplayReader.IndexSingleFile(args[1]);
    foreach (var death in ReplayReader.ReadPlayerDeaths(index))
        Console.WriteLine($"{death.Time:F3} {death.Name} {death.EntityId} ({death.Position.X:F2},{death.Position.Y:F2},{death.Position.Z:F2})");
    return 0;
}

if (args.Length == 2 && args[0] == "--inspect-audio")
{
    var recording = ReplayReader.Read(args[1]);
    var audio = recording.Events.Where(evt => evt.Category == "audio" && evt.Name == "source-block").ToArray();
    foreach (var group in audio.GroupBy(evt => evt.Data.GetValueOrDefault("source", "?")))
    {
        var peak = group.Select(evt => ReplayAudioCodec.Decode(Convert.FromBase64String(evt.Data["adpcm"]),
            int.Parse(evt.Data["samples"])).Max(sample => Math.Abs(sample))).Max();
        Console.WriteLine($"source={group.Key} blocks={group.Count()} peak={peak:F4} first={group.First().Time:F2} last={group.Last().Time:F2}");
    }
    Console.WriteLine($"audio blocks={audio.Length} particle frames={recording.Frames.Count(frame => frame.Particles.Count != 0)}");
    return 0;
}

var suite = new (string Name, Action Run)[]
{
    ("Round trip captures frames, world, events and metadata", RoundTrip),
    ("Every truncated byte boundary recovers only complete records", Truncation),
    ("Reject malformed schemas, sizes, collections, numbers and JSON", Validation),
    ("Bounded writer drains every accepted record", QueueCompletion),
    ("Invalid writer input exposes Error and leaves readable prefix", WriterFailure),
    ("Slow disk drains buffered capture in order without stopping recording", StorageBackpressure),
    ("Writer memory accounting applies backpressure to an in-flight large world", WriterMemoryBudget),
    ("Full disk and allocation failures keep readable records and release queues", StorageFailureRecovery),
    ("Closed writers release asynchronous capacity waiters", StorageCloseWaiter),
    ("A bounded overflow failure still drains its accepted prefix", StorageOverflowBudget),
    ("Early throttling resumes only below the low watermark", StorageLowWatermarks),
    ("A capture tick drains at most its record and elapsed-time budgets", StorageDrainBudget),
    ("Final snapshot records stream after accepted overflow without blocking completion", StorageFinalTail),
    ("Worker PNG encoding preserves bounded RGBA rows and PNG checksums", WorkerPng),
    ("A large in-flight map retains a bounded ordered motion tail before throttling", BulkWorldMotionTail),
    ("Optional index creation failure cannot stop the recording", OptionalIndexFailure),
    ("Streaming compression reduces allocations and enforces limits before appending", StreamingRecordWrite),
    ("Timeline seeks and applies discrete lifecycle boundaries", Timeline),
    ("Playback sampler matches independent samples across rapid forward and backward seeks", PlaybackSampler),
    ("Player teleports and death relocation do not interpolate through walls", PlayerDiscontinuities),
    ("Quaternion interpolation follows the shortest normalized arc", Quaternion),
    ("Recorded player view rotation survives file IO and interpolated seeking", PlayerViewRotation),
    ("Held scrap stays attached while a player turns between samples", HeldItemAttachment),
    ("Moving door renderers interpolate local transforms and discrete visibility", RendererPoses),
    ("Moving scene furniture interpolates world poses and preserves hidden baselines", MovingSceneRenderers),
    ("Textured multi-material skinned geometry survives recording and reading", AppearanceRoundTrip),
    ("Legacy recordings without appearance fields retain their defaults", LegacyAppearance),
    ("Texture headers, dimensions and cumulative byte budgets are bounded", TextureValidation),
    ("Appearance references, mesh arrays and skeletal influences reject invalid data", AppearanceValidation),
    ("Equal-time event and world records retain their original order", StableRecordOrder),
    ("Whole-recording timeline handles gaps, overlaps, boundary seeks and invalid parts", RecordingTimeline),
    ("Duration indexing recovers truncation and honors read limits and cancellation", DurationIndex),
    ("One physical replay file streams bounded playback windows with carried world state", SingleFileWindows),
    ("Initial playback preloads one complete map without reading future frames or captures", InitialMapPreload),
    ("Late map preload keeps a future door pose reference outside the motion timeline", LateDoorPoseReference),
    ("Delayed first moon capture becomes visible at landing without shifting later updates", DelayedLandingMap),
    ("Playback metadata indexing defers future payloads while validating selected windows", MetadataIndex),
    ("Indexed player deaths retain their time and position for camera bookmarks", PlayerDeathIndex),
    ("Bookmark counts and sparse sunlight state survive indexed forward and backward windows", BookmarkLightingIndex),
    ("Game clock follows native six-AM offset and configured day length", GameClock),
    ("Binary sidecar tolerates JSON double formatting at event timestamps", IndexFloatPrecision),
    ("Sparse actor motion tracks survive out-of-order capture and window seeks", AnimationTrackRoundTrip),
    ("Sparse item poses survive indexed seeks without repeated frame transforms", ItemMotionRoundTrip),
    ("HDRP filter changes and outline settings survive indexed seeks", PostFxWindowCarry),
    ("Static mesh references survive round trips and reject missing, cyclic or duplicated payloads", MeshReferences),
    ("Moving ship anchors interpolate and captured lights survive round trips", MovingEnvironment),
    ("Sky, fog, rooms and shader properties round trip with bounded validation", EnvironmentAndMaterials),
    ("Instanced vegetation, particle emitters and local fog round trip with bounded validation", ProceduralVisuals),
    ("Installed moon scene references and generation settings are bounded and round trip", SceneAssetReferences),
    ("Short-lived particle poses survive seek and reject invalid sizes", ShortLivedParticles),
    ("Particle motion and emitter clocks interpolate without seed reuse or premature births", ParticleMotion),
    ("Laser and rope lines survive seek while malformed paths are rejected", LineRenderers),
    ("Bounded mono audio blocks retain a waveform without accepting truncated data", AudioBlocks),
    ("Late replay windows carry bounded current states and active native sound loops", BoundedStateCarry),
    ("Authentic static emitter states survive sidecars, actor pruning and backward windows", AmbientSoundCarry),
    ("Installed actor references and secondary UV/tangent channels round trip with validation", NativeRenderData),
    ("Playback windows omit retired actors while backward seeks restore their current state", RetiredActorCarry),
};
int failures = 0;
foreach (var test in suite)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + e); }
}
Console.WriteLine($"{suite.Length - failures}/{suite.Length} test groups passed.");
return failures == 0 ? 0 : 1;

static void AmbientSoundCarry()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "actual-static-emitters.lcr");
        using (var writer = new ReplayWriter(path, Header(), 8192, indexed: true))
        {
            void Event(int time, string source, string name, string volume)
            {
                var evt = new ReplayEvent { Time = time, Category = "sound", Name = name,
                    Data = new() { ["ambient"] = "true", ["source"] = "ambient:" + source } };
                if (name == "play")
                {
                    evt.Data["loop"] = "true"; evt.Data["clip"] = source; evt.Data["volume"] = volume;
                    evt.Data["x"] = "4"; evt.Data["max"] = "15"; evt.Data["anchor"] = "ship-elevator";
                }
                Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = time, Event = evt }), "actual emitter state accepted");
            }
            for (var i = 0; i < 600; i++)
            {
                if (i == 0) Event(i, "rain", "play", ".7");
                if (i == 40) Event(i, "rain", "stop", "");
                if (i == 100) Event(i, "fire", "play", ".4");
                if (i == 350) Event(i, "fire", "play", ".2");
                Check(writer.TryWrite(Frame(i, Entity("moving-actor-" + i, i))), "independent actor frame");
            }
        }
        foreach (var index in new[] { ReplayReader.IndexSingleFile(path, windowExpandedBytes: 4096), ReplayReader.IndexSingleFile(path, windowExpandedBytes: 4096) })
        {
            index.TrimInactiveActorState = true;
            foreach (var time in new[] { 590d, 25d, 590d })
            {
                var session = ReplayReader.ReadWindow(index.Windows.Last(window => window.Start <= time));
                var bySource = session.Events.Where(evt => evt.Category == "sound").GroupBy(evt => evt.Data["source"])
                    .ToDictionary(group => group.Key, group => group.OrderBy(evt => evt.Time).Last());
                Check(bySource["ambient:rain"].Name == (time < 40 ? "play" : "stop"), "weather stop restored despite absent actor owner");
                if (time > 350)
                {
                    Check(bySource.Count == 2 && bySource["ambient:fire"].Data["volume"] == ".2", "latest emitter gain carried without entire history");
                    Check(bySource["ambient:fire"].Data["x"] == "4" && bySource["ambient:fire"].Data["anchor"] == "ship-elevator", "physical emitter anchor retained");
                }
                Check(session.Events.All(evt => evt.Category != "audio" && !evt.Data.ContainsKey("adpcm")), "no waveform stream");
            }
        }
    });
}

static void BoundedStateCarry()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "long-state-history.lcr");
        var animatorPath = new string('뼈', 160);
        using (var writer = new ReplayWriter(path, Header(), 8192, indexed: true))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = new WorldSnapshot { CaptureSetId = "same", Layer = "exterior" } }), "world");
            void Event(int time, ReplayEvent evt) { evt.Time = time; Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = time, Event = evt }), "event accepted"); }
            for (var i = 0; i < 1000; i++)
            {
                Event(i, new ReplayEvent { Category = "animation", Name = "state", EntityId = "p", Data = new() {
                    ["animatorPath"] = animatorPath, ["layer"] = "0", ["hash"] = i.ToString(), ["clip"] = "Walk" } });
                Event(i, new ReplayEvent { Category = "animation", Name = "parameters", EntityId = "p", Data = new() {
                    ["animatorPath"] = animatorPath, ["b1"] = (i % 2).ToString() } });
                if (i % 5 == 0) Event(i, new ReplayEvent { Category = "animation", Name = "parameters", EntityId = "p", Data = new() {
                    ["animatorPath"] = animatorPath, ["f2"] = i.ToString() } });
                Event(i, new ReplayEvent { Category = "item", Name = "pose", EntityId = "scrap", ItemMotion = new() { Mode = "rest", Position = new Vec3(i, 0, 0) } });
                Event(i, new ReplayEvent { Category = "visual", Name = "renderer", EntityId = "g", Data = new() { ["set"] = "same", ["visible"] = (i % 2 == 0 ? "true" : "false") } });
                Event(i, new ReplayEvent { Category = "sound", Name = "play", EntityId = "p", Data = new() { ["source"] = "foot", ["clip"] = "Footstep", ["loop"] = "false" } });
                if (i == 0) Event(i, new ReplayEvent { Category = "sound", Name = "play", EntityId = "scrap", Data = new() { ["source"] = "spray", ["clip"] = "Spray", ["loop"] = "true" } });
                if (i == 800) Event(i, new ReplayEvent { Category = "sound", Name = "stop", EntityId = "scrap", Data = new() { ["source"] = "spray" } });
                Check(writer.TryWrite(Frame(i, Entity("p", i))), "frame accepted");
            }
        }
        foreach (var index in new[] { ReplayReader.IndexSingleFile(path, windowExpandedBytes: 4096), ReplayReader.IndexSingleFile(path, windowExpandedBytes: 4096) })
        {
            var last = index.Windows.Last(); var session = ReplayReader.ReadWindow(last);
            var carried = session.Events.Where(evt => evt.Time < 0).ToArray();
            Check(last.Start > 980 && carried.Length <= 9, "old history stays bounded: " + carried.Length);
            var before = (int)Math.Ceiling(last.Start) - 1;
            Check(carried.Single(evt => evt.Category == "animation" && evt.Name == "state").Data["hash"] == before.ToString(), "latest state restored");
            Check(carried.Where(evt => evt.Name == "parameters").Last(evt => evt.Data.ContainsKey("f2")).Data["f2"] == (before / 5 * 5).ToString(), "unchanged parameter retains its older value");
            Near(carried.Single(evt => evt.Category == "item").ItemMotion!.Position.X, before);
            Check(carried.Single(evt => evt.Category == "sound").Name == "stop", "loop stop survives; old footsteps do not restart");
        }
    });
}

static void RetiredActorCarry()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "actor-lifetimes.lcr");
        using (var writer = new ReplayWriter(path, Header(), 8192, indexed: true))
        {
            for (var i = 0; i < 500; i++)
            {
                var id = "enemy-" + i;
                foreach (var evt in new[] {
                    new ReplayEvent { Category = "animation", Name = "state", EntityId = id, Data = new() { ["hash"] = "1", ["clip"] = "Walk" } },
                    new ReplayEvent { Category = "sound", Name = "play", EntityId = id, Data = new() { ["source"] = "loop-" + i, ["clip"] = "Ambience", ["loop"] = "true" } },
                    new ReplayEvent { Category = "item", Name = "pose", EntityId = id, ItemMotion = new() { Mode = "rest" } } })
                {
                    evt.Time = i;
                    Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = i, Event = evt }), "actor event");
                }
                Check(writer.TryWrite(Frame(i, Entity(id, i))), "actor frame");
            }
        }
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 4096);
        index.TrimInactiveActorState = true;
        foreach (var number in new[] { index.Windows.Count - 1, 2, index.Windows.Count - 1 })
        {
            var session = ReplayReader.ReadWindow(index.Windows[number]);
            var ids = session.Frames.SelectMany(frame => frame.Entities).Select(actor => actor.Id).ToHashSet();
            var carried = session.Events.Where(evt => evt.Time < 0).ToArray();
            Check(carried.Length <= 6 && carried.All(evt => ids.Contains(evt.EntityId)), "only boundary actors carried: " + carried.Length);
            Check(session.Events.Any(evt => evt.Category == "sound"), "current window native sounds restored");
        }
        var scanned = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 4096);
        scanned.TrimInactiveActorState = true;
        Check(ReplayReader.ReadWindow(scanned.Windows.Last()).Events.Count == ReplayReader.ReadWindow(index.Windows.Last()).Events.Count, "sidecar actor pruning agrees");
    });
}

static void NativeRenderData()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "native-assets.lcr");
        WorldSnapshot World() => new() { Geometry = new() {
            new GeometrySnapshot { Id = "butler", EntityId = "enemy", PrefabKey = "enemy:Butler", PrefabRendererPath = "mesh[0]" },
            new GeometrySnapshot { Id = "fixture", Vertices = new float[] { 0,0,0, 1,0,0, 0,1,0 }, Triangles = new[] { 0,1,2 },
                Uvs1 = new float[] { 0,0, 1,0, 0,1 }, Uvs2 = new float[] { 1,0, 1,1, 0,1 },
                Uvs3 = new float[] { 0,1, 0,0, 1,0 }, Tangents = new float[] { 1,0,0,-1, 1,0,0,-1, 1,0,0,-1 } } } };
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = World() });
        var restored = ReplayReader.Read(path).Worlds.Single().World!;
        Check(restored.Geometry[0].PrefabKey == "enemy:Butler" && restored.Geometry[0].Vertices.Length == 0, "prefab reference contains no exported vertices");
        Check(restored.Geometry[1].Uvs2.SequenceEqual(World().Geometry[1].Uvs2) && restored.Geometry[1].Tangents[3] == -1, "secondary maps and handed tangents survive");
        void Reject(Action<WorldSnapshot> change) { var world = World(); change(world); Raw(path, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world }); Expect<InvalidDataException>(() => ReplayReader.Read(path)); }
        Reject(w => w.Geometry[0].PrefabRendererPath = "");
        Reject(w => w.Geometry[0].EntityId = "");
        Reject(w => w.Geometry[0].IsBoundsProxy = true);
        Reject(w => w.Geometry[1].Uvs2 = new float[5]);
        Reject(w => w.Geometry[1].Tangents[1] = float.NaN);
    });
}

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

static void SceneAssetReferences()
{
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "scene-assets.lcr");
        var world = new WorldSnapshot
        {
            Scene = "Level1Experimentation", CaptureSetId = "capture-1", Layer = "exterior",
            AssetScene = "Level1Experimentation", AssetGameVersion = "81", AssetBuildIndex = 5,
            MapSeed = 123456, LevelId = 0, DungeonSeed = 98765, DungeonFlow = 2,
            AssetRendererPaths = new() { "0/1/2:MeshRenderer:0" },
            AssetTerrainPaths = new() { "0/3:Terrain:0" }
        };
        using (var writer = new ReplayWriter(file, Header()))
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = world }), "scene-reference world accepted");
        var loaded = ReplayReader.Read(file).Worlds.Single().World!;
        Check(loaded.AssetScene == world.AssetScene && loaded.AssetBuildIndex == 5 &&
            loaded.MapSeed == 123456 && loaded.DungeonSeed == 98765 && loaded.DungeonFlow == 2 &&
            loaded.AssetRendererPaths.Single() == world.AssetRendererPaths.Single(), "scene references round trip");
        void Reject(Action<WorldSnapshot> mutate)
        {
            var invalid = new WorldSnapshot
            {
                Layer = "exterior", CaptureSetId = "capture-1", AssetScene = "Level1Experimentation",
                AssetGameVersion = "81", AssetBuildIndex = 5,
                AssetRendererPaths = new() { "0/1:MeshRenderer:0" }
            };
            mutate(invalid);
            Raw(file, new ReplayRecord { Kind = "header", Header = Header() },
                new ReplayRecord { Kind = "world", World = invalid });
            Expect<InvalidDataException>(() => ReplayReader.Read(file));
        }
        Reject(value => value.AssetRendererPaths.Add(value.AssetRendererPaths[0]));
        Reject(value => value.AssetScene = "");
        Reject(value => value.AssetBuildIndex = -1);
        Reject(value => value.AssetRendererPaths[0] = new string('x', 257));
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
                ShadowCastingMode = 0, ReceiveShadows = false,
                Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 } } },
            Lights = new() { new LightSnapshot { Id = "lamp", AnchorId = "ship-elevator", Type = "Point",
                Position = new Vec3(0, 2, 0), Color = new[] { .2f, .4f, .9f, 1f },
                UseColorTemperature = true, ColorTemperature = 3200f,
                Intensity = 80, LightDimmer = .8f, Range = 12, Shadows = true,
                ShadowStrength = .35f, ShadowDimmer = .6f, BakeType = "Realtime" },
                new LightSnapshot { Id = "scrap-glow", EntityId = "item-1", Type = "Point",
                    Position = new Vec3(0, .2f, 0), Intensity = 40, Range = 4 } }
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
        Check(session.Worlds[0].World!.Geometry[0].ShadowCastingMode == 0 &&
            !session.Worlds[0].World!.Geometry[0].ReceiveShadows, "geometry retains source shadow behavior");
        Check(session.Worlds[0].World!.Lights[0].Shadows &&
            session.Worlds[0].World!.Lights[0].BakeType == "Realtime" &&
            session.Worlds[0].World!.Lights[0].ShadowStrength == .35f &&
            session.Worlds[0].World!.Lights[0].ShadowDimmer == .6f &&
            session.Worlds[0].World!.Lights[0].LightDimmer == .8f, "light retains realtime shadow source");
        Check(session.Worlds[0].World!.Lights[0].UseColorTemperature &&
            session.Worlds[0].World!.Lights[0].ColorTemperature == 3200f &&
            session.Worlds[0].World!.Lights[0].Color[2] == .9f, "light retains tint and color temperature");
        Check(session.Worlds[0].World!.Lights[1].EntityId == "item-1" &&
            session.Worlds[0].World!.Lights[1].Position.Y == .2f, "scrap light retains moving entity anchor");
        Near(ReplayTimeline.Sample(session, 0.5).Anchors[0].Position.Y, 50);
        world.Lights[0].ShadowStrength = 2f;
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        var malformed = new ReplayFrame { Time = 0, Anchors = new() { new AnchorPose { Id = "ship-elevator" }, new AnchorPose { Id = "ship-elevator" } } };
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "frame", Frame = malformed });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
    });
}

static void ShortLivedParticles()
{
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "bursts.lcr");
        var frame = new ReplayFrame { Time = 0,
            ParticleStyles = new() { new ParticleStyleSnapshot { Id = "muzzle", Name = "Bullet particles", ParentName = "Turret",
                MaterialName = "Tracer", ShaderName = "HDRP/Particles/Unlit", RenderMode = 1,
                VertexStreams = new[] { 0, 1, 2, 3 }, Simulate = true, Time = .4f, VelocityScale = .02f } },
            Particles = new() { new ParticlePose
        { EmitterId = "muzzle", Position = new Vec3(1, 2, 3), Size = .2f, Rotation = 45,
            Size3D = new Vec3(.2f, .1f, .1f), Velocity = new Vec3(40, 0, 0),
            Lifetime = .5f, RemainingLifetime = .25f, RandomSeed = 42,
            Color = new[] { 1f, .2f, 0f, .8f }, IsInterior = true } } };
        using (var writer = new ReplayWriter(file, Header()))
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = 0, Frame = frame }), "particle frame accepted");
        var session = ReplayReader.Read(file);
        var pose = ReplayTimeline.Sample(session, 0).Particles.Single();
        Check(pose.IsInterior && pose.Position.Y == 2 && pose.Color[1] == .2f, "particle pose retained");
        var style = ReplayTimeline.Sample(session, 0).ParticleStyles.Single();
        Check(style.MaterialName == "Tracer" && style.Simulate && style.RenderMode == 1 &&
            style.VertexStreams.Length == 4 && pose.EmitterId == style.Id && pose.Velocity.X == 40 &&
            pose.RandomSeed == 42 && pose.RemainingLifetime == .25f, "native effect identity, stretch and atlas age survive seek");
        var malformed = JsonConvert.DeserializeObject<ReplayFrame>(JsonConvert.SerializeObject(frame))!;
        malformed.ParticleStyles.Clear();
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "frame", Frame = malformed });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        malformed.Particles.Clear(); malformed.ParticleStyles.Add(new ParticleStyleSnapshot { Id = "invalid", VertexStreams = new[] { 999 } });
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "frame", Frame = malformed });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() },
            new ReplayRecord { Kind = "frame", Frame = new ReplayFrame { Particles = new() { new ParticlePose { Size = float.NaN } } } });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
    });
}

static void ParticleMotion()
{
    var left = new ReplayFrame { Time = 0,
        ParticleStyles = new() { new ParticleStyleSnapshot { Id = "effect", Name = "effect", Simulate = true, Time = 1, RandomSeed = 5 } },
        Particles = new() {
            new ParticlePose { EmitterId = "effect", RandomSeed = 1, Position = new Vec3(0, 0, 0), Velocity = new Vec3(10, 0, 0), RemainingLifetime = 1, Rotation = 350 },
            new ParticlePose { EmitterId = "effect", RandomSeed = 2, Velocity = new Vec3(20, 0, 0), RemainingLifetime = .02f },
            new ParticlePose { EmitterId = "effect", RandomSeed = 3, Velocity = new Vec3(10, 0, 0), RemainingLifetime = .4f } } };
    var right = new ReplayFrame { Time = .1,
        ParticleStyles = new() { new ParticleStyleSnapshot { Id = "effect", Name = "effect", Simulate = true, Time = 1.1f, RandomSeed = 5, Position = new Vec3(2, 0, 0) } },
        Particles = new() {
            new ParticlePose { EmitterId = "effect", RandomSeed = 1, Position = new Vec3(2, 0, 0), RemainingLifetime = .9f, Rotation = 10 },
            new ParticlePose { EmitterId = "effect", RandomSeed = 3, Position = new Vec3(100, 0, 0), RemainingLifetime = 1 },
            new ParticlePose { EmitterId = "effect", RandomSeed = 4, Position = new Vec3(500, 0, 0) } } };
    var session = new ReplaySession { Duration = .1, Frames = new() { left, right } };
    var sample = ReplayTimeline.Sample(session, .05);
    Check(sample.Particles.Count == 2, "expired particle removed and future birth not drawn early");
    Check(Math.Abs(sample.Particles[0].Position.X - 1) < .00001 && Math.Abs(sample.Particles[0].Rotation - 360) < .00001, "identity matched, position and shortest rotation interpolate");
    Check(Math.Abs(sample.Particles[1].Position.X - .5f) < .00001, "reused seed from another birth must extrapolate old velocity instead of teleporting");
    Check(Math.Abs(sample.ParticleStyles[0].Time - 1.05) < .00001 && sample.ParticleStyles[0].Position.X == 1, "native simulation clock and emitter move continuously");
    sample.Particles[0].Color[0] = 0; sample.ParticleStyles[0].Time = 500;
    Check(left.Particles[0].Color[0] == 1 && left.ParticleStyles[0].Time == 1, "independent sampling leaves recording immutable");
    var sampler = new ReplayTimelineSampler();
    foreach (var time in new[] { .08, .03, .1, 0, .05 })
        Check(JsonConvert.SerializeObject(sampler.Sample(session, time)) == JsonConvert.SerializeObject(ReplayTimeline.Sample(session, time)), "scratch sampler parity through seeks");
    var reusable = sampler.Sample(session, .04).Particles[0];
    Check(ReferenceEquals(reusable, sampler.Sample(session, .06).Particles[0]), "scratch reuses particle objects instead of growing per tick");
    right.ParticleStyles[0].Time = .01f;
    Check(Math.Abs(ReplayTimeline.Sample(session, .05).ParticleStyles[0].Time - 1.05f) < .00001, "loop reset does not rewind simulation between snapshots");
}

static void LineRenderers()
{
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "lines.lcr");
        var line = new LinePose { Id = "laser", Positions = new[] { 1f, 2f, 3f, 4f, 5f, 6f },
            MaterialName = "Laser red", ShaderName = "HDRP/Unlit", TextureMode = 1,
            StartWidth = .02f, EndWidth = .01f, StartColor = new[] { 1f, 0f, 0f, 1f },
            EndColor = new[] { 1f, .2f, 0f, .8f }, IsInterior = true };
        using (var writer = new ReplayWriter(file, Header()))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Frame = new ReplayFrame { Lines = new() { line } } }), "line frame accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = 1, Frame = new ReplayFrame { Time = 1,
                Lines = new() { new LinePose { Id = "laser", Positions = new[] { 3f, 2f, 3f, 6f, 5f, 6f },
                    StartWidth = .04f, EndWidth = .01f, IsInterior = true } } } }), "moving line accepted");
        }
        var session = ReplayReader.Read(file);
        var restored = ReplayTimeline.Sample(session, 0).Lines.Single();
        Check(restored.Id == "laser" && restored.IsInterior && restored.Positions[5] == 6f &&
            restored.StartWidth == .02f, "line path and styling retained");
        var midway = ReplayTimeline.Sample(session, .5).Lines.Single();
        Check(midway.Positions[0] == 2f && Math.Abs(midway.StartWidth - .03f) < .00001f,
            "moving line interpolates between samples");
        Check(midway.MaterialName == "Laser red" && midway.ShaderName == "HDRP/Unlit" && midway.TextureMode == 1,
            "line interpolation retains the original laser material and texture mapping");
        line.Positions = new[] { 1f, 2f, float.NaN, 4f, 5f, 6f };
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() },
            new ReplayRecord { Kind = "frame", Frame = new ReplayFrame { Lines = new() { line } } });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
    });
}

static void AudioBlocks()
{
    var waveform = Enumerable.Range(0, ReplayAudioCodec.MaxSamplesPerBlock)
        .Select(i => (short)Math.Round(Math.Sin(i * .07) * 12000)).ToArray();
    var encoded = ReplayAudioCodec.Encode(waveform, waveform.Length);
    var decoded = ReplayAudioCodec.Decode(encoded, waveform.Length);
    Check(encoded.Length == 2 + waveform.Length / 2, "ADPCM block is bounded to four bits per sample");
    var error = waveform.Zip(decoded, (source, result) => Math.Abs(source / 32768f - result)).Average();
    Check(error < .025, "audio waveform retains audible detail: " + error);
    Expect<InvalidDataException>(() => ReplayAudioCodec.Decode(encoded[..^1], waveform.Length));
    Expect<ArgumentOutOfRangeException>(() => ReplayAudioCodec.Encode(waveform, waveform.Length + 1));
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "spatial-audio.lcr");
        var evt = new ReplayEvent { Category = "audio", Name = "source-block", Data = new()
        {
            ["rate"] = ReplayAudioCodec.SampleRate.ToString(), ["samples"] = waveform.Length.ToString(),
            ["adpcm"] = Convert.ToBase64String(encoded), ["source"] = "s1",
            ["x"] = "12.5", ["y"] = "-2", ["z"] = "3", ["spatial"] = "1",
            ["min"] = "1", ["max"] = "30", ["rolloff"] = "0"
        } };
        using (var writer = new ReplayWriter(file, Header()))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Event = evt }), "spatial audio event accepted");
            writer.Dispose(); Check(writer.Error == null, "spatial audio event writes");
        }
        var saved = ReplayReader.Read(file).Events.Single();
        Check(saved.Data["x"] == "12.5" && saved.Data["spatial"] == "1" &&
            saved.Data["adpcm"] == evt.Data["adpcm"], "spatial source metadata survives archive");
    });
}

static void EnvironmentAndMaterials()
{
    WithTemp(dir =>
    {
        ReplaySkyRgbe.Encode(.0005f, .00025f, .0001f, out var red, out var green, out var blue, out var exponent);
        ReplaySkyRgbe.Decode(red, green, blue, exponent, out var restoredRed, out var restoredGreen, out var restoredBlue);
        Check(exponent != 0 && Math.Abs(restoredRed - .0005f) < .000005f &&
            Math.Abs(restoredGreen - .00025f) < .000005f && Math.Abs(restoredBlue - .0001f) < .000005f,
            "HDR sky encoding retains colors below one 8-bit color step");
        var file = Path.Combine(dir, "environment.lcr");
        var world = new WorldSnapshot
        {
            CaptureSetId = "capture-1", Layer = "exterior",
            Rooms = new() { new RoomSnapshot { Id = "r1", Center = new Vec3(2, 3, 4), Size = new Vec3(8, 5, 6),
                AdditionalVolumes = new() { new RoomVolumeSnapshot { Center = new Vec3(2, 30, 4), Size = new Vec3(4, 12, 4) } } } },
            Environment = new EnvironmentSnapshot { AmbientSkyColor = new[] { .1f, .2f, .3f, 1f },
                SkyFaceEncoding = "rgbe8", SkyFaces = Enumerable.Range(0, 6).Select(index => Texture("sky" + index)).ToList(),
                Components = new()
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
        Check(result.Environment!.SkyFaceEncoding == "rgbe8" && result.Environment.SkyFaces.Count == 6 &&
            new EnvironmentSnapshot().SkyFaceEncoding == "", "HDR sky encoding round trips with legacy default");
        Check(result.Rooms.Single().AdditionalVolumes.Single().Center.Y == 30 &&
            result.Rooms.Single().AdditionalVolumes.Single().Size.Y == 12, "separate entrance volume preserved");
        Check(result.Materials.Single().Properties.Single().Values[0] == .4f && result.Materials.Single().RenderQueue == 2450,
            "shader parameters preserved");
        world.Environment!.SkyFaceEncoding = "unknown";
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        world.Environment.SkyFaceEncoding = "rgbe8";
        world.Rooms.Add(new RoomSnapshot { Id = "r1", Size = Vec3.One });
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        world.Rooms.RemoveAt(1);
        world.Rooms[0].AdditionalVolumes[0].Size = new Vec3(4, -12, 4);
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "world", World = world });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
        world.Rooms[0].AdditionalVolumes[0].Size = new Vec3(4, 12, 4);
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
        // Streaming decoding must retain strict UTF-8 checks, even when the
        // malformed byte occurs in otherwise valid JSON string content.
        using (var output = File.Create(file))
        {
            output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            var raw = Encoding.UTF8.GetBytes("{\"Kind\":\"header\",\"Header\":{\"SessionId\":\"X\"}}");
            raw[Array.IndexOf(raw, (byte)'X')] = 0xff;
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(raw);
            using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            writer.Write((int)compressed.Length); writer.Write(raw.Length); writer.Write(compressed.ToArray());
        }
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

static void StorageBackpressure() => WithTemp(dir =>
{
    var output = new ControlledRecordingStream();
    using var writer = new ReplayWriter(output, Header(), 1);
    var capture = new ReplayCaptureBuffer(writer, 64);
    output.Block = true;
    Check(capture.TryWrite(Frame(0)), "first frame accepted");
    Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "worker reaches slow disk");
    try
    {
        for (int i = 1; i <= 12; i++) Check(capture.TryWrite(Frame(i, Entity("p", i))), "overflow frame retained");
        Check(capture.TryWrite(new ReplayRecord { Kind = "event", Time = 12, Event = new ReplayEvent { Time = 12, Category = "marker", Name = "bookmark" } }), "bookmark retained");
        Check(capture.PendingCount > 0 && capture.ShouldPauseCapture && capture.Error == null && writer.Error == null, "pressure is temporary, not fatal");
        var finish = capture.CompleteAsync(); Check(!finish.IsCompleted, "completion waits asynchronously for accepted backlog");
        output.Release.Set(); Check(finish.Wait(TimeSpan.FromSeconds(10)), "disk catch-up completes");
        var file = Path.Combine(dir, "slow.lcr"); File.WriteAllBytes(file, output.Bytes);
        var saved = ReplayReader.Read(file);
        Check(saved.IsComplete && saved.Frames.Select(f => f.Time).SequenceEqual(Enumerable.Range(0, 13).Select(i => (double)i)), "all frames survive in order");
        Check(saved.Events.Count(e => e.Category == "marker") == 1 && writer.WrittenBookmarkCount == 1 && writer.WrittenDuration == 12, "persisted counts, not attempted records");
        Check(capture.PendingCount == 0 && capture.PendingBytes == 0 && writer.QueuedBytes == 0, "queue memory released");
        Check(!capture.TryWrite(Frame(13)), "finished buffer rejects records");
    }
    finally { output.Release.Set(); }
});

static void BulkWorldMotionTail() => WithTemp(dir =>
{
    var output = new ControlledRecordingStream();
    using var writer = new ReplayWriter(output, Header(), 64, 4L * 1024 * 1024);
    var capture = new ReplayCaptureBuffer(writer);
    output.Block = true;
    try
    {
        var world = new WorldSnapshot { Geometry = new() { new GeometrySnapshot { Id = "bulk", Vertices = new float[1200000], Triangles = new[] { 0, 1, 2 } } } };
        Check(capture.TryWrite(new ReplayRecord { Kind = "world", World = world }), "large map accepted in empty output");
        Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "map serialization in flight");
        Check(!capture.ShouldPauseCapture, "isolated bulk map does not pause pose sampling");
        for (var i = 0; i < 48; i++)
        {
            Check(capture.TryWrite(Frame(i)), "bounded pose tail retained");
            if (i < 47) Check(!capture.ShouldPauseCapture, "tail below watermark keeps capture active");
        }
        Check(capture.ShouldPauseCapture && capture.PendingBytes < 1024 * 1024, "tail throttles at count limit, not unbounded growth");
        var finishing = capture.CompleteAsync(); Check(!finishing.IsCompleted, "completion waits for stalled bulk output");
        output.Release.Set(); finishing.GetAwaiter().GetResult();
        Check(writer.Error == null && capture.PendingBytes == 0 && writer.QueuedBytes == 0, "bulk and motion reservations released");
        var path = Path.Combine(dir, "bulk-tail.lcr"); File.WriteAllBytes(path, output.Bytes);
        var saved = ReplayReader.Read(path);
        Check(saved.IsComplete && saved.Worlds.Count == 1 && saved.Frames.Count == 48 && saved.Frames.Last().Time == 47, "ordered tail survives bulk compression");
    }
    finally { output.Release.Set(); }
});

static void WorkerPng()
{
    foreach (var size in new[] { (1, 1), (19, 7), (256, 256) })
    {
        var (width, height) = size; var pixels = new byte[width * height * 4]; new Random(81).NextBytes(pixels);
        var png = ReplayPng.EncodeRgba(pixels, width, height);
        Check(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
        using var encoded = new MemoryStream();
        for (var cursor = 8; cursor < png.Length;)
        {
            var count = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(cursor, 4));
            var type = Encoding.ASCII.GetString(png, cursor + 4, 4);
            uint crc = uint.MaxValue;
            for (var offset = cursor + 4; offset < cursor + 8 + count; offset++)
            { crc ^= png[offset]; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320; }
            Check((crc ^ uint.MaxValue) == System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(cursor + 8 + count, 4)), "valid PNG chunk checksum");
            if (type == "IDAT") encoded.Write(png, cursor + 8, count);
            cursor += count + 12;
        }
        encoded.Position = 0; using var decoder = new ZLibStream(encoded, CompressionMode.Decompress); using var raw = new MemoryStream(); decoder.CopyTo(raw);
        var rows = raw.ToArray(); Check(rows.Length == (width * 4 + 1) * height, "exact RGBA scanline length");
        for (var y = 0; y < height; y++)
            Check(rows[y * (width * 4 + 1)] == 0 && rows.AsSpan(y * (width * 4 + 1) + 1, width * 4).SequenceEqual(pixels.AsSpan((height - y - 1) * width * 4, width * 4)), "pixel values and vertical orientation");
    }
    Expect<ArgumentException>(() => ReplayPng.EncodeRgba(new byte[3], 1, 1));
    Expect<ArgumentException>(() => ReplayPng.EncodeRgba(new byte[4], 2049, 1));
}

static void StorageFinalTail() => WithTemp(dir =>
{
    var output = new ControlledRecordingStream();
    using var writer = new ReplayWriter(output, Header(), 2);
    var capture = new ReplayCaptureBuffer(writer);
    output.Block = true;
    try
    {
        Check(capture.TryWrite(Frame(0)), "initial frame accepted");
        Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "output blocked");
        for (var i = 1; i < 5; i++) Check(capture.TryWrite(Frame(i)), "overflow retained");
        IEnumerable<ReplayRecord> Tail()
        {
            for (var i = 5; i < 100; i++) yield return Frame(i);
            yield return new ReplayRecord { Kind = "event", Time = 99, Event = new ReplayEvent {
                Time = 99, Category = "marker", Name = "bookmark" } };
        }
        var finishing = capture.CompleteAsync(Tail());
        Check(!finishing.IsCompleted, "completion yields while stalled");
        output.Release.Set(); finishing.GetAwaiter().GetResult();
        Check(writer.Error == null && writer.WrittenBookmarkCount == 1, "streamed tail preserved");
        var path = Path.Combine(dir, "final-tail.lcr"); File.WriteAllBytes(path, output.Bytes);
        var saved = ReplayReader.Read(path);
        Check(saved.IsComplete && saved.Frames.Select(frame => frame.Time).SequenceEqual(Enumerable.Range(0, 100).Select(i => (double)i)), "frames stay ordered across frozen tail");
        Check(capture.PendingBytes == 0 && writer.QueuedBytes == 0, "tail releases queue payloads");
    }
    finally { output.Release.Set(); }
});

static void WriterMemoryBudget()
{
    var output = new ControlledRecordingStream();
    using var writer = new ReplayWriter(output, Header(), 256, 1024);
    output.Block = true;
    Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = new WorldSnapshot { Geometry = new() {
        new GeometrySnapshot { Id = "mesh", Vertices = new float[30000], Triangles = new[] { 0, 1, 2 } } } } }), "one large world admitted");
    Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "large world is in flight");
    try
    {
        Check(writer.QueuedBytes > 1024 && !writer.TryWrite(Frame(1)) && writer.Error == null, "in-flight payload prevents additional memory growth");
        var retry = writer.WriteAsync(Frame(1)); Check(!retry.IsCompleted, "producer waits without blocking its caller");
        output.Release.Set(); Check(retry.Wait(TimeSpan.FromSeconds(10)) && retry.Result, "same writer resumes after large record");
        writer.Dispose(); Check(writer.Error == null && writer.QueuedBytes == 0, "memory reservation released");
    }
    finally { output.Release.Set(); }
}

static void StorageFailureRecovery() => WithTemp(dir =>
{
    foreach (var allocationFailure in new[] { false, true })
    {
        var output = new ControlledRecordingStream();
        using var writer = new ReplayWriter(output, Header(), 16);
        Check(writer.TryWrite(Frame(1, Entity("p", 1))), "saved prefix accepted");
        Check(SpinWait.SpinUntil(() => writer.WrittenDuration == 1, TimeSpan.FromSeconds(5)), "saved prefix completed");
        output.Failure = allocationFailure ? new OutOfMemoryException("injected allocation failure") : new IOException("injected full disk");
        output.TornTail = !allocationFailure; output.FailTruncate = !allocationFailure;
        var capture = new ReplayCaptureBuffer(writer);
        for (int i = 2; i < 16; i++) capture.TryWrite(Frame(i));
        Check(capture.CompleteAsync().Wait(TimeSpan.FromSeconds(10)), "failed disk completion cannot hang");
        Check(allocationFailure ? writer.Error is OutOfMemoryException : writer.Error is IOException, "real failure reported");
        Check(writer.QueuedBytes == 0 && capture.PendingBytes == 0, "failed snapshots released");
        var file = Path.Combine(dir, allocationFailure ? "memory.lcr" : "disk.lcr"); File.WriteAllBytes(file, output.Bytes);
        var saved = ReplayReader.Read(file);
        Check(!saved.IsComplete && saved.Frames.Count == 1 && saved.Frames[0].Time == 1, "complete prefix remains watchable even when truncation fails");
        Check(writer.WrittenDuration == 1, "failed tail cannot inflate saved duration");
    }
});

static void StorageCloseWaiter()
{
    var output = new ControlledRecordingStream();
    using var writer = new ReplayWriter(output, Header(), 1);
    output.Block = true; writer.TryWrite(Frame(0));
    Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "worker blocked");
    try
    {
        Check(writer.TryWrite(Frame(1)), "queue filled");
        var waiter = writer.WriteAsync(Frame(2)); Check(!waiter.IsCompleted, "async producer waits");
        var done = writer.CompleteAsync();
        Check(waiter.Wait(TimeSpan.FromSeconds(5)) && !waiter.Result, "closing wakes producer with refusal");
        output.Release.Set(); Check(done.Wait(TimeSpan.FromSeconds(10)), "accepted prefix drains after closing");
    }
    finally { output.Release.Set(); }
}

static void StorageOverflowBudget() => WithTemp(dir =>
{
    var output = new ControlledRecordingStream(); using var writer = new ReplayWriter(output, Header(), 1);
    var capture = new ReplayCaptureBuffer(writer, 1, 1024); output.Block = true;
    capture.TryWrite(Frame(0)); Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "disk stalled");
    try
    {
        Check(capture.TryWrite(Frame(1)) && capture.TryWrite(Frame(2)), "writer and overflow each retain a record");
        Check(!capture.TryWrite(Frame(3)) && capture.Error is IOException && capture.PendingCount == 1, "backlog cannot grow without bound");
        var done = capture.CompleteAsync(); output.Release.Set(); Check(done.Wait(TimeSpan.FromSeconds(10)), "accepted prefix finishes despite overflow error");
        var file = Path.Combine(dir, "bounded.lcr"); File.WriteAllBytes(file, output.Bytes);
        Check(ReplayReader.Read(file).Frames.Count == 3 && capture.PendingBytes == 0, "accepted records preserved and references released");
    }
    finally { output.Release.Set(); }
});

static void OptionalIndexFailure() => WithTemp(dir =>
{
    var file = Path.Combine(dir, "optional.lcr"); File.WriteAllText(Path.ChangeExtension(file, ".lci"), "unavailable sidecar");
    using var writer = new ReplayWriter(file, Header(), indexed: true);
    Check(writer.IndexError is IOException && writer.Error == null, "sidecar failure is isolated");
    Check(writer.TryWrite(Frame(1)), "recording continues"); writer.Dispose();
    Check(writer.Error == null && ReplayReader.Read(file).IsComplete, "primary recording completes");
    var index = ReplayReader.IndexSingleFile(file);
    Check(index.Duration == 1 && ReplayReader.ReadWindow(index.Windows[0]).Frames.Count == 1, "missing sidecar rebuilt for playback");
});

static void StorageLowWatermarks()
{
    var output = new ControlledRecordingStream(); using var writer = new ReplayWriter(output, Header(), 64);
    var capture = new ReplayCaptureBuffer(writer); output.StepRecords = true;
    capture.TryWrite(Frame(0)); Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "first record stalled");
    try
    {
        for (var i = 1; i <= 48; i++) Check(capture.TryWrite(Frame(i)), "record below hard cap admitted");
        Check(capture.ShouldPauseCapture && capture.PendingCount == 0 && writer.Error == null, "capture pauses before overflowing the writer");
        output.RecordPermits.Release(16);
        Check(SpinWait.SpinUntil(() => writer.QueuedCount == 32, TimeSpan.FromSeconds(5)), "disk partially catches up");
        Check(capture.ShouldPauseCapture, "partial catch-up cannot trigger another capture burst");
        output.StepRecords = false; output.RecordPermits.Release(64);
        Check(SpinWait.SpinUntil(() => writer.QueuedBytes == 0, TimeSpan.FromSeconds(5)), "writer drains");
        Check(!capture.ShouldPauseCapture, "capture resumes below low watermark");
    }
    finally { output.StepRecords = false; output.RecordPermits.Release(128); }
}

static void StorageDrainBudget()
{
    var output = new ControlledRecordingStream(); using var writer = new ReplayWriter(output, Header(), 128);
    var capture = new ReplayCaptureBuffer(writer); output.Block = true;
    capture.TryWrite(Frame(0)); Check(output.Entered.Wait(TimeSpan.FromSeconds(5)), "worker stalled");
    try
    {
        for (var i = 1; i <= 170; i++) Check(capture.TryWrite(Frame(i)), "burst retained");
        output.Release.Set();
        Check(SpinWait.SpinUntil(() => writer.QueuedBytes == 0, TimeSpan.FromSeconds(5)), "writer has capacity");
        var before = capture.PendingCount; Check(before > 16, "real overflow present");
        capture.Drain(); Check(before - capture.PendingCount <= 16, "one tick cannot flush entire burst");
        before = capture.PendingCount; capture.Drain(512, 0); Check(before - capture.PendingCount <= 1, "elapsed-time gate bounds remaining work");
        Check(capture.CompleteAsync().Wait(TimeSpan.FromSeconds(10)), "remaining accepted records finish asynchronously");
    }
    finally { output.Release.Set(); }
}

static void StreamingRecordWrite()
{
    var record = new ReplayRecord { Kind = "world", World = new WorldSnapshot { Geometry = new() {
        new GeometrySnapshot { Id = "large", Vertices = Enumerable.Repeat(.12345f, 240000).ToArray(), Triangles = new[] { 0, 1, 2 } } } } };
    var settings = new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore };
    long Legacy()
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        var raw = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(record, settings));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true)) gzip.Write(raw);
        var copy = buffer.ToArray();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
    ReplayFormat.Write(Stream.Null, record, new ReplayReadLimits()); Legacy();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var expanded = ReplayFormat.Write(Stream.Null, record, new ReplayReadLimits());
    var streamed = GC.GetAllocatedBytesForCurrentThread() - before; var legacy = Legacy();
    Check(streamed < legacy * .75, $"streaming allocations lower: {streamed}/{legacy}");
    Check(expanded == Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.SerializeObject(record, settings)), "same expanded payload size");
    using var rejected = new MemoryStream();
    Expect<InvalidDataException>(() => ReplayFormat.Write(rejected, record, new ReplayReadLimits { MaxUncompressedRecordBytes = 1024 }));
    Check(rejected.Length == 0, "oversized record cannot append a corrupt length header");
    Console.WriteLine($"MEASURE streaming write allocated {streamed:N0} bytes; legacy {legacy:N0} bytes for {expanded:N0} expanded bytes");
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

static void PlaybackSampler()
{
    var first = Frame(0, Entity("worker", 0)).Frame!;
    var last = Frame(1, Entity("worker", 10)).Frame!;
    first.State["quota"] = "130"; last.State["quota"] = "0";
    first.Entities[0].State["held"] = "flashlight";
    last.Entities[0].State["held"] = "shovel";
    first.Entities[0].Bones.Add(new BonePose { Path = "head", Position = new Vec3(0, 1, 0) });
    last.Entities[0].Bones.Add(new BonePose { Path = "head", Position = new Vec3(0, 3, 0) });
    first.Entities[0].Renderers.Add(new RenderPose { Id = "suit", Position = new Vec3(0, 0, 0) });
    last.Entities[0].Renderers.Add(new RenderPose { Id = "suit", Position = new Vec3(10, 0, 0) });
    first.SceneRenderers.Add(new RenderPose { Id = "door", Position = new Vec3(0, 0, 0) });
    last.SceneRenderers.Add(new RenderPose { Id = "door", Position = new Vec3(2, 0, 0) });
    first.Anchors.Add(new AnchorPose { Id = "ship", Position = new Vec3(0, 0, 0) });
    last.Anchors.Add(new AnchorPose { Id = "ship", Position = new Vec3(0, -20, 0) });
    var session = new ReplaySession { Duration = 1, Frames = new() { first, last } };
    var sampler = new ReplayTimelineSampler();
    foreach (var time in new[] { .5, .9, 0, .75, .25, 1, .5 })
    {
        var expected = ReplayTimeline.Sample(session, time);
        var actual = sampler.Sample(session, time);
        Near(actual.Entities[0].Position.X, expected.Entities[0].Position.X);
        Near(actual.Entities[0].Bones[0].Position.Y, expected.Entities[0].Bones[0].Position.Y);
        Near(actual.Entities[0].Renderers[0].Position.X, expected.Entities[0].Renderers[0].Position.X);
        Near(actual.SceneRenderers[0].Position.X, expected.SceneRenderers[0].Position.X);
        Near(actual.Anchors[0].Position.Y, expected.Anchors[0].Position.Y);
        Check(actual.State["quota"] == expected.State["quota"] &&
            actual.Entities[0].State["held"] == expected.Entities[0].State["held"], "discrete state matches");
    }
    var held = sampler.Sample(session, .25);
    sampler.Sample(session, .75);
    Near(held.Entities[0].Position.X, 2.5);
    Check(held.Entities[0].State["held"] == "flashlight", "later samples do not replace earlier poses");
}

static void PlayerDiscontinuities()
{
    var before = Entity("player", 0); before.State["isPlayerDead"] = "False";
    var teleported = Entity("player", 120); teleported.State["isPlayerDead"] = "False";
    var session = new ReplaySession { Duration = .2,
        Frames = new() { Frame(0, before).Frame!, Frame(.2, teleported).Frame! } };
    Near(ReplayTimeline.Sample(session, .1).Entities[0].Position.X, 0);
    Near(ReplayTimeline.Sample(session, .2).Entities[0].Position.X, 120);
    var falling = Entity("player", 0); falling.State["isPlayerDead"] = "False";
    falling.Position = new Vec3(0, -4, 0);
    session.Frames[1].Entities[0] = falling;
    Near(ReplayTimeline.Sample(session, .1).Entities[0].Position.Y, -2);
    var dead = Entity("player", 1000); dead.State["isPlayerDead"] = "True";
    session.Frames[1].Entities[0] = dead;
    Near(ReplayTimeline.Sample(session, .1).Entities[0].Position.X, 0);
    var afterGap = Entity("player", 5); afterGap.State["isPlayerDead"] = "False";
    session.Frames[1] = Frame(1, afterGap).Frame!;
    session.Duration = 1;
    session.Events.Add(new ReplayEvent { Time = 1, Category = "capture", Name = "sample-gap" });
    Near(ReplayTimeline.Sample(session, .5).Entities[0].Position.X, 0);
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

static void PlayerViewRotation()
{
    WithTemp(dir =>
    {
        var left = Entity("player", 0); left.Kind = "player";
        var right = Entity("player", 0); right.Kind = "player";
        left.ViewRotation = Quat.Identity;
        right.ViewRotation = new Quat(0, 1, 0, 0);
        left.ViewPosition = new Vec3(0, 1.7f, 0);
        right.ViewPosition = new Vec3(10, 1.3f, 0);
        var path = Path.Combine(dir, "view.lcr");
        using (var writer = new ReplayWriter(path, Header()))
        {
            Check(writer.TryWrite(Frame(0, left)), "first view accepted");
            Check(writer.TryWrite(Frame(1, right)), "second view accepted");
            writer.Dispose(); Check(writer.Error == null, "player view writes");
        }
        var recording = ReplayReader.Read(path);
        var sample = ReplayTimeline.Sample(recording, .5).Entities[0];
        var view = sample.ViewRotation;
        Check(view.HasValue, "view available after indexed read");
        Near(view!.Value.Y, Math.Sqrt(.5)); Near(view.Value.W, Math.Sqrt(.5));
        Check(sample.ViewPosition.HasValue, "view position available after indexed read");
        Near(sample.ViewPosition!.Value.X, 5); Near(sample.ViewPosition.Value.Y, 1.5);
        var old = Entity("player", 0); old.Kind = "player";
        Check(!ReplayTimeline.Sample(new ReplaySession { Frames = new() { Frame(0, old).Frame! } }, 0)
            .Entities[0].ViewRotation.HasValue, "legacy recording keeps body-facing fallback");
    });
}

static void HeldItemAttachment()
{
    var firstPlayer = Entity("p", 0); firstPlayer.Kind = "player";
    var secondPlayer = Entity("p", 10); secondPlayer.Kind = "player";
    secondPlayer.Rotation = new Quat(0, (float)Math.Sqrt(.5), 0, (float)Math.Sqrt(.5));
    var firstItem = Entity("scrap", 1); firstItem.Kind = "item";
    firstItem.Position = new Vec3(1, 1, 0);
    firstItem.State["isHeld"] = "True"; firstItem.State["$heldBy"] = "p";
    var secondItem = Entity("scrap", 10); secondItem.Kind = "item";
    secondItem.Position = new Vec3(10, 1, -1);
    secondItem.State["isHeld"] = "True"; secondItem.State["$heldBy"] = "p";
    var session = new ReplaySession { Frames = new()
    { Frame(0, firstPlayer, firstItem).Frame!, Frame(1, secondPlayer, secondItem).Frame! } };
    var middle = ReplayTimeline.Sample(session, .5).Entities.Single(entity => entity.Id == "scrap");
    Near(middle.Position.X, 5 + Math.Sqrt(.5));
    Near(middle.Position.Y, 1);
    Near(middle.Position.Z, -Math.Sqrt(.5));
    firstItem.State.Remove("$heldBy"); secondItem.State.Remove("$heldBy");
    var legacy = ReplayTimeline.Sample(session, .5).Entities.Single(entity => entity.Id == "scrap");
    Near(legacy.Position.X, middle.Position.X); Near(legacy.Position.Z, middle.Position.Z);
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

static void MovingSceneRenderers()
{
    WithTemp(dir =>
    {
        var file = Path.Combine(dir, "furniture.lcr");
        var shelf = new GeometrySnapshot { Id = "shelf", IsInterior = true, IsMovingSceneRenderer = true,
            Active = false, Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 } };
        var first = Frame(0).Frame!;
        first.SceneRenderers.Add(new RenderPose { Id = "shelf", Active = true, Position = new Vec3(0, 0, 0) });
        var second = Frame(1).Frame!;
        second.SceneRenderers.Add(new RenderPose { Id = "shelf", Active = true, Position = new Vec3(4, 0, 0) });
        var hidden = Frame(2).Frame!;
        hidden.SceneRenderers.Add(new RenderPose { Id = "shelf", Active = false, Position = new Vec3(8, 0, 0) });
        using (var writer = new ReplayWriter(file, Header()))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = new WorldSnapshot { Layer = "interior", CaptureSetId = "furniture", Geometry = new() { shelf } } }), "world accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = first.Time, Frame = first }), "first pose accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = second.Time, Frame = second }), "second pose accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "frame", Time = hidden.Time, Frame = hidden }), "hidden pose accepted");
            writer.Dispose(); Check(writer.Error == null, "moving furniture writes: " + writer.Error);
        }
        var session = ReplayReader.Read(file);
        Check(!session.Worlds[0].World!.Geometry[0].Active && session.Worlds[0].World!.Geometry[0].IsMovingSceneRenderer, "hidden baseline survives");
        Near(ReplayTimeline.Sample(session, .5).SceneRenderers[0].Position.X, 2);
        Check(ReplayTimeline.Sample(session, 1.5).SceneRenderers[0].Active, "visibility holds before boundary");
        Check(!ReplayTimeline.Sample(session, 2).SceneRenderers[0].Active, "visibility changes at boundary");
        first.SceneRenderers.Add(new RenderPose { Id = "shelf" });
        Raw(file, new ReplayRecord { Kind = "header", Header = Header() }, new ReplayRecord { Kind = "frame", Frame = first });
        Expect<InvalidDataException>(() => ReplayReader.Read(file));
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
                Check(writer.WriteAsync(new ReplayRecord { Kind = "event", Time = time, Event = new ReplayEvent { Time = time, Name = i.ToString() } }).GetAwaiter().GetResult(), "event queued");
                Check(writer.WriteAsync(new ReplayRecord { Kind = "world", Time = time, World = new WorldSnapshot { Scene = i.ToString() } }).GetAwaiter().GetResult(), "world queued");
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
        source.Textures[0].Linear = true;
        source.Textures[0].FilterMode = 0;
        source.Geometry[0].RigBones = new() { new BonePose { Path = "hip[0]", Position = new Vec3(0, 1, 0) },
            new BonePose { Path = "hip[0]/head[0]", Position = new Vec3(0, 1.5f, 0) } };
        source.Geometry[0].AnimatorPath = "hip[0]";
        source.Geometry[0].AnimatorController = "PlayerBody";
        source.Geometry[0].AnimatorAvatar = "PlayerAvatar";
        source.Geometry[0].AttachedBonePath = "hip[0]/head[0]";
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
        Check(texture.Linear && !new TextureSnapshot().Linear, "linear data map survives with legacy sRGB default");
        Check(texture.FilterMode == 0 && new TextureSnapshot().FilterMode == -1,
            "point filtered map survives while legacy recordings retain replay filtering");
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
        Check(mesh.RigBones.Count == 2 && mesh.RigBones[1].Path == "hip[0]/head[0]" &&
            mesh.AnimatorPath == "hip[0]" && mesh.AnimatorController == "PlayerBody" &&
            mesh.AnimatorAvatar == "PlayerAvatar", "one-time rig pose and local animator asset keys survive");
        Check(mesh.AttachedBonePath == "hip[0]/head[0]", "rigid detail bone parent survives");
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
        Reject("invalid filter mode", world => world.Textures[0].FilterMode = 3);
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
        Reject("duplicate rig bone", world => world.Geometry[0].RigBones = new() { new BonePose { Path = "hip[0]" },
            new BonePose { Path = "hip[0]" } });
        Reject("non-finite rig pose", world => world.Geometry[0].RigBones = new() { new BonePose
            { Path = "hip[0]", Position = new Vec3(float.NaN, 0, 0) } });
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

static void InitialMapPreload()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "initial-map.lcr");
        var firstFrame = Frame(0, Entity("player", 0));
        firstFrame.Frame!.State["padding"] = new string('x', 2048);
        ReplayRecord World(double time, string set, string layer) => new()
        { Kind = "world", Time = time, World = new WorldSnapshot { CaptureSetId = set, Layer = layer } };
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() },
            World(0, "first", "exterior"), firstFrame,
            World(1, "first", "exterior"), Frame(1, Entity("player", 1)),
            World(2, "first", "exterior"), Frame(2, Entity("player", 2)),
            World(3, "first", "interior"), Frame(3, Entity("player", 3)),
            World(4, "first", "exterior"), Frame(4, Entity("player", 4)),
            World(5, "second", "exterior"), Frame(5, Entity("player", 5)),
            new ReplayRecord { Kind = "event", Time = 5,
                Event = new ReplayEvent { Time = 5, Category = "capture", Name = "future" } },
            new ReplayRecord { Kind = "end", Time = 6 });
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(index.Windows.Count > 1 && index.Windows[0].End == 1, "first map extends beyond startup window");
        var ordinary = ReplayReader.ReadWindow(index.Windows[0]);
        Check(ordinary.Worlds.Count == 1, "ordinary first window keeps its original world selection");
        var preloaded = ReplayReader.ReadWindow(index.Windows[0], preloadInitialMap: true);
        Check(preloaded.Worlds.Select(record => (record.Time, record.World!.CaptureSetId, record.World.Layer))
            .SequenceEqual(new[] { (0d, "first", "exterior"), (1d, "first", "exterior"),
                (2d, "first", "exterior"), (3d, "first", "interior") }),
            "initial map contains each exterior and the first interior exactly once, at its recorded time");
        Check(preloaded.Frames.Count == ordinary.Frames.Count &&
            preloaded.Frames.Select(frame => frame.Time).SequenceEqual(ordinary.Frames.Select(frame => frame.Time)) &&
            preloaded.Events.Count == ordinary.Events.Count,
            "preloading does not import later frames or events");

        var shipPath = Path.Combine(dir, "ship-then-facility.lcr");
        var shipFrame = Frame(0, Entity("player", 0));
        shipFrame.Frame!.State["padding"] = new string('x', 2048);
        Raw(shipPath, new ReplayRecord { Kind = "header", Header = Header() },
            World(0, "ship", "exterior"), shipFrame,
            World(1, "facility", "exterior"), Frame(1, Entity("player", 1)),
            new ReplayRecord { Kind = "event", Time = 1,
                Event = new ReplayEvent { Time = 1, Category = "capture", Name = "facility-started" } },
            World(2, "facility", "exterior"), Frame(2, Entity("player", 2)),
            World(3, "facility", "interior"), Frame(3, Entity("player", 3)),
            World(4, "later", "exterior"), Frame(4, Entity("player", 4)),
            new ReplayRecord { Kind = "end", Time = 5 });
        var shipIndex = ReplayReader.IndexSingleFile(shipPath, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(shipIndex.Windows[0].End == 1, "facility map and interior are outside the ship startup window");
        var shipOrdinary = ReplayReader.ReadWindow(shipIndex.Windows[0]);
        var shipPreloaded = ReplayReader.ReadWindow(shipIndex.Windows[0], preloadInitialMap: true);
        Check(shipPreloaded.Worlds.Select(record => (record.Time, record.World!.CaptureSetId, record.World.Layer))
            .SequenceEqual(new[] { (0d, "ship", "exterior"), (1d, "facility", "exterior"),
                (2d, "facility", "exterior"), (3d, "facility", "interior") }),
            "ship world keeps its time and the first complete facility set is ready before playback");
        Check(shipPreloaded.Frames.Select(frame => frame.Time).SequenceEqual(shipOrdinary.Frames.Select(frame => frame.Time)) &&
            shipPreloaded.Events.Select(evt => evt.Time).SequenceEqual(shipOrdinary.Events.Select(evt => evt.Time)) &&
            shipPreloaded.Duration == shipOrdinary.Duration,
            "ship and facility preload leaves the active frame/event window unchanged");

        var completeShipPath = Path.Combine(dir, "complete-ship-then-facility.lcr");
        var completeShipFrame = Frame(0, Entity("player", 0));
        completeShipFrame.Frame!.State["padding"] = new string('x', 2048);
        Raw(completeShipPath, new ReplayRecord { Kind = "header", Header = Header() },
            World(0, "ship", "exterior"), World(0, "ship", "interior"), completeShipFrame,
            World(1, "facility", "exterior"), Frame(1, Entity("player", 1)),
            new ReplayRecord { Kind = "event", Time = 1,
                Event = new ReplayEvent { Time = 1, Category = "capture", Name = "facility-started" } },
            World(2, "facility", "interior"), Frame(2, Entity("player", 2)),
            World(3, "later", "exterior"), Frame(3, Entity("player", 3)),
            World(4, "later", "interior"), Frame(4, Entity("player", 4)),
            new ReplayRecord { Kind = "end", Time = 5 });
        var completeShipIndex = ReplayReader.IndexSingleFile(completeShipPath, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(completeShipIndex.Windows[0].End == 1,
            "ship interior is in the startup window while facility interior is beyond it");
        var completeShipOrdinary = ReplayReader.ReadWindow(completeShipIndex.Windows[0]);
        var completeShipPreloaded = ReplayReader.ReadWindow(completeShipIndex.Windows[0], preloadInitialMap: true);
        Check(completeShipPreloaded.Worlds.Select(record => (record.Time, record.World!.CaptureSetId, record.World.Layer))
            .SequenceEqual(new[] { (0d, "ship", "exterior"), (0d, "ship", "interior"),
                (1d, "facility", "exterior"), (2d, "facility", "interior") }),
            "a complete ship interior does not prevent preloading the next facility set");
        Check(completeShipPreloaded.Frames.Select(frame => frame.Time)
                .SequenceEqual(completeShipOrdinary.Frames.Select(frame => frame.Time)) &&
            completeShipPreloaded.Events.Select(evt => evt.Time)
                .SequenceEqual(completeShipOrdinary.Events.Select(evt => evt.Time)),
            "a complete ship preload leaves later frame and event windows deferred");

        var landedPath = Path.Combine(dir, "complete-first-set-with-carried-state.lcr");
        var landedFrame = Frame(0, Entity("player", 0));
        landedFrame.Frame!.State["padding"] = new string('x', 2048);
        landedFrame.Frame.State["StartOfRound.inShipPhase"] = "False";
        Raw(landedPath, new ReplayRecord { Kind = "header", Header = Header() },
            World(0, "facility", "exterior"), World(0, "facility", "interior"), landedFrame,
            World(1, "later", "exterior"), Frame(1, Entity("player", 1)),
            World(2, "later", "interior"), Frame(2, Entity("player", 2)),
            new ReplayRecord { Kind = "end", Time = 3 });
        var landedIndex = ReplayReader.IndexSingleFile(landedPath, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(ReplayReader.ReadWindow(landedIndex.Windows[0], preloadInitialMap: true).Worlds.Count == 4,
            "frame state alone cannot rule out a carried ship world before the next complete map");
        var later = ReplayReader.ReadWindow(index.Windows[1], preloadInitialMap: true);
        var laterOrdinary = ReplayReader.ReadWindow(index.Windows[1]);
        Check(later.Worlds.Count == laterOrdinary.Worlds.Count,
            "preload option does not expand later playback windows");

        var broad = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 48L * 1024 * 1024,
            deferPayloadValidation: true);
        var broadOrdinary = ReplayReader.ReadWindow(broad.Windows[0]);
        var broadPreloaded = ReplayReader.ReadWindow(broad.Windows[0], preloadInitialMap: true);
        Check(broadPreloaded.Worlds.Count == broadOrdinary.Worlds.Count &&
            broadPreloaded.Worlds.Select(record => record.Time).SequenceEqual(broadOrdinary.Worlds.Select(record => record.Time)),
            "an interior already in the first window is not decoded twice");

        var invalidPath = Path.Combine(dir, "invalid-initial-map.lcr");
        var padded = Frame(0, Entity("player", 0));
        padded.Frame!.State["padding"] = new string('x', 2048);
        var invalidInterior = new WorldSnapshot { CaptureSetId = "first", Layer = "interior",
            Rooms = new() { new RoomSnapshot { Id = "bad" } } };
        Raw(invalidPath, new ReplayRecord { Kind = "header", Header = Header() },
            World(0, "first", "exterior"), padded,
            new ReplayRecord { Kind = "world", Time = 1, World = invalidInterior },
            Frame(1, Entity("player", 1)), new ReplayRecord { Kind = "end", Time = 2 });
        var invalidIndex = ReplayReader.IndexSingleFile(invalidPath, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(ReplayReader.ReadWindow(invalidIndex.Windows[0]).Worlds.Count == 1,
            "a future malformed world remains deferred without preloading");
        Expect<InvalidDataException>(() => ReplayReader.ReadWindow(invalidIndex.Windows[0], preloadInitialMap: true));

        var noInteriorPath = Path.Combine(dir, "no-initial-interior.lcr");
        var firstOnly = Frame(0, Entity("player", 0));
        firstOnly.Frame!.State["padding"] = new string('x', 2048);
        Raw(noInteriorPath, new ReplayRecord { Kind = "header", Header = Header() },
            World(0, "first", "exterior"), firstOnly,
            World(1, "first", "exterior"), Frame(1, Entity("player", 1)),
            World(2, "second", "exterior"), Frame(2, Entity("player", 2)),
            new ReplayRecord { Kind = "end", Time = 3 });
        var noInteriorIndex = ReplayReader.IndexSingleFile(noInteriorPath, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(ReplayReader.ReadWindow(noInteriorIndex.Windows[0], preloadInitialMap: true).Worlds
            .Select(record => record.World!.CaptureSetId).SequenceEqual(new[] { "first" }),
            "incomplete future capture sets are not imported into the startup window");

        var boundedPath = Path.Combine(dir, "bounded-initial-map.lcr");
        var boundedFrame = Frame(0, Entity("player", 0));
        boundedFrame.Frame!.State["padding"] = new string('x', 2048);
        var boundedRecords = new List<ReplayRecord> { new() { Kind = "header", Header = Header() },
            World(0, "first", "exterior"), boundedFrame };
        for (var time = 1; time <= 10; time++)
        { boundedRecords.Add(World(time, "first", time == 10 ? "interior" : "exterior"));
            boundedRecords.Add(Frame(time, Entity("player", time))); }
        boundedRecords.Add(new ReplayRecord { Kind = "end", Time = 11 });
        Raw(boundedPath, boundedRecords.ToArray());
        var boundedIndex = ReplayReader.IndexSingleFile(boundedPath, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(ReplayReader.ReadWindow(boundedIndex.Windows[0], preloadInitialMap: true).Worlds.Count == 1,
            "initial map preloading does not expose an unfinished set past its eight-record budget");
    });
}

static void LateDoorPoseReference()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "late-door.lcr");
        var first = Frame(0, Entity("player", 0));
        first.Frame!.State["padding"] = new string('x', 2048);
        var closed = Entity("door", 0); closed.Kind = "door";
        var opened = Entity("door", 1); opened.Kind = "door";
        var captured = Entity("door", 2); captured.Kind = "door";
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() },
            new ReplayRecord { Kind = "world", Time = 0,
                World = new WorldSnapshot { CaptureSetId = "ship", Layer = "exterior" } },
            first, Frame(1, closed),
            new ReplayRecord { Kind = "world", Time = 2,
                World = new WorldSnapshot { CaptureSetId = "moon", Layer = "exterior" } },
            Frame(2, opened),
            new ReplayRecord { Kind = "world", Time = 2.5,
                World = new WorldSnapshot { CaptureSetId = "moon", Layer = "interior", CaptureCompletedAt = 3,
                    Geometry = new() { new GeometrySnapshot { Id = "mesh", Name = "DoorMesh",
                        IsInterior = true, IsMovingSceneRenderer = true,
                        Vertices = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, Triangles = new[] { 0, 1, 2 } } } } },
            Frame(3, captured), new ReplayRecord { Kind = "end", Time = 4 });
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048,
            initialWindowExpandedBytes: 1024, deferPayloadValidation: true);
        var ordinary = ReplayReader.ReadWindow(index.Windows[0]);
        var preloaded = ReplayReader.ReadWindow(index.Windows[0], preloadInitialMap: true);
        Check(preloaded.DoorPoseReferences.TryGetValue("moon", out var doors) &&
            doors.Count == 1 && doors[0].Position.X == 2,
            "door reference uses the frame near the late interior capture");
        Check(preloaded.Worlds.Single(record => record.World?.Layer == "interior").SourceTime == 2.5,
            "the original world-capture time remains available after window normalization");
        Check(preloaded.Frames.Select(frame => frame.Time).SequenceEqual(ordinary.Frames.Select(frame => frame.Time)),
            "future door reference does not add motion frames to the opening window");
        Check(ReplayReader.Read(path).DoorPoseReferences["moon"].Single().Position.X == 2,
            "whole-file playback also retains the door pose at interior capture");
    });
}

static void DelayedLandingMap()
{
    ReplayRecord World(double time, string set, string layer, WorldSnapshot? snapshot = null) => new()
    {
        Kind = "world", Time = time, World = snapshot ?? new WorldSnapshot { CaptureSetId = set, Layer = layer }
    };
    var ship = new WorldSnapshot { CaptureSetId = "ship", Layer = "exterior",
        Geometry = new() { new GeometrySnapshot { Id = "cabin", Name = "ShipInside", AnchorId = "ship-elevator" } } };
    var exterior = new WorldSnapshot { CaptureSetId = "moon", Layer = "exterior",
        AssetScene = "Level2Assurance", AssetRendererPaths = new() { "Terrain" } };
    var interior = new WorldSnapshot { CaptureSetId = "moon", Layer = "interior",
        Rooms = new() { new RoomSnapshot { Id = "room" } } };
    var session = new ReplaySession
    {
        Worlds = new() { World(0, "ship", "exterior", ship), World(8.1, "ship", "interior"),
            World(32.49, "moon", "exterior", exterior), World(46.69, "moon", "interior", interior),
            World(60, "moon", "exterior") },
        Frames = new() { new ReplayFrame { Time = 0, State = new() { ["StartOfRound.inShipPhase"] = "True" } },
            new ReplayFrame { Time = 17.81, State = new() { ["StartOfRound.inShipPhase"] = "False" } } }
    };
    Check(ReplayMapTiming.TryFindFirstLandingMap(session, out var set, out var landing) &&
        set == "moon" && landing == 17.81, "ship-only opening and delayed moon set identify landing");
    ReplayMapTiming.AlignInitialCapture(session, set, landing);
    Check(session.Worlds.Select(record => record.Time).SequenceEqual(new[] { 0d, 8.1, 17.81, 17.81, 60d }),
        "first exterior and interior move to landing but later environment update retains its time");
    Check(session.Worlds[2].World == exterior && session.Worlds[3].World == interior &&
        session.Frames[1].Time == 17.81, "world payloads and frame timestamps stay unchanged");
    var completeOpening = new ReplaySession
    {
        Worlds = new() { World(0, "ship", "exterior", ship),
            World(0, "ship", "interior", new WorldSnapshot { CaptureSetId = "ship", Layer = "interior",
                Rooms = new() { new RoomSnapshot { Id = "ship-room" } } }),
            World(32.49, "moon", "exterior", exterior) },
        Frames = session.Frames
    };
    Check(!ReplayMapTiming.TryFindFirstLandingMap(completeOpening, out _, out _),
        "an already complete opening map is not retimed");
    var updateOnly = new ReplaySession { Worlds = new() { World(60, "moon", "exterior") } };
    ReplayMapTiming.AlignInitialCapture(updateOnly, "moon", 0);
    Check(updateOnly.Worlds[0].Time == 60,
        "a window with only a later world update is not mistaken for the initial map");
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
            if (i == 4 || i == 15) records.Add(new ReplayRecord { Kind = "event", Time = i,
                Event = new ReplayEvent { Time = i, Category = "visual", Name = i == 4 ? "renderer" : "spray",
                    EntityId = i == 4 ? "g42" : "0", Data = new Dictionary<string, string>
                    { ["set"] = i == 4 ? "first" : "second", ["visible"] = i == 15 ? "true" : "false" } } });
            if (i == 6) records.Add(new ReplayRecord { Kind = "event", Time = i,
                Event = new ReplayEvent { Time = i, Category = "animation", Name = "state", EntityId = "player",
                    Data = new Dictionary<string, string> { ["layer"] = "0", ["hash"] = "123",
                        ["normalizedTime"] = "0.25", ["duration"] = "1", ["speed"] = "1" } } });
            if (i == 19) records.Add(new ReplayRecord { Kind = "event", Time = i,
                Event = new ReplayEvent { Time = i, Name = "marker" } });
            if (i == 19) records.Add(new ReplayRecord { Kind = "event", Time = i,
                Event = new ReplayEvent { Time = i, Name = "marker-after" } });
        }
        records.Add(new ReplayRecord { Kind = "end", Time = 31 });
        Raw(path, records.ToArray());
        var indexProgress = new List<double>();
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048, progress: indexProgress.Add);
        Check(File.Exists(Path.ChangeExtension(path, ".lci")),
            "a complete legacy day gains a reusable sidecar after its first scan");
        File.WriteAllBytes(Path.ChangeExtension(path, ".lci"), new byte[] { 1, 2, 3 });
        var repaired = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048);
        Check(repaired.Windows.Count == index.Windows.Count &&
            new FileInfo(Path.ChangeExtension(path, ".lci")).Length > 8,
            "a corrupt sidecar is rebuilt from the complete day file");
        var fastPath = Path.Combine(dir, "fast-index.lcr");
        File.Copy(path, fastPath);
        var fast = ReplayReader.IndexSingleFile(fastPath, windowExpandedBytes: 2048, deferPayloadValidation: true);
        Check(fast.Duration == index.Duration && fast.FrameCount == index.FrameCount && fast.EventCount == index.EventCount &&
            fast.WorldCount == index.WorldCount && fast.Windows.Select(window => window.Start).SequenceEqual(index.Windows.Select(window => window.Start)),
            "metadata-only index retains every record and window boundary");
        for (var number = 0; number < index.Windows.Count; number++)
        {
            var expected = ReplayReader.ReadWindow(index.Windows[number]);
            var actual = ReplayReader.ReadWindow(fast.Windows[number]);
            Check(JsonConvert.SerializeObject(actual) == JsonConvert.SerializeObject(expected),
                "metadata-only windows preserve frames, worlds, carry state and equal-time event order");
        }
        Check(indexProgress.Count > 0 && indexProgress[^1] == 1 && indexProgress.All(value => value >= 0 && value <= 1),
            "index progress reaches completion within bounds");
        Check(index.WorldRevisionAt(0) == index.WorldRevisionAt(11) &&
            index.WorldRevisionAt(12) != index.WorldRevisionAt(11),
            "world revision changes only when the recorded scene changes");
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
        Check(indexed.IsComplete && indexed.Duration == index.Duration && indexed.FrameCount == index.FrameCount &&
            indexed.EventCount == index.EventCount && indexed.WorldCount == index.WorldCount,
            "sidecar and recovered scans agree on recorded content");
        var startup = ReplayReader.IndexSingleFile(indexedPath, windowExpandedBytes: 8192, initialWindowExpandedBytes: 1024);
        var regular = ReplayReader.IndexSingleFile(indexedPath, windowExpandedBytes: 8192);
        Check(startup.Windows[0].Duration < regular.Windows[0].Duration && startup.Windows[^1].End == regular.Duration,
            "small startup window opens promptly without shortening the recording");
        var startupFrames = startup.Windows.SelectMany(window => ReplayReader.ReadWindow(window).Frames
            .Select(frame => frame.Time + window.Start)).Distinct().OrderBy(time => time).ToArray();
        Check(startupFrames.SequenceEqual(Enumerable.Range(0, 32).Select(time => (double)time)),
            "startup and regular windows retain every frame across the first boundary");
        Expect<ArgumentOutOfRangeException>(() => ReplayReader.IndexSingleFile(indexedPath, initialWindowExpandedBytes: -1));
        foreach (var window in indexed.Windows.Where(window => window.Start > 15))
        {
            var carried = ReplayReader.ReadWindow(window).Events;
            Check(carried.Any(item => item.Category == "visual" && item.Name == "spray"),
                "writer sidecar carries earlier spray state into later windows");
            Check(carried.All(item => item.Category != "visual" || item.Name != "renderer"),
                "writer sidecar omits visual state from replaced capture sets");
            Check(carried.Any(item => item.Category == "animation" && item.Name == "state" && item.Time < 0),
                "writer sidecar carries animation phase and original event age into later windows");
        }
        Check(index.IsComplete && index.Windows.Count >= 3 && index.Duration == 31, "one day indexes into playback windows");
        var timeline = new ReplayRecordingTimeline(index.Windows.Select(window => new ReplayRecordingPart
            { FilePath = path, Duration = window.Duration, Window = window }));
        Near(timeline.Duration, 31);
        Check(timeline.Parts.All(part => part.FilePath == path), "virtual playback windows share one physical file");
        foreach (var window in index.Windows)
        {
            var readProgress = new List<double>();
            var session = ReplayReader.ReadWindow(window, progress: readProgress.Add);
            Check(readProgress.Count > 0 && readProgress[^1] == 1 && readProgress.All(value => value >= 0 && value <= 1),
                "window read progress reaches completion within bounds");
            Check(session.Frames.Count > 0 && session.Worlds.Count > 0, "each window has frames and a carried world");
            Check(session.Worlds[0].Time == 0, "carried world is available at the window start");
            Check(ReplayTimeline.Sample(session, 0).Entities.Single().Id == "player", "boundary frame is playable");
            if (window.Start > 12) Check(session.Worlds[0].World!.CaptureSetId == "second", "latest world replaces earlier set");
            if (window.Start > 4 && window.Start < 12)
                Check(session.Events.Any(item => item.Category == "visual" && item.Name == "renderer" && item.Time <= 0),
                    "hidden static geometry is carried into later playback windows");
            if (window.Start > 15)
            {
                Check(session.Events.Any(item => item.Category == "visual" && item.Name == "spray" && item.Time <= 0),
                    "spray decal state is carried into later playback windows");
                Check(session.Events.All(item => item.Category != "visual" || item.Name != "renderer"),
                    "reindexed window omits obsolete visual state");
            }
        }
        Check(index.Windows.Select(window => ReplayReader.ReadWindow(window)).SelectMany(session => session.Events).Any(item => item.Name == "marker"),
            "events survive the virtual window boundary");
        Check(index.Windows.Select(window => ReplayReader.ReadWindow(window)).SelectMany(session => session.Events)
            .Where(item => item.Name.StartsWith("marker", StringComparison.Ordinal)).Select(item => item.Name)
            .SequenceEqual(new[] { "marker", "marker-after" }),
            "parallel decoding preserves physical order for equal-time events");
        var independent = ReplayReader.ReadWindow(index.Windows[0]);
        Check(!ReferenceEquals(independent.Worlds[0].World, ReplayReader.ReadWindow(index.Windows[0]).Worlds[0].World),
            "normal reader callers retain independent mutable worlds");
        index.ReuseWorldPayloads = true;
        var firstShared = ReplayReader.ReadWindow(index.Windows[0]);
        var laterShared = ReplayReader.ReadWindow(index.Windows[1]);
        Check(ReferenceEquals(firstShared.Worlds[0].World, laterShared.Worlds[0].World),
            "opt-in playback shares carried world arrays while an earlier window owns them");
        Check(!ReferenceEquals(firstShared.Worlds[0], laterShared.Worlds[0]) &&
            firstShared.Worlds[0].Time == 0 && laterShared.Worlds[0].Time == 0,
            "shared worlds retain independent window-local timestamps");
        var originalScene = firstShared.Worlds[0].World!.Scene;
        firstShared.Worlds[0].World!.Scene = new string('x', 32769);
        Expect<InvalidDataException>(() => ReplayReader.ReadWindow(index.Windows[0]));
        firstShared.Worlds[0].World!.Scene = originalScene;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Expect<OperationCanceledException>(() => ReplayReader.ReadWindow(index.Windows[0], cancellation.Token));
        }
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        Expect<InvalidDataException>(() => ReplayReader.ReadWindow(index.Windows[0]));
    });
}

static void MetadataIndex()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "deferred-tail.lcr");
        var player = Entity("p", 0); player.State["padding"] = new string('x', 2048);
        var invalid = Entity("p", 1); invalid.Scale = new Vec3(float.NaN, 1, 1);
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() }, Frame(0, player), Frame(1, invalid),
            new ReplayRecord { Kind = "end", Time = 2 });
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 1024, deferPayloadValidation: true);
        Check(index.IsComplete && index.FrameCount == 2 && index.Windows.Count == 2, "future frame is indexed without decoding its body");
        Check(ReplayReader.ReadWindow(index.Windows[0]).Frames.Single().Entities.Single().Id == "p", "valid first section opens independently");
        Expect<InvalidDataException>(() => ReplayReader.ReadWindow(index.Windows[1]));
        File.Delete(Path.ChangeExtension(path, ".lci"));
        Expect<InvalidDataException>(() => ReplayReader.IndexSingleFile(path));

        var reordered = Path.Combine(dir, "reordered.lcr");
        using (var output = File.Create(reordered))
        {
            output.Write(Encoding.ASCII.GetBytes("LCREPL01"));
            RawPayload(output, "{\"Header\":" + JsonConvert.SerializeObject(Header()) + ",\"Time\":0,\"Kind\":\"header\"}");
            RawPayload(output, "{\"World\":{\"Geometry\":[],\"CaptureSetId\":\"reordered\"},\"Time\":0,\"Kind\":\"world\"}");
            RawPayload(output, "{\"Frame\":" + JsonConvert.SerializeObject(Frame(0, Entity("p", 0)).Frame) + ",\"Time\":0,\"Kind\":\"frame\"}");
            RawPayload(output, "{\"Time\":1,\"Kind\":\"end\"}");
        }
        var orderedIndex = ReplayReader.IndexSingleFile(reordered, deferPayloadValidation: true);
        Check(ReplayReader.ReadWindow(orderedIndex.Windows[0]).Worlds.Single().World!.CaptureSetId == "reordered",
            "nonstandard JSON property order falls back to complete payload reading");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Expect<OperationCanceledException>(() => ReplayReader.IndexSingleFile(reordered, cancelled.Token, deferPayloadValidation: true));

        var bytes = File.ReadAllBytes(reordered);
        var truncated = Path.Combine(dir, "truncated.lcr");
        File.WriteAllBytes(truncated, bytes[..^3]);
        var prefix = ReplayReader.IndexSingleFile(truncated, deferPayloadValidation: true);
        Check(!prefix.IsComplete && prefix.FrameCount == 1 && ReplayReader.ReadWindow(prefix.Windows[0]).Frames.Count == 1,
            "physically truncated last record retains its playable complete prefix");
    });
}

static void IndexFloatPrecision()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "precision.lcr");
        const double time = 40.389602000000004;
        using (var writer = new ReplayWriter(path, Header(), indexed: true))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = time,
                Event = new ReplayEvent { Time = time, Category = "animation", Name = "state",
                    EntityId = "player", Data = new() { ["layer"] = "0", ["hash"] = "123" } } }),
                "fractional animation event accepted");
            Check(writer.TryWrite(Frame(41, Entity("player", 1))), "later frame accepted");
            writer.Dispose(); Check(writer.Error == null, "precision writer failed");
        }
        var index = ReplayReader.IndexSingleFile(path);
        var session = ReplayReader.ReadWindow(index.Windows[0]);
        Check(session.Events.Single().Category == "animation", "indexed fractional event decodes");
        Near(session.Events.Single().Time, time);
    });
}

static void AnimationTrackRoundTrip()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "tracks.lcr");
        var track = new AnimationTrackSnapshot { Clip = "Dance1", StateHash = 12345, Layer = 1,
            AnimatorPath = "adult[0]/AnimContainer[0]",
            BonePaths = new() { "rig[0]/arm[1]" },
            Looping = true, Phases = new[] { .1f, .8f },
            Positions = new[] { 0f, 0f, 0f, 1f, 0f, 0f },
            Rotations = new[] { 0f, 0f, 0f, 1f, 0f, .5f, 0f, .8660254f } };
        using (var writer = new ReplayWriter(path, Header(), indexed: true))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", World = new WorldSnapshot { CaptureSetId = "set" } }), "track world accepted");
            Check(writer.TryWrite(Frame(0, Entity("player", 1))), "initial frame accepted");
            Check(writer.TryWrite(Frame(2, Entity("player", 2))), "later frame accepted");
            // Motion becomes known after observing it, but belongs to the
            // first clip occurrence and can be indexed at that earlier time.
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = .1,
                Event = new ReplayEvent { Time = .1, Category = "animation", Name = "track",
                    EntityId = "player", AnimationTrack = track } }), "learned track accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = 2.1,
                Event = new ReplayEvent { Time = 2.1, Category = "animation", Name = "parameters",
                    EntityId = "player", Data = new() { ["f123"] = "1.25", ["b456"] = "1",
                        ["animatorPath"] = "adult[0]/AnimContainer[0]" } } }),
                "Animator parameter change accepted");
            for (var i = 3; i < 20; i++) Check(writer.TryWrite(Frame(i, Entity("player", i))), "track frame accepted");
            writer.Dispose(); Check(writer.Error == null, "track writer completed");
        }
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048);
        Check(index.Windows.Count > 2, "track file has later windows");
        foreach (var window in index.Windows.Where(item => item.Start > 2))
        {
            var loaded = ReplayReader.ReadWindow(window).Events.Single(item => item.Name == "track");
            Check(loaded.Time < 0 && loaded.AnimationTrack?.BonePaths.Single() == "rig[0]/arm[1]" &&
                loaded.AnimationTrack.Positions[3] == 1f && loaded.AnimationTrack.StateHash == 12345 &&
                loaded.AnimationTrack.Layer == 1 &&
                loaded.AnimationTrack.AnimatorPath == "adult[0]/AnimContainer[0]",
                "state-specific track remains reusable after an out-of-order write");
            var parameters = ReplayReader.ReadWindow(window).Events.Single(item => item.Name == "parameters");
            Check(parameters.Time < 0 && parameters.Data["f123"] == "1.25" && parameters.Data["b456"] == "1" &&
                parameters.Data["animatorPath"] == "adult[0]/AnimContainer[0]",
                "Animator parameter state carries into later windows");
        }
        track.Phases = new[] { .8f, .1f };
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() },
            new ReplayRecord { Kind = "event", Time = .1,
                Event = new ReplayEvent { Time = .1, Category = "animation", Name = "track",
                    EntityId = "player", AnimationTrack = track } });
        Expect<InvalidDataException>(() => ReplayReader.Read(path));
    });
}

static void PostFxWindowCarry()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "postfx.lcr");
        var world = new WorldSnapshot { CaptureSetId = "postfx-world",
            Environment = new EnvironmentSnapshot { CustomPassCaptureComplete = true,
                CustomPasses = new() { new PostProcessPassSnapshot { Name = "LethalSponge",
                    ShaderName = "FullScreen/SpongePosterizeNew", MaterialName = "LethalSpongeMaterial",
                    InjectionPoint = "BeforeTransparent",
                    MaterialPassName = "ReadColor", FetchColorBuffer = true, Properties = new() {
                        new EnvironmentParameterSnapshot { Name = "_OutlineThickness", Kind = "float", Values = new[] { .001f } } } } },
                Components = new() { new EnvironmentComponentSnapshot { Type = "ColorCurves",
                    Parameters = new() { new EnvironmentParameterSnapshot { Name = "master", Kind = "curve",
                        Values = new[] { 0f, 1f, 0f }, CurveKeys = new[] {
                            0f, 0f, 0f, 0f, 0f, 0f, 0f,
                            1f, 1f, 0f, 0f, 0f, 0f, 0f } } } } } } };
        using (var writer = new ReplayWriter(path, Header(), indexed: true))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "world", Time = 0, World = world }), "HDRP world accepted");
            for (var i = 0; i < 25; i++)
            {
                Check(writer.TryWrite(Frame(i, Entity("player", i))), "HDRP frame accepted");
                if (i == 1 || i == 2 || i == 3)
                {
                    var type = i == 3 ? "Bloom" : "ColorAdjustments";
                    var parameter = i == 3
                        ? new EnvironmentParameterSnapshot { Name = "intensity", Kind = "float", Values = new[] { .4f } }
                        : new EnvironmentParameterSnapshot { Name = "postExposure", Kind = "float",
                            Values = new[] { i == 1 ? .25f : .75f } };
                    Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = i + .1,
                        Event = new ReplayEvent { Time = i + .1, Category = "postfx", Name = "component",
                            PostProcess = new EnvironmentComponentSnapshot { Type = type,
                                Parameters = new() { parameter } } } }), "HDRP change accepted");
                }
            }
            writer.Dispose(); Check(writer.Error == null, "HDRP writer completed");
        }
        foreach (var rebuild in new[] { false, true })
        {
            if (rebuild) File.Delete(Path.ChangeExtension(path, ".lci"));
            var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 1024);
            Check(index.Windows.Count > 2, "HDRP replay has seek windows");
            foreach (var window in index.Windows.Where(value => value.Start > 5))
            {
                var session = ReplayReader.ReadWindow(window);
                var changes = session.Events.Where(value => value.Category == "postfx").ToArray();
                Check(changes.Length == 2 && changes.All(value => value.Time < 0),
                    "only latest component changes carry into a later window");
                Check(changes.Single(value => value.PostProcess?.Type == "ColorAdjustments")
                    .PostProcess!.Parameters.Single().Values[0] == .75f,
                    "latest color adjustment survives indexed seek");
                var environment = session.Worlds.First().World!.Environment!;
                Check(environment.CustomPasses.Single().ShaderName == "FullScreen/SpongePosterizeNew" &&
                    environment.CustomPasses.Single().MaterialName == "LethalSpongeMaterial" &&
                    environment.CustomPasses.Single().FetchColorBuffer == true &&
                    environment.Components.Single(value => value.Type == "ColorCurves")
                        .Parameters.Single().CurveKeys.Length == 14,
                    "outline pass and bounded color curve survive world carry");
            }
        }
    });
}

static void ItemMotionRoundTrip()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "items.lcr");
        var item = new EntitySnapshot { Id = "item1", Kind = "item", PoseFromItemEvents = true,
            Position = new Vec3(8, 3, 2), Rotation = new Quat(0, .5f, 0, .8660254f) };
        var json = JsonConvert.SerializeObject(item);
        Check(json.Contains("\"PoseFromItemEvents\":true") && !json.Contains("\"Position\"") &&
            !json.Contains("\"Rotation\"") && !json.Contains("\"Scale\""),
            "item frame omits reconstructable transform fields");
        var motion = new ItemMotionSnapshot { Mode = "rest", Position = new Vec3(8, 3, 2),
            Rotation = new Quat(0, .5f, 0, .8660254f) };
        var restingJson = JsonConvert.SerializeObject(motion);
        Check(!restingJson.Contains("\"Target\"") && !restingJson.Contains("\"Curve\"") &&
            !restingJson.Contains("\"FallRate\""), "resting item omits unused fall inputs");
        using (var writer = new ReplayWriter(path, Header(), indexed: true))
        {
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = 0,
                Event = new ReplayEvent { Time = 0, Category = "item", Name = "pose",
                    EntityId = "item1", ItemMotion = motion } }), "initial item pose accepted");
            for (var i = 0; i < 24; i++)
                Check(writer.TryWrite(Frame(i, new EntitySnapshot { Id = "item1", Kind = "item",
                    PoseFromItemEvents = true, Position = item.Position, Rotation = item.Rotation })),
                    "item frame accepted");
            writer.Dispose(); Check(writer.Error == null, "item writer completed");
        }
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 1024);
        Check(index.Windows.Count > 2, "item replay has seek windows");
        foreach (var window in index.Windows.Where(value => value.Start > 2))
        {
            var session = ReplayReader.ReadWindow(window);
            var carried = session.Events.Single(value => value.Category == "item" && value.Name == "pose");
            Check(carried.Time < 0 && carried.ItemMotion?.Position.X == 8,
                "latest item pose carries into later windows");
            var sampled = ReplayTimeline.Sample(session, .1).Entities.Single();
            Check(sampled.PoseFromItemEvents && sampled.Position.X == 0,
                "event-backed item remains sparse through timeline sampling");
        }
        var bad = new ItemMotionSnapshot { Mode = "fall", Curve = new[] { 0f, 1f } };
        Raw(path, new ReplayRecord { Kind = "header", Header = Header() },
            new ReplayRecord { Kind = "event", Time = 0,
                Event = new ReplayEvent { Time = 0, Category = "item", Name = "pose",
                    EntityId = "item1", ItemMotion = bad } });
        Expect<InvalidDataException>(() => ReplayReader.Read(path));
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

static void PlayerDeathIndex()
{
    WithTemp(dir =>
    {
        var path = Path.Combine(dir, "deaths.lcr");
        var alive = Entity("p1", 7); alive.Name = "Asta"; alive.State["isPlayerDead"] = "False";
        var dead = Entity("p1", 9); dead.Name = "Asta"; dead.State["isPlayerDead"] = "True";
        using (var writer = new ReplayWriter(path, Header(), indexed: true))
        {
            Check(writer.TryWrite(Frame(1, alive)), "alive frame accepted");
            Check(writer.TryWrite(Frame(2, dead)), "dead frame accepted");
            Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = 2,
                Event = new ReplayEvent { Time = 2, Category = "state", Name = "isPlayerDead", EntityId = "p1",
                    Data = new() { ["from"] = "False", ["to"] = "True", ["entity"] = "Asta" } } }), "death accepted");
            writer.Dispose(); Check(writer.Error == null, "death writer error");
        }
        var index = ReplayReader.IndexSingleFile(path);
        var death = ReplayReader.ReadPlayerDeaths(index).Single();
        Near(death.Time, 2); Near(death.Position.X, 7);
        Check(death.Name == "Asta" && death.EntityId == "p1", "death identity");
    });
}

static void GameClock()
{
    Check(ReplayGameClock.Format(0) == "06:00 AM", "native morning offset");
    Check(ReplayGameClock.Format(.375) == "12:00 PM", "native noon");
    Check(ReplayGameClock.Format(.75) == "06:00 PM", "native evening");
    Check(ReplayGameClock.Format(1) == "10:00 PM", "native sixteen-hour day");
    Check(ReplayGameClock.Format(.5, 8) == "10:00 AM", "custom day length");
    Check(ReplayGameClock.Format(double.NaN) == "--:--", "invalid clock rejected");
    var state = new Dictionary<string, string> { ["TimeOfDay.normalizedTimeOfDay"] = ".75", ["TimeOfDay.numberOfHours"] = "12" };
    Near(ReplayGameClock.Normalized(state)!.Value, .75);
    Near(ReplayGameClock.Hours(state, new Dictionary<string, string>()), 12);
    state["TimeOfDay.normalizedTimeOfDay"] = "Infinity";
    Check(!ReplayGameClock.Normalized(state).HasValue, "nonfinite time rejected");
}

static void BookmarkLightingIndex() => WithTemp(dir =>
{
    var path = Path.Combine(dir, "bookmarks.lcr");
    using (var writer = new ReplayWriter(path, Header(), indexed: true))
    {
        for (var i = 0; i < 30; i++)
        {
            var frame = Frame(i, Entity("player", i));
            frame.Frame!.Entities[0].State["padding"] = new string('x', 512);
            Check(writer.TryWrite(frame), "frame accepted");
            if (i == 1 || i == 8 || i == 21)
                Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = i + .1,
                    Event = new ReplayEvent { Time = i + .1, Category = "marker", Name = "bookmark" } }), "bookmark accepted");
            if (i == 2 || i == 3)
                Check(writer.TryWrite(new ReplayRecord { Kind = "event", Time = i + .2,
                    Event = new ReplayEvent { Time = i + .2, Category = "lighting", Name = "sun", EntityId = "sunDirect",
                        Data = new() { ["values"] = i.ToString() } } }), "sun state accepted");
        }
        writer.Dispose(); Check(writer.Error == null, "writer completed");
    }
    foreach (var rebuild in new[] { false, true })
    {
        if (rebuild) File.Delete(Path.ChangeExtension(path, ".lci"));
        var index = ReplayReader.IndexSingleFile(path, windowExpandedBytes: 2048);
        Check(index.Bookmarks.SequenceEqual(new[] { 1.1, 8.1, 21.1 }), "all indexed bookmark times");
        Check(index.Windows.Count > 2, "bounded seek windows");
        foreach (var window in index.Windows.Where(window => window.Start > 5).Reverse())
        {
            var session = ReplayReader.ReadWindow(window);
            var lights = session.Events.Where(evt => evt.Category == "lighting").ToArray();
            Check(lights.Length == 1 && lights[0].Time < 0 && lights[0].Data["values"] == "3", "latest sun state carries into backward/forward seek");
            Check(session.Events.Where(evt => evt.Category == "marker").All(evt => evt.Time >= 0), "bookmarks never become historical carried events");
        }
    }
});

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

sealed class ControlledRecordingStream : Stream
{
    private readonly MemoryStream data = new();
    public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
    public readonly SemaphoreSlim RecordPermits = new(0);
    public volatile bool Block, TornTail, FailTruncate, StepRecords;
    public Exception? Failure;
    public byte[] Bytes => data.ToArray();
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (Block) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Slow-disk test did not release output"); }
        if (StepRecords && count > 8) { Entered.Set(); if (!RecordPermits.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Step disk was not released"); }
        if (Failure is Exception failure) { if (TornTail) data.Write(buffer, offset, Math.Min(3, count)); throw failure; }
        data.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer) { var bytes = buffer.ToArray(); Write(bytes, 0, bytes.Length); }
    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => data.Length;
    public override long Position { get => data.Position; set => data.Position = value; }
    public override void SetLength(long value) { if (FailTruncate) throw new IOException("injected truncate failure"); data.SetLength(value); }
    public override long Seek(long offset, SeekOrigin origin) => data.Seek(offset, origin);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
