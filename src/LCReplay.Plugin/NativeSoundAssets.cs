using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin
{
    // Resolve effects within their owning prefab. Common clip names (and even
    // controller names) are shared by unrelated enemies in the installed game.
    internal static class NativeSoundAssets
    {
        private static readonly Dictionary<string, Dictionary<string, AudioClip>> Catalogs =
            new Dictionary<string, Dictionary<string, AudioClip>>(StringComparer.Ordinal);
        // Global lookup must not pin every loaded audio buffer in memory.
        private static readonly Dictionary<string, WeakReference<AudioClip>?> UniqueClips =
            new Dictionary<string, WeakReference<AudioClip>?>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AudioSource?> Sources = new Dictionary<string, AudioSource?>(StringComparer.Ordinal);
        private static float nextScan;
        private static Component? playerPrefab;

        private static Component? Prefab(string key)
        {
            if (key != "player") return PrefabAssetRegistry.ResolvePrefab(key);
            if (playerPrefab) return playerPrefab;
            var type = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
            if (type == null) return null;
            var obj = GameAccess.Read(GameAccess.Singleton("StartOfRound"), "playerPrefab") as GameObject;
            playerPrefab = obj ? obj!.GetComponentInChildren(type, true) : Resources.FindObjectsOfTypeAll(type)
                .OfType<Component>().FirstOrDefault(component => component && !component.gameObject.scene.IsValid());
            return playerPrefab;
        }

        internal static string Identity(Component owner, AudioClip clip)
        {
            var clips = Catalog(owner);
            return clips.FirstOrDefault(pair => pair.Value == clip).Key ?? "";
        }

        internal static Dictionary<string, AudioClip> Catalog(Component owner)
        {
            var clips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
            foreach (var component in owner.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!component || (component.GetType().Namespace ?? "").StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                var path = EntityTracker.RelativePath(owner.transform, component.transform) + "|" + component.GetType().FullName;
                Fields(component, path, clips, 0);
            }
            foreach (var source in owner.GetComponentsInChildren<AudioSource>(true))
                if (source.clip) clips[EntityTracker.RelativePath(owner.transform, source.transform) + "|AudioSource|clip"] = source.clip;
            return clips;
        }

        internal static Dictionary<string, AudioClip>? InstalledCatalog(string key)
        {
            if (Catalogs.TryGetValue(key, out var clips)) return clips;
            var prefab = Prefab(key);
            if (!prefab) return null;
            clips = Catalog(prefab!);
            if (Catalogs.Count < 1024) Catalogs[key] = clips;
            return clips;
        }

        private static void Fields(object obj, string path, Dictionary<string, AudioClip> clips, int depth)
        {
            for (var type = obj.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(ScriptableObject); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic) continue;
                    var key = path + "|" + field.Name;
                    if (field.FieldType == typeof(AudioClip))
                    { if (field.GetValue(obj) is AudioClip clip && clip) clips[key] = clip; }
                    else if (field.FieldType == typeof(AudioClip[]) || field.FieldType == typeof(List<AudioClip>))
                    {
                        if (!(field.GetValue(obj) is IEnumerable values)) continue;
                        var index = 0;
                        foreach (var value in values) { if (value is AudioClip clip && clip) clips[key + "|" + index] = clip; if (++index >= 1024) break; }
                    }
                    else if (depth == 0 && typeof(ScriptableObject).IsAssignableFrom(field.FieldType) && field.GetValue(obj) is ScriptableObject asset && asset)
                        Fields(asset, key, clips, depth + 1);
                    else if (depth < 2 && field.FieldType.IsArray && field.FieldType.GetElementType()?.Name == "EnemyBehaviourState" && field.GetValue(obj) is IEnumerable states)
                    {
                        var index = 0;
                        foreach (var state in states) { if (state != null) Fields(state, key + "|" + index, clips, depth + 1); if (++index >= 128) break; }
                    }
                }
        }

        internal static AudioClip? Resolve(string prefabKey, string identity, string name, out Component? prefab)
        {
            prefab = prefabKey.Length == 0 ? null : Prefab(prefabKey);
            if (!prefab && prefabKey.StartsWith("enemy:", StringComparison.Ordinal)) return null;
            if (prefab)
            {
                var clips = InstalledCatalog(prefabKey)!;
                if (identity.Length != 0 && clips.TryGetValue(identity, out var exact) && exact && exact.name == name) return exact;
                var matching = clips.Values.Where(clip => clip && clip.name == name).Distinct().Take(2).ToArray();
                if (matching.Length == 1) return matching[0];
                // Never substitute another enemy's identically named clip.
                if (matching.Length > 1 || prefabKey.StartsWith("enemy:", StringComparison.Ordinal)) return null;
            }
            var known = UniqueClips.TryGetValue(name, out var reference);
            AudioClip? unique = null;
            reference?.TryGetTarget(out unique);
            if ((!known || reference != null && !unique) && Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 2f;
                UniqueClips.Clear();
                foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                {
                    if (!clip || clip.name.StartsWith("LCReplay", StringComparison.Ordinal) || clip.name.StartsWith("LC Replay", StringComparison.Ordinal) ||
                        clip.name.StartsWith("Dissonance", StringComparison.OrdinalIgnoreCase)) continue;
                    if (UniqueClips.TryGetValue(clip.name, out var prior))
                    { if (prior == null || !prior.TryGetTarget(out var previous) || previous != clip) UniqueClips[clip.name] = null; }
                    else UniqueClips[clip.name] = new WeakReference<AudioClip>(clip);
                }
                unique = null;
                if (UniqueClips.TryGetValue(name, out reference)) reference?.TryGetTarget(out unique);
            }
            return unique && unique!.name == name ? unique : null;
        }

        internal static AudioSource? Source(Component? prefab, string path, int index)
        {
            if (!prefab) return null;
            var key = prefab!.GetInstanceID() + "\n" + index + "\n" + path;
            if (Sources.TryGetValue(key, out var cached) && cached) return cached;
            var transform = prefab!.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(value => EntityTracker.RelativePath(prefab.transform, value) == path);
            var source = transform ? transform!.GetComponents<AudioSource>().ElementAtOrDefault(Math.Max(0, index)) : null;
            if (!source)
            {
                // Hazard audio can be a sibling of its controller. Its live
                // path includes dungeon ancestors absent from the prefab.
                var leaf = path.Substring(path.LastIndexOf('/') + 1);
                var references = prefab.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field => field.FieldType == typeof(AudioSource)).Select(field => field.GetValue(prefab) as AudioSource)
                    .Where(audio => audio && EntityTracker.RelativePath(prefab.transform, audio!.transform).Split('/').Last() == leaf)
                    .Distinct().Take(2).ToArray();
                if (references.Length == 1) source = references[0];
            }
            if (source && Sources.Count < 4096) Sources[key] = source;
            return source;
        }
    }
}
