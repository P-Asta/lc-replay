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
        private readonly TextMeshProUGUI _title, _clock, _pauseLabel, _speedLabel, _followLabel, _playerLabel, _detailsText, _visualLabel;
        private readonly TextMeshProUGUI _resolutionValue, _gammaValue, _cullingLabel, _playerAudioLabel;
        private readonly Slider _timeline;
        private readonly RectTransform _details, _players, _settings;
        private RectTransform _loadingOverlay = null!, _loadingFill = null!;
        private TextMeshProUGUI _loadingText = null!;
        private readonly Slider _resolutionSlider, _gammaSlider;
        private readonly Action<string> _selectPlayer;
        private readonly Action<bool> _setDisableInteriorCulling;
        private readonly Action<bool> _setMutePlayerAudio;
        private bool _disableInteriorCulling;
        private bool _mutePlayerAudio;
        private readonly bool _hasPlayerAudioTags;
        private string _playerSignature = "";
        private float _nextUpdate;
        private bool _showDetails;
        private bool _showEvents;
        private bool _showSettings;
        public GameObject Root => _ui.Root;

        internal NativePlaybackHud(ReplaySession session, Action togglePause, Action<double> seek, Action cycleSpeed,
            Action toggleFollow, Action focus, Action<string> selectPlayer, Action toggleVisuals, Action close,
            float resolutionScale, float gamma, Action<float> setResolution, Action<float> setGamma,
            bool disableInteriorCulling, Action<bool> setDisableInteriorCulling,
            bool mutePlayerAudio, Action<bool> setMutePlayerAudio)
        {
            _selectPlayer = selectPlayer;
            _disableInteriorCulling = disableInteriorCulling;
            _setDisableInteriorCulling = setDisableInteriorCulling;
            _mutePlayerAudio = mutePlayerAudio;
            _setMutePlayerAudio = setMutePlayerAudio;
            _hasPlayerAudioTags = session.Header.Capabilities.Contains("player-audio-origin");
            _ui = new NativeReplayUi("LCReplay.PlaybackHud", 31900, false);
            try
            {
            var top = _ui.CreatePanel(Root.transform, "Replay dock", NativeReplayUi.HeaderColor);
            NativeReplayUi.SetRect(top.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(28, -70), new Vector2(-28, -24));
            _ui.AddBorder(top.rectTransform, NativeReplayUi.Orange, 2);
            _title = _ui.CreateText(top.transform, "REPLAY", 22, alignment: TextAlignmentOptions.MidlineLeft);
            _title.fontStyle = FontStyles.Bold;
            _title.characterSpacing = 1.5f;
            NativeReplayUi.SetRect(_title.rectTransform, Vector2.zero, Vector2.one, new Vector2(18, 0), new Vector2(-650, 0));
            var settingsButton = _ui.CreateButton(top.transform, "Settings", ToggleSettings);
            NativeReplayUi.SetRect((RectTransform)settingsButton.transform, new Vector2(1, 0), Vector2.one, new Vector2(-638, 6), new Vector2(-432, -6));
            var detailsButton = _ui.CreateButton(top.transform, "Details", ToggleDetails);
            NativeReplayUi.SetRect((RectTransform)detailsButton.transform, new Vector2(1, 0), Vector2.one, new Vector2(-426, 6), new Vector2(-220, -6));
            var closeButton = _ui.CreateButton(top.transform, "Archive", close);
            NativeReplayUi.SetRect((RectTransform)closeButton.transform, new Vector2(1, 0), Vector2.one, new Vector2(-214, 6), new Vector2(-8, -6));

            var bottom = _ui.CreatePanel(Root.transform, "Playback controls", NativeReplayUi.Black);
            NativeReplayUi.SetRect(bottom.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(28, 24), new Vector2(-28, 184));
            _ui.AddBorder(bottom.rectTransform, NativeReplayUi.Orange, 2);
            var bottomHeader = _ui.CreatePanel(bottom.transform, "Playback bar header", NativeReplayUi.HeaderColor);
            NativeReplayUi.SetRect(bottomHeader.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(2, -40), new Vector2(-2, -2));
            bottomHeader.raycastTarget = false;
            _clock = _ui.CreateText(bottom.transform, "00:00 / 00:00", 23, alignment: TextAlignmentOptions.MidlineLeft);
            NativeReplayUi.Place(_clock.rectTransform, 20, 9, 300, 32);
            var hint = _ui.CreateText(bottom.transform, "Right mouse + WASD: move   Q / E: height   Shift: fast   Space: pause", 18, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.SetRect(hint.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(335, -41), new Vector2(-20, -9));
            _timeline = _ui.CreateSlider(bottom.transform, "Replay timeline", 0, (float)Math.Max(0.01, session.Duration), 0, value => seek(value));
            NativeReplayUi.SetRect((RectTransform)_timeline.transform, new Vector2(0, 1), Vector2.one, new Vector2(16, -78), new Vector2(-16, -42));
            _pauseLabel = Button(bottom.transform, "Pause", 20, 91, 206, togglePause);
            Button(bottom.transform, "−5s", 234, 91, 154, () => seek(double.NegativeInfinity));
            Button(bottom.transform, "+5s", 396, 91, 154, () => seek(double.PositiveInfinity));
            _speedLabel = Button(bottom.transform, "1×", 558, 91, 140, cycleSpeed);
            _followLabel = Button(bottom.transform, "Free camera", 706, 91, 218, toggleFollow);
            _playerLabel = Button(bottom.transform, "Focus player", 932, 91, 360, focus);
            var source = _ui.CreateText(bottom.transform, session.Header.Capabilities.Contains("embedded-render-assets") ? "" : "Older recording: appearance unavailable", 20, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.SetRect(source.rectTransform, new Vector2(1, 1), Vector2.one, new Vector2(-530, -135), new Vector2(-20, -94));

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
            var resolutionTitle = _ui.CreateText(_settings, "Render resolution", 23);
            NativeReplayUi.Place(resolutionTitle.rectTransform, 18, 79, 320, 38);
            _resolutionValue = _ui.CreateText(_settings, "", 22, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.Place(_resolutionValue.rectTransform, 330, 79, 188, 38);
            _resolutionSlider = _ui.CreateSlider(_settings, "Render resolution", .25f, 1f, resolutionScale, setResolution);
            NativeReplayUi.Place((RectTransform)_resolutionSlider.transform, 18, 123, 500, 42);
            var gammaTitle = _ui.CreateText(_settings, "Gamma", 23);
            NativeReplayUi.Place(gammaTitle.rectTransform, 18, 189, 320, 38);
            _gammaValue = _ui.CreateText(_settings, "", 22, alignment: TextAlignmentOptions.MidlineRight);
            NativeReplayUi.Place(_gammaValue.rectTransform, 330, 189, 188, 38);
            _gammaSlider = _ui.CreateSlider(_settings, "Gamma", .5f, 2f, gamma, setGamma);
            NativeReplayUi.Place((RectTransform)_gammaSlider.transform, 18, 233, 500, 42);
            _cullingLabel = Button(_settings, "", 18, 293, 500, ToggleInteriorCulling);
            UpdateCullingLabel();
            _playerAudioLabel = Button(_settings, "", 18, 352, 500, TogglePlayerAudio);
            UpdatePlayerAudioLabel();
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

        private void ToggleInteriorCulling()
        {
            _disableInteriorCulling = !_disableInteriorCulling;
            UpdateCullingLabel();
            _setDisableInteriorCulling(_disableInteriorCulling);
        }

        private void UpdateCullingLabel() => _cullingLabel.text =
            _disableInteriorCulling ? "[x] Disable culling" : "[ ] Disable culling";

        private void TogglePlayerAudio()
        {
            if (!_hasPlayerAudioTags) return;
            _mutePlayerAudio = !_mutePlayerAudio;
            UpdatePlayerAudioLabel();
            _setMutePlayerAudio(_mutePlayerAudio);
        }

        private void UpdatePlayerAudioLabel() => _playerAudioLabel.text = !_hasPlayerAudioTags
            ? "[-] Player sounds: older recording"
            : _mutePlayerAudio ? "[ ] Player sounds" : "[x] Player sounds";

        internal void Update(ReplaySession session, ReplayFrame frame, IReadOnlyList<EntitySnapshot> players, string selectedId,
            string scene, double time, bool playing, float speed, bool follow, bool diagnostics,
            double? totalDuration = null, double localTime = 0, bool buffering = false)
        {
            if (UnityEngine.Time.unscaledTime < _nextUpdate) return;
            _nextUpdate = UnityEngine.Time.unscaledTime + 0.1f;
            var hasDay = session.Header.Metadata.TryGetValue("dayNumber", out var day);
            session.Header.Metadata.TryGetValue("moon", out var moon);
            var hasQuota = session.Header.Metadata.TryGetValue("quotaRemaining", out var quota);
            var hasDeadline = session.Header.Metadata.TryGetValue("deadlineDaysRemaining", out var deadline);
            var context = hasQuota ? "Quota " + quota + "  /  " : "";
            context += hasDeadline ? "Deadline: " + deadline + " days  /  " : hasDay ? "Day " + day + "  /  " : "";
            _title.text = "REPLAY  /  " + context + (string.IsNullOrEmpty(moon) ? scene : moon) + (buffering ? "  /  Loading..." : "");
            var duration = Math.Max(0.01, totalDuration ?? session.Duration);
            _clock.text = Stamp(time) + " / " + Stamp(duration);
            // Clamp silently before a changing maximum can dispatch an unwanted seek.
            _timeline.SetValueWithoutNotify((float)Math.Min(time, duration));
            _timeline.maxValue = (float)duration;
            _timeline.SetValueWithoutNotify((float)time);
            _pauseLabel.text = playing ? "Pause" : "Play";
            _speedLabel.text = speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
            _followLabel.text = follow ? "Following" : "Free camera";
            var player = players.FirstOrDefault(value => value.Id == selectedId);
            _playerLabel.text = "Focus " + (player?.Name ?? "player");
            _visualLabel.text = diagnostics ? "Labels: on" : "Labels: off";
            _resolutionValue.text = Math.Round(_resolutionSlider.value * 100).ToString(CultureInfo.InvariantCulture) + "%";
            _gammaValue.text = _gammaSlider.value.ToString("0.00", CultureInfo.InvariantCulture);
            if (!_showDetails) { _ui.Tick(); return; }
            var signature = selectedId + "|" + string.Join("|", players.Select(value => value.Id + ":" + value.Name));
            if (signature != _playerSignature)
            {
                _playerSignature = signature;
                NativeReplayUi.Clear(_players);
                var y = 0f;
                foreach (var entity in players)
                {
                    var id = entity.Id;
                    var label = Button(_players, (id == selectedId ? "> " : "  ") + entity.Name, 0, y, 479, () => _selectPlayer(id));
                    if (id == selectedId) _ui.AddBorder((RectTransform)label.transform.parent, NativeReplayUi.Orange);
                    y += 54;
                }
                _players.sizeDelta = new Vector2(0, y);
            }
            if (_showEvents)
                _detailsText.text = string.Join("\n\n", session.Events.Where(value => value.Time <= localTime).Reverse().Take(30)
                    .Select(value => Stamp(Math.Max(0, time - localTime) + value.Time) + "  " + value.Category + " / " + value.Name));
            else
                _detailsText.text = string.Join("\n", (player?.State ?? frame.State).Take(60).Select(pair => pair.Key + " : " + pair.Value));
            _ui.Tick();
        }

        private static string Stamp(double value)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, value));
            return ((int)span.TotalHours).ToString("00", CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
        }
        public void Dispose() => _ui.Dispose();
    }
}
