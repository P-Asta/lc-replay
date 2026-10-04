using LCReplay.Core;
using Newtonsoft.Json;

internal static class VehicleLeadInTests
{
    internal static void Run()
    {
        var source = Recording();
        var original = JsonConvert.SerializeObject(source);
        var repair = ReplayVehicleLeadIn.Create(source, 0) ?? throw new Exception("Proven ship travel was rejected");
        var sample = Sample(4, new Vec3(42, 30, -7.5f));
        sample.Anchors[0].Rotation = new Quat(0, 1, 0, 0);
        if (!repair.Apply(sample)) throw new Exception("Missing opening vehicle was not repaired");
        var inferred = sample.Entities.Single();
        Near(inferred.Position, new Vec3(44.6f, 32.2f, -20.4f));
        if (inferred.Rotation.W != 1 || repair.FirstRecordedTime != 9.7 || repair.EntityId != "car")
            throw new Exception("Magnet pose or first observation changed");
        if (repair.Apply(sample) || sample.Entities.Count != 1) throw new Exception("Duplicate inferred vehicle");

        var next = Sample(8, new Vec3(20, 3, -7.5f));
        repair.Apply(next);
        if (!ReferenceEquals(inferred, next.Entities[0])) throw new Exception("Opening samples should reuse their vehicle");
        Near(next.Entities[0].Position, new Vec3(22.6f, 5.2f, -20.4f));
        var rewind = Sample(4, new Vec3(42, 30, -7.5f));
        repair.Apply(rewind);
        Near(rewind.Entities[0].Position, new Vec3(44.6f, 32.2f, -20.4f));
        // A visible copy is independent of immutable recording state and pose objects.
        rewind.Entities[0].State["gear"] = "edited sample";
        rewind.Entities[0].Bones[0].Position = new Vec3(99, 99, 99);
        rewind.Entities[0].Renderers[0].Active = false;
        if (JsonConvert.SerializeObject(source) != original) throw new Exception("Stored recording was modified");

        var boundary = Sample(9.7, default);
        boundary.Entities.Add(source.Frames[1].Entities[0]);
        if (repair.Apply(boundary) || !ReferenceEquals(boundary.Entities[0], source.Frames[1].Entities[0]))
            throw new Exception("First recorded boundary must remain authoritative");
        if (repair.Apply(Sample(-.001, default)) || repair.Apply(Sample(10, default)) ||
            repair.Apply(new ReplayFrame { Time = 3 })) throw new Exception("Invalid sample accepted");

        Reject("later playback part", value => { }, 1);
        Reject("window without opening frames", value => value.Frames.RemoveAt(0));
        Reject("no opening world", value => value.Worlds.Clear());
        Reject("geometry belongs to another owner", value => value.Worlds[0].World!.Geometry[0].EntityId = "other");
        Reject("late geometry", value => value.Worlds[0].Time = 1);
        Reject("late spawn", value => { foreach (var frame in value.Frames.Skip(1)) frame.Time += 6; });
        Reject("too few observations", value => value.Frames.RemoveRange(3, 4));
        Reject("observation gap", value => value.Frames[3].Time += 1);
        Reject("missing intervening vehicle", value => value.Frames[3].Entities.Clear());
        Reject("missing intervening ship", value => value.Frames[3].Anchors.Clear());
        Reject("moving gear", value => value.Frames[3].Entities[0].State["gear"] = "Drive");
        Reject("destroyed", value => value.Frames[3].Entities[0].State["carDestroyed"] = "True");
        Reject("hidden form", value => value.Frames[3].Entities[0].Active = false);
        Reject("unrecognized vehicle", value => { foreach (var frame in value.Frames.Skip(1)) frame.Entities[0].Name = "ModCar"; });
        Reject("offset drift", value => value.Frames[4].Entities[0].Position = new Vec3(400, 5, 0));
        Reject("rotating vehicle", value => value.Frames[4].Entities[0].Rotation = new Quat(0, .02f, 0, .9998f));
        Reject("stationary anchor", value =>
        {
            foreach (var frame in value.Frames.Skip(1))
            {
                frame.Anchors[0].Position = default;
                frame.Entities[0].Position = new Vec3(2.6f, 2.2f, -12.9f);
            }
        });
        Reject("non-finite anchor", value => value.Frames[2].Anchors[0].Position = new Vec3(float.NaN, 0, 0));
        Reject("non-finite opening time", value => value.Frames[0].Time = double.NaN);

        var signs = Recording();
        signs.Frames[3].Entities[0].Rotation = new Quat(0, 0, 0, -1);
        if (ReplayVehicleLeadIn.Create(signs, 0) == null) throw new Exception("Equivalent quaternion sign rejected");
        ActualOpeningEvidence();
    }

