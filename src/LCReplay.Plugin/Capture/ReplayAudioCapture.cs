using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using LCReplay.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    // Capture source audio before the game's final listener mix. The default
    // includes known gameplay sources on players but leaves voice chat opt-in.
    internal sealed class ReplayAudioCapture : IDisposable
    {
        private static ReplayAudioCapture? current;
        private readonly Harmony harmony = new Harmony(ReplayPlugin.Guid + ".audio");
        private readonly BlockingCollection<RawBlock> pending = new BlockingCollection<RawBlock>(new ConcurrentQueue<RawBlock>(), 256);
        private readonly ConcurrentQueue<Block> blocks = new ConcurrentQueue<Block>();
        private readonly HashSet<ReplayAudioTap> taps = new HashSet<ReplayAudioTap>();
        private readonly List<VirtualOneShot> oneShots = new List<VirtualOneShot>();
        private readonly bool includeVoice;
        private readonly Action<string> log;
        private readonly long origin = Stopwatch.GetTimestamp();
        private readonly int sourceRate;
        private readonly Task encoder;
        private int queued, dropped;
        private float nextScan;
        private float nextPose;
        private bool disposed;
        internal int Dropped => Volatile.Read(ref dropped);

        internal ReplayAudioCapture(bool includeVoice, Action<string> log)
        {
            this.includeVoice = includeVoice;
            this.log = log;
            sourceRate = Math.Max(8000, AudioSettings.outputSampleRate);
            encoder = Task.Factory.StartNew(EncodeLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            current = this;
            try
            {
                var postfix = new HarmonyMethod(typeof(ReplayAudioCapture).GetMethod(nameof(SourceStarted), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var method in typeof(AudioSource).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.Name == "Play" || method.Name == "PlayOneShot"))
                    try { harmony.Patch(method, postfix: postfix); }
                    catch { /* The bounded scan below still catches persistent sources. */ }
                Scan();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static void SourceStarted(AudioSource __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                var recorder = current;
                recorder?.Attach(__instance, restarted: __originalMethod.Name == "Play");
                if (__originalMethod.Name == "PlayOneShot" && __args.Length > 0 && __args[0] is AudioClip clip)
                    recorder?.RegisterOneShot(__instance, clip, __args.Length > 1 && __args[1] is float gain ? gain : 1f);
            }
            catch { /* Audio observation must never change the game's Play call. */ }
        }

        internal void Tick()
        {
            if (disposed) return;
            if (Time.unscaledTime >= nextPose)
            {
                nextPose = Time.unscaledTime + .1f;
                var budget = 32;
                foreach (var tap in taps)
                    if (tap)
                    {
                        tap.UpdatePose();
                        if (budget > 0 && tap.CaptureVirtual()) budget--;
                    }
                CaptureVirtualOneShots(ref budget);
            }
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 2f;
                Scan();
            }
        }

        private void Scan()
        {
            foreach (var tap in taps) if (tap) tap.FlushIfStopped();
            // Unity destroys the tap with its source. Dead entries must not
            // consume the active-source budget for the rest of a long day.
            taps.RemoveWhere(tap => !tap);
            foreach (var source in Object.FindObjectsOfType<AudioSource>(true))
                if (source && source.isPlaying) Attach(source);
        }

        private void Attach(AudioSource source, bool restarted = false)
        {
            if (disposed || !source || !source.gameObject.scene.IsValid() || !source.gameObject.scene.isLoaded ||
                ReplayIsolation.IsReplayScene(source.gameObject.scene) ||
                !includeVoice && IsVoiceSource(source)) return;
            var tap = source.GetComponents<ReplayAudioTap>().FirstOrDefault(candidate => candidate.Observes(source));
            if (tap && taps.Contains(tap))
            {
                if (restarted) tap.Restart();
                return;
            }
            if (taps.Count >= 512) return;
            if (!tap) tap = source.gameObject.AddComponent<ReplayAudioTap>();
            if (!taps.Add(tap!)) return;
            tap!.Configure(this, source, sourceRate, origin, source.GetInstanceID().ToString());
        }

        private void RegisterOneShot(AudioSource source, AudioClip clip, float gain)
        {
            if (disposed || !source || !clip || clip.loadType != AudioClipLoadType.DecompressOnLoad ||
                clip.loadState != AudioDataLoadState.Loaded || clip.samples <= 0 || clip.channels < 1 ||
                clip.channels > 8 || gain <= 0 || oneShots.Count >= 128) return;
            var tap = source.GetComponents<ReplayAudioTap>().FirstOrDefault(candidate => candidate.Observes(source));
            if (!tap || !taps.Contains(tap)) return;
            var now = Time.unscaledTime;
            if (oneShots.Any(job => job.Source == source && job.Clip == clip && now - job.Started < .02f)) return;
            oneShots.Add(new VirtualOneShot(source, clip, now, gain, source.GetInstanceID().ToString()));
        }

        private void CaptureVirtualOneShots(ref int budget)
        {
            var now = Time.unscaledTime;
            for (var index = oneShots.Count - 1; index >= 0; index--)
            {
                var job = oneShots[index];
                if (!job.Source || !job.Clip || !job.Source!.isPlaying ||
                    job.Clip.loadState != AudioDataLoadState.Loaded)
                { oneShots.RemoveAt(index); continue; }
                var pitch = Mathf.Clamp(Mathf.Abs(job.Source.pitch), .1f, 3f);
                var cursor = Math.Min(job.Clip.samples, Math.Max(0,
                    (int)((now - job.Started) * job.Clip.frequency * pitch)));
                if (budget > 0 && job.Source.isVirtual && !job.Source.mute && job.Source.volume > .0001f &&
                    cursor > job.Cursor && CaptureReadableClip(job.Source, job.Clip, job.Cursor,
                        cursor - job.Cursor, job.Gain * job.Source.volume, job.Id)) budget--;
                job.Cursor = cursor;
                if (cursor >= job.Clip.samples) oneShots.RemoveAt(index);
            }
        }

        internal bool CaptureReadableClip(AudioSource source, AudioClip clip, int firstFrame, int frameCount, float gain, string id)
        {
            if (disposed || !source || !clip || clip.loadType != AudioClipLoadType.DecompressOnLoad ||
                clip.loadState != AudioDataLoadState.Loaded || clip.frequency < 8000 || clip.channels < 1 ||
                clip.channels > 8 || frameCount <= 0 || gain <= 0 ||
                !GameAccess.Finite(source.transform.position)) return false;
            var pitch = Mathf.Clamp(Mathf.Abs(source.pitch), .1f, 3f);
            var maxFrames = Math.Min(16384, (int)(ReplayAudioCodec.MaxSamplesPerBlock * clip.frequency * pitch /
                (float)ReplayAudioCodec.SampleRate));
            if (maxFrames < 1) return false;
            if (frameCount > maxFrames)
            {
                firstFrame = (firstFrame + frameCount - maxFrames) % clip.samples;
                frameCount = maxFrames;
            }
            var count = Math.Min(ReplayAudioCodec.MaxSamplesPerBlock,
                Math.Max(1, (int)Math.Round(frameCount * ReplayAudioCodec.SampleRate / (double)(clip.frequency * pitch))));
            var input = ArrayPool<float>.Shared.Rent(frameCount * clip.channels);
            var output = ArrayPool<short>.Shared.Rent(count);
            try
            {
                if (!clip.GetData(input, firstFrame)) return false;
                var peak = 0;
                for (var index = 0; index < count; index++)
                {
                    var frame = count == 1 ? 0 : Math.Min(frameCount - 1,
                        (int)Math.Round(index * (frameCount - 1.0) / (count - 1)));
                    var value = 0f;
                    for (var channel = 0; channel < clip.channels; channel++)
                        value += input[frame * clip.channels + channel];
                    var sample = (short)Mathf.RoundToInt(Mathf.Clamp(value * gain / clip.channels, -1f, 1f) * 32767f);
                    output[index] = sample;
                    peak = Math.Max(peak, Math.Abs((int)sample));
                }
                if (peak <= 32) return false;
                var stamp = (Stopwatch.GetTimestamp() - origin) / (double)Stopwatch.Frequency -
                    count / (double)ReplayAudioCodec.SampleRate;
                EnqueuePcm(stamp, output, count, id,
                    new SourcePose(source.transform.position, source, virtualClip: true));
                return true;
            }
            catch { return false; }
            finally { ArrayPool<float>.Shared.Return(input); ArrayPool<short>.Shared.Return(output); }
        }

        private sealed class VirtualOneShot
        {
            internal readonly AudioSource Source;
            internal readonly AudioClip Clip;
            internal readonly float Started, Gain;
            internal readonly string Id;
            internal int Cursor;
            internal VirtualOneShot(AudioSource source, AudioClip clip, float started, float gain, string id)
            { Source = source; Clip = clip; Started = started; Gain = gain; Id = id; }
        }

        private static bool IsVoiceSource(AudioSource source)
        {
            var playerType = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
            var player = playerType == null ? null : source.GetComponentInParent(playerType);
            if (player)
            {
                if (GameAccess.Read(player, "currentVoiceChatAudioSource") as AudioSource == source) return true;
                var voiceSettings = GameAccess.Read(player, "currentVoiceChatIngameSettings");
                if (GameAccess.Read(voiceSettings, "voiceAudio") as AudioSource == source) return true;
            }
            for (var parent = source.transform; parent; parent = parent.parent)
            {
                if (player != null && player && parent == player.transform) break;
                var name = parent.name;
                if (name.IndexOf("Dissonance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("VoicePlayback", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("VoiceChat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("VoiceAudio", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                foreach (var component in parent.GetComponents<Component>())
                    if (component && (component.GetType().FullName ?? "").IndexOf("Dissonance", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            return false;
        }

        private static bool IsPlayerSource(AudioSource source)
        {
            var playerType = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
            if (playerType != null && source.GetComponentInParent(playerType)) return true;
            // A held item's AudioSource usually remains under the item rather
            // than under the player's transform.
            var itemType = GameAccess.Type("GrabbableObject");
            var item = itemType == null ? null : source.GetComponentInParent(itemType);
            return item && GameAccess.Read(item, "playerHeldBy") is Component holder && holder;
        }

        internal void EnqueuePcm(double time, short[] samples, int count, string source, SourcePose pose)
        {
            if (disposed) return;
            if (Interlocked.Increment(ref queued) > 256)
            { Interlocked.Decrement(ref queued); Interlocked.Increment(ref dropped); return; }
            short[]? copy = null;
            var accepted = false;
            try
            {
                copy = ArrayPool<short>.Shared.Rent(count);
                Array.Copy(samples, copy, count);
                accepted = pending.TryAdd(new RawBlock(Math.Max(0, time), count, copy, source, pose));
            }
            catch { /* Never throw into Unity's audio callback. */ }
            if (!accepted)
            {
                if (copy != null) ArrayPool<short>.Shared.Return(copy);
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref dropped);
            }
        }

        private void EncodeLoop()
        {
            foreach (var raw in pending.GetConsumingEnumerable())
            {
                try
                {
                    var encoded = Convert.ToBase64String(ReplayAudioCodec.Encode(raw.Samples, raw.Count));
                    blocks.Enqueue(new Block(raw.Time, raw.Count, encoded, raw.Source, raw.Pose));
                }
                catch (Exception)
                {
                    Interlocked.Decrement(ref queued);
                    Interlocked.Increment(ref dropped);
                }
                finally { ArrayPool<short>.Shared.Return(raw.Samples); }
            }
        }

        internal IEnumerable<ReplayEvent> Drain()
        {
            while (blocks.TryDequeue(out var block))
            {
                Interlocked.Decrement(ref queued);
                yield return new ReplayEvent { Time = block.Time, Category = "audio", Name = "source-block",
                    Data = new Dictionary<string, string>
                    {
                        ["rate"] = ReplayAudioCodec.SampleRate.ToString(), ["samples"] = block.Samples.ToString(),
                        ["adpcm"] = block.Adpcm, ["source"] = block.Source,
                        ["x"] = block.Pose.X.ToString("R", CultureInfo.InvariantCulture),
                        ["y"] = block.Pose.Y.ToString("R", CultureInfo.InvariantCulture),
                        ["z"] = block.Pose.Z.ToString("R", CultureInfo.InvariantCulture),
                        ["spatial"] = block.Pose.Blend.ToString("R", CultureInfo.InvariantCulture),
                        ["min"] = block.Pose.Min.ToString("R", CultureInfo.InvariantCulture),
                        ["max"] = block.Pose.Max.ToString("R", CultureInfo.InvariantCulture),
                        ["rolloff"] = block.Pose.Rolloff.ToString(CultureInfo.InvariantCulture),
                        ["player"] = block.Pose.Player ? "true" : "false",
                        ["global"] = block.Pose.Global ? "true" : "false",
                        ["capture"] = block.Pose.Virtual ? "clip" : "tap"
                    } };
            }
        }

        internal void Finish()
        {
            if (disposed) return;
            foreach (var tap in taps) if (tap) tap.Flush();
            Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            current = null;
            foreach (var tap in taps) if (tap) tap.DisableCapture();
            taps.Clear();
            disposed = true;
            pending.CompleteAdding();
            try { encoder.GetAwaiter().GetResult(); }
            catch (Exception ex) { log("Audio encoder stopped: " + ex.Message); }
            pending.Dispose();
            try { harmony.UnpatchSelf(); }
            catch (Exception ex) { log("Audio hooks could not all be removed: " + ex.Message); }
        }

        internal readonly struct SourcePose
        {
            internal readonly float X, Y, Z, Blend, Min, Max;
            internal readonly int Rolloff;
            internal readonly bool Player, Virtual, Global;
            internal SourcePose(Vector3 position, AudioSource source, bool virtualClip = false)
            {
                X = position.x; Y = position.y; Z = position.z;
                // Local player effect sources are often mixed as 2D for the
                // player wearing the suit. In a replay they belong at that
                // player's recorded world position for the free camera.
                var playerEffect = IsPlayerSource(source);
                Player = playerEffect;
                Virtual = virtualClip;
                // A source can be 2D for the recording player's listener while
                // still belonging to a world object. Preserve 2D only for
                // genuinely global music/UI; the replay listener can then hear
                // world effects from its own position.
                var global = !playerEffect && IsGlobalSource(source);
                Global = global;
                Blend = global ? 0f : 1f;
                Min = Mathf.Clamp(source.minDistance, .01f, 1000f);
                Max = Mathf.Clamp(source.maxDistance, Min, 10000f);
                Rolloff = source.rolloffMode == AudioRolloffMode.Custom ? (int)AudioRolloffMode.Logarithmic : (int)source.rolloffMode;
            }
        }

        private static bool IsGlobalSource(AudioSource source)
        {
            if (source.GetComponentInParent<Camera>() || source.GetComponentInParent<Canvas>()) return true;
            var sound = GameAccess.Singleton("SoundManager");
            foreach (var field in new[] { "ambienceAudio", "ambienceAudioNonDiagetic", "musicSource",
                "highAction1", "highAction2", "lowAction", "misc2DAudio" })
                if (GameAccess.Read(sound, field) as AudioSource == source) return true;
            if (GameAccess.Read(GameAccess.Singleton("TimeOfDay"), "TimeOfDayMusic") as AudioSource == source) return true;
            for (var parent = source.transform; parent; parent = parent.parent)
            {
                var name = parent.name;
                if (name.IndexOf("MenuManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("UIAudio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private readonly struct RawBlock
        {
            internal readonly double Time;
            internal readonly int Count;
            internal readonly short[] Samples;
            internal readonly string Source;
            internal readonly SourcePose Pose;
            internal RawBlock(double time, int count, short[] samples, string source, SourcePose pose)
            { Time = time; Count = count; Samples = samples; Source = source; Pose = pose; }
        }

        private readonly struct Block
        {
            internal readonly double Time;
            internal readonly int Samples;
            internal readonly string Adpcm;
            internal readonly string Source;
            internal readonly SourcePose Pose;
            internal Block(double time, int samples, string adpcm, string source, SourcePose pose)
            { Time = time; Samples = samples; Adpcm = adpcm; Source = source; Pose = pose; }
        }
    }

    internal sealed class ReplayAudioTap : MonoBehaviour
    {
        private readonly object gate = new object();
        private readonly short[] samples = new short[ReplayAudioCodec.MaxSamplesPerBlock];
        private volatile ReplayAudioCapture? capture;
        private long origin;
        private int inputRate, phase, count, peak;
        private double blockStart;
        private AudioSource? observedSource;
        private AudioClip? virtualClip;
        private int virtualCursor;
        private string source = "";
        private ReplayAudioCapture.SourcePose pose;

        internal bool Observes(AudioSource audio) => observedSource == audio;

        internal void Configure(ReplayAudioCapture owner, AudioSource audioSource, int rate, long start, string id)
        {
            lock (gate)
            {
                capture = owner; observedSource = audioSource; inputRate = rate; origin = start; source = id;
                phase = 0; count = 0; peak = 0; enabled = true;
            }
            UpdatePose();
            virtualClip = audioSource.clip;
            try { virtualCursor = audioSource.timeSamples; }
            catch { virtualCursor = 0; }
        }

        internal void Restart()
        {
            Flush();
            var audio = observedSource;
            if (!audio) return;
            virtualClip = audio!.clip;
            virtualCursor = 0;
        }

        internal bool CaptureVirtual()
        {
            var audio = observedSource;
            var owner = capture;
            if (!audio || owner == null) return false;
            var clip = audio!.clip;
            if (!clip) { virtualClip = null; virtualCursor = 0; return false; }
            int cursor;
            try { cursor = Mathf.Clamp(audio.timeSamples, 0, clip!.samples); }
            catch { return false; }
            if (virtualClip != clip)
            { virtualClip = clip; virtualCursor = cursor; return false; }
            var first = virtualCursor;
            virtualCursor = cursor;
            if (!audio.isPlaying || !audio.isVirtual || audio.mute || audio.volume <= .0001f) return false;
            var frames = cursor - first;
            if (frames < 0 && audio.loop) frames = clip.samples - first + cursor;
            if (frames <= 0) return false;
            return owner.CaptureReadableClip(audio, clip, first, frames, audio.volume, source);
        }

        internal void UpdatePose()
        {
            var audio = observedSource;
            if (!audio || !GameAccess.Finite(audio!.transform.position)) return;
            var latest = new ReplayAudioCapture.SourcePose(audio!.transform.position, audio);
            lock (gate) pose = latest;
        }

        internal void FlushIfStopped()
        {
            if (!observedSource || !observedSource!.isPlaying) Flush();
        }

        internal void DisableCapture()
        {
            lock (gate) { capture = null; observedSource = null; enabled = false; count = 0; }
        }

        internal void Flush()
        {
            lock (gate) Submit();
        }

        private void OnDisable()
        {
            lock (gate) Submit();
        }

        private void OnDestroy()
        {
            lock (gate) Submit();
        }

        // Unity calls this on the audio thread. Never touch Unity objects here.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (capture == null || channels <= 0 || inputRate <= 0) return;
            var callbackTime = (Stopwatch.GetTimestamp() - origin) / (double)Stopwatch.Frequency;
            lock (gate)
            {
                if (capture == null) return;
                var frames = data.Length / channels;
                for (var frame = 0; frame < frames; frame++)
                {
                    phase += ReplayAudioCodec.SampleRate;
                    if (phase < inputRate) continue;
                    phase -= inputRate;
                    if (count == 0) blockStart = callbackTime + frame / (double)inputRate;
                    float value = 0;
                    for (var channel = 0; channel < channels; channel++) value += data[frame * channels + channel];
                    value = Mathf.Clamp(value / channels, -1f, 1f);
                    var pcm = (short)Mathf.RoundToInt(value * 32767f);
                    samples[count++] = pcm;
                    peak = Math.Max(peak, Math.Abs((int)pcm));
                    if (count == samples.Length) Submit();
                }
            }
        }

        private void Submit()
        {
            var owner = capture;
            if (owner != null && count > 0 && peak > 32)
                owner.EnqueuePcm(blockStart, samples, count, source, pose);
            count = 0; peak = 0;
        }
    }
}
