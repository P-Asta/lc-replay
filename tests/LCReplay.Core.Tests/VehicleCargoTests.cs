using LCReplay.Core;

internal static class VehicleCargoTests
{
    internal static void Run()
    {
        var carA = new EntitySnapshot { Id = "car", Kind = "vehicle", Active = true, Scale = new Vec3(2, 2, 2) };
        var carB = new EntitySnapshot { Id = "car", Kind = "vehicle", Active = true, Position = new Vec3(10, 0, 0),
            Rotation = new Quat(0, MathF.Sqrt(.5f), 0, MathF.Sqrt(.5f)), Scale = carA.Scale };
        EntitySnapshot Item() => new EntitySnapshot { Id = "cargo", Kind = "item", Active = true, PoseFromItemEvents = true };
        var session = new ReplaySession { Duration = .1 };
        session.Frames.Add(new ReplayFrame { Time = 0, Entities = new() { carA, Item() } });
        session.Frames.Add(new ReplayFrame { Time = .1, Entities = new() { carB, Item() } });
        var before = new ReplayEvent { Time = 0, EntityId = "cargo", ItemMotion = new() { Mode = "rest", Position = new Vec3(2, 0, 0) } };
        var after = new ReplayEvent { Time = .1, EntityId = "cargo", ItemMotion = new() { Mode = "rest", Position = new Vec3(10, 0, -2), Rotation = carB.Rotation } };
        var cargo = new ReplayVehicleCargo(session);
        foreach (var time in new[] { .05, .09, .01, 0, .1, .05 })
        {
            var frame = ReplayTimeline.Sample(session, time);
            Check(cargo.TrySample(before, after, time, frame, out var position, out var rotation), "coherent cargo did not bind");
            var angle = (float)(time / .1 * Math.PI / 2);
            Near(position.X, (float)(time / .1 * 10) + 2 * MathF.Cos(angle));
            Near(position.Z, -2 * MathF.Sin(angle));
            Near(rotation.Y, MathF.Sin(angle / 2));
        }
        Near(before.ItemMotion!.Position.X, 2); Near(carA.Position.X, 0);
        var mid = ReplayTimeline.Sample(session, .05);
        after.ItemMotion!.Position = new Vec3(2, 0, 0);
        Check(!new ReplayVehicleCargo(session).TrySample(before, after, .05, mid, out _, out _), "nearby stationary ground item attached to moving car");
        after.ItemMotion.Position = new Vec3(10, 0, -2);
        after.ItemMotion.Mode = "fall";
        Check(!new ReplayVehicleCargo(session).TrySample(before, after, .05, mid, out _, out _), "rest/fall boundary interpolated");
        after.ItemMotion.Mode = "rest";
        after.ItemMotion.AnchorId = "entity:car";
        Check(!new ReplayVehicleCargo(session).TrySample(before, after, .05, mid, out _, out _), "explicit parent sent through legacy inference");
        after.ItemMotion.AnchorId = "";
        session.Frames[1].Entities[1].State["isHeld"] = "True";
        Check(!new ReplayVehicleCargo(session).TrySample(before, after, .05, mid, out _, out _), "pickup boundary interpolated");
        session.Frames[1].Entities[1].State.Clear();
        after.Time = .7;
        Check(!new ReplayVehicleCargo(session).TrySample(before, after, .05, mid, out _, out _), "unobserved long gap interpolated");
        after.Time = .1;
        session.Frames[0].Entities.Add(new EntitySnapshot { Id = "other", Kind = "vehicle", Active = true, Scale = carA.Scale });
        session.Frames[1].Entities.Add(new EntitySnapshot { Id = "other", Kind = "vehicle", Active = true,
            Position = carB.Position, Rotation = carB.Rotation, Scale = carB.Scale });
        Check(!new ReplayVehicleCargo(session).TrySample(before, after, .05, mid, out _, out _), "ambiguous vehicle accepted");
        SlowVehicleAndBoundaries();
        void Near(float actual, float expected) => Check(Math.Abs(actual - expected) < .0001, $"expected {expected}, got {actual}");
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
    }

    private static void SlowVehicleAndBoundaries()
    {
        var session = new ReplaySession { Duration = .5 };
        foreach (var point in new[] { (Time: 0d, X: 0f), (Time: .25, X: .25f), (Time: .5, X: .04f) })
            session.Frames.Add(new ReplayFrame { Time = point.Time, Entities = new()
            {
                new EntitySnapshot { Id = "car", Kind = "vehicle", Position = new Vec3(point.X, 0, 0) },
                new EntitySnapshot { Id = "cargo", Kind = "item", PoseFromItemEvents = true }
            } });
        var before = new ReplayEvent { Time = 0, EntityId = "cargo", ItemMotion = new() { Mode = "rest", Position = new Vec3(2, 0, 0) } };
        var after = new ReplayEvent { Time = .5, EntityId = "cargo", ItemMotion = new() { Mode = "rest", Position = new Vec3(2, 0, 0) } };
        var frame = ReplayTimeline.Sample(session, .25);
        if (new ReplayVehicleCargo(session).TrySample(before, after, .25, frame, out _, out _))
            throw new Exception("Stationary ground item followed a slow vehicle's intermediate excursion");
        after.ItemMotion.Position = new Vec3(2.04f, 0, 0);
        if (!new ReplayVehicleCargo(session).TrySample(before, after, .25, frame, out var position, out _) || Math.Abs(position.X - 2.25f) > .0001)
            throw new Exception("Actual slow-moving cargo lost its vehicle-relative pose");
        // A final event can arrive between capture ticks. The missing vehicle
        // endpoint is not evidence for an extrapolated cargo attachment.
        session.Frames.RemoveAt(2);
        after.ItemMotion.Position = new Vec3(2.25f, 0, 0);
        if (new ReplayVehicleCargo(session).TrySample(before, after, .25, frame, out _, out _))
            throw new Exception("Cargo inferred beyond the final vehicle observation");
    }
}
