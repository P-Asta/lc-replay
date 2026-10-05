using System;
using System.Collections.Generic;

namespace LCReplay.Core
{
    public static partial class ReplayTimeline
    {
        // A prepared window is immutable. Reuse identity joins and quaternion
        // normalization while rendering between the same two captured frames.
        // The general sampler remains the reference and builds each new bracket.
        internal sealed class PlaybackPlan
        {
            private ReplayFrame? left, right, output;
            private bool eligible, ready;
            private readonly List<EntityBinding> entities = new List<EntityBinding>();
            private readonly List<(BonePose Target, PoseBlend Blend)> bones = new List<(BonePose, PoseBlend)>();
            private readonly List<(RenderPose Target, PoseBlend Blend)> renderers = new List<(RenderPose, PoseBlend)>();
            private readonly List<(AnchorPose Target, PoseBlend Blend)> anchors = new List<(AnchorPose, PoseBlend)>();

            internal void Invalidate() { ready = false; }
            internal bool Matches(ReplayFrame before, ReplayFrame? after) =>
                ready && ReferenceEquals(left, before) && ReferenceEquals(right, after);
            internal void Begin(ReplayFrame before, ReplayFrame? after, ReplayFrame target)
            {
                left = before; right = after; output = target; ready = false;
                // Effects retain their original lifetime/seed and discrete line
                // handling. Their samples are not covered by this pose-only plan.
                eligible = before.Particles.Count == 0 && before.ParticleStyles.Count == 0 && before.Lines.Count == 0 &&
                    (after == null || after.Particles.Count == 0 && after.ParticleStyles.Count == 0 && after.Lines.Count == 0);
                entities.Clear(); bones.Clear(); renderers.Clear(); anchors.Clear();
            }
            internal void Commit() { ready = eligible; }

            internal void Add(EntitySnapshot target, EntitySnapshot before, EntitySnapshot? after)
            {
                if (!eligible) return;
                var bodyAfter = before.PoseFromItemEvents || after?.PoseFromItemEvents == true ? null : after;
                var blendShapes = false;
                if (after != null)
                    foreach (var pair in before.State)
                        if (pair.Key.StartsWith(ReplayBlendShapes.Prefix, StringComparison.Ordinal) &&
                            after.State.TryGetValue(pair.Key, out var next) && pair.Value != next)
                        { blendShapes = true; break; }
                entities.Add(new EntityBinding {
                    Target = target, Before = before, After = after, BlendShapes = blendShapes,
                    Body = new PoseBlend(before.Position, before.Rotation, before.Scale,
                        bodyAfter?.Position, bodyAfter?.Rotation, bodyAfter?.Scale),
                    View = before.ViewRotation.HasValue && after?.ViewRotation.HasValue == true
                        ? new RotationBlend(before.ViewRotation.Value, after.ViewRotation.Value) : default
                });
            }
            internal void Add(BonePose target, BonePose before, BonePose? after)
            {
                if (eligible) bones.Add((target, new PoseBlend(before.Position, before.Rotation, before.Scale,
                    after?.Position, after?.Rotation, after?.Scale)));
            }
            internal void Add(RenderPose target, RenderPose before, RenderPose? after)
            {
                if (eligible) renderers.Add((target, new PoseBlend(before.Position, before.Rotation, before.Scale,
                    after?.Position, after?.Rotation, after?.Scale)));
            }
            internal void Add(AnchorPose target, AnchorPose before, AnchorPose? after)
            {
                if (eligible) anchors.Add((target, new PoseBlend(before.Position, before.Rotation, before.Scale,
                    after?.Position, after?.Rotation, after?.Scale)));
            }

