using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin
{
    // Resolve the game's installed item sound without instantiating gameplay
    // prefabs or serializing the same drop clip into every replay.
    internal static class ItemAssetRegistry
    {
        private sealed class Candidate
        {
            internal string ItemId = "";
            internal AudioClip Clip = null!;
        }
        private sealed class HandCandidate
        {
            internal string Name = "";
            internal bool TwoHandedAnimation;
            internal Vector3 PositionOffset;
            internal Vector3 RotationOffset;
        }
        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly List<HandCandidate> HandCandidates = new List<HandCandidate>();
        private static readonly Dictionary<string, bool> HandCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> NextHandScan =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (Vector3 Position, Vector3 Rotation)> HeldPoseCache =
            new Dictionary<string, (Vector3, Vector3)>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, float> NextHeldPoseScan =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static object? scannedConfig;

        private static void Remember(object? properties)
        {
            if (properties == null) return;
            var name = GameAccess.Read(properties, "itemName") as string;
            if (!string.IsNullOrWhiteSpace(name) && GameAccess.Read(properties, "twoHandedAnimation") is bool twoHanded &&
                GameAccess.Read(properties, "positionOffset") is Vector3 positionOffset &&
                GameAccess.Read(properties, "rotationOffset") is Vector3 rotationOffset &&
                GameAccess.Finite(positionOffset) && GameAccess.Finite(rotationOffset) &&
                !HandCandidates.Any(candidate => candidate.Name == name && candidate.TwoHandedAnimation == twoHanded &&
                    candidate.PositionOffset == positionOffset && candidate.RotationOffset == rotationOffset))
                HandCandidates.Add(new HandCandidate { Name = name!, TwoHandedAnimation = twoHanded,
                    PositionOffset = positionOffset, RotationOffset = rotationOffset });
            var clip = GameAccess.Read(properties, "dropSFX") as AudioClip;
            if (!clip) return;
            var id = GameAccess.Scalar(GameAccess.Read(properties, "itemId")) ?? "";
            if (Candidates.Any(candidate => candidate.ItemId == id && candidate.Clip == clip)) return;
            Candidates.Add(new Candidate { ItemId = id, Clip = clip! });
        }

        private static void Scan()
        {
            var config = GameAccess.Read(GameAccess.Singleton("Unity.Netcode.NetworkManager"), "NetworkConfig");
            if (config != null && !ReferenceEquals(config, scannedConfig))
            {
                var grabbable = GameAccess.Type("GrabbableObject");
                var entries = GameAccess.Read(GameAccess.Read(config, "Prefabs"), "Prefabs") as IEnumerable;
                if (grabbable != null && entries != null)
                    foreach (var entry in entries)
                    {
                        var prefab = GameAccess.Read(entry, "Prefab") as GameObject;
                        if (!prefab) continue;
                        var item = prefab!.GetComponentInChildren(grabbable, true);
                        if (item) Remember(GameAccess.Read(item, "itemProperties"));
                    }
                scannedConfig = config;
            }
            var itemType = GameAccess.Type("Item");
            if (itemType != null)
                foreach (var asset in Resources.FindObjectsOfTypeAll(itemType)) Remember(asset);
        }

        internal static AudioClip? Resolve(string itemId, string clipName)
        {
            if (itemId.Length == 0 && clipName.Length == 0) return null;
            var cached = Match(itemId, clipName);
            if (cached != null) return cached;
            Scan();
            return Match(itemId, clipName);
        }

        internal static bool? ResolveTwoHandedAnimation(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;
            if (HandCache.TryGetValue(itemName, out var cached)) return cached;
            if (NextHandScan.TryGetValue(itemName, out var nextScan) &&
                Time.realtimeSinceStartup < nextScan) return null;
            Scan();
            var matches = HandCandidates.Where(candidate =>
                string.Equals(candidate.Name, itemName, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.TwoHandedAnimation).Distinct().Take(2).ToArray();
            if (matches.Length == 1) return HandCache[itemName] = matches[0];
            NextHandScan[itemName] = Time.realtimeSinceStartup + 1f;
            return null;
        }

        internal static bool TryResolveHeldPose(string itemName, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (string.IsNullOrWhiteSpace(itemName)) return false;
            if (!HeldPoseCache.TryGetValue(itemName, out var pose))
            {
                if (NextHeldPoseScan.TryGetValue(itemName, out var nextScan) &&
                    Time.realtimeSinceStartup < nextScan) return false;
                Scan();
                var matches = HandCandidates.Where(candidate =>
                    string.Equals(candidate.Name, itemName, StringComparison.OrdinalIgnoreCase))
                    .Select(candidate => (candidate.PositionOffset, candidate.RotationOffset))
                    .Distinct().Take(2).ToArray();
                if (matches.Length != 1)
                {
                    // A menu replay may query this before the installed item
                    // assets finish loading with the moon. Retry later.
                    NextHeldPoseScan[itemName] = Time.realtimeSinceStartup + 1f;
                    return false;
                }
                pose = matches[0];
                HeldPoseCache[itemName] = pose;
            }
            position = pose.Position;
            rotation = Quaternion.Euler(pose.Rotation);
            return true;
        }

        private static AudioClip? Match(string itemId, string clipName)
        {
            var matches = Candidates.Where(candidate => candidate.Clip &&
                (itemId.Length == 0 || candidate.ItemId == itemId) &&
                (clipName.Length == 0 || candidate.Clip.name == clipName))
                .Select(candidate => candidate.Clip).Distinct().Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
}
