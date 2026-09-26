using System;
using System.Collections.Generic;

namespace LCReplay.Core
{
    public static class ReplayTimeline
    {
        /// <summary>
        /// Samples a full-state timeline. Spawn, despawn, activation and discrete state change exactly
        /// at the next recorded frame. Adjacent matching poses interpolate; missing entities never do.
        /// The returned frame is independent of the stored recording and safe for a viewer to modify.
        /// </summary>
        public static ReplayFrame Sample(ReplaySession session, double time)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (double.IsNaN(time) || double.IsInfinity(time)) throw new ArgumentOutOfRangeException(nameof(time));
            var frames = session.Frames;
            if (frames.Count == 0) return new ReplayFrame { Time = Math.Max(0, Math.Min(time, session.Duration)) };
            time = Math.Max(0, Math.Min(time, Math.Max(session.Duration, frames[frames.Count - 1].Time)));
            int low = 0, high = frames.Count;
            // Upper bound picks the final snapshot at a duplicated timestamp deterministically.
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (frames[mid].Time <= time) low = mid + 1;
                else high = mid;
            }
            int leftIndex = Math.Max(0, low - 1);
            var left = frames[leftIndex];
            var result = new ReplayFrame { Time = time, State = Copy(left.State) };
            ReplayFrame? right = leftIndex + 1 < frames.Count && time > left.Time ? frames[leftIndex + 1] : null;
            double amount = right == null ? 0 : Math.Max(0, Math.Min(1, (time - left.Time) / (right.Time - left.Time)));
            var rightAnchors = new Dictionary<string, AnchorPose>(StringComparer.Ordinal);
            if (right != null) foreach (var anchor in right.Anchors) rightAnchors[anchor.Id] = anchor;
            foreach (var anchor in left.Anchors)
            {
                rightAnchors.TryGetValue(anchor.Id, out var other);
                result.Anchors.Add(new AnchorPose
                {
                    Id = anchor.Id,
                    Position = other == null ? anchor.Position : Lerp(anchor.Position, other.Position, amount),
                    Rotation = other == null ? anchor.Rotation : Slerp(anchor.Rotation, other.Rotation, amount),
                    Scale = other == null ? anchor.Scale : Lerp(anchor.Scale, other.Scale, amount)
                });
            }
            var rightEntities = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
            if (right != null) foreach (var entity in right.Entities) rightEntities[entity.Id] = entity;
            foreach (var entity in left.Entities)
            {
                EntitySnapshot? other = null;
                if (rightEntities.TryGetValue(entity.Id, out var candidate) && candidate.Kind == entity.Kind && candidate.Active == entity.Active)
                    other = candidate;
                var sampled = new EntitySnapshot
                {
                    Id = entity.Id, Kind = entity.Kind, Name = entity.Name, Active = entity.Active,
                    Position = other == null ? entity.Position : Lerp(entity.Position, other.Position, amount),
                    Rotation = other == null ? entity.Rotation : Slerp(entity.Rotation, other.Rotation, amount),
                    Scale = other == null ? entity.Scale : Lerp(entity.Scale, other.Scale, amount),
                    State = Copy(entity.State)
                };
                var rightBones = new Dictionary<string, BonePose>(StringComparer.Ordinal);
                if (other != null) foreach (var bone in other.Bones) rightBones[bone.Path] = bone;
                foreach (var bone in entity.Bones)
                {
                    bool match = rightBones.TryGetValue(bone.Path, out var otherBone);
                    sampled.Bones.Add(new BonePose
                    {
                        Path = bone.Path,
                        Position = match ? Lerp(bone.Position, otherBone!.Position, amount) : bone.Position,
                        Rotation = match ? Slerp(bone.Rotation, otherBone!.Rotation, amount) : bone.Rotation,
                        Scale = match ? Lerp(bone.Scale, otherBone!.Scale, amount) : bone.Scale
                    });
                }
                var rightRenderers = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
                if (other != null) foreach (var renderer in other.Renderers) rightRenderers[renderer.Id] = renderer;
                foreach (var renderer in entity.Renderers)
                {
                    bool match = rightRenderers.TryGetValue(renderer.Id, out var otherRenderer) && renderer.Active && otherRenderer.Active;
                    sampled.Renderers.Add(new RenderPose
                    {
                        Id = renderer.Id, Active = renderer.Active,
                        Position = match ? Lerp(renderer.Position, otherRenderer!.Position, amount) : renderer.Position,
                        Rotation = match ? Slerp(renderer.Rotation, otherRenderer!.Rotation, amount) : renderer.Rotation,
                        Scale = match ? Lerp(renderer.Scale, otherRenderer!.Scale, amount) : renderer.Scale
                    });
                }
                result.Entities.Add(sampled);
            }
            return result;
        }

        private static Dictionary<string, string> Copy(Dictionary<string, string> source) =>
            new Dictionary<string, string>(source, StringComparer.Ordinal);

        private static Vec3 Lerp(Vec3 a, Vec3 b, double t) => new Vec3(
            (float)(a.X + ((double)b.X - a.X) * t),
            (float)(a.Y + ((double)b.Y - a.Y) * t),
            (float)(a.Z + ((double)b.Z - a.Z) * t));

        private static Quat Normalize(Quat q)
        {
            double norm = Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W);
            if (norm < 1e-12) return Quat.Identity;
            return new Quat((float)(q.X / norm), (float)(q.Y / norm), (float)(q.Z / norm), (float)(q.W / norm));
        }

        private static Quat Slerp(Quat a, Quat b, double t)
        {
            a = Normalize(a); b = Normalize(b);
            double dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
            if (dot < 0)
            {
                b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }
            dot = Math.Max(0, Math.Min(1, dot));
            double first = 1 - t, second = t;
            if (dot < 0.9995)
            {
                double theta = Math.Acos(dot), sine = Math.Sin(theta);
                first = Math.Sin((1 - t) * theta) / sine;
                second = Math.Sin(t * theta) / sine;
            }
            return Normalize(new Quat(
                (float)(a.X * first + b.X * second), (float)(a.Y * first + b.Y * second),
                (float)(a.Z * first + b.Z * second), (float)(a.W * first + b.W * second)));
        }
    }
}
