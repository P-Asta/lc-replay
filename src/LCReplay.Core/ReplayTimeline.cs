using System;
using System.Collections.Generic;
using System.Linq;

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
            var result = new ReplayFrame { Time = time, State = scratch == null ? Copy(left.State) : left.State };
            ReplayFrame? right = leftIndex + 1 < frames.Count && time > left.Time ? frames[leftIndex + 1] : null;
            double amount = right == null ? 0 : Math.Max(0, Math.Min(1, (time - left.Time) / (right.Time - left.Time)));
            var sampleGap = right != null && right.Time - left.Time > .25 && session.Events.Any(evt =>
                evt.Category == "capture" && evt.Name == "sample-gap" &&
                evt.Time > left.Time && evt.Time <= right.Time + .0001);
            SampleParticles(left, right, result, amount, Math.Max(0, time - left.Time), scratch);
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
                if (other != null && entity.Kind == "player")
                {
                    var elapsed = right!.Time - left.Time;
                    var horizontalReach = Math.Max(3.0, 12.0 * elapsed);
                    var verticalReach = Math.Max(6.0, 50.0 * elapsed);
                    var dx = (double)entity.Position.X - other.Position.X;
                    var dy = (double)entity.Position.Y - other.Position.Y;
                    var dz = (double)entity.Position.Z - other.Position.Z;
                    var wasDead = entity.State.TryGetValue("isPlayerDead", out var oldDead) &&
                        string.Equals(oldDead, "True", StringComparison.OrdinalIgnoreCase);
                    var isDead = other.State.TryGetValue("isPlayerDead", out var newDead) &&
                        string.Equals(newDead, "True", StringComparison.OrdinalIgnoreCase);
                    if (sampleGap || wasDead != isDead ||
                        dx * dx + dz * dz > horizontalReach * horizontalReach ||
                        Math.Abs(dy) > verticalReach)
                        other = null;
                }
                var sampled = new EntitySnapshot
                {
                    Id = entity.Id, Kind = entity.Kind, Name = entity.Name, Active = entity.Active,
                    PoseFromItemEvents = entity.PoseFromItemEvents,
                    Position = other == null || entity.PoseFromItemEvents || other.PoseFromItemEvents
                        ? entity.Position : Lerp(entity.Position, other.Position, amount),
                    Rotation = other == null || entity.PoseFromItemEvents || other.PoseFromItemEvents
                        ? entity.Rotation : Slerp(entity.Rotation, other.Rotation, amount),
                    ViewRotation = entity.ViewRotation.HasValue && other?.ViewRotation.HasValue == true
                        ? Slerp(entity.ViewRotation.Value, other.ViewRotation.Value, amount) : entity.ViewRotation,
                    ViewPosition = entity.ViewPosition.HasValue && other?.ViewPosition.HasValue == true
                        ? Lerp(entity.ViewPosition.Value, other.ViewPosition.Value, amount) : entity.ViewPosition,
                    Scale = other == null || entity.PoseFromItemEvents || other.PoseFromItemEvents
                        ? entity.Scale : Lerp(entity.Scale, other.Scale, amount),
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
            if (right != null && amount > 0 && amount < 1)
                AttachHeldItems(left, right, result, amount);
            return result;
        }

        private static void AttachHeldItems(ReplayFrame left, ReplayFrame right, ReplayFrame sampled, double amount)
        {
            if (!left.Entities.Exists(entity => entity.Kind == "item" && Held(entity))) return;
            var leftPlayers = left.Entities.Where(entity => entity.Kind == "player" && entity.Active)
                .ToDictionary(entity => entity.Id, StringComparer.Ordinal);
            var rightPlayers = right.Entities.Where(entity => entity.Kind == "player" && entity.Active)
                .ToDictionary(entity => entity.Id, StringComparer.Ordinal);
            var sampledPlayers = sampled.Entities.Where(entity => entity.Kind == "player" && entity.Active)
                .ToDictionary(entity => entity.Id, StringComparer.Ordinal);
            var rightItems = right.Entities.Where(entity => entity.Kind == "item")
                .ToDictionary(entity => entity.Id, StringComparer.Ordinal);
            foreach (var item in sampled.Entities.Where(entity => entity.Kind == "item" && !entity.PoseFromItemEvents))
            {
                var before = left.Entities.FirstOrDefault(entity => entity.Id == item.Id);
                if (before == null || !rightItems.TryGetValue(item.Id, out var after) ||
                    after.PoseFromItemEvents || !Held(before) || !Held(after)) continue;
                var holderId = before.State.TryGetValue("$heldBy", out var explicitHolder) &&
                    after.State.TryGetValue("$heldBy", out var nextHolder) && explicitHolder == nextHolder
                    ? explicitHolder : "";
                if (holderId.Length == 0)
                {
                    // Old recordings omit the holder ID. A nearby player at both
                    // endpoints is sufficient to recover the held transform.
                    holderId = leftPlayers.Values.Where(player => rightPlayers.ContainsKey(player.Id) &&
                        DistanceSquared(player.Position, before.Position) < 12.25 &&
                        DistanceSquared(rightPlayers[player.Id].Position, after.Position) < 12.25)
                        .OrderBy(player => DistanceSquared(player.Position, before.Position))
                        .Select(player => player.Id).FirstOrDefault() ?? "";
                }
                if (!leftPlayers.TryGetValue(holderId, out var leftPlayer) ||
                    !rightPlayers.TryGetValue(holderId, out var rightPlayer) ||
                    !sampledPlayers.TryGetValue(holderId, out var sampledPlayer)) continue;
                var localBefore = Rotate(Inverse(leftPlayer.Rotation), Subtract(before.Position, leftPlayer.Position));
                var localAfter = Rotate(Inverse(rightPlayer.Rotation), Subtract(after.Position, rightPlayer.Position));
                item.Position = Add(sampledPlayer.Position, Rotate(sampledPlayer.Rotation, Lerp(localBefore, localAfter, amount)));
                var rotationBefore = Multiply(Inverse(leftPlayer.Rotation), before.Rotation);
                var rotationAfter = Multiply(Inverse(rightPlayer.Rotation), after.Rotation);
                item.Rotation = Multiply(sampledPlayer.Rotation, Slerp(rotationBefore, rotationAfter, amount));
            }
        }

        private static bool Held(EntitySnapshot entity) => entity.State.TryGetValue("isHeld", out var value) &&
            string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

        private static void SampleParticles(ReplayFrame left, ReplayFrame? right, ReplayFrame result,
            double amount, double elapsed, ReplayTimelineSampler? scratch)
        {
            var matches = scratch?.RightParticles ?? new Dictionary<(string, uint), ParticlePose?>();
            matches.Clear();
            if (right != null)
                foreach (var particle in right.Particles)
                {
                    var key = (particle.EmitterId, particle.RandomSeed);
                    // Legacy seed=0 and repeated seeds have no stable identity.
                    if (particle.RandomSeed == 0) continue;
                    if (matches.ContainsKey(key)) matches[key] = null; else matches.Add(key, particle);
                }
            var used = 0;
            foreach (var particle in left.Particles)
            {
                if (particle.RemainingLifetime <= elapsed) continue;
                matches.TryGetValue((particle.EmitterId, particle.RandomSeed), out var next);
                var matched = next != null && right != null && particle.RandomSeed != 0 &&
                    Math.Abs(next.Lifetime - particle.Lifetime) < .01f &&
                    Math.Abs(particle.RemainingLifetime - next.RemainingLifetime - (right.Time - left.Time)) < .05;
                var pose = scratch?.ParticleAt(used++) ?? new ParticlePose();
                pose.EmitterId = particle.EmitterId; pose.RandomSeed = particle.RandomSeed;
                pose.IsInterior = particle.IsInterior; pose.Lifetime = particle.Lifetime;
                pose.RemainingLifetime = matched ? (float)(particle.RemainingLifetime + (next!.RemainingLifetime - particle.RemainingLifetime) * amount) :
                    (float)(particle.RemainingLifetime - elapsed);
                pose.Position = matched ? Lerp(particle.Position, next!.Position, amount) :
                    new Vec3(particle.Position.X + particle.Velocity.X * (float)elapsed,
                        particle.Position.Y + particle.Velocity.Y * (float)elapsed, particle.Position.Z + particle.Velocity.Z * (float)elapsed);
                pose.Velocity = matched ? Lerp(particle.Velocity, next!.Velocity, amount) : particle.Velocity;
                pose.Size3D = matched ? Lerp(particle.Size3D, next!.Size3D, amount) : particle.Size3D;
                pose.Rotation3D = matched ? LerpAngles(particle.Rotation3D, next!.Rotation3D, amount) : particle.Rotation3D;
                pose.Size = matched ? (float)(particle.Size + (next!.Size - particle.Size) * amount) : particle.Size;
                pose.Rotation = matched ? Angle(particle.Rotation, next!.Rotation, amount) : particle.Rotation;
                for (var i = 0; i < 4; i++) pose.Color[i] = matched ?
                    (float)(particle.Color[i] + (next!.Color[i] - particle.Color[i]) * amount) : particle.Color[i];
                result.Particles.Add(pose);
            }
            var styles = scratch?.RightParticleStyles ?? new Dictionary<string, ParticleStyleSnapshot>(StringComparer.Ordinal);
            styles.Clear();
            if (right != null) foreach (var style in right.ParticleStyles) styles[style.Id] = style;
            used = 0;
            foreach (var style in left.ParticleStyles)
            {
                styles.TryGetValue(style.Id, out var next);
                var matching = next != null && next.RandomSeed == style.RandomSeed && next.Name == style.Name;
                var pose = scratch?.StyleAt(used++) ?? new ParticleStyleSnapshot();
                pose.Id = style.Id; pose.Name = style.Name; pose.ParentName = style.ParentName;
                pose.MaterialName = style.MaterialName; pose.ShaderName = style.ShaderName; pose.MeshName = style.MeshName;
                pose.Simulate = style.Simulate; pose.IsInterior = style.IsInterior; pose.RandomSeed = style.RandomSeed;
                pose.Position = matching ? Lerp(style.Position, next!.Position, amount) : style.Position;
                pose.Rotation = matching ? Slerp(style.Rotation, next!.Rotation, amount) : style.Rotation;
                pose.Scale = matching ? Lerp(style.Scale, next!.Scale, amount) : style.Scale;
                // Extrapolate through a looping clock reset; seeking still picks the new snapshot.
                pose.Time = matching && next!.Time >= style.Time ? (float)(style.Time + (next.Time - style.Time) * amount) :
                    style.Time + (style.Simulate ? (float)elapsed : 0);
                pose.RenderMode = style.RenderMode; pose.Alignment = style.Alignment;
                pose.VertexStreams = scratch == null ? (int[])style.VertexStreams.Clone() : style.VertexStreams;
                pose.LengthScale = style.LengthScale; pose.VelocityScale = style.VelocityScale;
                pose.CameraVelocityScale = style.CameraVelocityScale; pose.Pivot = style.Pivot;
                result.ParticleStyles.Add(pose);
            }
        }

        private static float Angle(float a, float b, double t) => a + (float)(((b - a + 540) % 360 - 180) * t);
        private static Vec3 LerpAngles(Vec3 a, Vec3 b, double t) => new Vec3(Angle(a.X, b.X, t), Angle(a.Y, b.Y, t), Angle(a.Z, b.Z, t));
        private static double DistanceSquared(Vec3 a, Vec3 b) =>
            (double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y) + (double)(a.Z - b.Z) * (a.Z - b.Z);
        private static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static Vec3 Subtract(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Quat Inverse(Quat q) { q = Normalize(q); return new Quat(-q.X, -q.Y, -q.Z, q.W); }
        private static Quat Multiply(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        private static Vec3 Rotate(Quat q, Vec3 value)
        {
            q = Normalize(q);
            var x = q.W * value.X + q.Y * value.Z - q.Z * value.Y;
            var y = q.W * value.Y + q.Z * value.X - q.X * value.Z;
            var z = q.W * value.Z + q.X * value.Y - q.Y * value.X;
            var w = -q.X * value.X - q.Y * value.Y - q.Z * value.Z;
            return new Vec3(
                x * q.W + w * -q.X + y * -q.Z - z * -q.Y,
                y * q.W + w * -q.Y + z * -q.X - x * -q.Z,
                z * q.W + w * -q.Z + x * -q.Y - y * -q.X);
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
    /// Reuses interpolation lookup tables and particle/style objects during playback.
    /// Consume sampled effects before calling Sample again. State dictionaries and
    /// vertex streams are read-only recording views; a caller must not modify them.
    /// </summary>
    public sealed class ReplayTimelineSampler
    {
        internal readonly Dictionary<(string, uint), ParticlePose?> RightParticles = new Dictionary<(string, uint), ParticlePose?>();
        internal readonly Dictionary<string, ParticleStyleSnapshot> RightParticleStyles = new Dictionary<string, ParticleStyleSnapshot>(StringComparer.Ordinal);
        private readonly List<ParticlePose> particles = new List<ParticlePose>();
        private readonly List<ParticleStyleSnapshot> styles = new List<ParticleStyleSnapshot>();
        internal ParticlePose ParticleAt(int index) { if (index == particles.Count) particles.Add(new ParticlePose()); return particles[index]; }
        internal ParticleStyleSnapshot StyleAt(int index) { if (index == styles.Count) styles.Add(new ParticleStyleSnapshot()); return styles[index]; }
        internal readonly Dictionary<string, LinePose> RightLines = new Dictionary<string, LinePose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, AnchorPose> RightAnchors = new Dictionary<string, AnchorPose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, RenderPose> RightSceneRenderers = new Dictionary<string, RenderPose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, EntitySnapshot> RightEntities = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        internal readonly Dictionary<string, BonePose> RightBones = new Dictionary<string, BonePose>(StringComparer.Ordinal);
        internal readonly Dictionary<string, RenderPose> RightRenderers = new Dictionary<string, RenderPose>(StringComparer.Ordinal);

        public ReplayFrame Sample(ReplaySession session, double time) => ReplayTimeline.SampleCore(session, time, this);
    }
}
