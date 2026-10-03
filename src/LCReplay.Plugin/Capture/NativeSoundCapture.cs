using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    // Clip identifiers and actions only: never sample or serialize audio data.
    internal sealed class NativeSoundCapture : IDisposable
    {
        private readonly Harmony harmony = new Harmony(ReplayPlugin.Guid + ".native-sounds");
        private static NativeSoundCapture? current;
        private readonly EntityTracker tracker;
        private readonly Action<ReplayEvent> emit;
        private readonly NativeAmbientCapture ambient;
        private readonly Dictionary<int, (AudioSource Source, EntityTracker.Entry Owner)> owners =
            new Dictionary<int, (AudioSource, EntityTracker.Entry)>();
        private readonly Dictionary<int, (int Frame, string Clip, string Method, float Gain, bool Stopped)> observed =
            new Dictionary<int, (int, string, string, float, bool)>();
        private readonly Dictionary<string, (Component Owner, AudioSource[] Sources)> actorSources =
            new Dictionary<string, (Component, AudioSource[])>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> activeLoops = new Dictionary<int, string>();
        private readonly Dictionary<int, AudioSource> ignoredSources = new Dictionary<int, AudioSource>();
        private readonly Dictionary<string, (Component Owner, Dictionary<int, string> Clips)> assetIds =
            new Dictionary<string, (Component, Dictionary<int, string>)>(StringComparer.Ordinal);
        private int actorCursor;
        private float nextCleanup;
        internal NativeSoundCapture(EntityTracker tracker, Action<ReplayEvent> emit)
        {
            this.tracker = tracker; this.emit = emit; ambient = new NativeAmbientCapture(emit); current = this;
            var patch = new HarmonyMethod(typeof(NativeSoundCapture), nameof(Observe));
            foreach (var method in typeof(AudioSource).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == "Play" || method.Name == "PlayOneShot" || method.Name == "Stop"))
                try { harmony.Patch(method, postfix: patch); } catch { }
        }
        private static void Observe(AudioSource __instance, object[] __args, MethodBase __originalMethod)
        {
            try { current?.Capture(__instance, __args, __originalMethod.Name); }
            catch { /* An optional sound observation cannot affect gameplay. */ }
        }
        internal void Tick()
        {
            Cleanup();
            ambient.Tick();
            // Unity playOnAwake does not call the managed Play wrapper. Inspect
            // one tracked actor per frame, reusing its source list thereafter.
            var entries = tracker.Entries;
            if (entries.Count == 0) return;
            if (actorCursor >= entries.Count) actorCursor = 0;
            var owner = entries[actorCursor++];
            if (!owner.Component || !(owner.Kind == "player" || owner.Kind == "item" || owner.Kind == "enemy" || owner.Kind == "hazard")) return;
            if (!actorSources.TryGetValue(owner.Id, out var cached) || cached.Owner != owner.Component)
            {
                cached = (owner.Component, owner.Component.GetComponentsInChildren<AudioSource>(true)
                    .Concat(owner.Kind == "hazard" ? HazardSources(owner.Component) : Array.Empty<AudioSource>()).Distinct().ToArray());
                if (actorSources.Count < 1024) actorSources[owner.Id] = cached;
            }
            foreach (var source in cached.Sources)
            {
                if (!source) continue;
                ambient.Forget(source);
                var id = source.GetInstanceID();
                if (source.loop && source.isPlaying && source.clip)
                {
                    if (!activeLoops.TryGetValue(id, out var clip) || clip != source.clip.name)
                        Capture(source, Array.Empty<object>(), "Play");
                }
                else if (activeLoops.ContainsKey(id)) Capture(source, Array.Empty<object>(), "Stop");
            }
        }
        private void Cleanup()
        {
            if (Time.realtimeSinceStartup < nextCleanup) return;
            nextCleanup = Time.realtimeSinceStartup + 2f;
            foreach (var id in owners.Where(pair => !pair.Value.Source || !pair.Value.Owner.Component).Select(pair => pair.Key).ToArray())
            { owners.Remove(id); observed.Remove(id); activeLoops.Remove(id); }
            foreach (var id in actorSources.Where(pair => !pair.Value.Owner).Select(pair => pair.Key).ToArray()) actorSources.Remove(id);
            foreach (var id in ignoredSources.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray()) ignoredSources.Remove(id);
            foreach (var id in assetIds.Where(pair => !pair.Value.Owner).Select(pair => pair.Key).ToArray()) assetIds.Remove(id);
        }
        private void Capture(AudioSource source, object[] args, string method)
        {
            if (!source || !source.gameObject.scene.IsValid() || !source.gameObject.scene.isLoaded ||
                ReplayIsolation.IsReplayScene(source.gameObject.scene)) return;
            Cleanup();
            var sourceId = source.GetInstanceID();
            if (ignoredSources.TryGetValue(sourceId, out var ignored) && ignored == source) return;
            var known = owners.TryGetValue(sourceId, out var cached) && cached.Source == source && cached.Owner.Component;
            var owner = known ? cached.Owner : tracker.Entries.FirstOrDefault(entry => entry.Component &&
                (entry.Kind == "player" || entry.Kind == "item" || entry.Kind == "enemy" || entry.Kind == "hazard") &&
                (source.transform == entry.Component.transform || source.transform.IsChildOf(entry.Component.transform) ||
                 entry.Kind == "hazard" && HazardSources(entry.Component).Contains(source)));
            if (owner == null) { ambient.Observe(source, args, method); return; }
            ambient.Forget(source);
            // Dissonance sources often live beneath a player. Live voice is
            // never recorded or resolved as an installed effect clip.
            if (!known && (GameAccess.Read(owner.Component, "currentVoiceChatAudioSource") as AudioSource == source ||
                source.GetComponents<Component>().Any(component => component &&
                    (component.GetType().Namespace ?? "").StartsWith("Dissonance", StringComparison.Ordinal))))
            { if (ignoredSources.Count < 1024) ignoredSources[sourceId] = source; return; }
            if (!known && owners.Count < 1024) owners[sourceId] = (source, owner);
            var evt = new ReplayEvent { Category = "sound", Name = method == "Stop" ? "stop" : "play", EntityId = owner.Id };
            observed.TryGetValue(sourceId, out var previous);
            if (method == "Stop" && (!observed.ContainsKey(sourceId) || previous.Stopped)) return;
            evt.Data["source"] = source.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            if (method != "Stop")
            {
                var shot = method == "PlayOneShot";
                var clip = shot && args.Length != 0 ? args[0] as AudioClip : source.clip;
                if (clip == null || !clip || string.IsNullOrEmpty(clip.name)) return;
                var gain = shot && args.Length > 1 && args[1] is float volume ? volume : 1f;
                // PlayOneShot(clip) forwards to PlayOneShot(clip, 1). Observe
                // that sound once, and suppress repeated Stop calls on idle sources.
                if (previous.Frame == Time.frameCount && previous.Clip == clip.name && previous.Method == method && previous.Gain == gain) return;
                if (observed.Count < 1024 || observed.ContainsKey(sourceId))
                    observed[sourceId] = (Time.frameCount, clip.name, method, gain, false);
                evt.Data["clip"] = clip.name;
                if (!assetIds.TryGetValue(owner.Id, out var assets) || assets.Owner != owner.Component)
                {
                    var catalog = owner.Kind == "player" ? NativeSoundAssets.InstalledCatalog("player") :
                        owner.Kind == "enemy" || owner.Kind == "item" || owner.Kind == "hazard"
                        ? NativeSoundAssets.InstalledCatalog(PrefabAssetRegistry.Key(owner.Component, owner.Kind)) : null;
                    assets = (owner.Component, (catalog ?? NativeSoundAssets.Catalog(owner.Component)).GroupBy(pair => pair.Value.GetInstanceID())
                        .ToDictionary(group => group.Key, group => group.First().Key));
                    if (assetIds.Count < 1024) assetIds[owner.Id] = assets;
                }
                if (assets.Clips.TryGetValue(clip.GetInstanceID(), out var identity)) evt.Data["asset"] = identity;
                if (owner.Kind == "enemy" || owner.Kind == "item" || owner.Kind == "hazard") evt.Data["prefab"] = PrefabAssetRegistry.Key(owner.Component, owner.Kind);
                else if (owner.Kind == "player") evt.Data["prefab"] = "player";
                evt.Data["path"] = EntityTracker.RelativePath(owner.Component.transform, source.transform);
                evt.Data["component"] = Array.IndexOf(source.GetComponents<AudioSource>(), source).ToString(CultureInfo.InvariantCulture);
                var position = owner.Component.transform.InverseTransformPoint(source.transform.position);
                evt.Data["x"] = position.x.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["y"] = position.y.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["z"] = position.z.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["loop"] = !shot && source.loop ? "true" : "false";
                if (!shot && source.loop)
                {
                    if (activeLoops.Count < 1024 || activeLoops.ContainsKey(sourceId)) activeLoops[sourceId] = clip.name;
                    evt.Data["offset"] = source.time.ToString("R", CultureInfo.InvariantCulture);
                }
                else if (!shot) activeLoops.Remove(sourceId);
                evt.Data["volume"] = (source.volume * gain).ToString("R", CultureInfo.InvariantCulture);
                evt.Data["pitch"] = source.pitch.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["spatial"] = source.spatialBlend.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["min"] = source.minDistance.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["max"] = source.maxDistance.ToString("R", CultureInfo.InvariantCulture);
                evt.Data["rolloff"] = ((int)source.rolloffMode).ToString(CultureInfo.InvariantCulture);
                evt.Data["player"] = owner.Kind == "player" || owner.Kind == "item" ? "true" : "false";
            }
            else { observed[sourceId] = (Time.frameCount, "", method, 0f, true); activeLoops.Remove(sourceId); }
            emit(evt);
        }
        private static IEnumerable<AudioSource> HazardSources(Component owner)
        {
            foreach (var name in new[] { "mainAudio", "farAudio", "bulletCollisionAudio", "berserkAudio", "mineAudio" })
                if (GameAccess.Read(owner, name) is AudioSource source && source) yield return source;
        }
        internal void RefreshAmbient() => ambient.Refresh();
        public void Dispose() { if (current == this) current = null; harmony.UnpatchSelf(); ambient.Dispose(); owners.Clear(); observed.Clear(); actorSources.Clear(); activeLoops.Clear(); ignoredSources.Clear(); assetIds.Clear(); }
    }
}
