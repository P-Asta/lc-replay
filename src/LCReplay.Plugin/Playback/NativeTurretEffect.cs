using System;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    internal sealed class NativeTurretEffect
    {
        internal readonly ParticleSystem? Bullets;
        internal readonly AudioSource? Main, Far, Collision;
        internal bool PlayAudio { get; set; } = true;
        private readonly AudioClip? detect, firing, distant;
        private readonly string id;
        private readonly Transform owner;
        private readonly Vector3 origin;
        private readonly Quaternion rotation;
        private readonly Quaternion aimRotation;
        private readonly Vector3 aimOffset;
        private readonly float mainPitch, farPitch, collisionPitch;
        private readonly ParticleSystem[] systems;
        private int previousMode = -1;
        private double previousTime = -1;
        private NativeSoundPlayback? recorded;

        internal static bool IsFiring(EntitySnapshot entity)
        {
            var mode = entity.State.TryGetValue("turretMode", out var value) ? value : "Detection";
            return entity.Active && !Flag(entity, "turretActive", false) &&
                (mode == "Firing" || mode == "2" || (mode == "Berserk" || mode == "3") && !Flag(entity, "enteringBerserkMode", true));
        }
        private static bool Flag(EntitySnapshot entity, string key, bool value) =>
            entity.State.TryGetValue(key, out var state) && bool.TryParse(state, out var parsed) && parsed == value;

        internal static bool AudioAllowed(EntitySnapshot entity, string clip)
        {
            if (!entity.Active) return false;
            var prefab = PrefabAssetRegistry.ResolvePrefab("hazard:Turret");
            if (!prefab) return false;
            var collision = GameAccess.Read(prefab, "bulletCollisionAudio") as AudioSource;
            if ((GameAccess.Read(prefab, "firingSFX") as AudioClip)?.name == clip ||
                (GameAccess.Read(prefab, "firingFarSFX") as AudioClip)?.name == clip || collision?.clip?.name == clip)
                return IsFiring(entity);
            var mode = entity.State.TryGetValue("turretMode", out var state) ? state : "Detection";
            if ((GameAccess.Read(prefab, "detectPlayerSFX") as AudioClip)?.name == clip)
                return !Flag(entity, "turretActive", false) && (mode == "Charging" || mode == "1");
            if ((GameAccess.Read(prefab, "berserkAudio") as AudioSource)?.clip?.name == clip)
                return !Flag(entity, "turretActive", false) && (mode == "Berserk" || mode == "3");
            return true;
        }

        internal static NativeTurretEffect? Create(EntitySnapshot entity, Transform parent, int layer, NativeSoundPlayback? sounds)
        {
            var prefab = PrefabAssetRegistry.ResolvePrefab("hazard:Turret");
            return prefab ? new NativeTurretEffect(prefab!, entity.Id, parent, layer, sounds) : null;
        }

        private NativeTurretEffect(Component prefab, string entityId, Transform parent, int layer, NativeSoundPlayback? sounds)
        {
            owner = parent; id = entityId; recorded = sounds;
            detect = GameAccess.Read(prefab, "detectPlayerSFX") as AudioClip;
            firing = GameAccess.Read(prefab, "firingSFX") as AudioClip; distant = GameAccess.Read(prefab, "firingFarSFX") as AudioClip;
            if (GameAccess.Read(prefab, "bulletParticles") is ParticleSystem particles)
            {
                origin = prefab.transform.InverseTransformPoint(particles.transform.position);
                rotation = Quaternion.Inverse(prefab.transform.rotation) * particles.transform.rotation;
                var aim = GameAccess.Read(prefab, "aimPoint") as Transform;
                aimRotation = aim ? Quaternion.Inverse(aim!.rotation) * particles.transform.rotation : rotation;
                aimOffset = aim ? Quaternion.Inverse(aim!.rotation) * (particles.transform.position - aim.position) : Vector3.zero;
                Bullets = NativeEffectCopy.Particles(particles, parent, layer);
            }
            systems = Bullets ? Bullets!.GetComponentsInChildren<ParticleSystem>(true) : Array.Empty<ParticleSystem>();
            AudioSource? Source(string field)
            {
                if (!(GameAccess.Read(prefab, field) is AudioSource source)) return null;
                var copy = NativeEffectCopy.Audio(source, parent, "turret " + field);
                copy.transform.localPosition = prefab.transform.InverseTransformPoint(source.transform.position); return copy;
            }
            Main = Source("mainAudio"); Far = Source("farAudio"); Collision = Source("bulletCollisionAudio");
            mainPitch = Main ? Main!.pitch : 1; farPitch = Far ? Far!.pitch : 1; collisionPitch = Collision ? Collision!.pitch : 1;
        }

        internal void Sync(EntitySnapshot entity, ReplayFrame frame, double time, bool playing, float speed, Vector3 listener, NativeSoundPlayback? sounds)
        {
            recorded = sounds;
            var active = entity.Active && (!entity.State.TryGetValue("turretActive", out var enabled) || !string.Equals(enabled, "False", StringComparison.OrdinalIgnoreCase));
            var mode = entity.State.TryGetValue("turretMode", out var value) ? value : "Detection";
            var number = mode == "Charging" ? 1 : mode == "Firing" ? 2 : mode == "Berserk" ? 3 : int.TryParse(mode, out var parsed) ? parsed : 0;
            var firingNow = IsFiring(entity);
            var seeking = previousTime < 0 || time < previousTime - .02 || time - previousTime > .45;
            Loop(Main, firing, firingNow, playing, speed, mainPitch, listener);
            Loop(Far, distant, firingNow, playing, speed, farPitch, listener);
            Loop(Collision, Collision ? Collision!.clip : null, firingNow, playing, speed, collisionPitch, listener);
            if (Collision && firingNow) Collision!.volume = 1f;
            if (Main && (!PlayAudio || !active || number == 0)) Main!.Stop();
            if (PlayAudio && Main && detect && active && number == 1 && previousMode != 1 && !seeking && playing && speed <= 3 &&
                recorded?.ContainsClip(id, detect!.name) != true && InRange(Main!, listener))
            { Main!.loop = false; Main.PlayOneShot(detect); }
            if (Bullets)
            {
                var aim = frame.Lines.FirstOrDefault(line => line.Id == "turret-laser-" + id && line.Positions.Length >= 6);
                if (aim != null)
                {
                    var points = aim.Positions; var start = new Vector3(points[0], points[1], points[2]);
                    var end = new Vector3(points[3], points[4], points[5]);
                    var direction = (end - start).sqrMagnitude > .001f ? Quaternion.LookRotation(end - start) : owner.rotation;
                    Bullets!.transform.SetPositionAndRotation(start + direction * aimOffset, direction * aimRotation);
                    if (Collision) Collision!.transform.position = end;
                }
                else { Bullets!.transform.localPosition = origin; Bullets.transform.localRotation = rotation; }
                if (!firingNow) Bullets!.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                else
                {
                    if (seeking) Bullets!.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    if (!Bullets!.isPlaying) Bullets.Play(true);
                    foreach (var system in systems)
                    { var main = system.main; main.simulationSpeed = playing ? speed : 0; }
                }
            }
            previousMode = number; previousTime = time;
        }

        private void Loop(AudioSource? source, AudioClip? clip, bool enabled, bool playing, float speed, float pitch, Vector3 listener)
        {
            if (!source || !clip) return;
            source!.mute = !PlayAudio || !playing || speed > 3 || !InRange(source, listener);
            if (!playing) source.Pause();
            enabled &= PlayAudio && recorded?.ContainsClip(id, clip!.name) != true;
            if (!enabled) { if (source.loop) { source.Stop(); source.loop = false; } return; }
            if (source!.clip != clip) { source.Stop(); source.clip = clip; }
            source.loop = true; source.pitch = Mathf.Clamp(pitch * speed, .1f, 3);
            source.mute = !playing || speed > 3 || !InRange(source, listener);
            if (!source.isPlaying && playing) source.Play();
            if (!playing) source.Pause();
            else source.UnPause();
        }
        private static bool InRange(AudioSource source, Vector3 listener) => (source.transform.position - listener).sqrMagnitude < source.maxDistance * source.maxDistance;
    }
}
