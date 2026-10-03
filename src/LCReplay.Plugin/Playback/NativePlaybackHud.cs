using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LCReplay.Plugin.Playback
{
    /// <summary>Small native playback controls; recorded diagnostic data stays in an optional panel.</summary>
    internal sealed class NativePlaybackHud : IDisposable
    {
        private readonly NativeReplayUi _ui;
        private readonly TextMeshProUGUI _title, _clock, _pauseLabel, _speedLabel, _cameraLabel, _detailsText, _visualLabel, _fogLabel;
        private readonly TextMeshProUGUI _resolutionValue, _gammaValue, _cullingLabel, _noShadowLabel,
            _cinematicLabel, _cameraSpeedValue;
        private readonly Slider _timeline;
        private readonly RectTransform _details, _players, _settings, _cameraChoices;
        private readonly RectTransform _deathBookmark;
        private readonly Action<double> _seek;
        private readonly List<RectTransform> _bookmarks = new List<RectTransform>();
        private RectTransform _loadingOverlay = null!, _loadingFill = null!;
        private TextMeshProUGUI _loadingText = null!;
        private readonly Slider _resolutionSlider, _gammaSlider, _cameraSpeedSlider;
        private readonly Action<string> _selectCamera;
        private readonly Action<bool> _setDisableInteriorCulling;
        private readonly Action<bool> _setNoShadow;
        private readonly Action<bool> _setCinematicMove;
        private readonly Action<bool> _setFog;
        private bool _disableInteriorCulling;
        private bool _noShadow;
        private bool _cinematicMove;
        private bool _fog;
        private string _playerSignature = "";
        private string _cameraSignature = "";
        private float _nextUpdate;
        private bool _showDetails;
        private bool _showEvents;
        private bool _showSettings;
        public GameObject Root => _ui.Root;

        internal NativePlaybackHud(ReplaySession session, Action togglePause, Action<double> seek, Action cycleSpeed,
            Action<string> selectCamera, Action toggleVisuals, Action close,
            float resolutionScale, float gamma, Action<float> setResolution, Action<float> setGamma,
            bool disableInteriorCulling, Action<bool> setDisableInteriorCulling,
            bool noShadow, Action<bool> setNoShadow,
            bool cinematicMove, Action<bool> setCinematicMove, float cameraSpeed, Action<float> setCameraSpeed,
            bool fog, Action<bool> setFog)
        {
            _seek = seek;
            _selectCamera = selectCamera;
            _disableInteriorCulling = disableInteriorCulling;
            _setDisableInteriorCulling = setDisableInteriorCulling;
            _noShadow = noShadow;
            _setNoShadow = setNoShadow;
            _cinematicMove = cinematicMove;
            _setCinematicMove = setCinematicMove;
            _fog = fog;
            _setFog = setFog;
            _ui = new NativeReplayUi("LCReplay.PlaybackHud", 31900, false);
            try
            {
            var top = _ui.CreatePanel(Root.transform, "Replay dock", NativeReplayUi.HeaderColor);
            NativeReplayUi.SetRect(top.rectTransform, new Vector2(0, 1), Vector2.one,
                new Vector2(28, -78), new Vector2(-28, -24));
            _ui.AddBorder(top.rectTransform, NativeReplayUi.Orange, 2);
            _title = _ui.CreateText(top.transform, "REPLAY", 22, alignment: TextAlignmentOptions.MidlineLeft);
            _title.fontStyle = FontStyles.Bold;
            _title.characterSpacing = 1.5f;
            NativeReplayUi.SetRect(_title.rectTransform, Vector2.zero, Vector2.one, new Vector2(24, 0), new Vector2(-460, 0));
            var settingsButton = _ui.CreateButton(top.transform, "Settings", ToggleSettings);
            NativeReplayUi.SetRect((RectTransform)settingsButton.transform, new Vector2(1, 0), Vector2.one, new Vector2(-438, 7), new Vector2(-294, -7));
            var detailsButton = _ui.CreateButton(top.transform, "Details", ToggleDetails);
            NativeReplayUi.SetRect((RectTransform)detailsButton.transform, new Vector2(1, 0), Vector2.one, new Vector2(-286, 7), new Vector2(-142, -7));
            var closeButton = _ui.CreateButton(top.transform, "X", close);
            NativeReplayUi.SetRect((RectTransform)closeButton.transform, new Vector2(1, 0), Vector2.one, new Vector2(-134, 7), new Vector2(-8, -7));

            var bottom = _ui.CreatePanel(Root.transform, "Playback controls", NativeReplayUi.Black);
            NativeReplayUi.SetRect(bottom.rectTransform, Vector2.zero, new Vector2(1, 0),
                new Vector2(28, 24), new Vector2(-28, 166));
            _ui.AddBorder(bottom.rectTransform, NativeReplayUi.Orange, 2);
            var bottomHeader = _ui.CreatePanel(bottom.transform, "Playback bar header", NativeReplayUi.HeaderColor);
            NativeReplayUi.SetRect(bottomHeader.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(2, -40), new Vector2(-2, -2));
            bottomHeader.raycastTarget = false;
            _clock = _ui.CreateText(bottom.transform, "00:00 / 00:00", 23, alignment: TextAlignmentOptions.MidlineLeft);
            NativeReplayUi.Place(_clock.rectTransform, 24, 9, 300, 32);
            var hint = _ui.CreateText(bottom.transform, "WASD/QE move  |  Wheel speed/follow  |  Click actor  |  B cursor  |  RMB look  |  C smooth  |  L UI", 18, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.SetRect(hint.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(335, -41), new Vector2(-24, -9));
            _timeline = _ui.CreateSlider(bottom.transform, "Replay timeline", 0, (float)Math.Max(0.01, session.Duration), 0, value => seek(value));
            NativeReplayUi.SetRect((RectTransform)_timeline.transform, new Vector2(0, 1), Vector2.one, new Vector2(24, -82), new Vector2(-24, -44));
            _deathBookmark = _ui.CreatePanel(_timeline.transform, "Focused player death", NativeReplayUi.Orange).rectTransform;
            _deathBookmark.GetComponent<Image>().raycastTarget = false;
            _deathBookmark.gameObject.SetActive(false);
            _pauseLabel = Button(bottom.transform, "Pause", 24, 91, 205, togglePause);
            _speedLabel = Button(bottom.transform, "1x", 237, 91, 135, cycleSpeed);
            _cameraLabel = Button(bottom.transform, "Camera: Freecam", 380, 91, 420, ToggleCameraChoices);
            _cameraChoices = _ui.CreateWindow(Root.transform, "Camera modes", "Camera mode", 38);
            NativeReplayUi.SetRect(_cameraChoices, new Vector2(.5f, 0), new Vector2(.5f, 0),
                new Vector2(-355, 174), new Vector2(90, 290));
            _cameraChoices.gameObject.SetActive(false);

            _details = _ui.CreateWindow(Root.transform, "Recording details", "Recording Details", 52);
            NativeReplayUi.SetRect(_details, new Vector2(1, 0), Vector2.one, new Vector2(-566, 208), new Vector2(-28, -94));
            _ui.CreateScroll(_details, "Players", out _players);
            NativeReplayUi.Place((RectTransform)_players.parent.parent, 18, 62, 502, 134);
            Button(_details, "State", 18, 208, 150, () => { _showEvents = false; _nextUpdate = 0; });
            Button(_details, "Events", 176, 208, 150, () => { _showEvents = true; _nextUpdate = 0; });
            _visualLabel = Button(_details, "Labels: off", 334, 208, 186, toggleVisuals);
            _ui.CreateScroll(_details, "Recorded information", out var detailContent);
            NativeReplayUi.SetRect((RectTransform)detailContent.parent.parent, Vector2.zero, Vector2.one, new Vector2(18, 18), new Vector2(-18, -269));
            _detailsText = _ui.CreateText(detailContent, "", 21);
            NativeReplayUi.SetRect(_detailsText.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(0, -1800), Vector2.zero);
            detailContent.sizeDelta = new Vector2(0, 1800);
            _details.gameObject.SetActive(false);

            _settings = _ui.CreateWindow(Root.transform, "Replay settings", "Replay Settings", 52);
            NativeReplayUi.SetRect(_settings, new Vector2(1, 0), Vector2.one, new Vector2(-566, 208), new Vector2(-28, -94));
            var settingsScroll = _ui.CreateScroll(_settings, "Settings controls", out var settingsContent);
            NativeReplayUi.SetRect((RectTransform)settingsScroll.transform, Vector2.zero, Vector2.one,
                new Vector2(0, 14), new Vector2(0, -62));
            settingsContent.sizeDelta = new Vector2(0, 578);
            var resolutionTitle = _ui.CreateText(settingsContent, "Render resolution", 23);
            NativeReplayUi.Place(resolutionTitle.rectTransform, 18, 70, 320, 38);
            _resolutionValue = _ui.CreateText(settingsContent, "", 22, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.Place(_resolutionValue.rectTransform, 330, 70, 188, 38);
            _resolutionSlider = _ui.CreateSlider(settingsContent, "Render resolution", .25f, 1f, resolutionScale, setResolution);
            NativeReplayUi.Place((RectTransform)_resolutionSlider.transform, 18, 108, 500, 42);
            var gammaTitle = _ui.CreateText(settingsContent, "Gamma", 23);
            NativeReplayUi.Place(gammaTitle.rectTransform, 18, 160, 320, 38);
            _gammaValue = _ui.CreateText(settingsContent, "", 22, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.Place(_gammaValue.rectTransform, 330, 160, 188, 38);
            _gammaSlider = _ui.CreateSlider(settingsContent, "Gamma", .5f, 2f, gamma, setGamma);
            NativeReplayUi.Place((RectTransform)_gammaSlider.transform, 18, 198, 500, 42);
            _cullingLabel = Button(settingsContent, "", 18, 258, 500, ToggleInteriorCulling);
            UpdateCullingLabel();
            _noShadowLabel = Button(settingsContent, "", 18, 310, 500, ToggleNoShadow);
            UpdateNoShadowLabel();
            _cinematicLabel = Button(settingsContent, "", 18, 362, 500, ToggleCinematicMove);
            SetCinematicMove(cinematicMove);
            _fogLabel = Button(settingsContent, "", 18, 505, 500, ToggleFog);
            UpdateFogLabel();
            var cameraSpeedTitle = _ui.CreateText(settingsContent, "Camera speed", 23);
            NativeReplayUi.Place(cameraSpeedTitle.rectTransform, 18, 415, 320, 38);
            _cameraSpeedValue = _ui.CreateText(settingsContent, "", 22, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.Place(_cameraSpeedValue.rectTransform, 330, 415, 188, 38);
            _cameraSpeedSlider = _ui.CreateSlider(settingsContent, "Camera speed", 1f, 30f, cameraSpeed, setCameraSpeed);
            NativeReplayUi.Place((RectTransform)_cameraSpeedSlider.transform, 18, 451, 500, 42);
            _settings.gameObject.SetActive(false);
            _loadingOverlay = _ui.CreateWindow(Root.transform, "Replay loading", "Loading Replay", 48);
            NativeReplayUi.SetRect(_loadingOverlay, new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                new Vector2(-390, -105), new Vector2(390, 105));
            _loadingText = _ui.CreateText(_loadingOverlay, "Building replay scene", 24,
                alignment: TextAlignmentOptions.Center);
            NativeReplayUi.SetRect(_loadingText.rectTransform, new Vector2(0, .43f), new Vector2(1, .68f),
                new Vector2(18, 0), new Vector2(-18, 0));
            var loadingTrack = _ui.CreatePanel(_loadingOverlay, "Progress track", NativeReplayUi.Gray);
            NativeReplayUi.SetRect(loadingTrack.rectTransform, new Vector2(0, .20f), new Vector2(1, .35f),
                new Vector2(30, 0), new Vector2(-30, 0));
            _loadingFill = _ui.CreatePanel(loadingTrack.transform, "Progress fill", NativeReplayUi.Orange).rectTransform;
            NativeReplayUi.SetRect(_loadingFill, Vector2.zero, new Vector2(.85f, 1), Vector2.zero, Vector2.zero);
            _loadingOverlay.gameObject.SetActive(false);
            }
            catch { _ui.Dispose(); throw; }
        }

        private TextMeshProUGUI Button(Transform parent, string label, float x, float y, float width, Action action)
        {
            var button = _ui.CreateButton(parent, label, action);
            NativeReplayUi.Place((RectTransform)button.transform, x, y, width, 42);
            return button.GetComponentInChildren<TextMeshProUGUI>();
        }

        internal void SetLoading(bool visible, string stage, float progress)
        {
            if (!_loadingOverlay || _loadingOverlay.gameObject.activeSelf != visible)
                _loadingOverlay.gameObject.SetActive(visible);
            if (!visible) return;
            var value = Mathf.Clamp01(progress);
            _loadingText.text = stage + "  " + Mathf.RoundToInt(value * 100) + "%";
            _loadingFill.anchorMax = new Vector2(value, 1);
            _ui.Tick();
        }

        private void ToggleDetails()
        {
            _showDetails = !_showDetails;
            _details.gameObject.SetActive(_showDetails);
            if (_showDetails) { _showSettings = false; _settings.gameObject.SetActive(false); }
        }

        private void ToggleSettings()
        {
            _showSettings = !_showSettings;
            _settings.gameObject.SetActive(_showSettings);
            if (_showSettings) { _showDetails = false; _details.gameObject.SetActive(false); }
        }

        private void ToggleCameraChoices()
        {
            _cameraChoices.gameObject.SetActive(!_cameraChoices.gameObject.activeSelf);
        }

        private void ToggleFog()
        {
            _fog = !_fog;
            UpdateFogLabel();
            _setFog(_fog);
        }

        private void UpdateFogLabel() => _fogLabel.text = _fog ? "[x] Fog / Fake Fog" : "[ ] Fog / Fake Fog";

        private void ToggleInteriorCulling()
        {
            _disableInteriorCulling = !_disableInteriorCulling;
            UpdateCullingLabel();
            _setDisableInteriorCulling(_disableInteriorCulling);
        }

        private void UpdateCullingLabel() => _cullingLabel.text =
            _disableInteriorCulling ? "[x] Disable culling" : "[ ] Disable culling";

        private void ToggleNoShadow()
        {
            _noShadow = !_noShadow;
            UpdateNoShadowLabel();
            _setNoShadow(_noShadow);
        }

        private void UpdateNoShadowLabel() => _noShadowLabel.text = _noShadow ? "[x] No shadow" : "[ ] No shadow";

        private void ToggleCinematicMove() => _setCinematicMove(!_cinematicMove);

        internal void SetCinematicMove(bool enabled)
        {
            _cinematicMove = enabled;
            _cinematicLabel.text = enabled ? "[x] Cinematic movement (C)" : "[ ] Cinematic movement (C)";
        }

        internal void SetCameraSpeed(float value) => _cameraSpeedSlider.SetValueWithoutNotify(value);

        internal void SetBookmarks(IReadOnlyList<double> times, double duration)
        {
            foreach (var marker in _bookmarks) if (marker) UnityEngine.Object.Destroy(marker.gameObject);
            _bookmarks.Clear();
            // Bound UI objects for unusually marker-heavy recordings. The file
            // index and archive count still retain every bookmark.
            var step = Math.Max(1, (int)Math.Ceiling(times.Count / 512d));
            for (var i = 0; i < times.Count; i += step)
            {
                var time = times[i];
                var panel = _ui.CreatePanel(_timeline.transform, "Bookmark " + (i + 1) + " at " + Stamp(time),
                    new Color(.2f, .8f, .85f, 1f));
                var button = panel.gameObject.AddComponent<Button>();
                button.targetGraphic = panel;
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => _seek(time));
                var rect = (RectTransform)button.transform;
                var anchor = new Vector2((float)Math.Max(0, Math.Min(1, time / Math.Max(.01, duration))), .5f);
                NativeReplayUi.SetRect(rect, anchor, anchor, new Vector2(-3, -17), new Vector2(3, 17));
                _bookmarks.Add(rect);
            }
        }

        internal void Update(ReplaySession session, ReplayFrame frame, IReadOnlyList<EntitySnapshot> players, string selectedId,
            string scene, double time, bool playing, float speed, bool follow, bool diagnostics,
            double? totalDuration = null, double localTime = 0, bool buffering = false, double? deathTime = null,
            string? focusName = null)
        {
            if (UnityEngine.Time.unscaledTime < _nextUpdate) return;
            _nextUpdate = UnityEngine.Time.unscaledTime + 0.1f;
            session.Header.Metadata.TryGetValue("moon", out var moon);
            var hasQuota = session.Header.Metadata.TryGetValue("quotaRemaining", out var quota);
            var context = hasQuota ? "Quota " + quota + "  /  " : "";
            context += "Day " + LCReplay.Core.Archive.QuotaDay.Number(session.Header.Metadata) + "  /  ";
            var gameTime = ReplayGameClock.Normalized(frame.State);
            var hours = ReplayGameClock.Hours(frame.State, session.Header.Metadata);
            _title.text = "REPLAY  /  " + context + (string.IsNullOrEmpty(moon) ? scene : moon) +
                (gameTime.HasValue ? "  /  " + ReplayGameClock.Format(gameTime.Value, hours) : "") + (buffering ? "  /  Loading..." : "");
            var duration = Math.Max(0.01, totalDuration ?? session.Duration);
            _clock.text = Stamp(time) + " / " + Stamp(duration);
            // Clamp silently before a changing maximum can dispatch an unwanted seek.
            _timeline.SetValueWithoutNotify((float)Math.Min(time, duration));
            _timeline.maxValue = (float)duration;
            _timeline.SetValueWithoutNotify((float)time);
            _deathBookmark.gameObject.SetActive(deathTime.HasValue);
            if (deathTime.HasValue)
                NativeReplayUi.SetRect(_deathBookmark, new Vector2((float)Math.Max(0, Math.Min(1, deathTime.Value / duration)), .5f),
                    new Vector2((float)Math.Max(0, Math.Min(1, deathTime.Value / duration)), .5f),
                    new Vector2(-3, -17), new Vector2(3, 17));
            _pauseLabel.text = playing ? "Pause" : "Play";
            _speedLabel.text = speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
            var player = players.FirstOrDefault(value => value.Id == selectedId);
            _cameraLabel.text = follow && (player != null || !string.IsNullOrEmpty(focusName))
                ? "Camera: " + (player?.Name ?? focusName) : "Camera: Freecam";
            _visualLabel.text = diagnostics ? "Labels: on" : "Labels: off";
            _resolutionValue.text = Math.Round(_resolutionSlider.value * 100).ToString(CultureInfo.InvariantCulture) + "%";
            _gammaValue.text = _gammaSlider.value.ToString("0.00", CultureInfo.InvariantCulture);
            _cameraSpeedValue.text = _cameraSpeedSlider.value.ToString("0.0", CultureInfo.InvariantCulture);
            var cameraSignature = selectedId + "|" + follow + "|" + string.Join("|", players.Select(value =>
                value.Id + ":" + value.Name + ":" + IsDead(value)));
            if (cameraSignature != _cameraSignature)
            {
                _cameraSignature = cameraSignature;
                for (var i = _cameraChoices.childCount - 1; i >= 0; i--)
                    if (_cameraChoices.GetChild(i).name.StartsWith("Button ", StringComparison.Ordinal))
                    {
                        var old = _cameraChoices.GetChild(i).gameObject;
                        old.SetActive(false);
                        UnityEngine.Object.Destroy(old);
                    }
                var y = 43f;
                CameraChoice(follow ? "  Freecam" : "> Freecam", y, "", false);
                y += 48;
                foreach (var entity in players.Take(12))
                {
                    CameraChoice((follow && entity.Id == selectedId ? "> " : "  ") + entity.Name,
                        y, entity.Id, IsDead(entity));
                    y += 48;
                }
                NativeReplayUi.SetRect(_cameraChoices, new Vector2(.5f, 0), new Vector2(.5f, 0),
                    new Vector2(-355, 174), new Vector2(90, 174 + Math.Max(106, y + 6)));
            }
            if (!_showDetails) { _ui.Tick(); return; }
            var signature = selectedId + "|" + follow + "|" + string.Join("|", players.Select(value =>
                value.Id + ":" + value.Name + ":" + IsDead(value)));
            if (signature != _playerSignature)
            {
                _playerSignature = signature;
                NativeReplayUi.Clear(_players);
                var y = 0f;
                foreach (var entity in players)
                {
                    var id = entity.Id;
                    var label = Button(_players, (follow && id == selectedId ? "> " : "  ") + entity.Name, 0, y, 479, () => _selectCamera(id));
                    if (id == selectedId) _ui.AddBorder((RectTransform)label.transform.parent, NativeReplayUi.Orange);
                    if (IsDead(entity))
                    {
                        label.transform.parent.GetComponent<Button>().interactable = false;
                        label.color = new Color(.46f, .32f, .27f, 1f);
                    }
                    y += 54;
                }
                _players.sizeDelta = new Vector2(0, y);
            }
            if (_showEvents)
                _detailsText.text = string.Join("\n\n", session.Events.Where(value => value.Time <= localTime).Reverse().Take(30)
                    .Select(value => Stamp(Math.Max(0, time - localTime) + value.Time) + "  " + value.Category + " / " + value.Name));
            else
            {
                var start = session.Header.Metadata.TryGetValue("gameStartNormalizedTime", out var initial) &&
                    double.TryParse(initial, NumberStyles.Float, CultureInfo.InvariantCulture, out var normalized)
                    ? ReplayGameClock.Format(normalized, hours) : "Unknown (older recording)";
                var recorded = DateTime.TryParse(session.Header.StartedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var utc) ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : session.Header.StartedUtc;
                _detailsText.text = "Game start: " + start + "\nRecorded: " + recorded + "\n\n" +
                    string.Join("\n", (player?.State ?? frame.State).Take(60).Select(pair => pair.Key + " : " + pair.Value));
            }
            _ui.Tick();
        }

        private void CameraChoice(string label, float y, string id, bool dead)
        {
            var button = _ui.CreateButton(_cameraChoices, label, () =>
            { _selectCamera(id); _cameraChoices.gameObject.SetActive(false); });
            NativeReplayUi.Place((RectTransform)button.transform, 10, y, 425, 42);
            if (!dead) return;
            button.interactable = false;
            button.GetComponent<Image>().color = new Color(.23f, .025f, .02f, 1f);
            button.GetComponentInChildren<TextMeshProUGUI>().color = new Color(.46f, .32f, .27f, 1f);
        }

        private static bool IsDead(EntitySnapshot player) => player.State.TryGetValue("isPlayerDead", out var value) &&
            string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

        private static string Stamp(double value)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, value));
            return ((int)span.TotalHours).ToString("00", CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
        }
        internal void ReleaseInput() => _ui.ReleaseInput();
        internal void AcquireInput() => _ui.AcquireInput();
        public void Dispose() => _ui.Dispose();
    }
}
