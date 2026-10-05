using LCReplay.Core;

internal static class PoseOrderTests
{
    internal static void Run()
    {
        var a = new EntitySnapshot { Id = "actor", Kind = "enemy", Active = true };
        var b = new EntitySnapshot { Id = "actor", Kind = "enemy", Active = true };
        BonePose Bone(string id, float x) => new() { Path = id, Position = new Vec3(x, 0, 0) };
        RenderPose Renderer(string id, float x, bool active = true) => new() { Id = id, Position = new Vec3(x, 0, 0), Active = active };
        a.Bones.AddRange(new[] { Bone("root", 0), Bone("root/arm", 10), Bone("root/removed", 30) });
        b.Bones.AddRange(new[] { Bone("root/arm", 20), Bone("root", 4), Bone("root/new", 50) });
        a.Renderers.AddRange(new[] { Renderer("body", 0), Renderer("hand", 10), Renderer("removed", 30) });
        b.Renderers.AddRange(new[] { Renderer("hand", 20), Renderer("body", 4, false), Renderer("new", 50) });
        var session = new ReplaySession { Duration = 1, Frames = new() {
            new() { Time = 0, Entities = new() { a } }, new() { Time = 1, Entities = new() { b } } } };
        var sampler = new ReplayTimelineSampler();
        void Check(ReplayFrame frame)
        {
            var e = frame.Entities.Single();
            if (e.Bones[0].Position.X != 2 || e.Bones[1].Position.X != 15 || e.Bones[2].Position.X != 30 ||
                e.Renderers[0].Position.X != 0 || !e.Renderers[0].Active || e.Renderers[1].Position.X != 15 || e.Renderers[2].Position.X != 30)
                throw new Exception("Reordered or missing poses must match by identity; activation stays discrete.");
        }
        Check(sampler.SampleReusable(session, .5));
        b.Bones.Reverse(); b.Renderers.Reverse();
        Check(sampler.SampleReusable(session, .5));
        sampler.SampleReusable(session, 1);
        Check(sampler.SampleReusable(session, .5));
        Check(ReplayTimeline.Sample(session, .5));
        if (a.Bones[0].Position.X != 0 || a.Renderers[1].Position.X != 10)
            throw new Exception("Sampling mutated its source.");
    }
}
