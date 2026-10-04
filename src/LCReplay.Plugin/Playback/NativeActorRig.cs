using System;
using System.Collections.Generic;
using System.Globalization;
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
        private readonly Transform actorRoot;
        private readonly Transform? nutcrackerTorso;
        private readonly string nutcrackerTorsoPath = "";
        private readonly List<(string Id, Transform Node, Quat Rotation)> nutcrackerReferences =
            new List<(string, Transform, Quat)>();
        private readonly Dictionary<string, ActorLodGroup> nativeLods = new Dictionary<string, ActorLodGroup>(StringComparer.Ordinal);
        private readonly Dictionary<string, (ActorLodGroup Group, string Path)> rendererLods =
            new Dictionary<string, (ActorLodGroup, string)>(StringComparer.Ordinal);
        private readonly List<ActorLodGroup> lodGroups = new List<ActorLodGroup>();
        private sealed class ActorLodGroup
        {
            internal readonly List<HashSet<string>> Levels = new List<HashSet<string>>();
            internal readonly HashSet<string> BoundPaths = new HashSet<string>(StringComparer.Ordinal);
            internal int Preferred = -1;
            internal void Bind(string path)
            {
                // Older captures can contain only a coarser body. Select a
                // complete available level, keeping all of that level's parts.
                // If every level is partial, leave the available parts visible.
                BoundPaths.Add(path);
                Preferred = -1;
                for (var level = 0; level < Levels.Count; level++)
                    if (Levels[level].Count != 0 && Levels[level].All(BoundPaths.Contains))
                    { Preferred = level; break; }
            }
        }
        private bool built;
        private readonly Dictionary<Animator, (MonoBehaviour Builder, PlayableGraph Graph, AnimatorControllerPlayable Controller, MethodInfo? Sync)> playerGraphs =
            new Dictionary<Animator, (MonoBehaviour, PlayableGraph, AnimatorControllerPlayable, MethodInfo?)>();
        private readonly SkinnedMeshRenderer[] skins;
        private readonly List<(SkinnedMeshRenderer Skin, float[] Weights)> restBlendShapes = new List<(SkinnedMeshRenderer, float[])>();
        private readonly Dictionary<string, SkinBinding> displayedSkins = new Dictionary<string, SkinBinding>(StringComparer.Ordinal);
        private sealed class SkinBinding
        {
            internal SkinnedMeshRenderer Displayed = null!;
            internal SkinnedMeshRenderer? Native;
            internal int[] NativeIndices = Array.Empty<int>();
            internal string StateKey = "";
            internal string? Encoded;
            internal float[] Captured = Array.Empty<float>();
        }
        private readonly Transform? playerView;
        internal readonly Transform? ServerItemHolder;
        private readonly Dictionary<string, (Component Constraint, PropertyInfo Weight)> playerConstraints = new Dictionary<string, (Component, PropertyInfo)>();
        private readonly List<(Transform Node, Vector3 Position, Quaternion Rotation, Vector3 Scale)> rest =
            new List<(Transform, Vector3, Quaternion, Vector3)>();

        internal NativeActorRig(Component prefab, Transform parent, int layer, RuntimeAnimatorController? playerController = null)
        {
            actorRoot = parent;
            container = new GameObject("Native actor animation") { hideFlags = HideFlags.DontSave, layer = layer };
            try
            {
            container.SetActive(false); container.transform.SetParent(parent, false);
            var clone = Object.Instantiate(prefab.gameObject, container.transform, false);
            skins = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var skin in skins)
                if (skin.sharedMesh && skin.sharedMesh.blendShapeCount != 0)
                {
                    var weights = new float[skin.sharedMesh.blendShapeCount];
                    for (var index = 0; index < weights.Length; index++) weights[index] = skin.GetBlendShapeWeight(index);
                    restBlendShapes.Add((skin, weights));
                }
            foreach (var group in clone.GetComponentsInChildren<LODGroup>(true))
            {
                var membership = new ActorLodGroup();
                lodGroups.Add(membership);
                var levels = group.GetLODs();
                for (var level = 0; level < levels.Length; level++)
                {
                    var paths = new HashSet<string>(StringComparer.Ordinal);
                    membership.Levels.Add(paths);
                    foreach (var renderer in levels[level].renderers)
                    {
                        if (!renderer || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) ||
                            CaptureVisibility.IsDebugRenderer(renderer)) continue;
                        var path = EntityTracker.RelativePath(clone.transform, renderer.transform);
                        // Common accessories can belong to several levels;
                        // preserve their complete membership, not just LOD0.
                        paths.Add(path);
                        nativeLods[path] = membership;
                    }
                }
            }
            clone.transform.localPosition = Vector3.zero; clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;
            if (prefab.GetType().Name == "NutcrackerEnemyAI")
            {
                nutcrackerTorso = GameAccess.Read(clone.GetComponent(prefab.GetType()), "torsoContainer") as Transform;
                if (nutcrackerTorso) nutcrackerTorsoPath = EntityTracker.RelativePath(clone.transform, nutcrackerTorso!);
            }
            var playerType = GameAccess.Type("PlayerControllerB");
            var player = playerType != null && playerType.IsInstanceOfType(prefab) ? clone.GetComponent(prefab.GetType()) : null;
            var playerAnimator = GameAccess.Read(player, "playerBodyAnimator") as Animator;
            playerView = (GameAccess.Read(player, "gameplayCamera") as Camera)?.transform;
            ServerItemHolder = GameAccess.Read(player, "serverItemHolder") as Transform;
            foreach (var name in new[] { "cameraLookRig1", "cameraLookRig2", "leftArmRig", "rightArmRig", "leftArmRigSecondary", "rightArmRigSecondary", "rightArmProceduralRig" })
                if (GameAccess.Read(player, name) is Component constraint && constraint.GetType().GetProperty("weight") is PropertyInfo weight)
                    playerConstraints.Add(name, (constraint, weight));
            if (playerAnimator && playerController) playerAnimator!.runtimeAnimatorController = playerController;
            // Strip gameplay before activation: no AI, network spawn, events,
            // colliders, renderers or sound playback can run from this copy.
            StripGameplay(clone);
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
                // All gameplay has been stripped and native renderers cannot
                // draw. A recorded alternate form may have no Animator of its
                // own, so keep every inert branch available for its visible
                // replay renderer. Recorded Active still controls that copy.
                transform.gameObject.SetActive(true);
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
                // A transformed form can be disabled in the prefab (for
                // example the Maneater adult). Only our renderer copies draw;
                // its animation hierarchy must still be available for sampling.
                for (var node = animator.transform; node && node != clone.transform; node = node.parent)
                    if (!node.gameObject.activeSelf) node.gameObject.SetActive(true);
            }
            foreach (var builder in clone.GetComponentsInChildren<MonoBehaviour>(true).Where(component => component && component.GetType().Name == "RigBuilder"))
                builders.Add((builder, builder.GetType().GetMethod("Build", Type.EmptyTypes), builder.GetType().GetMethod("Evaluate", new[] { typeof(float) })));
            clone.SetActive(true); container.SetActive(true);
            foreach (var animator in Animators.Values) animator.Rebind();
            foreach (var item in builders)
                if (item.Builder.GetComponent<Animator>() is Animator animator && animator.runtimeAnimatorController)
                    BuildPlayerGraph(item.Builder, animator);
            FreezeAutomaticGraphs();
            }
            catch
            {
                Dispose();
                if (container) Object.DestroyImmediate(container);
                throw;
            }
        }

        private static void StripGameplay(GameObject clone)
        {
            var all = clone.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(component => component).OrderBy(component => component.GetType().Name == "VFXPropertyBinder" ? 1 : 0).ToList();
            var pending = all.Where(component => !(component.GetType().Namespace ?? "")
                .StartsWith("UnityEngine.Animations.Rigging", StringComparison.Ordinal)).ToList();
            var requirements = new Dictionary<Type, Type[]>();
            Type[] Required(Type type)
            {
                if (!requirements.TryGetValue(type, out var result))
                    requirements[type] = result = type.GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>()
                        .SelectMany(attribute => new[] { attribute.m_Type0, attribute.m_Type1, attribute.m_Type2 })
                        .Where(required => required != null).ToArray()!;
                return result;
            }
            while (pending.Count != 0)
            {
                MonoBehaviour? removable = null;
                foreach (var candidate in pending)
                {
                    var required = false;
                    foreach (var dependent in all)
                        if (dependent && dependent != candidate && dependent.gameObject == candidate.gameObject &&
                            Required(dependent.GetType()).Any(type => type.IsAssignableFrom(candidate.GetType())))
                        { required = true; break; }
                    if (!required) { removable = candidate; break; }
                }
                // Disabled MonoBehaviours still receive Awake on activation.
                // A dependency cycle must fall back to recorded visuals rather
                // than activate an unstripped gameplay or network component.
                if (!removable)
                    throw new InvalidOperationException("Replay native rig has a gameplay component dependency cycle: " + pending[0].GetType().FullName);
                Object.DestroyImmediate(removable);
                if (removable)
                    throw new InvalidOperationException("Replay native rig could not remove a gameplay component.");
                pending.Remove(removable!);
            }
        }

        private void BuildPlayerGraph(MonoBehaviour builder, Animator animator)
        {
            if (playerGraphs.TryGetValue(animator, out var prior) && prior.Graph.IsValid()) prior.Graph.Destroy();
            var graph = PlayableGraph.Create("Replay actor controller and IK");
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

        internal void BindRenderer(GeometrySnapshot geometry)
        {
            if (nativeLods.TryGetValue(geometry.PrefabRendererPath, out var lod))
            {
                rendererLods[geometry.Id] = (lod, geometry.PrefabRendererPath);
                lod.Bind(geometry.PrefabRendererPath);
            }
            if (!nutcrackerTorso || geometry.PrefabRendererPath.Length == 0 ||
                !Bones.TryGetValue(geometry.PrefabRendererPath, out var node) || !node ||
                !node.IsChildOf(nutcrackerTorso) ||
                geometry.Name != "TestShotgun" && geometry.Name != "GunBarrel") return;
            for (var i = 0; i < nutcrackerReferences.Count; i++)
                if (nutcrackerReferences[i].Id == geometry.Id)
                { nutcrackerReferences[i] = (geometry.Id, node, geometry.Rotation); return; }
            nutcrackerReferences.Add((geometry.Id, node, geometry.Rotation));
        }

        internal bool IsPreferredLod(string geometryId) => !rendererLods.TryGetValue(geometryId, out var lod) ||
            lod.Group.Preferred < 0 || lod.Group.Levels[lod.Group.Preferred].Contains(lod.Path);

        internal void EnsureVisibleBranch(Transform displayed)
        {
            if (!displayed || !displayed.IsChildOf(container.transform)) return;
            for (var node = displayed.parent; node && node != container.transform; node = node.parent)
                if (!node.gameObject.activeSelf) node.gameObject.SetActive(true);
        }

        internal void ClearRendererBindings()
        {
            rendererLods.Clear();
            nutcrackerReferences.Clear();
            displayedSkins.Clear();
            foreach (var group in lodGroups) { group.BoundPaths.Clear(); group.Preferred = -1; }
        }

        internal void BindSkin(GeometrySnapshot geometry, SkinnedMeshRenderer displayed)
        {
            if (!displayed || !displayed.sharedMesh || displayed.sharedMesh.blendShapeCount == 0) return;
            var native = SkinTransform(geometry)?.GetComponent<SkinnedMeshRenderer>();
            var indices = new int[displayed.sharedMesh.blendShapeCount];
            for (var index = 0; index < indices.Length; index++)
                indices[index] = native && native!.sharedMesh ? native.sharedMesh.GetBlendShapeIndex(displayed.sharedMesh.GetBlendShapeName(index)) : -1;
            displayedSkins[geometry.Id] = new SkinBinding { Displayed = displayed, Native = native, NativeIndices = indices,
                StateKey = EnemyVisualPoseCapture.BlendShapeStatePrefix + geometry.PrefabRendererPath };
        }

        internal void ApplyBlendShapes(EntitySnapshot entity)
        {
            foreach (var skin in displayedSkins.Values)
            {
                if (!skin.Displayed) continue;
                entity.State.TryGetValue(skin.StateKey, out var encoded);
                if (!string.Equals(encoded, skin.Encoded, StringComparison.Ordinal))
                {
                    skin.Encoded = encoded;
                    if (encoded == null) skin.Captured = Array.Empty<float>();
                    else
                    {
                        var values = encoded.Split(new[] { ',' }, 129);
                        skin.Captured = new float[Math.Min(128, values.Length)];
                        for (var index = 0; index < skin.Captured.Length; index++)
                            skin.Captured[index] = float.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                                !float.IsNaN(value) && !float.IsInfinity(value) ? value : float.NaN;
                    }
                }
                for (var index = 0; index < skin.NativeIndices.Length; index++)
                {
                    var nativeIndex = skin.NativeIndices[index];
                    var weight = skin.Native && skin.Native!.sharedMesh && nativeIndex >= 0 &&
                        nativeIndex < skin.Native.sharedMesh.blendShapeCount ? skin.Native.GetBlendShapeWeight(nativeIndex) : 0f;
                    if (index < skin.Captured.Length && !float.IsNaN(skin.Captured[index])) weight = skin.Captured[index];
                    skin.Displayed.SetBlendShapeWeight(index, weight);
                }
            }
        }

        internal void ApplyEnemyProceduralPose(EntitySnapshot entity)
        {
            if (entity.Kind != "enemy") return;
            // The recording stores the game's final procedural pose. The
            // controller/IK graph also writes transforms, so applying these
            // only before Evaluate loses spider leg motion and AI-facing
            // transforms. Inputs are supplied before Evaluate as well; the
            // recorded output remains authoritative afterwards.
            foreach (var bone in entity.Bones)
                if (Bones.TryGetValue(bone.Path, out var node) && node)
                {
                    node.localPosition = new Vector3(bone.Position.X, bone.Position.Y, bone.Position.Z);
                    node.localRotation = new Quaternion(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);
                    node.localScale = new Vector3(bone.Scale.X, bone.Scale.Y, bone.Scale.Z);
                }
            if (!nutcrackerTorso) return;
            foreach (var bone in entity.Bones)
                if (bone.Path == nutcrackerTorsoPath) return;
            // Older recordings omitted the torso bone but retained the hidden
            // shotgun's root-relative pose. Its animated transform chain lets
            // us recover the missing torso rotation without simulating AI.
            foreach (var reference in nutcrackerReferences)
            {
                if (!reference.Node) continue;
                var rotation = reference.Rotation;
                foreach (var pose in entity.Renderers)
                    if (pose.Id == reference.Id) { rotation = pose.Rotation; break; }
                var torsoToRenderer = Quaternion.Inverse(nutcrackerTorso!.rotation) * reference.Node.rotation;
                var worldRotation = actorRoot.rotation *
                    new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W) * Quaternion.Inverse(torsoToRenderer);
                var localRotation = Quaternion.Inverse(nutcrackerTorso.parent.rotation) * worldRotation;
                // NutcrackerEnemyAI writes Euler(turn + 90, 90, 90). Keep
                // that single turn axis: sparse animation phase differences
                // in the gun's pitch must not tilt the entire upper body.
                var neutral = Quaternion.Euler(90f, 90f, 90f);
                var delta = localRotation * Quaternion.Inverse(neutral);
                var length = Mathf.Sqrt(delta.z * delta.z + delta.w * delta.w);
                if (length > .00001f)
                    nutcrackerTorso.localRotation = new Quaternion(0f, 0f, delta.z / length, delta.w / length) * neutral;
                return;
            }
        }

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

        internal void ResetAnimationHistory()
        {
            // A controller retains its own animation stream, including values
            // left unkeyed by the destination clip. Resetting scene transforms
            // or WriteDefaultValues alone does not clear that stream. Rebuild
            // it for explicit seeks and cached transition references, before
            // replay parameters and state phases are reapplied. Ordinary
            // playback and repeated paused samples keep their existing graph.
            ResetPose();
            foreach (var entry in playerGraphs.Values) if (entry.Graph.IsValid()) entry.Graph.Destroy();
            foreach (var animator in Animators.Values) animator.Rebind();
            foreach (var item in builders)
                if (item.Builder && item.Builder.GetComponent<Animator>() is Animator animator &&
                    animator.runtimeAnimatorController && playerGraphs.ContainsKey(animator))
                    BuildPlayerGraph(item.Builder, animator);
            built = false;
            FreezeAutomaticGraphs();
        }

        internal void ResetPose()
        {
            // Full bone recordings bypass animation sampling/Prepare. Native
            // Animator or RigBuilder graphs must still never advance after the
            // authoritative recorded pose has been applied.
            if (!built) FreezeAutomaticGraphs();
            // Spawn clips key transforms which locomotion clips leave unkeyed.
            // Sampling must start from the prefab pose, independent of seeks.
            foreach (var pose in rest)
                if (pose.Node)
                {
                    pose.Node.localPosition = pose.Position;
                    pose.Node.localRotation = pose.Rotation;
                    pose.Node.localScale = pose.Scale;
                }
            foreach (var item in restBlendShapes)
                if (item.Skin && item.Skin.sharedMesh)
                    for (var index = 0; index < Math.Min(item.Weights.Length, item.Skin.sharedMesh.blendShapeCount); index++)
                        item.Skin.SetBlendShapeWeight(index, item.Weights[index]);
        }

        private void FreezeAutomaticGraphs()
        {
            foreach (var animator in Animators.Values)
                if (animator.playableGraph.IsValid()) animator.playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            foreach (var builder in builders)
                if (GameAccess.Read(builder.Builder, "graph") is PlayableGraph graph && graph.IsValid())
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
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