    private static void ActualOpeningEvidence()
    {
        // The reported legacy file's first six ship/cruiser observations. The
        // small physics-tick lag is below the strict 6 cm translation bound.
        var observations = new (double Time, Vec3 Ship, Vec3 Car)[]
        {
            (9.69968, new Vec3(14.3821888f, 1.70877171f, -7.500001f), new Vec3(16.9998932f, 3.91687655f, -20.3999977f)),
            (9.8187839, new Vec3(13.5573444f, 1.78298616f, -7.500001f), new Vec3(16.1717186f, 3.99295163f, -20.4f)),
            (9.9331185, new Vec3(12.7528343f, 1.84626913f, -7.500001f), new Vec3(15.3637915f, 4.058016f, -20.3999977f)),
            (10.0385587, new Vec3(12.0984173f, 1.89102268f, -7.500001f), new Vec3(14.7064524f, 4.104189f, -20.4f)),
            (10.1427733, new Vec3(11.4589081f, 1.92886257f, -7.5f), new Vec3(14.0639219f, 4.143387f, -20.4000015f)),
            (10.2568093, new Vec3(10.7116508f, 1.96560144f, -7.5f), new Vec3(13.31297f, 4.18166637f, -20.4f))
        };
        var session = Recording();
        for (var i = 0; i < observations.Length; i++)
        {
            var frame = session.Frames[i + 1];
            frame.Time = observations[i].Time;
            frame.Anchors[0].Position = observations[i].Ship;
            frame.Entities[0].Position = observations[i].Car;
            frame.Entities[0].Rotation = new Quat(.0305287931f, -.706447542f, -.0305289365f, -.7064475f);
        }
        var repair = ReplayVehicleLeadIn.Create(session, 0) ?? throw new Exception("Actual opening evidence rejected");
        var sample = Sample(5, new Vec3(57.08425f, 35.43535f, -7.5f));
        if (!repair.Apply(sample)) throw new Exception("Actual early pose was not inferred");
        Near(sample.Entities[0].Position, new Vec3(59.7019544f, 37.64345484f, -20.3999967f));
    }

    private static void Reject(string name, Action<ReplaySession> change, int part = 0)
    {
        var session = Recording(); change(session);
        if (ReplayVehicleLeadIn.Create(session, part) != null) throw new Exception("Unsafe lead-in accepted: " + name);
    }

    private static ReplaySession Recording()
    {
        var session = new ReplaySession();
        session.Worlds.Add(new ReplayRecord { Time = 0, World = new WorldSnapshot
        {
            Geometry = new List<GeometrySnapshot> { new GeometrySnapshot { EntityId = "car", Id = "car-body" } }
        } });
        session.Frames.Add(Sample(.01, new Vec3(90, 70, -7.5f)));
        for (var i = 0; i < 6; i++)
        {
            var frame = Sample(9.7 + i * .1, new Vec3(14 - i, 2, -7.5f));
            frame.Entities.Add(new EntitySnapshot
            {
                Id = "car", Kind = "vehicle", Name = "CompanyCruiser(Clone)",
                Position = new Vec3(16.6f - i, 4.2f, -20.4f),
                State = new Dictionary<string, string> { ["gear"] = "Park", ["carDestroyed"] = "False" },
                Bones = new List<BonePose> { new BonePose { Path = "Wheel" } },
                Renderers = new List<RenderPose> { new RenderPose { Id = "car-body" } }
            });
            session.Frames.Add(frame);
        }
        return session;
    }

    private static ReplayFrame Sample(double time, Vec3 ship) => new ReplayFrame
    {
        Time = time, Anchors = new List<AnchorPose> { new AnchorPose { Id = "ship-elevator", Position = ship } }
    };
    private static void Near(Vec3 actual, Vec3 expected)
    {
        if (Math.Abs(actual.X - expected.X) > .00002f || Math.Abs(actual.Y - expected.Y) > .00002f || Math.Abs(actual.Z - expected.Z) > .00002f)
            throw new Exception($"Unexpected inferred pose {actual.X},{actual.Y},{actual.Z}");
    }
}
