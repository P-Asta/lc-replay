using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Library
{
    // Reuse the game's own font and hover animation without a dependency on its UI assemblies.
    internal sealed class ReplayMenuButton : IDisposable
    {
        private const string ButtonName = "LCReplayMenuButton";
        private static readonly HashSet<string> SafeComponents = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine.RectTransform", "UnityEngine.Transform", "UnityEngine.CanvasRenderer",
            "UnityEngine.Animator", "UnityEngine.UI.Button", "UnityEngine.UI.Image",
            // Menu mods may add this passive size hint to the vanilla Settings row.
            "UnityEngine.UI.LayoutElement",
            "TMPro.TextMeshProUGUI", "TMPro.TMP_SubMeshUI"
        };
        private readonly Action open;
        private readonly Action<string> log;
        private readonly Action<string> info;
        private readonly Harmony harmony = new Harmony(ReplayPlugin.Guid + ".menu");
        private readonly List<Row> changed = new List<Row>();
        private static ReplayMenuButton? instance;
        private GameObject? menuRoot, clone;
        private Component? replayButton;
        private Type? buttonType, textType;
        private bool allowed = true, disposed, rootSearchReported;
        private float nextCheck;
        private int deferUntilFrame;
        private string lastFailure = "";

        public ReplayMenuButton(Action open, Action<string> log, Action<string>? info = null)
        {
            this.open = open;
            this.log = log;
            this.info = info ?? log;
            instance = this;
            SceneManager.sceneLoaded += SceneLoaded;
            try
            {
                var method = AccessTools.Method(GameAccess.Type("MenuManager"), "Start");
                if (method == null) throw new MissingMethodException("MenuManager.Start");
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(ReplayMenuButton), nameof(MenuStarted)));
                this.info("Replay menu hook ready: MenuManager.Start.");
            }
            catch (Exception ex) { log("Menu start hook unavailable; scene search remains active: " + ex.GetBaseException().Message); }
        }

        private static void MenuStarted(MonoBehaviour __instance)
        {
            var owner = instance;
            if (owner == null || owner.disposed) return;
            try
            {
                owner.info("Replay observed menu start: " + __instance.gameObject.scene.name + ".");
                owner.deferUntilFrame = Time.frameCount + 1;
                // Wait until the other menu patches have finished, then locate the actual
                // MenuContainer/MainButtons hierarchy rather than a manager's active state.
                __instance.StartCoroutine(owner.AttachAfterStart());
            }
            catch (Exception ex) { owner.ReportFailure(ex); }
        }

        private IEnumerator AttachAfterStart()
        {
            yield return null;
            if (!disposed) TryAttach();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            deferUntilFrame = Time.frameCount + 1;
            nextCheck = 0;
            lastFailure = "";
            rootSearchReported = false;
        }

        public void Tick(bool allowed)
        {
            if (disposed) return;
            this.allowed = allowed;
            try
            {
                if (replayButton) Set(replayButton!, "interactable", allowed);
                if (!allowed || Time.realtimeSinceStartup < nextCheck || Time.frameCount < deferUntilFrame) return;
                nextCheck = Time.realtimeSinceStartup + .5f;
                TryAttach();
            }
            catch (Exception ex) { ReportFailure(ex); }
        }

        private void TryAttach()
        {
            try
            {
                var root = FindMainButtons();
                if (!root)
                {
                    if (!rootSearchReported) info("Replay menu search: MenuContainer/MainButtons is not present in the loaded scenes yet.");
                    rootSearchReported = true;
                    return;
                }
                if (root != menuRoot || (!clone && changed.Count != 0)) Restore();
                menuRoot = root;
                if (clone) return;
                Install();
                lastFailure = "";
                info("Replay button attached below Settings: " + menuRoot!.scene.name + "/MenuContainer/MainButtons.");
            }
            catch (Exception ex) { ReportFailure(ex); }
        }

        private static GameObject? FindMainButtons()
        {
            var container = GameObject.Find("MenuContainer");
            var buttons = container ? container.transform.Find("MainButtons") : null;
            if (buttons && buttons!.Find("SettingsButton") && buttons.Find("QuitButton")) return buttons.gameObject;
            // Notifications and submenus can temporarily disable the main container.
            // Loaded scene roots include inactive children and exclude prefab assets.
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
                        if (candidate.name == "MainButtons" && candidate.parent && candidate.parent.name == "MenuContainer"
                            && candidate.Find("SettingsButton") && candidate.Find("QuitButton")) return candidate.gameObject;
            }
            return null;
        }

        private void ReportFailure(Exception ex)
        {
            Restore();
            nextCheck = Time.realtimeSinceStartup + 2f;
            var message = ex.GetBaseException().Message;
            if (message == lastFailure) return;
            lastFailure = message;
            log("Replay menu attachment failed; will retry. F9 opens the archive. " + message);
        }

        private void Install()
        {
            buttonType = GameAccess.Type("UnityEngine.UI.Button") ?? throw new InvalidOperationException("Unity UI Button was not found.");
            textType = GameAccess.Type("TMPro.TMP_Text") ?? throw new InvalidOperationException("TextMeshPro was not found.");
            var candidates = menuRoot!.GetComponentsInChildren(buttonType, true).OfType<Component>().ToArray();
            var settings = candidates.FirstOrDefault(c => string.Equals(c.name, "SettingsButton", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(c => c.gameObject.GetComponentsInChildren(textType, true).OfType<Component>()
                    .Any(t => string.Equals((GameAccess.Read(t, "text") as string ?? "").Trim(' ', '>'), "Settings", StringComparison.OrdinalIgnoreCase)));
            if (!settings || !(settings!.transform is RectTransform) || !settings.gameObject.activeSelf)
                throw new InvalidOperationException("The main Settings button was not found.");
            ValidateTemplate(settings.gameObject);
            var parent = settings.transform.parent;
            if (!parent || parent.Find(ButtonName)) throw new InvalidOperationException("A replay menu button already exists.");
            var layoutGroup = GameAccess.Type("UnityEngine.UI.LayoutGroup");
            if (layoutGroup != null && parent.GetComponents(layoutGroup).OfType<Behaviour>().Any(b => b && b.enabled))
                throw new InvalidOperationException("The menu layout is controlled by another component.");
            // Only visible sibling button rows belong to this menu. Inactive LAN/online alternatives
            // and the offscreen JoinCode panel must not affect the occupied menu area.
            var rows = new List<Row>();
            foreach (Transform child in parent)
            {
                if (!(child is RectTransform rect) || !child.gameObject.activeSelf) continue;
                var button = child.GetComponent(buttonType) as Component;
                if (!button) button = child.GetComponentInChildren(buttonType) as Component;
                if (!button) continue;
                rows.Add(new Row(rect, button!));
            }
            rows.Sort((a, b) => b.Position.y.CompareTo(a.Position.y));
            var settingsIndex = rows.FindIndex(r => r.Button == settings);
            if (settingsIndex < 0 || settingsIndex == rows.Count - 1 || rows.Count < 3)
                throw new InvalidOperationException("The Settings menu row cannot be positioned safely.");
            var step = rows.Zip(rows.Skip(1), (a, b) => a.Position.y - b.Position.y).Where(gap => gap > 1f).DefaultIfEmpty(0f).Min();
            var maxHeight = rows.Max(r => r.Rect.rect.height * Mathf.Abs(r.Rect.localScale.y));
            if (step < maxHeight || step <= 0)
                throw new InvalidOperationException("There is no free vertical space for another menu row.");

            // Instantiate while inactive: copied listeners never receive input before replacement.
            var staging = new GameObject("LCReplayMenuStaging");
            staging.SetActive(false);
            try
            {
                clone = Object.Instantiate(settings.gameObject, staging.transform, false);
                clone.name = ButtonName;
                clone.SetActive(false);
                replayButton = clone.GetComponent(buttonType) as Component;
                if (!replayButton) throw new InvalidOperationException("Cloned menu button is missing.");
                var onClick = Property(replayButton!, "onClick");
                var clickEvent = Activator.CreateInstance(onClick.PropertyType)!;
                var addListener = clickEvent.GetType().GetMethod("AddListener")!;
                var callback = Delegate.CreateDelegate(addListener.GetParameters()[0].ParameterType, this,
                    GetType().GetMethod(nameof(Open), BindingFlags.Instance | BindingFlags.NonPublic)!);
                addListener.Invoke(clickEvent, new object[] { callback });
                onClick.SetValue(replayButton, clickEvent, null);
                // Spatial navigation includes buttons injected later by other mods.
                var navigation = GameAccess.Read(replayButton, "navigation") ?? throw new InvalidOperationException("Button navigation is unavailable.");
                var mode = Property(navigation, "mode");
                mode.SetValue(navigation, Enum.Parse(mode.PropertyType, "Automatic"), null);
                Set(replayButton!, "navigation", navigation);
                var labels = clone.GetComponentsInChildren(textType, true).OfType<Component>().ToArray();
                if (labels.Length != 1) throw new InvalidOperationException("Settings has an unsupported label layout.");
                Set(labels[0], "text", "> Replay");
                // The Settings clone keeps the game's font, colors and hover animation.
                clone.transform.SetParent(parent, false);
                clone.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
                ((RectTransform)clone.transform).localPosition = rows[settingsIndex].Position;
                // Match LethalConfig's upward expansion without compressing/repositioning
                // its row or Quit. Both injection orders leave a full gap for each button.
                var offset = new Vector3(0, step, 0);
                for (var i = 0; i <= settingsIndex; i++)
                {
                    var row = rows[i];
                    row.Offset = offset;
                    changed.Add(row);
                    row.Rect.localPosition += offset;
                }
                Set(replayButton!, "interactable", allowed);
                clone.SetActive(true);
            }
            finally { Object.Destroy(staging); }
        }

        private static void ValidateTemplate(GameObject template)
        {
            foreach (var component in template.GetComponentsInChildren<Component>(true))
            {
                if (!component || !SafeComponents.Contains(component!.GetType().FullName ?? ""))
                    throw new InvalidOperationException("Settings contains an unsupported component: " + (component ? component!.GetType().FullName : "missing script"));
                if (!(component is Animator animator)) continue;
                if (animator.GetBehaviours<StateMachineBehaviour>().Length != 0 ||
                    (animator.runtimeAnimatorController && animator.runtimeAnimatorController.animationClips.Any(c => c && c.events.Length != 0)))
                    throw new InvalidOperationException("Settings contains custom animation callbacks.");
            }
        }

        private void Open()
        {
            if (!allowed || disposed || !menuRoot || !menuRoot!.activeInHierarchy || GameAccess.Connected) return;
            try { info("Replay button clicked: opening archive."); open(); }
            catch (Exception ex) { log("Could not open the replay archive: " + ex.GetBaseException().Message); }
        }

        private void Restore()
        {
            if (clone)
            {
                clone!.SetActive(false);
                clone.transform.SetParent(null, false);
                Object.Destroy(clone);
            }
            clone = null;
            replayButton = null;
            foreach (var row in changed)
            {
                if (row.Rect) row.Rect.localPosition -= row.Offset;
            }
            changed.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(instance, this)) instance = null;
            SceneManager.sceneLoaded -= SceneLoaded;
            try { harmony.UnpatchSelf(); }
            catch (Exception ex) { log("Replay menu hook cleanup failed: " + ex.Message); }
            Restore();
            menuRoot = null;
        }

        private static PropertyInfo Property(object value, string name) => value.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingMemberException(value.GetType().Name, name);
        private static void Set(object value, string name, object data) => Property(value, name).SetValue(value, data, null);

        private sealed class Row
        {
            public readonly RectTransform Rect;
            public readonly Component Button;
            public readonly Vector3 Position;
            public Vector3 Offset;
            public Row(RectTransform rect, Component button)
            {
                Rect = rect;
                Button = button;
                Position = rect.localPosition;
            }
        }
    }
}
