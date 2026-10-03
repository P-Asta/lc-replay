using System;
using System.Collections.Generic;
using System.Globalization;
using LCReplay.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin.Capture
{
    // Store references and changed playback state, never audio samples or a
    // guessed emitter belonging to a different mesh.
    internal sealed class NativeAmbientCapture : IDisposable
    {
        private sealed class Entry
        {
            internal AudioSource Source = null!;
            internal int NativeId, LastFrame = -1;
            internal string Id = "", Clip = "", LastClip = "", LastMethod = "";
            internal float Volume, Pitch, LastGain;
            internal bool Playing, Poll, Shot;
        }
        private readonly Action<ReplayEvent> emit;
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private readonly List<Entry> polling = new List<Entry>();
        private readonly Queue<Transform> walking = new Queue<Transform>();
        private int cursor;
        private bool refresh;

        internal NativeAmbientCapture(Action<ReplayEvent> emit) { this.emit = emit; Refresh(); }
        internal void Refresh() { refresh = true; }

        internal void Tick()
        {
            if (walking.Count == 0 && refresh)
            {
                refresh = false;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || ReplayIsolation.IsReplayScene(scene)) continue;
                    foreach (var root in scene.GetRootGameObjects()) walking.Enqueue(root.transform);
                }
            }
            // Discovery and state checks have a fixed per-frame budget.
            for (var i = 0; i < 8 && walking.Count != 0; i++)
            {
                var node = walking.Dequeue(); if (!node) continue;
                for (var child = 0; child < node.childCount; child++) walking.Enqueue(node.GetChild(child));
                foreach (var source in node.GetComponents<AudioSource>()) Track(source);
            }
            for (var i = 0; i < 4 && polling.Count != 0; i++)
            {
                if (cursor >= polling.Count) cursor = 0;
                var entry = polling[cursor];
                if (!entry.Source || !entry.Source.gameObject.scene.isLoaded)
                {
                    Stop(entry); entries.Remove(entry.NativeId); polling.RemoveAt(cursor); continue;
                }
                cursor++;
                var source = entry.Source;
                if (entry.Shot && !source.isPlaying) Stop(entry);
                if ((!entry.Poll && !entry.Playing) || entry.Shot) continue;
                var playing = source.isActiveAndEnabled && source.isPlaying && source.loop && source.clip;
                if (!playing) { Stop(entry); continue; }
                if (!entry.Playing || entry.Clip != source.clip.name || Math.Abs(entry.Volume - source.volume) >= .02f || Math.Abs(entry.Pitch - source.pitch) >= .02f)
                    Play(entry, source.clip, true, 1);
            }
        }

        internal void Observe(AudioSource source, object[] args, string method)
        {
            var entry = Track(source); if (entry == null) return;
            if (method == "Stop") { Stop(entry); return; }
            var shot = method == "PlayOneShot";
            var clip = shot && args.Length != 0 ? args[0] as AudioClip : source.clip;
            if (!clip) return;
            var gain = shot && args.Length > 1 && args[1] is float volume ? volume : 1f;
            if (entry.LastFrame == Time.frameCount && entry.LastClip == clip!.name && entry.LastMethod == method && entry.LastGain == gain) return;
            entry.LastFrame = Time.frameCount; entry.LastClip = clip!.name; entry.LastMethod = method; entry.LastGain = gain;
            // A one-shot also sets isPlaying. It is not evidence that the
            // source's configured looping clip was started.
            if (!entry.Playing) entry.Shot = shot;
            Play(entry, clip, !shot && source.loop, gain);
        }

        internal void Forget(AudioSource source)
        {
            if (!source || !entries.TryGetValue(source.GetInstanceID(), out var entry)) return;
            Stop(entry); entries.Remove(entry.NativeId); polling.Remove(entry);
        }

        private Entry? Track(AudioSource source)
        {
            if (!Eligible(source)) return null;
            var id = source.GetInstanceID();
            if (entries.TryGetValue(id, out var entry))
            {
                if (entry.Source == source) return entry;
                Stop(entry); entries.Remove(id); polling.Remove(entry);
            }
            if (entries.Count >= 256) return null;
            var entrance = false;
            for (var node = source.transform; node; node = node.parent)
                foreach (var component in node.GetComponents<MonoBehaviour>())
                    if (component && component.GetType().Name == "EntranceTeleport") entrance = true;
            entry = new Entry { Source = source, NativeId = id, Id = "ambient:" + id.ToString(CultureInfo.InvariantCulture),
                Poll = source.loop && source.playOnAwake && !entrance };
            entries[id] = entry; polling.Add(entry); return entry;
        }

        internal static bool Eligible(AudioSource source)
        {
            if (!source || source.spatialBlend < .5f || !source.gameObject.scene.IsValid() || !source.gameObject.scene.isLoaded ||
                ReplayIsolation.IsReplayScene(source.gameObject.scene)) return false;
            for (var node = source.transform; node; node = node.parent)
                foreach (var component in node.GetComponents<MonoBehaviour>())
                {
                    if (!component) continue;
                    var type = component.GetType();
                    if (type.Name == "PlayerControllerB" || type.Name == "Turret" || type.Name == "Landmine" ||
                        GameAccess.Type("GrabbableObject")?.IsAssignableFrom(type) == true || GameAccess.Type("EnemyAI")?.IsAssignableFrom(type) == true ||
                        (type.Namespace ?? "").StartsWith("Dissonance", StringComparison.Ordinal)) return false;
                }
            return true;
        }

        private void Play(Entry entry, AudioClip clip, bool loop, float gain)
        {
            var source = entry.Source;
            if (!source.isActiveAndEnabled || !GameAccess.Finite(source.transform.position)) return;
            var position = source.transform.position;
            var anchor = GameAccess.Read(GameAccess.Singleton("StartOfRound"), "elevatorTransform") as Transform;
            var anchored = anchor && source.transform.IsChildOf(anchor);
            if (anchored) position = anchor!.InverseTransformPoint(position);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ambient"] = "true", ["source"] = entry.Id, ["clip"] = clip.name,
                ["loop"] = loop ? "true" : "false", ["anchor"] = anchored ? "ship-elevator" : "",
                ["x"] = Number(position.x), ["y"] = Number(position.y), ["z"] = Number(position.z),
                ["volume"] = Number(source.volume * gain), ["pitch"] = Number(source.pitch),
                ["min"] = Number(source.minDistance), ["max"] = Number(source.maxDistance),
                ["rolloff"] = ((int)source.rolloffMode).ToString(CultureInfo.InvariantCulture),
                ["offset"] = loop ? Number(source.time) : "0"
            };
            if (source.rolloffMode == AudioRolloffMode.Custom)
            {
                var curve = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                var values = new string[16];
                for (var i = 0; i < values.Length; i++) values[i] = Number(curve.Evaluate(i / 15f));
                data["attenuation"] = string.Join(",", values);
            }
            emit(new ReplayEvent { Category = "sound", Name = "play", Data = data });
            if (loop) { entry.Playing = true; entry.Clip = clip.name; entry.Volume = source.volume; entry.Pitch = source.pitch; }
        }
        private void Stop(Entry entry)
        {
            if (!entry.Playing && !entry.Shot) return;
            emit(new ReplayEvent { Category = "sound", Name = "stop", Data = new Dictionary<string, string> { ["ambient"] = "true", ["source"] = entry.Id } });
            entry.Playing = false; entry.Shot = false;
        }
        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        public void Dispose() { entries.Clear(); polling.Clear(); walking.Clear(); }
    }
}
