using System;
using System.Collections.Generic;
using System.Globalization;

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

        internal static ReplayFrame SampleCore(ReplaySession session, double time, ReplayTimelineSampler? scratch, bool reusePoses = false)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (double.IsNaN(time) || double.IsInfinity(time)) throw new ArgumentOutOfRangeException(nameof(time));
            var frames = session.Frames;
            if (frames.Count == 0)
            {
                var empty = reusePoses ? scratch!.BeginFrame() : new ReplayFrame();
                empty.Time = Math.Max(0, Math.Min(time, session.Duration));
                return empty;
            }
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
            var result = reusePoses ? scratch!.BeginFrame() : new ReplayFrame();
            result.Time = time;
            result.State = scratch == null ? Copy(left.State) : left.State;
            ReplayFrame? right = leftIndex + 1 < frames.Count && time > left.Time ? frames[leftIndex + 1] : null;
            double amount = right == null ? 0 : Math.Max(0, Math.Min(1, (time - left.Time) / (right.Time - left.Time)));
            var sampleGap = right != null && right.Time - left.Time > .25 && HasSampleGap(session, left.Time, right.Time);
            SampleParticles(left, right, result, amount, Math.Max(0, time - left.Time), scratch);
            var rightLines = scratch?.RightLines ?? new Dictionary<string, LinePose>(StringComparer.Ordinal);
            rightLines.Clear();
            if (right != null) foreach (var line in right.Lines) rightLines[line.Id] = line;
            foreach (var line in left.Lines)
            {
                var match = rightLines.TryGetValue(line.Id, out var next) && next.Positions.Length == line.Positions.Length;
                var pose = reusePoses ? scratch!.NextLine() : new LinePose();
                var positions = pose.Positions.Length == line.Positions.Length ? pose.Positions : new float[line.Positions.Length];
                for (var i = 0; i < positions.Length; i++)
                    positions[i] = match ? (float)(line.Positions[i] + (next!.Positions[i] - line.Positions[i]) * amount) : line.Positions[i];
                pose.Id = line.Id; pose.Positions = positions; pose.IsInterior = line.IsInterior;
                pose.MaterialName = line.MaterialName; pose.ShaderName = line.ShaderName;
                pose.TextureMode = line.TextureMode; pose.Alignment = line.Alignment;
                pose.StartColor = scratch == null ? (float[])line.StartColor.Clone() : line.StartColor;
                pose.EndColor = scratch == null ? (float[])line.EndColor.Clone() : line.EndColor;
                pose.StartWidth = match ? (float)(line.StartWidth + (next!.StartWidth - line.StartWidth) * amount) : line.StartWidth;
                pose.EndWidth = match ? (float)(line.EndWidth + (next!.EndWidth - line.EndWidth) * amount) : line.EndWidth;
                result.Lines.Add(pose);
            }
            var rightAnchors = scratch?.RightAnchors ?? new Dictionary<string, AnchorPose>(StringComparer.Ordinal);
            rightAnchors.Clear();
            if (right != null) foreach (var anchor in right.Anchors) rightAnchors[anchor.Id] = anchor;
            foreach (var anchor in left.Anchors)
            {
                rightAnchors.TryGetValue(anchor.Id, out var other);
                var pose = reusePoses ? scratch!.NextAnchor() : new AnchorPose();
                pose.Id = anchor.Id;
                pose.Position = other == null ? anchor.Position : Lerp(anchor.Position, other.Position, amount);
                pose.Rotation = other == null ? anchor.Rotation : Slerp(anchor.Rotation, other.Rotation, amount);
                pose.Scale = other == null ? anchor.Scale : Lerp(anchor.Scale, other.Scale, amount);
                result.Anchors.Add(pose);
            }
            var rightSceneRenderers = scratch?.RightSceneRenderers ?? new Dictionary<string, RenderPose>(StringComparer.Ordinal);
            rightSceneRenderers.Clear();
            if (right != null) foreach (var renderer in right.SceneRenderers) rightSceneRenderers[renderer.Id] = renderer;
            foreach (var renderer in left.SceneRenderers)
            {
                bool match = rightSceneRenderers.TryGetValue(renderer.Id, out var other) && renderer.Active && other.Active;
                var pose = reusePoses ? scratch!.NextRenderer() : new RenderPose();
                pose.Id = renderer.Id; pose.Active = renderer.Active;
                pose.Position = match ? Lerp(renderer.Position, other!.Position, amount) : renderer.Position;
                pose.Rotation = match ? Slerp(renderer.Rotation, other!.Rotation, amount) : renderer.Rotation;
                pose.Scale = match ? Lerp(renderer.Scale, other!.Scale, amount) : renderer.Scale;
                result.SceneRenderers.Add(pose);
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
                    if (sampleGap && !CanBridgePlayerGap(session, entity, other, left.Time, right.Time) || wasDead != isDead ||
                        HasPlayerTeleport(session, entity, other, left.Time, right.Time) ||
                        dx * dx + dz * dz > horizontalReach * horizontalReach ||
                        Math.Abs(dy) > verticalReach)
                        other = null;
                }
                var sampled = reusePoses ? scratch!.NextEntity() : new EntitySnapshot();
                sampled.Id = entity.Id; sampled.Kind = entity.Kind; sampled.Name = entity.Name; sampled.Active = entity.Active;
                sampled.PoseFromItemEvents = entity.PoseFromItemEvents;
                sampled.Position = other == null || entity.PoseFromItemEvents || other.PoseFromItemEvents
                    ? entity.Position : Lerp(entity.Position, other.Position, amount);
                sampled.Rotation = other == null || entity.PoseFromItemEvents || other.PoseFromItemEvents
                    ? entity.Rotation : Slerp(entity.Rotation, other.Rotation, amount);
                sampled.ViewRotation = entity.ViewRotation.HasValue && other?.ViewRotation.HasValue == true
                    ? Slerp(entity.ViewRotation.Value, other.ViewRotation.Value, amount) : entity.ViewRotation;
                sampled.ViewPosition = entity.ViewPosition.HasValue && other?.ViewPosition.HasValue == true
                    ? Lerp(entity.ViewPosition.Value, other.ViewPosition.Value, amount) : entity.ViewPosition;
                sampled.Scale = other == null || entity.PoseFromItemEvents || other.PoseFromItemEvents
                    ? entity.Scale : Lerp(entity.Scale, other.Scale, amount);
                sampled.State = scratch == null ? Copy(entity.State) : entity.State;
                if (other != null)
                    sampled.State = ReplayBlendShapes.Interpolate(entity, other, amount, scratch?.BlendShapes) ?? sampled.State;
                var rightBones = scratch?.RightBones ?? new Dictionary<string, BonePose>(StringComparer.Ordinal);
                rightBones.Clear();
                if (other != null) foreach (var bone in other.Bones) rightBones[bone.Path] = bone;
                foreach (var bone in entity.Bones)
                {
                    bool match = rightBones.TryGetValue(bone.Path, out var otherBone);
                    var pose = reusePoses ? scratch!.NextBone() : new BonePose();
                    pose.Path = bone.Path;
                    pose.Position = match ? Lerp(bone.Position, otherBone!.Position, amount) : bone.Position;
                    pose.Rotation = match ? Slerp(bone.Rotation, otherBone!.Rotation, amount) : bone.Rotation;
                    pose.Scale = match ? Lerp(bone.Scale, otherBone!.Scale, amount) : bone.Scale;
                    sampled.Bones.Add(pose);
                }
                var rightRenderers = scratch?.RightRenderers ?? new Dictionary<string, RenderPose>(StringComparer.Ordinal);
                rightRenderers.Clear();
                if (other != null) foreach (var renderer in other.Renderers) rightRenderers[renderer.Id] = renderer;
                foreach (var renderer in entity.Renderers)
                {
                    bool match = rightRenderers.TryGetValue(renderer.Id, out var otherRenderer) && renderer.Active && otherRenderer.Active;
                    var pose = reusePoses ? scratch!.NextRenderer() : new RenderPose();
                    pose.Id = renderer.Id; pose.Active = renderer.Active;
                    pose.Position = match ? Lerp(renderer.Position, otherRenderer!.Position, amount) : renderer.Position;
                    pose.Rotation = match ? Slerp(renderer.Rotation, otherRenderer!.Rotation, amount) : renderer.Rotation;
                    pose.Scale = match ? Lerp(renderer.Scale, otherRenderer!.Scale, amount) : renderer.Scale;
                    sampled.Renderers.Add(pose);
                }
                result.Entities.Add(sampled);
            }
            if (right != null && amount > 0 && amount < 1)
                AttachHeldItems(left, right, result, amount, scratch);
            return result;
        }

        private static bool CanBridgePlayerGap(ReplaySession session, EntitySnapshot before, EntitySnapshot after, double start, double end)
        {
            // Older recordings can miss several seconds while exporting the
            // world. Holding a living player until the next sample invents a
            // stop followed by a jump. Bridge short, plausible gaps only;
            // the ordinary distance/death checks still apply to the result.
            if (end - start > 5) return false;
            // Missing indoor samples do not establish a route through doors or
            // around walls. A speed-plausible straight chord can cut through the
            // facility. Keep the last observation until the next one arrives.
            if (before.State.TryGetValue("isInsideFactory", out var inside) &&
                string.Equals(inside, "True", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var field in new[] { "isInsideFactory", "isInElevator" })
            {
                before.State.TryGetValue(field, out var a);
                after.State.TryGetValue(field, out var b);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
            }
            foreach (var evt in session.Events)
                if (evt.Time > start && evt.Time <= end && evt.Category == "call" &&
                    evt.Name.IndexOf("TeleportPlayer", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static bool HasPlayerTeleport(ReplaySession session, EntitySnapshot before, EntitySnapshot after, double start, double end)
        {
            foreach (var evt in session.Events)
            {
                if (evt.Time <= start || evt.Time > end || evt.Category != "call" ||
                    evt.Name.IndexOf("TeleportPlayer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (evt.EntityId.Length != 0)
                {
                    if (evt.EntityId == before.Id) return true;
                    continue;
                }
                // Legacy hooks stored the destination but no replay entity ID.
                // Only associate an observed relocation when this player's next
                // sample is near that destination and the previous one was not.
                if (!evt.Data.TryGetValue("pos", out var text)) continue;
                var fields = text.Split(',');
                if (fields.Length != 3 || !float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
                double Distance(Vec3 p) => (double)(p.X - x) * (p.X - x) + (double)(p.Y - y) * (p.Y - y) + (double)(p.Z - z) * (p.Z - z);
                if (Distance(after.Position) <= 1 && Distance(before.Position) > 1) return true;
            }
            return false;
        }

        private static bool HasSampleGap(ReplaySession session, double start, double end)
        {
            foreach (var evt in session.Events)
                if (evt.Category == "capture" && evt.Name == "sample-gap" && evt.Time > start && evt.Time <= end + .0001)
                    return true;
            return false;
        }

        private static void AttachHeldItems(ReplayFrame left, ReplayFrame right, ReplayFrame sampled, double amount,
            ReplayTimelineSampler? scratch)
        {
            bool hasHeldItem = false;
            foreach (var entity in left.Entities)
                if (entity.Kind == "item" && !entity.PoseFromItemEvents && Held(entity)) { hasHeldItem = true; break; }
            if (!hasHeldItem) return;
            var leftPlayers = scratch?.LeftPlayers ?? new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
            var rightPlayers = scratch?.RightPlayers ?? new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
            var sampledPlayers = scratch?.SampledPlayers ?? new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
            var rightItems = scratch?.RightItems ?? new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
            leftPlayers.Clear(); rightPlayers.Clear(); sampledPlayers.Clear(); rightItems.Clear();
            foreach (var player in left.Entities)
                if (player.Kind == "player" && player.Active) leftPlayers[player.Id] = player;
            foreach (var entity in right.Entities)
            {
                if (entity.Kind == "player" && entity.Active) rightPlayers[entity.Id] = entity;
                else if (entity.Kind == "item") rightItems[entity.Id] = entity;
            }
            foreach (var player in sampled.Entities)
                if (player.Kind == "player" && player.Active) sampledPlayers[player.Id] = player;
            for (var index = 0; index < sampled.Entities.Count; index++)
            {
                var item = sampled.Entities[index];
                if (item.Kind != "item" || item.PoseFromItemEvents) continue;
                // Sampling preserves the left snapshot's entity order.
                var before = left.Entities[index];
                if (!rightItems.TryGetValue(item.Id, out var after) ||
                    after.PoseFromItemEvents || !Held(before) || !Held(after)) continue;
                var holderId = before.State.TryGetValue("$heldBy", out var explicitHolder) &&
                    after.State.TryGetValue("$heldBy", out var nextHolder) && explicitHolder == nextHolder
                    ? explicitHolder : "";
                if (holderId.Length == 0)
                {
                    // Old recordings omit the holder ID. A nearby player at both
                    // endpoints is sufficient to recover the held transform.
                    var closestDistance = 12.25;
                    foreach (var player in leftPlayers.Values)
                    {
                        var distance = DistanceSquared(player.Position, before.Position);
                        if (distance >= closestDistance || !rightPlayers.TryGetValue(player.Id, out var nextPlayer) ||
                            DistanceSquared(nextPlayer.Position, after.Position) >= 12.25) continue;
                        holderId = player.Id;
                        closestDistance = distance;
                    }
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
    /// vertex streams and line colors are read-only recording views; a caller must not modify them.
    /// </summary>
    public sealed class ReplayTimelineSampler
    {
        internal readonly ReplayBlendShapes.Cache BlendShapes = new ReplayBlendShapes.Cache();
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
        internal readonly Dictionary<string, EntitySnapshot> LeftPlayers = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        internal readonly Dictionary<string, EntitySnapshot> RightPlayers = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        internal readonly Dictionary<string, EntitySnapshot> SampledPlayers = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        internal readonly Dictionary<string, EntitySnapshot> RightItems = new Dictionary<string, EntitySnapshot>(StringComparer.Ordinal);
        private readonly ReplayFrame reusableFrame = new ReplayFrame();
        private readonly Dictionary<string, string> emptyState = new Dictionary<string, string>();
        private readonly List<EntitySnapshot> entities = new List<EntitySnapshot>();
        private readonly List<BonePose> bones = new List<BonePose>();
        private readonly List<RenderPose> renderers = new List<RenderPose>();
        private readonly List<AnchorPose> anchors = new List<AnchorPose>();
        private readonly List<LinePose> lines = new List<LinePose>();
        private int entityIndex, boneIndex, rendererIndex, anchorIndex, lineIndex;

        internal ReplayFrame BeginFrame()
        {
            reusableFrame.Entities.Clear(); reusableFrame.Anchors.Clear(); reusableFrame.SceneRenderers.Clear();
            reusableFrame.Particles.Clear(); reusableFrame.ParticleStyles.Clear(); reusableFrame.Lines.Clear();
            reusableFrame.State = emptyState;
            entityIndex = boneIndex = rendererIndex = anchorIndex = lineIndex = 0;
            return reusableFrame;
        }

        internal EntitySnapshot NextEntity()
        {
            if (entityIndex == entities.Count) entities.Add(new EntitySnapshot());
            var entity = entities[entityIndex++];
            entity.Bones.Clear(); entity.Renderers.Clear();
            return entity;
        }

        internal BonePose NextBone() { if (boneIndex == bones.Count) bones.Add(new BonePose()); return bones[boneIndex++]; }
        internal RenderPose NextRenderer() { if (rendererIndex == renderers.Count) renderers.Add(new RenderPose()); return renderers[rendererIndex++]; }
        internal AnchorPose NextAnchor() { if (anchorIndex == anchors.Count) anchors.Add(new AnchorPose()); return anchors[anchorIndex++]; }
        internal LinePose NextLine() { if (lineIndex == lines.Count) lines.Add(new LinePose()); return lines[lineIndex++]; }

        public ReplayFrame Sample(ReplaySession session, double time) => ReplayTimeline.SampleCore(session, time, this);

        /// <summary>
        /// Samples into reusable playback storage. The returned frame and every child pose are
        /// valid only until the next sampling call; consume them immediately. Do not pass
        /// this frame back as recording input or retain it for history. Recorded state dictionaries,
        /// particle vertex streams and line colors remain read-only views, as with Sample.
        /// </summary>
        public ReplayFrame SampleReusable(ReplaySession session, double time) => ReplayTimeline.SampleCore(session, time, this, true);
    }
}
