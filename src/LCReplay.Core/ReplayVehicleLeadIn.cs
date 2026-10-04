using System;
using System.Collections.Generic;

namespace LCReplay.Core
{
    /// <summary>
    /// Repairs a delayed opening Company Cruiser observation only when its first
    /// samples prove that it was travelling with the ship's magnet. The inferred
    /// entity is owned by this helper and is valid until the next Apply call.
    /// </summary>
    public sealed class ReplayVehicleLeadIn
    {
        private const string ShipAnchor = "ship-elevator";
        private readonly EntitySnapshot vehicle;
        private readonly Vec3 offset;
        public double FirstRecordedTime { get; }
        public string EntityId => vehicle.Id;

        private ReplayVehicleLeadIn(EntitySnapshot first, Vec3 offset, double time)
        {
            FirstRecordedTime = time;
            this.offset = offset;
            vehicle = new EntitySnapshot
            {
                Id = first.Id, Kind = first.Kind, Name = first.Name, Active = first.Active,
                Position = first.Position, Rotation = first.Rotation, Scale = first.Scale,
                ViewPosition = first.ViewPosition, ViewRotation = first.ViewRotation,
                State = new Dictionary<string, string>(first.State)
            };
            foreach (var bone in first.Bones)
                vehicle.Bones.Add(new BonePose
                {
                    Path = bone.Path, Position = bone.Position, Rotation = bone.Rotation, Scale = bone.Scale
                });
            foreach (var renderer in first.Renderers)
                vehicle.Renderers.Add(new RenderPose
                {
                    Id = renderer.Id, Position = renderer.Position, Rotation = renderer.Rotation,
                    Scale = renderer.Scale, Active = renderer.Active
                });
        }

        /// <summary>Returns at most one repair, restricted to a recording's opening part.</summary>
        public static ReplayVehicleLeadIn? Create(ReplaySession session, int partIndex)
        {
            if (partIndex != 0 || session.Frames.Count == 0 || !Finite(session.Frames[0].Time) ||
                session.Frames[0].Time < 0 || session.Frames[0].Time > .25 ||
                FindAnchor(session.Frames[0]) == null) return null;

            var openingOwners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in session.Worlds)
                if (record.Time >= 0 && record.Time <= .01 && record.World != null)
                    foreach (var geometry in record.World.Geometry)
                        if (!string.IsNullOrEmpty(geometry.EntityId)) openingOwners.Add(geometry.EntityId);
            if (openingOwners.Count == 0) return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < session.Frames.Count; index++)
            {
                var frame = session.Frames[index];
                if (frame.Time > 15) break;
                foreach (var entity in frame.Entities)
                {
                    if (entity.Kind != "vehicle" || !seen.Add(entity.Id) || !openingOwners.Contains(entity.Id) ||
                        frame.Time <= session.Frames[0].Time || !IsParkedCruiser(entity)) continue;
                    if (ProvesShipTravel(session.Frames, index, entity, out var translation))
                        return new ReplayVehicleLeadIn(entity, translation, frame.Time);
                }
            }
            return null;
        }

        /// <summary>
        /// Adds the inferred pose to a reusable playback sample. Never call with
        /// a stored recording frame; recorded observations remain authoritative.
        /// The magnet maintains a world translation offset, not a rotated offset.
        /// </summary>
        public bool Apply(ReplayFrame sample)
        {
            if (!(sample.Time >= 0 && sample.Time < FirstRecordedTime)) return false;
            foreach (var entity in sample.Entities)
                if (entity.Id == vehicle.Id) return false;
            var ship = FindAnchor(sample);
            if (ship == null) return false;
            vehicle.Position = new Vec3(ship.Position.X + offset.X, ship.Position.Y + offset.Y, ship.Position.Z + offset.Z);
            sample.Entities.Add(vehicle);
            return true;
        }

        private static bool ProvesShipTravel(List<ReplayFrame> frames, int firstIndex,
            EntitySnapshot first, out Vec3 translation)
        {
            translation = default;
            var initial = frames[firstIndex];
            var firstShip = FindAnchor(initial);
            if (firstShip == null || !Finite(first.Position) || !Finite(first.Scale) || !ValidRotation(first.Rotation)) return false;
            translation = Difference(first.Position, firstShip.Position);
            var observations = 0;
            var moved = false;
            var previousTime = double.NegativeInfinity;
            for (var index = firstIndex; index < frames.Count && observations < 6; index++)
            {
                var frame = frames[index];
                if (frame.Time > initial.Time + .7) break;
                if (!Finite(frame.Time) || frame.Time <= previousTime) return false;
                previousTime = frame.Time;
                var ship = FindAnchor(frame);
                EntitySnapshot? current = null;
                foreach (var entity in frame.Entities)
                    if (entity.Id == first.Id) { current = entity; break; }
                if (ship == null || current == null || !IsParkedCruiser(current) || !Finite(current.Position) ||
                    SquaredDistance(Difference(current.Position, ship.Position), translation) >= .06 * .06 ||
                    !SameRotation(current.Rotation, first.Rotation)) return false;
                if (SquaredDistance(ship.Position, firstShip.Position) >= .5 * .5) moved = true;
                observations++;
            }
            return observations >= 3 && moved;
        }

        private static bool IsParkedCruiser(EntitySnapshot entity) =>
            entity.Kind == "vehicle" && entity.Active &&
            (entity.Name == "CompanyCruiser" || entity.Name == "CompanyCruiser(Clone)") &&
            entity.State.TryGetValue("gear", out var gear) && gear == "Park" &&
            entity.State.TryGetValue("carDestroyed", out var destroyed) &&
            string.Equals(destroyed, "False", StringComparison.OrdinalIgnoreCase);

        private static AnchorPose? FindAnchor(ReplayFrame frame)
        {
            foreach (var anchor in frame.Anchors)
                if (anchor.Id == ShipAnchor && Finite(anchor.Position)) return anchor;
            return null;
        }

        private static Vec3 Difference(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static double SquaredDistance(Vec3 a, Vec3 b)
        {
            var x = (double)a.X - b.X; var y = (double)a.Y - b.Y; var z = (double)a.Z - b.Z;
            return x * x + y * y + z * z;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Finite(Vec3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
        private static double RotationLength(Quat q) => (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
        private static bool ValidRotation(Quat q) =>
            Finite(q.X) && Finite(q.Y) && Finite(q.Z) && Finite(q.W) && RotationLength(q) > .000001;
        private static bool SameRotation(Quat a, Quat b)
        {
            if (!ValidRotation(a) || !ValidRotation(b)) return false;
            var dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
            // abs(dot) accounts for the two quaternion representations of one rotation.
            var cosine = Math.Abs(dot) / Math.Sqrt(RotationLength(a) * RotationLength(b));
            return cosine > Math.Cos(Math.PI / 360);
        }
    }
}
