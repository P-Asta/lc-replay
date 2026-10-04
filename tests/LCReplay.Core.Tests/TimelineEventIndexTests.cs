using LCReplay.Core;
using Newtonsoft.Json;

internal static class TimelineEventIndexTests
{
    internal static void Run()
    {
        ReplaySession Window(double duration = .2, bool indoor = false)
        {
            var result = new ReplaySession { Duration = duration };
            foreach (var time in new[] { 0d, duration })
            {
                var frame = new ReplayFrame { Time = time };
                for (var player = 0; player < 2; player++)
                    frame.Entities.Add(new EntitySnapshot
                    {
                        Id = "p" + player, Kind = "player", Active = true,
                        Position = new Vec3(player * 10 + (time == 0 ? 0 : 2), 0, 0),
                        State = new() { ["isInsideFactory"] = indoor ? "True" : "False" }
                    });
                result.Frames.Add(frame);
            }
            return result;
        }
        ReplayEvent Teleport(double time, string id = "p0") => new ReplayEvent
        { Time = time, Category = "call", Name = "PlayerControllerB.TeleportPlayer", EntityId = id };
        ReplayEvent Gap(double time) => new ReplayEvent { Time = time, Category = "capture", Name = "sample-gap" };
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Expect(ReplayTimelineSampler sampler, ReplaySession session, float first, float second, string reason)
        {
            foreach (var frame in new[] { sampler.Sample(session, session.Duration / 2), sampler.SampleReusable(session, session.Duration / 2) })
                Check(frame.Entities[0].Position.X == first && frame.Entities[1].Position.X == second, reason);
            Check(JsonConvert.SerializeObject(sampler.SampleReusable(session, session.Duration / 2)) ==
                JsonConvert.SerializeObject(ReplayTimeline.Sample(session, session.Duration / 2)), "Stateless/indexed parity: " + reason);
        }

        var sampler = new ReplayTimelineSampler();
        var session = Window();
        session.Events.Add(Teleport(0));
        session.Events.Add(Teleport(.20001));
        sampler.PrepareEvents(session);
        Expect(sampler, session, 1, 11, "Teleport interval excludes its left boundary and events after the right boundary.");
        session.Events.Add(Teleport(.2));
        // A replaced or resized list uses the ordinary scan until explicitly prepared again.
        Expect(sampler, session, 0, 11, "Newly appended teleport cannot use the old index.");
        sampler.PrepareEvents(session);
        Expect(sampler, session, 0, 11, "Teleport at the right boundary stops only its matching player.");
        session.Events = new List<ReplayEvent> { Teleport(.1, "p1"), Teleport(.1, "unrelated"), Teleport(0) };
        Expect(sampler, session, 1, 10, "Replacing a list with the same count must not reuse the previous window's index.");
        sampler.PrepareEvents(session);
        Expect(sampler, session, 1, 10, "Duplicate-time and unrelated teleport IDs preserve association.");

        session.Events.Clear();
        session.Events.Add(new ReplayEvent { Time = .1, Category = "call", Name = "TeleportPlayer", Data = new() { ["pos"] = "2,0,0" } });
        sampler.PrepareEvents(session);
        Expect(sampler, session, 0, 11, "Legacy destination associates only the player near that destination.");
        session.Events[0].Time = 0;
        sampler.PrepareEvents(session);
        Expect(sampler, session, 1, 11, "Explicit preparation refreshes edited event fields.");

        session = Window(1, true);
        foreach (var time in new[] { 0d, 1.00011, 1.0001, 1d })
        {
            session.Events = new List<ReplayEvent> { Gap(time) };
            sampler.PrepareEvents(session);
            var blocks = time > 0 && time <= 1.0001;
            Expect(sampler, session, blocks ? 0 : 1, blocks ? 10 : 11, "Gap endpoint tolerance must match existing indoor hold semantics.");
        }
        session = Window(1);
        session.Events.Add(Gap(1));
        session.Events.Add(Teleport(.5, "unrelated"));
        sampler.PrepareEvents(session);
        Expect(sampler, session, 0, 10, "Any teleport in a missing interval blocks legacy outdoor gap bridging.");
        session.Events[1] = Teleport(0, "unrelated");
        sampler.PrepareEvents(session);
        Expect(sampler, session, 1, 11, "Teleport at the preceding sample does not block later outdoor movement.");

        var mutable = new ReplayTimelineSampler();
        session = Window();
        session.Events.Add(Teleport(.1));
        Expect(mutable, session, 0, 11, "Unprepared samplers retain mutable event support.");
        session.Events[0].Category = "sound";
        Expect(mutable, session, 1, 11, "Changing an event in place remains visible without explicit preparation.");
        session.Events[0] = Teleport(.1, "p1");
        Expect(mutable, session, 1, 10, "Same-count event replacement remains visible without explicit preparation.");

        // Many unrelated events exercise the actual recording shape. Input order
        // must be untouched, and backward seeks must select the same sparse events.
        session = Window(1, true);
        for (var i = 0; i < 20000; i++)
            session.Events.Add(new ReplayEvent { Time = (i % 1000) / 1000d, Category = "sound", Name = "play" });
        session.Events.Insert(12000, Gap(1));
        session.Events.Insert(4000, Teleport(.5, "p1"));
        var order = session.Events.ToArray();
        sampler.PrepareEvents(session);
        foreach (var time in new[] { .9, 0, .1, 1, .5, .01, .99 })
            Check(JsonConvert.SerializeObject(sampler.SampleReusable(session, time)) == JsonConvert.SerializeObject(ReplayTimeline.Sample(session, time)),
                "Dense interleaved events match through forward and backward seeks at " + time);
        Check(session.Events.SequenceEqual(order), "Preparing sparse event indexes must not sort the recording itself.");
        for (var i = 0; i < 16; i++) sampler.SampleReusable(session, .5);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) sampler.SampleReusable(session, .5);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Indexed event queries allocate during steady playback.");
    }
}
