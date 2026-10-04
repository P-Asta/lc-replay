using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    // The player prefab is a serialized reference in the round scene, not a
    // Resources asset. A fresh main menu has not loaded it yet. Activate one
    // isolated copy only after its script callbacks and scene notifications are
    // guarded, retain the prefab assets, then unload the inert scene again.
    internal static class NativePlayerAssetLoader
    {
        private const string AssetSceneName = "SampleSceneRelay";
        private const string PatchId = "io.lcreplay.player-asset-loader";
        private static readonly HashSet<string> LifecycleNames = new HashSet<string>(StringComparer.Ordinal)
            { "Awake", "OnEnable", "Start", "OnDisable", "OnDestroy" };
        private static Load? current;
        internal static bool HasPendingLoads => current != null && !current.Completed;
        internal static bool RequiresIsolation => current?.Started == true;
        internal static void Tick() => current?.Tick();
        private static bool HasLiveRound => GameAccess.Singleton("StartOfRound") is Component round && round;

        internal static IEnumerable<float> Prepare()
        {
            if (PrefabAssetRegistry.ResolvePrefab("player")) yield break;
            var load = current;
            var owner = load == null;
            if (load == null)
            {
                // Never load a second round beside an active/connecting game.
                var network = GameAccess.Singleton("Unity.Netcode.NetworkManager");
                if (GameAccess.Bool(network, "IsListening") || HasLiveRound)
                    yield break;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).name == AssetSceneName) yield break;
                var buildIndex = -1;
                var scenePath = "";
                for (var i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                {
                    var path = SceneUtility.GetScenePathByBuildIndex(i);
                    if (!string.Equals(Path.GetFileNameWithoutExtension(path), AssetSceneName, StringComparison.Ordinal)) continue;
                    if (buildIndex >= 0) throw new InvalidOperationException("The installed player asset scene is ambiguous.");
                    buildIndex = i; scenePath = path;
                }
                if (buildIndex < 0) throw new InvalidOperationException("The installed player asset scene is unavailable.");
                current = load = new Load(buildIndex, scenePath);
            }
            try
            {
                if (owner)
                {
                    foreach (var progress in load.InstallGuards()) yield return progress;
                    // Hosting may have started while the setup yielded. Fail
                    // before activation rather than borrowing a live round.
                    if (GameAccess.Bool(GameAccess.Singleton("Unity.Netcode.NetworkManager"), "IsListening") ||
                        HasLiveRound)
                        throw new InvalidOperationException("Player assets cannot be prepared while a game is starting.");
                    load.Start();
                }
                while (!load.Completed) yield return .25f + .7f * load.Progress;
                if (load.Failure != null) throw new InvalidOperationException("Could not prepare the installed player prefab safely.", load.Failure);
                if (!PrefabAssetRegistry.ResolvePrefab("player"))
                    throw new InvalidOperationException("The installed round scene did not provide a player prefab.");
                yield return 1f;
            }
            finally
            {
                // Unity cannot cancel a scene load. Its completion callbacks
                // keep the guards alive and unload it even if this iterator is
                // disposed when the viewer closes or switches recording.
                if (owner && !load.Completed) load.Abandon();
            }
        }

        private static bool LifecyclePrefix(MonoBehaviour __instance)
        {
            var load = current;
            if (load == null || !__instance || !load.Owns(__instance.gameObject)) return true;
            load.RememberRoot(__instance.transform.root.gameObject);
            if (__instance.enabled) __instance.enabled = false;
            return false;
        }

        private static bool SceneLoadedPrefix(Scene __0)
        {
            var load = current;
            if (load == null || !load.Owns(__0)) return true;
            load.Loaded(__0);
            return false;
        }

        private static bool SceneUnloadedPrefix(Scene __0)
        {
            var load = current;
            if (load == null || !load.Owns(__0)) return true;
            load.Unloaded();
            return false;
        }

        private sealed class Load
        {
            private readonly int buildIndex;
            private readonly string scenePath;
            private readonly HashSet<int> existingScenes = new HashSet<int>();
            private readonly HashSet<GameObject> roots = new HashSet<GameObject>();
            private readonly Harmony harmony = new Harmony(PatchId);
            private readonly List<MethodBase> guardedMethods = new List<MethodBase>();
            private AsyncOperation? loading, unloading;
            private Scene scene;
            private bool handlingLoaded, cleaned;
            private int cleanupFailures;
            private float nextCleanupAttempt;
            internal bool Started { get; private set; }
            internal bool Completed { get; private set; }
            internal Exception? Failure { get; private set; }
            internal float Progress => unloading != null ? .98f : loading?.progress ?? 0f;

            internal Load(int index, string path)
            {
                buildIndex = index; scenePath = path;
                for (var i = 0; i < SceneManager.sceneCount; i++) existingScenes.Add(SceneManager.GetSceneAt(i).handle);
            }

            internal bool Owns(Scene candidate) => Started && !existingScenes.Contains(candidate.handle) &&
                // Unity may already invalidate the scene before publishing its
                // unloaded notification. Its remembered handle still identifies
                // the private scene, so that event must remain suppressed.
                // Once known, never match another round by path: a user could
                // start a real game while a failed cleanup is quarantined.
                (handlingLoaded ? candidate.handle == scene.handle : candidate.IsValid() &&
                    candidate.path == scenePath && candidate.name == AssetSceneName);

            internal bool Owns(GameObject obj) => Owns(obj.scene) || roots.Contains(obj.transform.root.gameObject);
            internal void RememberRoot(GameObject root) { if (root) roots.Add(root); }

            internal IEnumerable<float> InstallGuards()
            {
                // Suppress publication of this private scene to global game,
                // Netcode and mod scene subscribers. Do not replace event lists:
                // subscriptions added during loading must remain intact.
                Guard(AccessTools.Method(typeof(SceneManager), "Internal_SceneLoaded"), nameof(SceneLoadedPrefix));
                Guard(AccessTools.Method(typeof(SceneManager), "Internal_SceneUnloaded"), nameof(SceneUnloadedPrefix));
                var callbacks = new HashSet<MethodInfo>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException error) { types = error.Types.Where(type => type != null).Cast<Type>().ToArray(); }
                    foreach (var type in types)
                    {
                        try
                        {
                        if (type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                        // BepInEx plugin hosts already exist outside the private
                        // asset scene. Rewriting their bootstrap callbacks can
                        // force optional compatibility assemblies to resolve
                        // (e.g. MrovLib's LLL integration) although unused.
                        // Ordinary scene behaviours still require all guards.
                        if (typeof(BepInEx.BaseUnityPlugin).IsAssignableFrom(type)) continue;
                        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (LifecycleNames.Contains(method.Name) && !method.IsAbstract && !method.ContainsGenericParameters &&
                                method.GetParameters().Length == 0 && method.GetMethodBody() != null) callbacks.Add(method);
                        }
                        catch (TypeLoadException) when (assembly.GetName().Name != "Assembly-CSharp")
                        {
                            // Optional integration helper types can be listed by
                            // GetTypes yet fail when Mono resolves their fields.
                            // Such a type cannot be instantiated as a scene script.
                            Debug.LogWarning("LC Replay skipped unavailable optional type: " + type.FullName);
                        }
                        catch (FileNotFoundException) when (assembly.GetName().Name != "Assembly-CSharp")
                        { Debug.LogWarning("LC Replay skipped optional type with a missing dependency: " + type.FullName); }
                    }
                }
                var installed = 0;
                foreach (var method in callbacks)
                {
                    Guard(method, nameof(LifecyclePrefix));
                    if (++installed % 16 == 0) yield return .25f * installed / Math.Max(1, callbacks.Count);
                }
            }

            private void Guard(MethodInfo? method, string prefix)
            {
                if (method == null || method.GetMethodBody() == null)
                    throw new InvalidOperationException("A player asset isolation callback is unavailable: " + prefix);
                guardedMethods.Add(method);
                try { harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativePlayerAssetLoader), prefix) { priority = Priority.First }); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Cannot isolate player assets at " + method.DeclaringType?.FullName + "." +
                        method.Name + ": " + error.GetBaseException().Message, error);
                }
            }

            internal void Start()
            {
                Started = true;
                try
                {
                    loading = SceneManager.LoadSceneAsync(buildIndex, LoadSceneMode.Additive)
                        ?? throw new InvalidOperationException("The installed round scene could not be loaded.");
                    // Do not hold allowSceneActivation at .9: that also stalls
                    // subsequent Unity scene unloads if the viewer is closed.
                    loading.allowSceneActivation = true;
                    loading.completed += _ =>
                    {
                        if (handlingLoaded || Completed) return;
                        for (var i = 0; i < SceneManager.sceneCount; i++)
                        {
                            var candidate = SceneManager.GetSceneAt(i);
                            if (Owns(candidate)) { Loaded(candidate); return; }
                        }
                        Failure = new InvalidOperationException("The player asset scene completed without an isolated scene.");
                        Finish();
                    };
                }
                catch (Exception error) { Failure = error; Finish(); throw; }
            }

            internal void Loaded(Scene loaded)
            {
                if (handlingLoaded) return;
                handlingLoaded = true;
                scene = loaded;
                try
                {
                    ReplayIsolation.Register(scene);
                    foreach (var root in scene.GetRootGameObjects()) roots.Add(root);
                    // Activation cannot be canceled. Retain its already loaded
                    // serialized assets even when the initiating viewer closed;
                    // another caller may be waiting on the same operation.
                    CacheAssets();
                }
                catch (Exception error) { Failure = error; }
                finally
                {
                    try { DeactivateRoots(); }
                    finally { Unload(); }
                }
            }

            private void DeactivateRoots()
            {
                foreach (var root in roots.ToArray())
                {
                    if (!root) continue;
                    try
                    {
                        foreach (var component in root.GetComponentsInChildren<Component>(true))
                        {
                            if (!component) continue;
                            if (component is Renderer renderer) { renderer.forceRenderingOff = true; renderer.enabled = false; }
                            else if (component is Behaviour behaviour) behaviour.enabled = false;
                            else if (component is Collider collider) collider.enabled = false;
                            else if (component is Rigidbody body) { body.isKinematic = true; body.detectCollisions = false; }
                        }
                    }
                    catch (Exception error) { Failure ??= error; }
                    finally
                    {
                        // A mod's postfix may have moved a guarded root into
                        // DontDestroyOnLoad even though its own Awake was skipped.
                        try { if (root) { root.SetActive(false); if (root.scene != scene) Object.DestroyImmediate(root); } }
                        catch (Exception error) { Failure ??= error; }
                    }
                }
            }

            private void CacheAssets()
            {
                var roundType = GameAccess.Type("StartOfRound");
                var playerType = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
                if (roundType == null || playerType == null) throw new InvalidOperationException("Installed player types are unavailable.");
                foreach (var root in roots)
                {
                    if (!root) continue;
                    foreach (var round in root.GetComponentsInChildren(roundType, true))
                    {
                        ReplayParticleAssets.CacheShipMagnet(round);
                        var prefab = GameAccess.Read(round, "playerPrefab") as GameObject;
                        if (!prefab || prefab!.scene.IsValid()) continue;
                        var player = prefab.GetComponentInChildren(playerType, true);
                        if (!player) continue;
                        var local = GameAccess.Read(round, "localClientAnimatorController") as RuntimeAnimatorController;
                        var remote = GameAccess.Read(round, "otherClientsAnimatorController") as RuntimeAnimatorController;
                        if (PrefabAssetRegistry.CachePlayerAssets(player, local, remote)) return;
                    }
                }
                throw new InvalidOperationException("The isolated round scene did not reference a native player prefab asset.");
            }

            private void Unload()
            {
                if (unloading != null || cleaned) return;
                if (!scene.IsValid() || !scene.isLoaded) { Unloaded(); return; }
                try
                {
                    unloading = SceneManager.UnloadSceneAsync(scene)
                        ?? throw new InvalidOperationException("The isolated player asset scene could not be unloaded.");
                    unloading.completed += _ => Unloaded();
                }
                catch (Exception error)
                {
                    nextCleanupAttempt = Time.realtimeSinceStartup + .5f;
                    if (++cleanupFailures == 3)
                    {
                        Failure ??= error;
                        // Keep the inactive scene isolated and retry through the
                        // runtime tick. Stop holding the loading UI indefinitely.
                        Completed = true;
                        Debug.LogError("LC Replay player asset cleanup failed; retrying while isolated: " + error.Message);
                    }
                }
            }

            internal void Tick()
            {
                if (handlingLoaded && !cleaned && unloading == null &&
                    (loading == null || loading.isDone) && Time.realtimeSinceStartup >= nextCleanupAttempt)
                    Unload();
            }

            internal void Abandon()
            {
                if (!Started) Finish();
                else if (handlingLoaded) Unload();
            }

            internal void Unloaded()
            {
                ReplayIsolation.Unloaded(scene);
                Finish();
            }

            private void Finish()
            {
                if (cleaned) return;
                cleaned = true;
                Completed = true;
                ReplayIsolation.Unregister(scene);
                foreach (var method in guardedMethods)
                    try { harmony.Unpatch(method, HarmonyPatchType.Prefix, PatchId); }
                    catch (Exception error) { Debug.LogError("LC Replay player asset guard cleanup failed: " + error.Message); }
                guardedMethods.Clear(); roots.Clear();
                if (ReferenceEquals(current, this)) current = null;
            }
        }
    }
}
