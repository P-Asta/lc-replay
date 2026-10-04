using System;
using System.Collections.Generic;
using System.Reflection;
using LCReplay.Plugin.Capture;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin
{
    // Keep render-only replay scenes out of the live recorder when playback is
    // opened from an active multiplayer session.
    internal static class ReplayIsolation
    {
        private static readonly HashSet<int> scenes = new HashSet<int>();
        private static object? networkSceneManager;
        private static Func<Scene, bool>? networkFilter;
        internal static bool PlaybackActive { get; set; }
        static ReplayIsolation() { SceneManager.sceneUnloaded += scene => scenes.Remove(scene.handle); }
        internal static void Register(Scene scene) { if (scene.IsValid()) scenes.Add(scene.handle); }
        // UnloadSceneAsync can take multiple frames. Keep the handle isolated until
        // Unity confirms the scene is actually gone.
        internal static void Unregister(Scene scene) { if (scene.IsValid() && !scene.isLoaded) scenes.Remove(scene.handle); }
        // A private asset scene suppresses global scene notifications; remove
        // its remembered handle after confirmed unload, even if Unity has
        // already invalidated the Scene value.
        internal static void Unloaded(Scene scene) => scenes.Remove(scene.handle);
        internal static bool IsReplayScene(Scene scene) => scene.IsValid() && scenes.Contains(scene.handle);

        // Netcode synchronizes every loaded scene when a new client joins. A
        // replay's dynamic scene has no build path, and an installed moon scene
        // would appear as an extra gameplay scene. Keep both out of that list.
        internal static bool EnsureNetworkExclusion()
        {
            var manager = GameAccess.Singleton("Unity.Netcode.NetworkManager");
            var sceneManager = GameAccess.Read(manager, "SceneManager");
            if (sceneManager == null) return false;
            var field = sceneManager.GetType().GetField("ExcludeSceneFromSychronization",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.FieldType != typeof(Func<Scene, bool>)) return false;
            try
            {
                var current = field.GetValue(sceneManager) as Func<Scene, bool>;
                if (ReferenceEquals(sceneManager, networkSceneManager) && ReferenceEquals(current, networkFilter))
                    return true;
                // Preserve any filter installed by the game or another mod. If
                // one replaced our wrapper, wrap the new value on the next tick.
                Func<Scene, bool> wrapped = scene => !IsReplayScene(scene) && (current?.Invoke(scene) ?? true);
                field.SetValue(sceneManager, wrapped);
                networkSceneManager = sceneManager;
                networkFilter = wrapped;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