            internal ReplayFrame Sample(double time, ReplayTimelineSampler scratch)
            {
                var result = output!;
                result.Time = time;
                // Legacy vehicle lead-in may append a transient entity after
                // sampling. Restore the recorded membership before each pass.
                result.Entities.Clear();
                var amount = right == null ? 0 : Math.Max(0, Math.Min(1, (time - left!.Time) / (right.Time - left.Time)));
                foreach (var binding in entities)
                {
                    var target = binding.Target; var before = binding.Before; var after = binding.After;
                    result.Entities.Add(target);
                    binding.Body.Sample(amount, out var p, out var q, out var s);
                    target.Position = p; target.Rotation = q; target.Scale = s;
                    target.ViewPosition = before.ViewPosition.HasValue && after?.ViewPosition.HasValue == true
                        ? Lerp(before.ViewPosition.Value, after.ViewPosition.Value, amount) : before.ViewPosition;
                    target.ViewRotation = before.ViewRotation.HasValue && after?.ViewRotation.HasValue == true
                        ? binding.View.Sample(amount) : before.ViewRotation;
                    target.State = binding.BlendShapes
                        ? ReplayBlendShapes.Interpolate(before, after!, amount, scratch.BlendShapes) ?? before.State : before.State;
                }
                foreach (var binding in bones)
                {
                    binding.Blend.Sample(amount, out var p, out var q, out var s);
                    binding.Target.Position = p; binding.Target.Rotation = q; binding.Target.Scale = s;
                }
                foreach (var binding in renderers)
                {
                    binding.Blend.Sample(amount, out var p, out var q, out var s);
                    binding.Target.Position = p; binding.Target.Rotation = q; binding.Target.Scale = s;
                }
                foreach (var binding in anchors)
                {
                    binding.Blend.Sample(amount, out var p, out var q, out var s);
                    binding.Target.Position = p; binding.Target.Rotation = q; binding.Target.Scale = s;
                }
                if (right != null && amount > 0 && amount < 1) AttachHeldItems(left!, right, result, amount, scratch);
                return result;
            }

            private struct EntityBinding
            {
                internal EntitySnapshot Target, Before;
                internal EntitySnapshot? After;
                internal PoseBlend Body;
                internal RotationBlend View;
                internal bool BlendShapes;
            }
            private readonly struct PoseBlend
            {
                private readonly Vec3 position, nextPosition, scale, nextScale;
                private readonly Quat rotation;
                private readonly RotationBlend blend;
                private readonly bool matched;
                internal PoseBlend(Vec3 p, Quat q, Vec3 s, Vec3? np, Quat? nq, Vec3? ns)
                {
                    position = p; rotation = q; scale = s; matched = np.HasValue;
                    nextPosition = np ?? p; nextScale = ns ?? s;
                    blend = matched ? new RotationBlend(q, nq!.Value) : default;
                }
                internal void Sample(double amount, out Vec3 p, out Quat q, out Vec3 s)
                {
                    p = matched ? Lerp(position, nextPosition, amount) : position;
                    q = matched ? blend.Sample(amount) : rotation;
                    s = matched ? Lerp(scale, nextScale, amount) : scale;
                }
            }
            private readonly struct RotationBlend
            {
                private readonly Quat before, after;
                private readonly double theta, sine;
                private readonly bool spherical;
                internal RotationBlend(Quat a, Quat b)
                {
                    a = Normalize(a); b = Normalize(b);
                    var dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
                    if (dot < 0) { b = new Quat(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
                    dot = Math.Max(0, Math.Min(1, dot));
                    before = a; after = b; spherical = dot < .9995;
                    theta = spherical ? Math.Acos(dot) : 0;
                    sine = spherical ? Math.Sin(theta) : 1;
                }
                internal Quat Sample(double amount)
                {
                    var a = spherical ? Math.Sin((1 - amount) * theta) / sine : 1 - amount;
                    var b = spherical ? Math.Sin(amount * theta) / sine : amount;
                    return Normalize(new Quat((float)(before.X * a + after.X * b), (float)(before.Y * a + after.Y * b),
                        (float)(before.Z * a + after.Z * b), (float)(before.W * a + after.W * b)));
                }
            }
        }
    }
}
