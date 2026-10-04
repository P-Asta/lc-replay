using System;
using System.Collections.Generic;
using System.Linq;
using LCReplay.Plugin.Capture;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Holding clips animate IK targets outside the skin. Evaluate an inert
    // copy of the installed player rig once per hold, then reuse its arm pose.
    internal static class NativeHeldItemPose
    {
        private static readonly Dictionary<string, Dictionary<string, Quaternion>> Poses =
            new Dictionary<string, Dictionary<string, Quaternion>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, (Component Prefab, string Path, string ItemName)> EnemyHolders =
            new Dictionary<string, (Component, string, string)>(StringComparer.Ordinal);

        internal static bool TryEnemyHolderPath(string enemyName, string itemName, out string path)
        {
            path = "";
            if (!EnemyHolders.TryGetValue(enemyName, out var binding) || !binding.Prefab)
            {
                var prefab = PrefabAssetRegistry.ResolvePrefab("enemy:" + enemyName);
                if (!prefab) return false;
                // These are the installed game's serialized attachment points.
                // New captures store the exact parent path, so arbitrary modded
                // holders do not need field-name guesses during playback.
                var point = GameAccess.Read(prefab, "gunPoint") as Transform;
                var requiredItem = "";
                if (point && GameAccess.Read(prefab, "gunPrefab") is GameObject gun && gun &&
                    GameAccess.Type("GrabbableObject") is Type itemType)
                    requiredItem = GameAccess.Read(GameAccess.Read(gun.GetComponent(itemType), "itemProperties"), "itemName") as string ?? "";
                if (!point) point = GameAccess.Read(prefab, "grabTarget") as Transform;
                var parentPath = point && (point == prefab!.transform || point!.IsChildOf(prefab.transform))
                    ? EntityTracker.RelativePath(prefab.transform, point!) : "";
                if (EnemyHolders.Count >= 256) EnemyHolders.Clear();
                EnemyHolders[enemyName] = binding = (prefab!, parentPath, requiredItem);
            }
            if (binding.Path.Length == 0 || binding.ItemName.Length != 0 &&
                !string.Equals(binding.ItemName, itemName, StringComparison.OrdinalIgnoreCase)) return false;
            path = binding.Path;
            return true;
        }

        internal static bool Apply(Dictionary<string, Transform> bones, bool bothHands, string clip, int hash, float weight)
        {
            var key = bothHands + ":" + hash + ":" + clip;
            if (!Poses.TryGetValue(key, out var pose))
            {
                pose = NativeHeldPoseProfiles.Resolve(clip) ?? Sample(bothHands, clip, hash);
                if (pose == null) return false;
                Poses[key] = pose;
            }
            foreach (var pair in bones)
            {
                if (!pair.Value || (pair.Key.IndexOf("/shoulder.R[", StringComparison.Ordinal) < 0 &&
                    (!bothHands || pair.Key.IndexOf("/shoulder.L[", StringComparison.Ordinal) < 0))) continue;
                var segment = pair.Key.Substring(pair.Key.LastIndexOf('/') + 1);
                var suffix = segment.IndexOf('[');
                if (suffix >= 0) segment = segment.Substring(0, suffix);
                if (pose.TryGetValue(segment, out var rotation))
                    pair.Value.localRotation = Quaternion.Slerp(pair.Value.localRotation, rotation, Mathf.Clamp01(weight));
            }
            return true;
        }

        private static Dictionary<string, Quaternion>? Sample(bool bothHands, string clip, int hash)
        {
            var round = GameAccess.Singleton("StartOfRound");
            if (round == null && GameAccess.Type("StartOfRound") is Type roundType)
                round = Resources.FindObjectsOfTypeAll(roundType).FirstOrDefault(value =>
                    GameAccess.Read(value, "otherClientsAnimatorController") is RuntimeAnimatorController);
            var prefab = GameAccess.Read(round, "playerPrefab") as GameObject;
            var live = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController") as Component;
            if (live) prefab = live!.gameObject;
            if (!prefab) prefab = GameAccess.Read(GameAccess.Read(GameAccess.Singleton("Unity.Netcode.NetworkManager"),
                "NetworkConfig"), "PlayerPrefab") as GameObject;
            if (!prefab && GameAccess.Type("GameNetcodeStuff.PlayerControllerB") is Type playerType)
                prefab = Resources.FindObjectsOfTypeAll(playerType).OfType<Component>()
                    .Where(component => component && !component.gameObject.scene.IsValid())
                    .Select(component => component.gameObject).FirstOrDefault();
            if (!prefab) return null;
            var container = new GameObject("LC Replay holding pose sampler") { hideFlags = HideFlags.HideAndDontSave };
            container.SetActive(false);
            try
            {
                // The inactive parent prevents gameplay Awake/OnEnable calls.
                var clone = Object.Instantiate(prefab!, container.transform, false);
                var player = clone.GetComponentInChildren(GameAccess.Type("GameNetcodeStuff.PlayerControllerB"), true);
                var animator = GameAccess.Read(player, "playerBodyAnimator") as Animator;
                var skin = GameAccess.Read(player, "thisPlayerModel") as SkinnedMeshRenderer;
                if (!animator || !skin) return null;
                foreach (var field in new[] { "rightArmNormalRig", "rightArmProceduralRig", "rightArmRig", "leftArmRig", "cameraLookRig1", "cameraLookRig2", "leftArmRigSecondary", "rightArmRigSecondary" })
                {
                    var constraint = GameAccess.Read(player, field);
                    constraint?.GetType().GetProperty("weight")?.SetValue(constraint,
                        field.StartsWith("camera", StringComparison.Ordinal) || field.EndsWith("Secondary", StringComparison.Ordinal) || field == "rightArmProceduralRig" ? 0f : 1f);
                }
                var builders = clone.GetComponentsInChildren<MonoBehaviour>(true)
                    .Where(component => component && component.GetType().FullName == "UnityEngine.Animations.Rigging.RigBuilder").ToArray();
                foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour && !(behaviour.GetType().Namespace ?? "").StartsWith("UnityEngine.Animations.Rigging", StringComparison.Ordinal))
                        Object.DestroyImmediate(behaviour);
                foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var source in clone.GetComponentsInChildren<AudioSource>(true)) source.enabled = false;
                foreach (var camera in clone.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
                foreach (var listener in clone.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
                foreach (var other in clone.GetComponentsInChildren<Animator>(true)) other.enabled = other == animator;
                var remote = GameAccess.Read(round, "otherClientsAnimatorController") as RuntimeAnimatorController;
                if (remote) animator!.runtimeAnimatorController = remote;
                animator!.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                container.SetActive(true); clone.SetActive(true);
                animator.Rebind();
                var right = animator.GetLayerIndex("HoldingItemsRightHand");
                var both = animator.GetLayerIndex("HoldingItemsBothHands");
                var selected = bothHands ? both : right;
                if (selected < 0 || builders.Length == 0) return null;
                for (var layer = 1; layer < animator.layerCount; layer++) animator.SetLayerWeight(layer, 0f);
                animator.SetBool("Walking", false); animator.SetBool("Sprinting", false); animator.SetBool("Grab", true);
                animator.SetLayerWeight(right, 1f); animator.SetLayerWeight(both, bothHands ? 1f : 0f);
                animator.Play("Idle1", 0, .5f);
                animator.Play("HoldOneHandedItem", right, .5f);
                animator.Play(animator.HasState(selected, hash) ? hash : Animator.StringToHash(clip), selected, .5f);
                animator.Update(.02f);
                foreach (var builder in builders) builder.GetType().GetMethod("Build", Type.EmptyTypes)?.Invoke(builder, null);
                for (var step = 0; step < 4; step++)
                    foreach (var builder in builders) builder.GetType().GetMethod("Evaluate", new[] { typeof(float) })?.Invoke(builder, new object[] { .02f });
                return skin!.bones.Where(bone => bone && bone.GetComponentsInParent<Transform>(true)
                    .Any(parent => parent.name == "shoulder.R" || bothHands && parent.name == "shoulder.L"))
                    .GroupBy(bone => bone.name).ToDictionary(group => group.Key, group => group.First().localRotation);
            }
            catch (Exception) { return null; }
            finally { Object.DestroyImmediate(container); }
        }
    }
}
