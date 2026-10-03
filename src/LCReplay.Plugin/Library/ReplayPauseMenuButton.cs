using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LCReplay.Plugin.Capture;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Library
{
    internal sealed class ReplayPauseMenuButton : IDisposable
    {
        private readonly Action _open;
        private readonly Action<string> _log;
        private readonly Dictionary<RectTransform, Vector3> _offsets = new Dictionary<RectTransform, Vector3>();
        private Component? _manager;
        private GameObject? _panel, _clone;
        private Button? _button;
        private float _nextCheck;

        internal ReplayPauseMenuButton(Action open, Action<string> log)
        { _open = open; _log = log; }

        internal void Tick(bool allowed)
        {
            if (_button) _button!.interactable = allowed;
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + .5f;
            var manager = GameAccess.Find("QuickMenuManager").FirstOrDefault();
            var panel = GameAccess.Read(manager, "mainButtonsPanel") as GameObject;
            if (panel != _panel || !manager) { Restore(); _manager = manager; _panel = panel; }
            if (!allowed || panel == null || _clone) return;
            try { Attach(panel); }
            catch (Exception ex) { Restore(); _log("Pause menu Replay button: " + ex.GetBaseException().Message); _nextCheck += 2f; }
        }

        private void Attach(GameObject panel)
        {
            var buttons = panel.GetComponentsInChildren<Button>(true)
                .Where(button => button && button.GetComponentInChildren<TMP_Text>(true)).ToArray();
            var settings = buttons.FirstOrDefault(button => Label(button).Equals("Settings", StringComparison.OrdinalIgnoreCase));
            if (!settings || !(settings!.transform is RectTransform settingsRect)) return;
            var parent = settings.transform.parent;
            if (!parent || parent.Find("LCReplayPauseButton")) return;
            var layout = parent.GetComponent<LayoutGroup>();
            var rows = buttons.Where(button => button.gameObject.activeSelf && button.transform.parent == parent && button.transform is RectTransform)
                .OrderByDescending(button => ((RectTransform)button.transform).localPosition.y).ToArray();
            var index = Array.IndexOf(rows, settings);
            if (index < 0) return;
            var step = rows.Zip(rows.Skip(1), (first, next) =>
                first.transform.localPosition.y - next.transform.localPosition.y)
                .Where(gap => gap > 1f).DefaultIfEmpty(settingsRect.rect.height + 12f).Min();
            if (step <= 1f) step = settingsRect.rect.height + 12f;
            var staging = new GameObject("LCReplayPauseStaging");
            staging.SetActive(false);
            try
            {
                _clone = Object.Instantiate(settings.gameObject, staging.transform, false);
                _clone.name = "LCReplayPauseButton";
                _clone.SetActive(false);
                _button = _clone.GetComponent<Button>() ?? _clone.GetComponentInChildren<Button>(true);
                if (!_button) throw new InvalidOperationException("Cloned Settings button has no Button component.");
                _button.onClick = new Button.ButtonClickedEvent();
                _button.onClick.AddListener(Open);
                _button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
                var caption = _clone.GetComponentInChildren<TMP_Text>(true);
                if (!caption) throw new InvalidOperationException("Cloned Settings button has no caption.");
                caption.text = caption.text.Replace("Settings", "Replay");
                _clone.transform.SetParent(parent, false);
                _clone.transform.SetSiblingIndex(settings.transform.GetSiblingIndex() + 1);
                if (!layout || !layout.enabled)
                {
                    var cloneRect = (RectTransform)_clone.transform;
                    cloneRect.localPosition = settingsRect.localPosition;
                    // Grow upwards, leaving lower rows (including other mods' buttons and Quit)
                    // in place. LethalConfig uses this same direction when adding its menu row.
                    var offset = new Vector3(0, step, 0);
                    for (var row = 0; row <= index; row++)
                    {
                        var rect = (RectTransform)rows[row].transform;
                        _offsets[rect] = offset;
                        rect.localPosition += offset;
                    }
                }
                _clone.SetActive(true);
            }
            finally { Object.Destroy(staging); }
        }

        private static string Label(Button button) => (button.GetComponentInChildren<TMP_Text>(true)?.text ?? "")
            .Trim().TrimStart('>', ' ');

        private void Open()
        {
            if (!_button || !_button!.interactable || !_manager || !GameAccess.Bool(_manager, "isMenuOpen")) return;
            try
            {
                _manager!.GetType().GetMethod("CloseQuickMenu", BindingFlags.Instance | BindingFlags.Public)?.Invoke(_manager, null);
                _open();
            }
            catch (Exception ex) { _log("Cannot open Replay from pause menu: " + ex.GetBaseException().Message); }
        }

        private void Restore()
        {
            if (_clone) { _clone!.SetActive(false); Object.Destroy(_clone); }
            _clone = null; _button = null;
            // Undo only our offset so a later menu injection keeps its own positioning.
            foreach (var entry in _offsets) if (entry.Key) entry.Key.localPosition -= entry.Value;
            _offsets.Clear();
        }

        public void Dispose() { Restore(); _manager = null; _panel = null; }
    }
}
