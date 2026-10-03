using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Plugin.Capture;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Keep the installed animation hierarchy and RigBuilder constraints. A skin's
    // bone list alone omits animated IK targets and auxiliary transform bindings.
    internal sealed class NativeActorRig : IDisposable
    {
        internal readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Animator> Animators = new Dictionary<string, Animator>(StringComparer.Ordinal);
        private readonly List<(MonoBehaviour Builder, MethodInfo? Build, MethodInfo? Evaluate)> builders = new List<(MonoBehaviour, MethodInfo?, MethodInfo?)>();
        private readonly GameObject container;
        private bool built;
        private readonly Dictionary<Animator, (MonoBehaviour Builder, PlayableGraph Graph, AnimatorControllerPlayable Controller, MethodInfo? Sync)> playerGraphs =
            new Dictionary<Animator, (MonoBehaviour, PlayableGraph, AnimatorControllerPlayable, MethodInfo?)>();
        private readonly SkinnedMeshRenderer[] skins;
        private readonly Transform? playerView;
        internal readonly Transform? ServerItemHolder;
        private readonly Dictionary<string, (Component Constraint, PropertyInfo Weight)> playerConstraints = new Dictionary<string, (Component, PropertyInfo)>();
        private readonly List<(Transform Node, Vector3 Position, Quaternion Rotation, Vector3 Scale)> rest =
            new List<(Transform, Vector3, Quaternion, Vector3)>();

        internal NativeActorRig(Component prefab, Transform parent, int layer, RuntimeAnimatorController? playerController = null)
        {
            container = new GameObject("Native actor animation") { hideFlags = HideFlags.DontSave, layer = layer };
            container.SetActive(false); container.transform.SetParent(parent, false);
            var clone = Object.Instantiate(prefab.gameObject, container.transform, false);
            skins = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            clone.transform.localPosition = Vector3.zero; clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;
            var player = playerController ? clone.GetComponent(prefab.GetType()) : null;
            var playerAnimator = GameAccess.Read(player, "playerBodyAnimator") as Animator;
            playerView = (GameAccess.Read(player, "gameplayCamera") as Camera)?.transform;
            ServerItemHolder = GameAccess.Read(player, "serverItemHolder") as Transform;
            foreach (var name in new[] { "cameraLookRig1", "cameraLookRig2", "leftArmRig", "rightArmRig", "leftArmRigSecondary", "rightArmRigSecondary", "rightArmProceduralRig" })
                if (GameAccess.Read(player, name) is Component constraint && constraint.GetType().GetProperty("weight") is PropertyInfo weight)
                    playerConstraints.Add(name, (constraint, weight));
            if (playerAnimator) playerAnimator!.runtimeAnimatorController = playerController;
            // Strip gameplay before activation: no AI, network spawn, events,
            // colliders, renderers or sound playback can run from this copy.
            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true)
                .OrderBy(component => component.GetType().Name == "VFXPropertyBinder" ? 1 : 0))
                if (behaviour && !(behaviour.GetType().Namespace ?? "").StartsWith("UnityEngine.Animations.Rigging", StringComparison.Ordinal)) Object.DestroyImmediate(behaviour);
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true))
                if (behaviour && !(behaviour is Animator) && !(behaviour.GetType().Namespace ?? "").StartsWith("UnityEngine.Animations.Rigging", StringComparison.Ordinal)) behaviour.enabled = false;
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = false;
                // Animation and LOD systems can enable these again. Only the
                // replay's own renderer copies may draw, including radar cones.
                renderer.forceRenderingOff = true;
            }
            foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in clone.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
            foreach (var particles in clone.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main; main.playOnAwake = false;
                var emission = particles.emission; emission.enabled = false;
                particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            foreach (var transform in clone.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = layer;
                rest.Add((transform, transform.localPosition, transform.localRotation, transform.localScale));
                if (transform != clone.transform) Bones[EntityTracker.RelativePath(clone.transform, transform)] = transform;
            }
            foreach (var animator in clone.GetComponentsInChildren<Animator>(true))
            {
                if (!animator.runtimeAnimatorController) continue;
                if (RenderVisibilityPolicy.IsDebugObject(animator.name)) { animator.enabled = false; continue; }
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false; animator.speed = 1f;
                animator.fireEvents = false;
                Animators[EntityTracker.RelativePath(clone.transform, animator.transform)] = animator;
            }
            foreach (var builder in clone.GetComponentsInChildren<MonoBehaviour>(true).Where(component => component && component.GetType().Name == "RigBuilder"))
                builders.Add((builder, builder.GetType().GetMethod("Build", Type.EmptyTypes), builder.GetType().GetMethod("Evaluate", new[] { typeof(float) })));
            clone.SetActive(true); container.SetActive(true);
            foreach (var animator in Animators.Values) animator.Rebind();
            if (playerAnimator)
                foreach (var item in builders)
                    if (item.Builder.GetComponent<Animator>() is Animator animator && animator.runtimeAnimatorController)
                        BuildPlayerGraph(item.Builder, animator);
        }

        private void BuildPlayerGraph(MonoBehaviour builder, Animator animator)
        {
            if (playerGraphs.TryGetValue(animator, out var prior) && prior.Graph.IsValid()) prior.Graph.Destroy();
            var graph = PlayableGraph.Create("Replay player controller and IK");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var controller = AnimatorControllerPlayable.Create(graph, animator.runtimeAnimatorController);
                var output = AnimationPlayableOutput.Create(graph, "Native body controller", animator);
                output.SetSourcePlayable(controller);
                // RigBuilder's PreviousInputs outputs have priority 1000. Their
                // preceding controller output must be evaluated in this same
                // graph, rather than reading a stale/default separate stream.
                builder.GetType().GetMethod("Build", new[] { typeof(PlayableGraph) })!.Invoke(builder, new object[] { graph });
                graph.Play();
                playerGraphs[animator] = (builder, graph, controller, builder.GetType().GetMethod("SyncLayers", Type.EmptyTypes));
            }
            catch { if (graph.IsValid()) graph.Destroy(); throw; }
        }

        internal AnimatorControllerPlayable? PlayerController(Animator animator) =>
            playerGraphs.TryGetValue(animator, out var entry) && entry.Controller.IsValid() ? entry.Controller : (AnimatorControllerPlayable?)null;

        public void Dispose()
        {
            foreach (var entry in playerGraphs.Values) if (entry.Graph.IsValid()) entry.Graph.Destroy();
            playerGraphs.Clear();
        }

        internal bool Prepare()
        {
            if (!container.activeInHierarchy) return false;
            foreach (var item in builders)
            {
                var current = GameAccess.Read(item.Builder, "graph");
                if (item.Builder.GetComponent<Animator>() is Animator animator && playerGraphs.ContainsKey(animator))
                {
                    // OnEnable recreates the internal graph after a cached rig
                    // is reactivated. Replace it and its disposed rig jobs.
                    if (current is PlayableGraph enabledGraph && enabledGraph.IsValid()) BuildPlayerGraph(item.Builder, animator);
                    continue;
                }
                if (!built || !(current is PlayableGraph valid) || !valid.IsValid()) item.Build?.Invoke(item.Builder, null);
                if (GameAccess.Read(item.Builder, "graph") is PlayableGraph graph && graph.IsValid())
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            }
            foreach (var animator in Animators.Values)
                if (animator.playableGraph.IsValid()) animator.playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            built = true;
            return true;
        }

        internal void ResetPose()
        {
            // Spawn clips key transforms which locomotion clips leave unkeyed.
            // Sampling must start from the prefab pose, independent of seeks.
            foreach (var pose in rest)
                if (pose.Node)
                {
                    pose.Node.localPosition = pose.Position;
                    pose.Node.localRotation = pose.Rotation;
                    pose.Node.localScale = pose.Scale;
                }
        }

        internal Transform? SkinTransform(GeometrySnapshot geometry)
        {
            if (geometry.PrefabRendererPath.Length != 0 && Bones.TryGetValue(geometry.PrefabRendererPath, out var node)) return node;
            var matches = skins.Where(skin => skin && skin.name == geometry.Name && skin.sharedMesh &&
                (geometry.MeshName.Length == 0 || skin.sharedMesh.name == geometry.MeshName)).ToArray();
            return matches.Length == 1 ? matches[0].transform : null;
        }

        internal void ApplyView(Quat? rotation)
        {
            if (playerView && rotation.HasValue)
            {
                var value = rotation.Value;
                playerView!.rotation = new Quaternion(value.X, value.Y, value.Z, value.W);
            }
        }

        internal void Evaluate()
        {
            if (!container.activeInHierarchy || !built) return;
            foreach (var entry in playerGraphs.Values)
            {
                entry.Sync?.Invoke(entry.Builder, null);
                if (entry.Graph.IsValid()) entry.Graph.Evaluate(0f);
            }
            foreach (var item in builders)
                if (item.Builder && !playerGraphs.ContainsKey(item.Builder.GetComponent<Animator>())) item.Evaluate?.Invoke(item.Builder, new object[] { 0f });
        }

        internal void ApplyPlayerState(EntitySnapshot entity)
        {
            if (playerConstraints.Count == 0) return;
            bool Flag(string name) => entity.State.TryGetValue(name, out var value) && string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
            // PlayerControllerB.LateUpdate drives these outside the Animator.
            var vehicle = Flag("inVehicleAnimation");
            var special = Flag("inSpecialInteractAnimation") && !Flag("inShockingMinigame");
            SetWeight("cameraLookRig1", vehicle ? .33f : special ? 0f : .45f);
            SetWeight("cameraLookRig2", special && !vehicle ? 0f : 1f);
            SetWeight("leftArmRigSecondary", vehicle ? 1f : 0f);
            SetWeight("rightArmRigSecondary", vehicle ? 1f : 0f);
            SetWeight("leftArmRig", vehicle ? 0f : 1f);
            SetWeight("rightArmRig", vehicle ? 0f : 1f);
            SetWeight("rightArmProceduralRig", Flag("IsInspectingItem") ? 1f : 0f);
        }

        private void SetWeight(string name, float value)
        {
            if (playerConstraints.TryGetValue(name, out var item) && item.Constraint) item.Weight.SetValue(item.Constraint, value);
        }
    }
}
