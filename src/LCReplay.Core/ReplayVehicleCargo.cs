using System;
using System.Collections.Generic;
using System.Numerics;

namespace LCReplay.Core
{
    /// <summary>Recovers rigid vehicle cargo from closely spaced legacy world-space rest poses.</summary>
    public sealed class ReplayVehicleCargo
    {
        private readonly ReplaySession session;
        private readonly Dictionary<ReplayEvent, Binding?> bindings = new Dictionary<ReplayEvent, Binding?>();
        private sealed class Binding
        {
            internal string Vehicle = "";
            internal Vector3 Start, End;
            internal Quaternion StartRotation, EndRotation;
        }
        public ReplayVehicleCargo(ReplaySession session) { this.session = session; }

        public bool TrySample(ReplayEvent before, ReplayEvent? after, double time, ReplayFrame frame,
            out Vec3 position, out Quat rotation)
        {
            position = default; rotation = Quat.Identity;
            if (after == null || before.ItemMotion?.Mode != "rest" || after.ItemMotion?.Mode != "rest" ||
                before.ItemMotion.AnchorId.Length != 0 || after.ItemMotion.AnchorId.Length != 0 ||
                before.EntityId != after.EntityId || after.Time <= before.Time || after.Time - before.Time > .5 ||
                time < before.Time || time > after.Time) return false;
            if (!bindings.TryGetValue(before, out var binding))
            {
                // A bounded playback window owns this cache. Long recordings
                // must not retain every historical cargo event indefinitely.
                if (bindings.Count >= 8192) bindings.Clear();
                bindings[before] = binding = FindBinding(before, after);
            }
            if (binding == null) return false;
            var vehicle = Find(frame, binding.Vehicle);
            if (vehicle == null || !vehicle.Active) return false;
            var amount = (float)((time - before.Time) / (after.Time - before.Time));
            var local = Vector3.Lerp(binding.Start, binding.End, amount);
            position = V(P(vehicle.Position) + Vector3.Transform(local * P(vehicle.Scale), Q(vehicle.Rotation)));
            rotation = R(Q(vehicle.Rotation) * Quaternion.Slerp(binding.StartRotation, binding.EndRotation, amount));
            return true;
        }

        private Binding? FindBinding(ReplayEvent before, ReplayEvent after)
        {
            var leftIndex = FrameIndex(before.Time);
            if (leftIndex < 0) return null;
            // Do not interpolate across a pickup/drop or despawn between the
            // sparse poses, even if both endpoint positions happen to match.
            for (var i = leftIndex; i < session.Frames.Count && session.Frames[i].Time <= after.Time; i++)
            {
                var item = Find(session.Frames[i], before.EntityId);
                if (item == null || !item.Active || !item.PoseFromItemEvents || Flag(item, "isHeld") ||
                    Flag(item, "isHeldByEnemy") || Flag(item, "isPocketed")) return null;
            }
            Binding? match = null;
            foreach (var candidate in session.Frames[leftIndex].Entities)
            {
                if (candidate.Kind != "vehicle" || !candidate.Active ||
                    !Pose(candidate.Id, before.Time, out var startPosition, out var startRotation, out var startScale) ||
                    !Pose(candidate.Id, after.Time, out var endPosition, out var endRotation, out var endScale)) continue;
                var a = before.ItemMotion!; var b = after.ItemMotion!;
                var localA = Vector3.Transform(P(a.Position) - startPosition, Quaternion.Inverse(startRotation)) / startScale;
                var localB = Vector3.Transform(P(b.Position) - endPosition, Quaternion.Inverse(endRotation)) / endScale;
                // The old format has no parent ID. Require coherent local
                // motion inside the vehicle, not merely a nearby world item.
                if (!Inside(localA) || !Inside(localB) || Vector3.DistanceSquared(localA, localB) > .08f * .08f) continue;
                var carriedEnd = endPosition + Vector3.Transform(localA * endScale, endRotation);
                var carriedDistance = Vector3.DistanceSquared(P(a.Position), carriedEnd);
                // With little vehicle motion, an ordinary ground item also
                // satisfies the local-distance tolerance. Require positive
                // observed movement with the vehicle before inventing a parent.
                if (carriedDistance <= .005f * .005f ||
                    Vector3.DistanceSquared(P(b.Position), carriedEnd) > carriedDistance * .25f * .25f) continue;
                var rotationA = Quaternion.Inverse(startRotation) * Q(a.Rotation);
                var rotationB = Quaternion.Inverse(endRotation) * Q(b.Rotation);
                if (Math.Abs(Quaternion.Dot(rotationA, rotationB)) < .99965f) continue;
                if (match != null) return null; // Ambiguous overlapping vehicles remain unmodified.
                match = new Binding { Vehicle = candidate.Id, Start = localA, End = localB,
                    StartRotation = rotationA, EndRotation = rotationB };
            }
            return match;
        }

        private bool Pose(string id, double time, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = default; rotation = Quaternion.Identity; scale = Vector3.One;
            var index = FrameIndex(time);
            if (index < 0) return false;
            var left = session.Frames[index]; var a = Find(left, id);
            if (a == null || !a.Active) return false;
            if (time > left.Time && index + 1 == session.Frames.Count) return false;
            position = P(a.Position); rotation = Q(a.Rotation); scale = P(a.Scale);
            if (index + 1 < session.Frames.Count && time > left.Time)
            {
                var right = session.Frames[index + 1]; var b = Find(right, id);
                if (b == null || !b.Active || b.Kind != a.Kind || right.Time - left.Time > .5) return false;
                var amount = (float)((time - left.Time) / (right.Time - left.Time));
                position = Vector3.Lerp(position, P(b.Position), amount);
                rotation = Quaternion.Slerp(rotation, Q(b.Rotation), amount);
                scale = Vector3.Lerp(scale, P(b.Scale), amount);
            }
            return Math.Abs(scale.X) > .00001 && Math.Abs(scale.Y) > .00001 && Math.Abs(scale.Z) > .00001;
        }

        private int FrameIndex(double time)
        {
            var lo = 0; var hi = session.Frames.Count;
            while (lo < hi) { var mid = (lo + hi) / 2; if (session.Frames[mid].Time <= time) lo = mid + 1; else hi = mid; }
            return lo - 1;
        }
        private static EntitySnapshot? Find(ReplayFrame frame, string id)
        { foreach (var entity in frame.Entities) if (entity.Id == id) return entity; return null; }
        private static bool Flag(EntitySnapshot entity, string name) => entity.State.TryGetValue(name, out var value) &&
            string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
        private static bool Inside(Vector3 value) => Math.Abs(value.X) < 2.5f && value.Y > -3 && value.Y < 3.5f && Math.Abs(value.Z) < 5;
        private static Vector3 P(Vec3 value) => new Vector3(value.X, value.Y, value.Z);
        private static Quaternion Q(Quat value) => Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
        private static Vec3 V(Vector3 value) => new Vec3(value.X, value.Y, value.Z);
        private static Quat R(Quaternion value) => new Quat(value.X, value.Y, value.Z, value.W);
    }
}
