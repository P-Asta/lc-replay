using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    // Save an item pose once while it rests, or the game's fall inputs once
    // while it drops. Held and otherwise unpredictable items keep frame poses.
    internal sealed class ItemMotionCapture
    {
        private readonly Dictionary<string, ItemMotionSnapshot> latest =
            new Dictionary<string, ItemMotionSnapshot>(StringComparer.Ordinal);

        internal IEnumerable<ReplayEvent> Observe(ReplayFrame frame, EntityTracker tracker)
        {
            var snapshots = frame.Entities.Where(entity => entity.Kind == "item")
                .ToDictionary(entity => entity.Id, StringComparer.Ordinal);
            var round = GameAccess.Singleton("StartOfRound");
            var elevator = GameAccess.Read(round, "elevatorTransform") as Transform;
            foreach (var entry in tracker.Entries)
            {
                if (entry.Kind != "item" || !entry.Component ||
                    !snapshots.TryGetValue(entry.Id, out var entity) || !entity.Active) continue;
                var item = entry.Component;
                if (GameAccess.Bool(item, "isHeld") || GameAccess.Bool(item, "isHeldByEnemy") ||
                    GameAccess.Bool(item, "isPocketed") || GameAccess.Read(item, "parentObject") is Transform parent && parent)
                { latest.Remove(entry.Id); continue; }
                var transform = item.transform;
                var anchor = elevator && (transform == elevator || transform.IsChildOf(elevator)) ? elevator : null;
                var anchorId = anchor ? "ship-elevator" : "";
                var worldRotation = transform.rotation;
                var rotation = anchor ? Quaternion.Inverse(anchor!.rotation) * worldRotation : worldRotation;
                var currentPosition = anchor ? anchor!.InverseTransformPoint(transform.position) : transform.position;
                if (!GameAccess.Finite(currentPosition) || !GameAccess.Finite(rotation)) continue;
                var motion = new ItemMotionSnapshot
                {
                    Mode = "rest", AnchorId = anchorId,
                    Position = GameAccess.Vec(currentPosition), Rotation = GameAccess.Rot(rotation),
                    Scale = entity.Scale
                };
                AnimationCurve? fallCurve = null;
                var fallTime = GameAccess.Read(item, "fallTime");
                if (fallTime is float phase && GameAccess.Finite(phase) && phase < 1f &&
                    GameAccess.Read(item, "startFallingPosition") is Vector3 startLocal &&
                    GameAccess.Read(item, "targetFloorPosition") is Vector3 targetLocal &&
                    GameAccess.Finite(startLocal) && GameAccess.Finite(targetLocal))
                {
                    var parentTransform = transform.parent;
                    var startWorld = parentTransform ? parentTransform.TransformPoint(startLocal) : startLocal;
                    var targetWorld = parentTransform ? parentTransform.TransformPoint(targetLocal) : targetLocal;
                    var start = anchor ? anchor!.InverseTransformPoint(startWorld) : startWorld;
                    var target = anchor ? anchor!.InverseTransformPoint(targetWorld) : targetWorld;
                    var distanceY = startLocal.y - targetLocal.y;
                    var fallRate = Mathf.Abs(distanceY) > .001f ? Mathf.Min(10000f, 6f / Mathf.Abs(distanceY)) : 0f;
                    var properties = GameAccess.Read(item, "itemProperties");
                    var resting = GameAccess.Read(properties, "restingRotation") is Vector3 rest ? rest : Vector3.zero;
                    var floorRot = GameAccess.Read(item, "floorYRot") is int yaw ? yaw : -1;
                    var floorYOffset = GameAccess.Read(properties, "floorYOffset") is float offset ? offset : 0f;
                    var targetRotationWorld = Quaternion.Euler(resting.x,
                        floorRot == -1 ? worldRotation.eulerAngles.y : floorRot + floorYOffset + 90f, resting.z);
                    var targetRotation = anchor ? Quaternion.Inverse(anchor!.rotation) * targetRotationWorld : targetRotationWorld;
                    var curve = GameAccess.Read(round, distanceY > 5f ?
                        "objectFallToGroundCurveNoBounce" : "objectFallToGroundCurve") as AnimationCurve;
                    if (curve != null && fallRate > 0f && GameAccess.Finite(start) && GameAccess.Finite(target) &&
                        GameAccess.Finite(targetRotation))
                    {
                        motion.Mode = "fall";
                        motion.Position = GameAccess.Vec(start);
                        motion.Target = GameAccess.Vec(target);
                        motion.TargetRotation = GameAccess.Rot(targetRotation);
                        motion.FallTime = Mathf.Clamp(phase - Time.deltaTime * fallRate, -2f, 2f);
                        motion.FallRate = fallRate;
                        fallCurve = curve;
                    }
                }
                if (fallTime is float activeFall && activeFall < 1f && motion.Mode != "fall")
                { latest.Remove(entry.Id); continue; }
                entity.PoseFromItemEvents = true;
                if (latest.TryGetValue(entry.Id, out var prior) && SameMotion(prior, motion)) continue;
                if (fallCurve != null)
                {
                    var samples = new float[17];
                    var valid = true;
                    for (var index = 0; index < samples.Length; index++)
                    {
                        var value = fallCurve.Evaluate(index / 16f);
                        if (!GameAccess.Finite(value) || Math.Abs(value) > 10f) { valid = false; break; }
                        samples[index] = value;
                    }
                    if (!valid) { entity.PoseFromItemEvents = false; latest.Remove(entry.Id); continue; }
                    motion.Curve = samples;
                }
                latest[entry.Id] = motion;
                yield return new ReplayEvent { Time = frame.Time, Category = "item", Name = "pose",
                    EntityId = entry.Id, ItemMotion = motion };
            }
            foreach (var id in latest.Keys.Where(id => !snapshots.ContainsKey(id)).ToArray()) latest.Remove(id);
        }

        private static bool SameMotion(ItemMotionSnapshot a, ItemMotionSnapshot b)
        {
            if (a.Mode != b.Mode || a.AnchorId != b.AnchorId) return false;
            static bool Near(Vec3 x, Vec3 y) =>
                Math.Abs(x.X - y.X) < .02f && Math.Abs(x.Y - y.Y) < .02f && Math.Abs(x.Z - y.Z) < .02f;
            static bool NearRotation(Quat x, Quat y) => Math.Abs(x.X * y.X + x.Y * y.Y + x.Z * y.Z + x.W * y.W) > .999f;
            if (!Near(a.Position, b.Position) || !Near(a.Scale, b.Scale)) return false;
            return a.Mode == "fall"
                ? Near(a.Target, b.Target) && NearRotation(a.TargetRotation, b.TargetRotation)
                : NearRotation(a.Rotation, b.Rotation);
        }
    }
}
