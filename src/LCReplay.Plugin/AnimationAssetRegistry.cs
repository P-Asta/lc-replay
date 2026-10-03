using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin
{
    // Keep references to installed game animation assets, never gameplay objects.
    // Asset names alone are insufficient: several enemies use different
    // controllers with the same name (notably "metarig").
    internal static class AnimationAssetRegistry
    {
        private sealed class Candidate
        {
            internal RuntimeAnimatorController Controller = null!;
            internal Avatar? Avatar;
            internal string Kind = "";
        }

        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static object? scannedNetworkConfig;

        internal static void Remember(Animator animator, string kind = "")
        {
            if (!animator || !animator.runtimeAnimatorController) return;
            if (kind.Length == 0) kind = Classify(animator.gameObject);
            Add(animator.runtimeAnimatorController, animator.avatar, kind);
        }

        private static string Classify(GameObject obj)
        {
            var player = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
            if (player != null && (obj.GetComponentInParent(player) || obj.GetComponentInChildren(player, true)))
                return "player";
            var enemy = GameAccess.Type("EnemyAI");
            return enemy != null && (obj.GetComponentInParent(enemy) || obj.GetComponentInChildren(enemy, true))
                ? "enemy" : "";
        }

        private static void Add(RuntimeAnimatorController controller, Avatar? avatar, string kind)
        {
            if (Candidates.Any(candidate => candidate.Controller == controller && candidate.Avatar == avatar &&
                candidate.Kind == kind)) return;
            Candidates.Add(new Candidate { Controller = controller, Avatar = avatar, Kind = kind });
        }

        private static void RememberInstalledPrefabs()
        {
            // Netcode's configured enemy prefabs may exist in a fresh menu
            // process, before any enemy has spawned. Do not instantiate them.
            var network = GameAccess.Singleton("Unity.Netcode.NetworkManager");
            var config = GameAccess.Read(network, "NetworkConfig");
            if (config == null || ReferenceEquals(config, scannedNetworkConfig)) return;
            var prefab = GameAccess.Read(config, "PlayerPrefab") as GameObject;
            if (!prefab)
                prefab = GameAccess.Read(GameAccess.Singleton("StartOfRound"), "playerPrefab") as GameObject;
            if (prefab)
                foreach (var animator in prefab!.GetComponentsInChildren<Animator>(true)) Remember(animator, "player");
            var entries = GameAccess.Read(GameAccess.Read(config, "Prefabs"), "Prefabs") as IEnumerable;
            if (entries == null) return;
            foreach (var entry in entries)
            {
                var configured = GameAccess.Read(entry, "Prefab") as GameObject;
                if (!configured) continue;
                var kind = Classify(configured!);
                foreach (var animator in configured!.GetComponentsInChildren<Animator>(true)) Remember(animator, kind);
            }
            scannedNetworkConfig = config;
        }

        internal static (RuntimeAnimatorController? Controller, Avatar? Avatar) Resolve(
            string controllerName, string avatarName, string actorKind, IEnumerable<string> recordedClips)
        {
            if (controllerName.Length == 0) return (null, null);
            RememberInstalledPrefabs();
            foreach (var animator in Resources.FindObjectsOfTypeAll<Animator>())
                if (animator && animator.runtimeAnimatorController &&
                    animator.runtimeAnimatorController.name == controllerName) Remember(animator);
            // Some assets are loaded without an Animator instance in this scene.
            foreach (var controller in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
                if (controller && controller.name == controllerName &&
                    !Candidates.Any(candidate => candidate.Controller == controller)) Add(controller, null, "");

            var matches = Candidates.Where(candidate => candidate.Controller &&
                candidate.Controller.name == controllerName).ToArray();
            if (actorKind == "player" || actorKind == "enemy")
            {
                var typed = matches.Where(candidate => candidate.Kind == actorKind).ToArray();
                // An untyped loaded asset is acceptable for an enemy when the
                // game did not expose a prefab. A player must come from a
                // verified player prefab/instance, never a masked enemy.
                matches = typed.Length != 0 ? typed : actorKind == "player"
                    ? Array.Empty<Candidate>() : matches.Where(candidate => candidate.Kind.Length == 0).ToArray();
            }
            if (matches.Length == 0) return (null, null);
            if (avatarName.Length != 0)
            {
                var paired = matches.Where(candidate => candidate.Avatar &&
                    candidate.Avatar!.name == avatarName).ToArray();
                if (paired.Length != 0) matches = paired;
                else
                {
                    var avatars = Resources.FindObjectsOfTypeAll<Avatar>()
                        .Where(avatar => avatar && avatar.name == avatarName).Take(2).ToArray();
                    if (avatars.Length != 1) return (null, null);
                    matches = matches.Where(candidate => candidate.Avatar == null ||
                        candidate.Avatar == avatars[0]).ToArray();
                    if (matches.Length == 0) return (null, null);
                    return Select(matches, recordedClips, avatars[0]);
                }
            }
            return Select(matches, recordedClips, null);
        }

        private static (RuntimeAnimatorController? Controller, Avatar? Avatar) Select(
            Candidate[] candidates, IEnumerable<string> recordedClips, Avatar? fallbackAvatar)
        {
            var clips = recordedClips.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal)
                .Take(32).ToArray();
            // Require every observed base-layer clip when more than one asset
            // shares this name. A tie is safer left to the recorded track.
            if (candidates.Select(item => item.Controller).Distinct().Count() > 1 && clips.Length != 0)
            {
                candidates = candidates.Where(candidate =>
                {
                    try
                    {
                        var available = new HashSet<string>(candidate.Controller.animationClips
                            .Where(clip => clip).Select(clip => clip.name), StringComparer.Ordinal);
                        return clips.All(available.Contains);
                    }
                    catch { return false; }
                }).ToArray();
            }
            var controllers = candidates.Select(item => item.Controller).Distinct().ToArray();
            if (controllers.Length != 1) return (null, null);
            var chosen = candidates.First(item => item.Controller == controllers[0]);
            return (chosen.Controller, chosen.Avatar ? chosen.Avatar : fallbackAvatar);
        }
    }
}
