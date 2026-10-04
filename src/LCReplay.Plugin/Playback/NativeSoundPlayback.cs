using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    internal sealed class NativeSoundPlayback : IDisposable
    {
        private readonly ReplayEvent[] events;
        private readonly HashSet<(string Entity, string Clip)> recordedClips;
        private readonly Dictionary<string, AudioSource> sources = new Dictionary<string, AudioSource>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> actorPrefabs;
        private readonly HashSet<string> playerSources = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> pausedSources = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> pitches = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, ReplayEvent> sourceEvents = new Dictionary<string, ReplayEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, ReplayEvent> sourceLoops = new Dictionary<string, ReplayEvent>(StringComparer.Ordinal);
        private Func<string, EntitySnapshot?>? state;
        private double previous = -1;
        private int cursor;
        private float nextCleanup;
        internal Vector3? ListenerPosition { get; set; }
        internal NativeSoundPlayback(ReplaySession session)
        {
            events = session.Events.Where(evt => evt.Category == "sound").OrderBy(evt => evt.Time).ToArray();
            recordedClips = new HashSet<(string, string)>(events.Select(evt => (evt.EntityId, evt.Data.GetValueOrDefault("clip", ""))));
            actorPrefabs = session.Frames.SelectMany(frame => frame.Entities).Where(actor => actor.Kind == "enemy" || actor.Kind == "hazard")
                .GroupBy(actor => actor.Id).ToDictionary(group => group.Key, group => group.First().Kind == "enemy" ? "enemy:" + group.First().Name :
                    group.First().Name.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0 ? "hazard:Turret" : "hazard:Landmine", StringComparer.Ordinal);
        }

        internal void Sync(double time, bool playing, float speed, bool mutePlayers, Func<string, Transform?> actor,
            Func<string, EntitySnapshot?>? entityState = null)
        {
            state = entityState;
            if (Time.realtimeSinceStartup >= nextCleanup)
            {
                nextCleanup = Time.realtimeSinceStartup + 2f;
                foreach (var key in sources.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray())
                { sources.Remove(key); pitches.Remove(key); sourceEvents.Remove(key); sourceLoops.Remove(key); playerSources.Remove(key); pausedSources.Remove(key); }
            }
            if (time < previous - .02 || time - previous > .45 || previous < 0)
            {
                foreach (var source in sources.Values) if (source) { source.Stop(); source.loop = false; source.clip = null; }
                sourceEvents.Clear(); sourceLoops.Clear();
                pausedSources.Clear();
                var loops = new Dictionary<string, ReplayEvent>();
                cursor = 0;
                while (cursor < events.Length && events[cursor].Time <= time)
                {
                    var evt = events[cursor++];
                    var key = evt.Data.GetValueOrDefault("source", "");
                    if (evt.Name == "stop") loops.Remove(key);
                    else if (evt.Data.GetValueOrDefault("loop") == "true") loops[key] = evt;
                }
                foreach (var evt in loops.Values) Play(evt, time, speed, actor);
            }
            previous = time;
            // Always advance while scrubbing/fast forwarding. Suppressed
            // one-shots must not accumulate and burst out on normal playback.
            while (cursor < events.Length && events[cursor].Time <= time)
                {
                    var evt = events[cursor++];
                    if (evt.Name == "stop")
                    {
                        var key = evt.Data.GetValueOrDefault("source", ""); pausedSources.Remove(key); sourceLoops.Remove(key);
                        if (sources.TryGetValue(key, out var stop) && stop) { stop.Stop(); stop.loop = false; stop.clip = null; }
                    }
                    else if (evt.Data.GetValueOrDefault("loop") == "true" || playing && speed <= 3f && time - evt.Time <= .25)
                        Play(evt, time, speed, actor);
                }
            foreach (var pair in sources)
            {
                if (!pair.Value) continue;
                if (pair.Value.loop && sourceLoops.TryGetValue(pair.Key, out var loop) && !Allowed(loop))
                { pair.Value.Stop(); pair.Value.loop = false; sourceLoops.Remove(pair.Key); pausedSources.Remove(pair.Key); }
                if (sourceEvents.TryGetValue(pair.Key, out var action) && !Allowed(action))
                { pair.Value.Stop(); pausedSources.Remove(pair.Key); continue; }
                if (pitches.TryGetValue(pair.Key, out var pitch)) pair.Value.pitch = Mathf.Clamp(pitch * speed, .1f, 3f);
                pair.Value.mute = !playing || speed > 3f || mutePlayers && playerSources.Contains(pair.Key) || OutOfRange(pair.Value);
                // Mute preserves loop phase on pause; restore its native pitch on resume.
                if (!playing && pair.Value.isPlaying) { pair.Value.Pause(); pausedSources.Add(pair.Key); }
                else if (playing && pausedSources.Remove(pair.Key)) pair.Value.UnPause();
            }
        }

        private void Play(ReplayEvent evt, double time, float speed, Func<string, Transform?> actor)
        {
            if (!Allowed(evt)) return;
            var name = evt.Data.GetValueOrDefault("clip", "");
            var prefabKey = evt.Data.GetValueOrDefault("prefab", actorPrefabs.GetValueOrDefault(evt.EntityId, ""));
            var clip = NativeSoundAssets.Resolve(prefabKey, evt.Data.GetValueOrDefault("asset", ""), name, out var prefab);
            if (!clip) return;
            var ambient = evt.Data.GetValueOrDefault("ambient") == "true";
            var anchor = ambient ? evt.Data.GetValueOrDefault("anchor", "") : "";
            var parent = actor(anchor.Length != 0 ? "anchor:" + anchor : evt.EntityId);
            if (!parent) return;
            var key = evt.Data.GetValueOrDefault("source", "");
            if (!sources.TryGetValue(key, out var source) || !source)
            {
                if (sources.Count >= 512) return;
                var obj = new GameObject("LCReplay native sound " + key) { hideFlags = HideFlags.DontSave };
                obj.transform.SetParent(parent, false);
                source = obj.AddComponent<AudioSource>(); source.playOnAwake = false; source.mute = true;
                sources[key] = source;
            }
            if (source.loop && sourceLoops.TryGetValue(key, out var oldLoop) && !Allowed(oldLoop))
            { source.Stop(); source.loop = false; sourceLoops.Remove(key); pausedSources.Remove(key); }
            sourceEvents[key] = evt;
            if (source.transform.parent != parent) { source.Stop(); source.loop = false; pausedSources.Remove(key); }
            source.transform.SetParent(parent, false);
            source.transform.localPosition = new Vector3(Number(evt, "x", 0f), Number(evt, "y", 0f), Number(evt, "z", 0f));
            if (evt.Data.GetValueOrDefault("player") == "true") playerSources.Add(key);
            else playerSources.Remove(key);
            // Gameplay uses several local 2D sources for the controlling player.
            // Spectating replays their world location, so those also need distance.
            source.spatialBlend = 1f;
            source.minDistance = Mathf.Clamp(Number(evt, "min", 1f), .01f, 1000f);
            source.maxDistance = Mathf.Clamp(Number(evt, "max", 20f), source.minDistance, 10000f);
            source.rolloffMode = (AudioRolloffMode)Mathf.Clamp(Mathf.RoundToInt(Number(evt, "rolloff", 0f)), 0, 2);
            // Helmet and held-item effects (including TZP inhalation) are 2D
            // only for their owner in the live game. Their AudioSource often
            // has a very large, unused 3D range. A replay spectator must hear
            // them near the actor rather than across the entire moon.
            if (evt.Data.GetValueOrDefault("player") == "true" && Number(evt, "spatial", 1f) < .5f)
            {
                source.maxDistance = Mathf.Min(source.maxDistance, 20f);
                source.minDistance = Mathf.Min(source.minDistance, source.maxDistance, 2f);
                source.rolloffMode = AudioRolloffMode.Logarithmic;
            }
            if (ambient && source.rolloffMode == AudioRolloffMode.Custom && evt.Data.TryGetValue("attenuation", out var attenuation))
            {
                var values = attenuation.Split(',');
                if (values.Length == 16)
                {
                    var keys = new Keyframe[16];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        var valid = float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) && !float.IsNaN(gain) && !float.IsInfinity(gain);
                        keys[i] = new Keyframe(i / 15f, valid ? Mathf.Clamp01(gain) : 0);
                    }
                    source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(keys));
                }
            }
            var prototype = evt.Data.ContainsKey("path") ? NativeSoundAssets.Source(prefab, evt.Data.GetValueOrDefault("path", ""),
                Mathf.RoundToInt(Number(evt, "component", 0f))) : null;
            if (prototype)
            {
                // The recording contains the runtime source range. A prefab
                // may have different defaults, especially for local-only SFX.
                source.spread = prototype!.spread;
                if (source.rolloffMode == AudioRolloffMode.Custom && prototype.rolloffMode == AudioRolloffMode.Custom)
                    source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, prototype.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            }
            source.dopplerLevel = 0f;
            pitches[key] = Number(evt, "pitch", 1f);
            source.pitch = Mathf.Clamp(pitches[key] * speed, .1f, 3f);
            var volume = Mathf.Clamp01(Number(evt, "volume", 1f));
            if (evt.Data.GetValueOrDefault("loop") == "true")
            {
                sourceLoops[key] = evt;
                source.clip = clip; source.loop = true; source.volume = volume;
                source.time = clip!.length > .01f ? Mathf.Repeat(Number(evt, "offset", 0f) + (float)(time - evt.Time) * pitches[key], clip.length) : 0f;
                source.Play();
            }
            else
            {
                if (OutOfRange(source)) return;
                if (!source.loop) source.volume = 1f;
                source.PlayOneShot(clip, source.volume > .001f ? volume / source.volume : 0f);
            }
        }
        private bool Allowed(ReplayEvent evt)
        {
            if (evt.Data.GetValueOrDefault("ambient") == "true")
                return evt.EntityId.Length == 0 && evt.Data.GetValueOrDefault("source", "").StartsWith("ambient:", StringComparison.Ordinal);
            if (state == null) return true;
            var entity = state(evt.EntityId);
            if (entity == null || !entity.Active) return false;
            // LungProp.DisconnectFromMachinery stops the docked hum. Older
            // recordings can carry a stale loop after the apparatus is taken.
            if (entity.Kind == "item" && entity.Name.IndexOf("Apparatus", StringComparison.OrdinalIgnoreCase) >= 0 && evt.Data.GetValueOrDefault("loop") == "true")
            {
                if (entity.State.TryGetValue("isLungDocked", out var docked) && bool.TryParse(docked, out var isDocked)) return isDocked;
                if (entity.State.GetValueOrDefault("isHeld") == "True" || entity.State.GetValueOrDefault("isInShipRoom") == "True") return false;
            }
            return entity.Kind != "hazard" || entity.Name.IndexOf("turret", StringComparison.OrdinalIgnoreCase) < 0 ||
                NativeTurretEffect.AudioAllowed(entity, evt.Data.GetValueOrDefault("clip", ""));
        }
        private bool OutOfRange(AudioSource source) => ListenerPosition is Vector3 listener &&
            (source.transform.position - listener).sqrMagnitude >= source.maxDistance * source.maxDistance;
        private static float Number(ReplayEvent evt, string key, float fallback) =>
            float.TryParse(evt.Data.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !float.IsNaN(value) && !float.IsInfinity(value) ? value : fallback;
        internal bool ContainsImpact(ReplayEvent impact) => events.Any(evt => evt.Name == "play" &&
            Math.Abs(evt.Time - impact.Time) < .2 && evt.Data.GetValueOrDefault("clip") == impact.Data.GetValueOrDefault("clip") &&
            (evt.EntityId == impact.EntityId || evt.Data.GetValueOrDefault("source") == impact.Data.GetValueOrDefault("source")));
        internal bool ContainsClip(string entity, string name) => recordedClips.Contains((entity, name));
        internal void Stop() { foreach (var source in sources.Values) if (source) source.Stop(); pausedSources.Clear(); previous = -1; }
        public void Dispose()
        {
            Stop();
            foreach (var source in sources.Values) if (source) UnityEngine.Object.Destroy(source.gameObject);
            sources.Clear(); actorPrefabs.Clear(); recordedClips.Clear(); pitches.Clear(); sourceEvents.Clear(); sourceLoops.Clear(); state = null; playerSources.Clear(); pausedSources.Clear();
        }
    }
}
