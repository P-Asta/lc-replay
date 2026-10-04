using LCReplay.Core;

internal static class BlendShapeTests
{
    internal static void Run()
    {
        const string key = "$blendshape:Visual[0]/Face[1]";
        EntitySnapshot Entity(string weights) => new EntitySnapshot
        {
            Id = "mod-enemy", Kind = "enemy", Active = true,
            State = new Dictionary<string, string> { [key] = weights, ["behaviour"] = "idle" }
        };
        var left = Entity("0,100,-25"); var right = Entity("100,0,25");
        right.State["behaviour"] = "attack";
        var session = new ReplaySession();
        session.Frames.Add(new ReplayFrame { Time = 0, Entities = new List<EntitySnapshot> { left } });
        session.Frames.Add(new ReplayFrame { Time = 1, Entities = new List<EntitySnapshot> { right } });
        var sampler = new ReplayTimelineSampler();
        void Expect(double time, string expected)
        {
            foreach (var sampled in new[] { ReplayTimeline.Sample(session, time), sampler.Sample(session, time), sampler.SampleReusable(session, time) })
                if (sampled.Entities[0].State[key] != expected)
                    throw new Exception($"Blendshape at {time}: expected {expected}, got {sampled.Entities[0].State[key]}");
        }
        Expect(.75, "75,25,12.5"); Expect(.25, "25,75,-12.5"); Expect(0, "0,100,-25");
        Expect(1, "100,0,25"); Expect(.5, "50,50,0");
        var sample = ReplayTimeline.Sample(session, .5);
        if (sample.Entities[0].State["behaviour"] != "idle" || left.State[key] != "0,100,-25" || right.State[key] != "100,0,25")
            throw new Exception("Blendshape interpolation changed ordinary state or recording snapshots.");
        sample.Entities[0].State[key] = "999";
        Expect(.5, "50,50,0");
        right.Active = false; Expect(.5, "0,100,-25"); right.Active = true;
        right.Kind = "item"; Expect(.5, "0,100,-25"); right.Kind = "enemy";
        left.Renderers.Add(new RenderPose { Id = "alternate", Active = false });
        right.Renderers.Add(new RenderPose { Id = "alternate", Active = true });
        Expect(.5, "0,100,-25");
        left.Renderers.Clear(); right.Renderers.Clear();
        foreach (var invalid in new[] { "0,100", "NaN,0,25", "Infinity,0,25", "1,,3", "x,0,25", new string('1', 4097), string.Join(",", Enumerable.Repeat("0", 129)) })
        { right.State[key] = invalid; Expect(.5, "0,100,-25"); }
        right.State.Remove(key); Expect(.5, "0,100,-25");
        right.State[key] = left.State[key]; Expect(.5, "0,100,-25");
        if (!ReferenceEquals(sampler.SampleReusable(session, .5).Entities[0].State, left.State))
            throw new Exception("Unchanged blendshapes copied read-only state unnecessarily.");
        left.State.Remove(key); right.State.Remove(key);
        for (var i = 0; i < 16; i++) sampler.SampleReusable(session, .5);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) sampler.SampleReusable(session, .5);
        if (GC.GetAllocatedBytesForCurrentThread() != start)
            throw new Exception("Ordinary sampled state acquired blendshape allocations.");
    }
}
