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
            => SampleCore(session, time, null);

        internal static ReplayFrame SampleCore(ReplaySession session, double time, ReplayTimelineSampler? scratch)
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
            var result = new ReplayFrame { Time = time, State = scratch == null ? Copy(left.State) : left.State,
                ParticleStyles = scratch == null ? new List<ParticleStyleSnapshot>(left.ParticleStyles) : left.ParticleStyles,
                Particles = scratch == null ? new List<ParticlePose>(left.Particles) : left.Particles };
            ReplayFrame? right = leftIndex + 1 < frames.Count && time > left.Time ? frames[leftIndex + 1] : null;
            double amount = right == null ? 0 : Math.Max(0, Math.Min(1, (time - left.Time) / (right.Time - left.Time)));
            var rightLines = scratch?.RightLines ?? new Dictionary<string, LinePose>(StringComparer.Ordinal);
            rightLines.Clear();
            if (right != null) foreach (var line in right.Lines) rightLines[line.Id] = line;
            foreach (var line in left.Lines)
            {
                if (!rightLines.TryGetValue(line.Id, out var next) || next.Positions.Length != line.Positions.Length)
                { result.Lines.Add(line); continue; }
                var positions = new float[line.Positions.Length];
                for (var i = 0; i < positions.Length; i++)
                    positions[i] = (float)(line.Positions[i] + (next.Positions[i] - line.Positions[i]) * amount);
                result.Lines.Add(new LinePose { Id = line.Id, Positions = positions, IsInterior = line.IsInterior,
                    MaterialName = line.MaterialName, ShaderName = line.ShaderName,
                    TextureMode = line.TextureMode, Alignment = line.Alignment,
                    StartColor = line.StartColor, EndColor = line.EndColor,
                    StartWidth = (float)(line.StartWidth + (next.StartWidth - line.StartWidth) * amount),
                    EndWidth = (float)(line.EndWidth + (next.EndWidth - line.EndWidth) * amount) });
            }
            var rightAnchors = scratch?.RightAnchors ?? new Dictionary<string, AnchorPose>(StringComparer.Ordinal);
            rightAnchors.Clear();
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
            var rightSceneRenderers = scratch?.RightSceneRenderers ?? new Dictionary<string, RenderPose>(StringComparer.Ordinal);
            rightSceneRenderers.Clear();
            if (right != null) foreach (var renderer in right.SceneRenderers) rightSceneRenderers[renderer.Id] = renderer;
            foreach (var renderer in left.SceneRenderers)
            {
                bool match = rightSceneRenderers.TryGetValue(renderer.Id, out var other) && renderer.Active && other.Active;
                result.SceneRenderers.Add(new RenderPose
                {
                    Id = renderer.Id, Active = renderer.Active,
                    Position = match ? Lerp(renderer.Position, other!.Position, amount) : renderer.Position,
                    Rotation = match ? Slerp(renderer.Rotation, other!.Rotation, amount) : renderer.Rotation,
                    Scale = match ? Lerp(renderer.Scale, other!.Scale, amount) : renderer.Scale
                });
            }
            var rightEntities = scratch?.RightEntities ?? new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
            rightEntities.Clear();
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
                    State = scratch == null ? Copy(entity.State) : entity.State
                };
                var rightBones = scratch?.RightBones ?? new Dictionary<string, BonePose>(StringComparer.Ordinal);
                rightBones.Clear();
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
                var rightRenderers = scratch?.RightRenderers ?? new Dictionary<string, RenderPose>(StringComparer.Ordinal);
                rightRenderers.Clear();
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

    /// <summary>
    /// Reuses interpolation lookup tables during playback. Sampled poses are new,
    /// but state dictionaries and particle lists are read-only views of the recording.
    /// A caller must not modify them.
    /// </summary>
    public sealed class ReplayTimelineSampler
    {
        internal readonly Dictionary<string, LinePose> RightLines = new Dictionary<string, LinePose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, AnchorPose> RightAnchors = new Dictionary<string, AnchorPose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, RenderPose> RightSceneRenderers = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, EntitySnapshot> RightEntities = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        internal readonly Dictionary<string, BonePose> RightBones = new Dictionary<string, BonePose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, RenderPose> RightRenderers = new Dictionary<string, RenderPose>(StringComparer.Ordinal);

        public ReplayFrame Sample(ReplaySession session, double time) => ReplayTimeline.SampleCore(session, time, this);
    }
}
