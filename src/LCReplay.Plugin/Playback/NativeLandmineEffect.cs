using System;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // Start() in the game fires startIdle once. An inert mesh copy never does.
    internal sealed class NativeLandmineEffect
    {
        internal readonly GameObject Root;
        internal readonly Animator Animator;
        private readonly Transform[] nodes;
        private readonly Vector3[] positions, scales;
        private readonly Quaternion[] rotations;
        private double previous = -1;
        internal static NativeLandmineEffect? Create(Transform parent, int layer)
        {
            var prefab = PrefabAssetRegistry.ResolvePrefab("hazard:Landmine");
            var source = GameAccess.Read(prefab, "mineAnimator") as Animator;
            return prefab && source && source!.runtimeAnimatorController ? new NativeLandmineEffect(prefab!, source, parent, layer) : null;
        }
        private NativeLandmineEffect(Component prefab, Animator source, Transform parent, int layer)
        {
            var staging = new GameObject("Inactive mine staging"); staging.SetActive(false); staging.transform.SetParent(parent, false);
            Root = Object.Instantiate(source.gameObject, staging.transform, false); Root.SetActive(false);
            foreach (var script in Root.GetComponentsInChildren<MonoBehaviour>(true))
                if (script.GetType().Name != "HDAdditionalLightData") Object.DestroyImmediate(script);
            foreach (var audio in Root.GetComponentsInChildren<AudioSource>(true)) { audio.Stop(); Object.DestroyImmediate(audio); }
            foreach (var collider in Root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in Root.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
            foreach (var ps in Root.GetComponentsInChildren<ParticleSystem>(true))
            { var main = ps.main; main.playOnAwake = false; ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
            foreach (var light in Root.GetComponentsInChildren<Light>(true)) light.cullingMask = 1 << layer;
            Root.transform.SetParent(parent, false);
            Root.transform.localPosition = prefab.transform.InverseTransformPoint(source.transform.position);
            Root.transform.localRotation = Quaternion.Inverse(prefab.transform.rotation) * source.transform.rotation;
            Root.transform.localScale = source.transform == prefab.transform ? Vector3.one : source.transform.localScale;
            nodes = Root.GetComponentsInChildren<Transform>(true); positions = new Vector3[nodes.Length]; rotations = new Quaternion[nodes.Length]; scales = new Vector3[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            { nodes[i].gameObject.layer = layer; positions[i] = nodes[i].localPosition; rotations[i] = nodes[i].localRotation; scales[i] = nodes[i].localScale; }
            Animator = Root.GetComponent<Animator>(); Animator.fireEvents = false; Animator.applyRootMotion = false;
            Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Root.SetActive(true); Object.Destroy(staging);
        }
        internal void Sync(EntitySnapshot entity, double time, bool playing, float speed)
        {
            var exploded = entity.State.TryGetValue("hasExploded", out var value) && string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
            Root.SetActive(entity.Active && !exploded);
            if (!Root.activeInHierarchy) { previous = -1; return; }
            if (Animator.playableGraph.IsValid()) Animator.playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            Animator.speed = 1;
            if (previous < 0 || time < previous || time - previous > .45)
            {
                for (var i = 0; i < nodes.Length; i++)
                { nodes[i].localPosition = positions[i]; nodes[i].localRotation = rotations[i]; nodes[i].localScale = scales[i]; }
                Animator.Rebind(); Animator.SetTrigger("startIdle"); Animator.Update(.02f);
                var state = Animator.GetCurrentAnimatorStateInfo(0);
                var clips = Animator.GetCurrentAnimatorClipInfo(0);
                if (clips.Length != 0 && state.length > .001f)
                { Animator.Play(state.fullPathHash, 0, (float)(time / state.length)); Animator.Update(0); }
            }
            else if (playing && time > previous) Animator.Update((float)(time - previous));
            previous = time;
        }
    }
}
