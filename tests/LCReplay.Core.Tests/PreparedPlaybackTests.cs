using LCReplay.Core;
using Newtonsoft.Json;

internal static class PreparedPlaybackTests
{
    internal static void Run()
    {
        var random = new Random(1743);
        Vec3 V() => new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
        Quat Q() => new((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1,
            (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1);
        var session = new ReplaySession { Duration = 2 };
        for (var f = 0; f < 5; f++)
        {
            var frame = new ReplayFrame { Time = f * .5 };
            frame.Anchors.Add(new AnchorPose { Id = "ship", Position = V(), Rotation = Q(), Scale = V() });
            frame.SceneRenderers.Add(new RenderPose { Id = "door", Active = f != 2, Position = V(), Rotation = Q(), Scale = V() });
            for (var i = 0; i < 6; i++)
            {
                var e = new EntitySnapshot { Id = "e" + i, Kind = i == 0 ? "player" : i == 1 ? "item" : "enemy",
                    Active = f != 2 || i != 3, Position = V(), Rotation = Q(), Scale = V(), ViewPosition = V(), ViewRotation = Q(),
                    PoseFromItemEvents = i == 1 && f > 2 };
                if (i == 1) { e.State["isHeld"] = "True"; e.State["$heldBy"] = "e0"; }
                if (i == 2) e.State["$blendshape:skin"] = (f * 15) + ",20";
                for (var b = 0; b < (f == 2 ? 3 : 4); b++)
                {
                    e.Bones.Add(new BonePose { Path = "b" + b, Position = V(), Rotation = Q(), Scale = V() });
                    e.Renderers.Add(new RenderPose { Id = "r" + i + b, Active = f != 3 || b != 1, Position = V(), Rotation = Q(), Scale = V() });
                }
                if (f % 2 == 1) { e.Bones.Reverse(); e.Renderers.Reverse(); }
                frame.Entities.Add(e);
            }
            session.Frames.Add(frame);
        }
        // Exercise all safety decisions and the effect fallback, then return to a pose-only bracket.
        session.Events.Add(new ReplayEvent { Time = .75, Category = "call", Name = "TeleportPlayer", EntityId = "e0" });
        session.Events.Add(new ReplayEvent { Time = 1.5, Category = "capture", Name = "sample-gap" });
        session.Frames[3].Particles.Add(new ParticlePose { RemainingLifetime = 5, Lifetime = 5, Position = V(), Velocity = V() });
        session.Frames[3].Lines.Add(new LinePose { Id = "line", Positions = new float[] { 0, 1, 2, 3, 4, 5 } });
        var original = JsonConvert.SerializeObject(session);
        var fast = new ReplayTimelineSampler(); var reference = new ReplayTimelineSampler();
        fast.PreparePlayback(session);
        void Check(double time)
        {
            var expected = JsonConvert.SerializeObject(reference.SampleReusable(session, time));
            var frame = fast.SampleReusable(session, time);
            if (JsonConvert.SerializeObject(frame) != expected) throw new Exception("Prepared sampling differs at " + time);
            // Playback may adjust the root for legacy vehicle/item attachment.
            frame.Entities[0].Position = new Vec3(999, 999, 999);
            frame.Entities.Add(new EntitySnapshot { Id = "temporary inferred vehicle" });
        }
        for (var i = 0; i <= 200; i++) Check(i * .01);
        for (var i = 200; i >= 0; i--) Check(i * .01);
        foreach (var time in new[] { .17, .19, 1.8, .8, .82, 0, .17, .21 }) Check(time);
        if (JsonConvert.SerializeObject(session) != original) throw new Exception("Prepared sampling mutated the recording.");
        session.Frames[1].Entities[0].Position = V();
        fast.PreparePlayback(session); Check(.21); Check(.22);
        session.Events.Clear(); fast.PrepareEvents(session); Check(.81); Check(.82);
        fast.PreparePlayback(session);
        Check(.17); var layout = fast.PoseLayoutVersion;
        Check(.19);
        if (fast.PoseLayoutVersion != layout) throw new Exception("Stable prepared bracket rebuilt its pose identities.");
        Check(.61);
        if (fast.PoseLayoutVersion == layout) throw new Exception("A new bracket did not invalidate renderer bindings.");
    }
}
