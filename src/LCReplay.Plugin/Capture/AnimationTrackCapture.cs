using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    // Learn each observed clip once from the real, already animated skeleton.
    // Upper-layer tracks contain the final blended pose (including emotes and
    // spawn animations), rather than a bone stream in every gameplay frame.
    internal sealed class AnimationTrackCapture
    {
        private readonly Dictionary<string, Builder> active = new Dictionary<string, Builder>(StringComparer.Ordinal);
        private readonly HashSet<string> completed = new HashSet<string>(StringComparer.Ordinal);

        private sealed class Builder
        {
            internal string Key = "", EntityId = "", Clip = "", AnimatorPath = "";
            internal int Hash, Layer;
            internal bool Looping;
            internal double FirstTime, LastTime;
            internal float FirstNormalized, LastNormalized;
            internal Transform[] Bones = Array.Empty<Transform>();
            internal string[] Paths = Array.Empty<string>();
            internal readonly List<(float Phase, float[] Position, float[] Rotation)> Samples =
                new List<(float, float[], float[])>();
        }

        internal void Forget(string entityId)
        {
            var prefix = entityId + "\u001f";
            foreach (var key in active.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) active.Remove(key);
            completed.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        internal ReplayEvent? Observe(EntityTracker.Entry owner, Animator animator, string animatorPath,
            int layer, AnimatorStateInfo state, string clip, float weight, double time)
        {
            if (!GameAccess.Finite(state.normalizedTime)) return null;
            var actorKey = owner.Id + "\u001f" + animatorPath + ":" + layer;
            var key = actorKey + ":" + state.fullPathHash + ":" + clip;
            active.TryGetValue(actorKey, out var builder);
            ReplayEvent? finished = null;
            if (builder != null && (builder.Hash != state.fullPathHash || builder.Clip != clip ||
                (layer > 0 && weight < .05f)))
            {
                active.Remove(actorKey);
                finished = Finish(builder);
                builder = null;
            }
            if (clip.Length == 0 || clip.Length > 256 || (layer > 0 && weight < .1f)) return finished;
            if (completed.Contains(key)) return finished;
            if (builder == null)
            {
                var skin = owner.Component.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(renderer => renderer && renderer.bones.Length != 0)
                    .Where(renderer => renderer.bones.Any(bone => bone &&
                        (bone == animator.transform || bone.IsChildOf(animator.transform))))
                    .OrderByDescending(renderer => renderer.bones.Length).FirstOrDefault();
                if (!skin) return finished;
                var hierarchy = new HashSet<Transform>();
                foreach (var bone in skin!.bones)
                    for (var node = bone; node && node != owner.Component.transform &&
                        node.IsChildOf(owner.Component.transform); node = node.parent)
                        hierarchy.Add(node);
                var ordered = hierarchy.OrderBy(node => EntityTracker.RelativePath(owner.Component.transform, node), StringComparer.Ordinal)
                    .Take(128).ToArray();
                if (ordered.Length == 0) return finished;
                builder = new Builder { Key = key, EntityId = owner.Id, Clip = clip,
                    AnimatorPath = animatorPath, Hash = state.fullPathHash, Layer = layer,
                    Looping = state.loop, FirstTime = time, LastTime = time,
                    FirstNormalized = state.normalizedTime,
                    LastNormalized = state.normalizedTime,
                    Bones = ordered, Paths = ordered.Select(node => EntityTracker.RelativePath(owner.Component.transform, node)).ToArray() };
                active[actorKey] = builder;
            }
            builder.LastNormalized = state.normalizedTime;
            builder.LastTime = time;
            var phase = state.loop ? Mathf.Repeat(state.normalizedTime, 1f) : Mathf.Clamp01(state.normalizedTime);
            var newPhase = builder.Samples.All(sample => Mathf.Abs(sample.Phase - phase) > .0001f);
            // An idle state may hold the same normalized time for seconds.
            // Keep its latest settled pose even when there is no new phase.
            if (newPhase || builder.Layer == 0 && IsIdle(builder.Clip))
            {
                var positions = new float[builder.Bones.Length * 3];
                var rotations = new float[builder.Bones.Length * 4];
                for (var i = 0; i < builder.Bones.Length; i++)
                {
                    var bone = builder.Bones[i];
                    if (!bone || !GameAccess.Finite(bone.localPosition) || !GameAccess.Finite(bone.localRotation))
                    { active.Remove(actorKey); return finished; }
                    var p = bone.localPosition; var q = bone.localRotation;
                    positions[i * 3] = p.x; positions[i * 3 + 1] = p.y; positions[i * 3 + 2] = p.z;
                    rotations[i * 4] = q.x; rotations[i * 4 + 1] = q.y;
                    rotations[i * 4 + 2] = q.z; rotations[i * 4 + 3] = q.w;
                }
                if (newPhase) builder.Samples.Add((phase, positions, rotations));
                else if (builder.Samples.Count != 0)
                {
                    var last = builder.Samples.Count - 1;
                    builder.Samples[last] = (builder.Samples[last].Phase, positions, rotations);
                }
            }
            if (state.normalizedTime - builder.FirstNormalized >= .95f || builder.Samples.Count >= 40 ||
                time - builder.FirstTime >= 3)
            {
                active.Remove(actorKey);
                return Finish(builder) ?? finished;
            }
            return finished;
        }

        private ReplayEvent? Finish(Builder builder)
        {
            var span = builder.LastNormalized - builder.FirstNormalized;
            var staticIdle = builder.Layer == 0 && IsIdle(builder.Clip) &&
                builder.LastTime - builder.FirstTime >= 1 && span < .8f && builder.Samples.Count != 0;
            if (staticIdle)
            {
                var settled = builder.Samples[builder.Samples.Count - 1];
                builder.Samples.Clear();
                builder.Samples.Add((0f, settled.Position, settled.Rotation));
                builder.Samples.Add((1f, settled.Position, settled.Rotation));
            }
            if (builder.Samples.Count < 2) return null;
            // An interrupted base walk is not a reusable full cycle. An
            // interrupted emote is still useful during its recorded interval,
            // but must stop at its last observed pose instead of wrapping.
            if (!staticIdle && builder.Looping && (builder.Samples.Count < 4 ||
                span < (builder.Layer == 0 ? .8f : .2f))) return null;
            if (!staticIdle && !builder.Looping && span < .12f) return null;
            var ordered = builder.Samples.OrderBy(sample => sample.Phase).ToArray();
            var positions = new float[ordered.Length * builder.Bones.Length * 3];
            var rotations = new float[ordered.Length * builder.Bones.Length * 4];
            for (var i = 0; i < ordered.Length; i++)
            {
                Array.Copy(ordered[i].Position, 0, positions, i * ordered[i].Position.Length, ordered[i].Position.Length);
                Array.Copy(ordered[i].Rotation, 0, rotations, i * ordered[i].Rotation.Length, ordered[i].Rotation.Length);
            }
            completed.Add(builder.Key);
            return new ReplayEvent { Time = builder.FirstTime, Category = "animation", Name = "track",
                EntityId = builder.EntityId, AnimationTrack = new AnimationTrackSnapshot
                {
                    Clip = builder.Clip, AnimatorPath = builder.AnimatorPath,
                    StateHash = builder.Hash, Layer = builder.Layer,
                    Looping = !staticIdle && builder.Looping && span >= .8f,
                    BonePaths = builder.Paths.ToList(), Phases = ordered.Select(sample => sample.Phase).ToArray(),
                    Positions = positions, Rotations = rotations
                } };
        }

        private static bool IsIdle(string clip) => clip.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
