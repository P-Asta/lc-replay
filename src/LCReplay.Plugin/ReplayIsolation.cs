using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin
{
    // Keep render-only replay scenes out of the live recorder when playback is
    // opened from an active multiplayer session.
    internal static class ReplayIsolation
    {
        private static readonly HashSet<int> scenes = new HashSet<int>();
        internal static bool PlaybackActive { get; set; }
        static ReplayIsolation() { SceneManager.sceneUnloaded += scene => scenes.Remove(scene.handle); }
        internal static void Register(Scene scene) { if (scene.IsValid()) scenes.Add(scene.handle); }
        // UnloadSceneAsync can take multiple frames. Keep the handle isolated until
        // Unity confirms the scene is actually gone.
        internal static void Unregister(Scene scene) { if (scene.IsValid() && !scene.isLoaded) scenes.Remove(scene.handle); }
        internal static bool IsReplayScene(Scene scene) => scene.IsValid() && scenes.Contains(scene.handle);
    }
}
