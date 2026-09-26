using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LCReplay.Core.Archive;
using LCReplay.Plugin.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LCReplay.Plugin.Library
{
    /// <summary>A game-native archive screen with independent uGUI/TMP controls.</summary>
    public sealed class ReplayLibraryWindow : IDisposable
    {
        private readonly HashSet<string> _expandedRuns = new HashSet<string>();
        private ArchiveIndex _index = new ArchiveIndex();
        private NativeReplayUi? _ui;
        private RectTransform? _treeContent, _daysContent, _detailsContent;
        private ScrollRect? _treeScroll, _daysScroll, _detailsScroll;
        private TextMeshProUGUI? _statusText, _daysTitle;
        private Button? _playButton, _refreshButton, _folderButton;
        private readonly List<(Button Button, ArchiveSegment Segment)> _partButtons = new List<(Button, ArchiveSegment)>();
        private string _selectedDay = "", _selectedSession = "", _selectedPart = "", _recordingPath = "";
        private ArchiveRun? _run;
        private ArchiveSession? _session;
        private ArchiveDay? _day;
        private string _status = "", _recordingFailure = "";
        private bool _canPlay, _isLoading, _disposed, _dirty;

        public bool IsOpen => _ui != null;
        public event Action? CloseRequested;
        public event Action? RefreshRequested;
        public event Action<string>? PlayRequested;
        public event Action<string>? OpenFolderRequested;

        public void Open()
        {
            if (_disposed || IsOpen) return;
            try
            {
                _ui = new NativeReplayUi("LCReplay.Library", 32000);
                BuildWindow();
                Rebuild();
            }
            catch { Close(); throw; }
        }

        public void Close()
        {
            var ui = _ui;
            _ui = null;
            _treeContent = _daysContent = _detailsContent = null;
            _treeScroll = _daysScroll = _detailsScroll = null;
            _statusText = _daysTitle = null;
            _playButton = _refreshButton = _folderButton = null;
            _partButtons.Clear();
            ui?.Dispose();
        }

        public void Tick()
        {
            if (_ui == null) return;
            _ui.Tick();
            if (_dirty) Rebuild();
        }

        public void SetRecordingPath(string? path)
        {
            var next = path ?? "";
            if (SamePath(_recordingPath, next) || _recordingPath == next) return;
            _recordingPath = next;
            _dirty = true;
        }

        public void SetIndex(ArchiveIndex index)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            ResolveSelection();
            if (_session == null)
            {
                _session = _index.Runs.SelectMany(run => run.Sessions).FirstOrDefault(session => session.Days.Count > 0)
                    ?? _index.Runs.SelectMany(run => run.Sessions).FirstOrDefault();
                _selectedSession = _session?.Id ?? "";
            }
            if (_day == null)
            {
                _day = _session?.Days.FirstOrDefault();
                _selectedDay = _day?.Id ?? "";
            }
            ResolveSelection();
            if (_run != null) _expandedRuns.Add(_run.Id);
            if (_day != null && !_day.Segments.Any(segment => SamePath(segment.FilePath, _selectedPart)))
                _selectedPart = _day.Segments.FirstOrDefault()?.FilePath ?? "";
            _dirty = true;
        }

        /// <summary>Native canvases draw themselves; this compatibility method only updates state.</summary>
        public void DrawGui(string status, bool canPlay, bool isLoading, string recordingFailure = "")
        {
            _status = status ?? "";
            _recordingFailure = recordingFailure ?? "";
            _canPlay = canPlay;
            _isLoading = isLoading;
            if (_ui == null) return;
            if (_dirty) Rebuild();
            UpdateControls();
        }

        private void BuildWindow()
        {
            var ui = _ui!;
            var shade = ui.CreatePanel(ui.Rect, "Modal backdrop", new Color(0.114f, 0, 0, 0.79f));
            NativeReplayUi.Stretch(shade.rectTransform);
            var window = ui.CreatePanel(ui.Rect, "Replay archive window", NativeReplayUi.Black);
            NativeReplayUi.SetRect(window.rectTransform, new Vector2(0.045f, 0.07f), new Vector2(0.955f, 0.93f), Vector2.zero, Vector2.zero);
            ui.AddBorder(window.rectTransform, NativeReplayUi.ButtonColor, 3);
            var innerFrame = NativeReplayUi.NewRect(window.transform, "Inner frame");
            NativeReplayUi.Stretch(innerFrame, 8);
            ui.AddBorder(innerFrame, NativeReplayUi.Gray, 2);
            var tree = Pane(window.transform, "Runs and quotas", new Vector2(0.018f, 0.12f), new Vector2(0.256f, 0.98f));
            var days = Pane(window.transform, "Deadline recordings", new Vector2(0.269f, 0.12f), new Vector2(0.727f, 0.98f));
            var details = Pane(window.transform, "Recording details", new Vector2(0.740f, 0.12f), new Vector2(0.982f, 0.98f));
            PaneTitle(tree, "[Runs / quotas]");
            _daysTitle = PaneTitle(days, "[Recordings]");
            PaneTitle(details, "[Recording details]");
            _treeScroll = ui.CreateScroll(tree, "Saved quotas", out _treeContent);
            _daysScroll = ui.CreateScroll(days, "Recorded deadlines", out _daysContent);
            _detailsScroll = ui.CreateScroll(details, "Recording details", out _detailsContent);
            foreach (var scroll in new[] { _treeScroll, _daysScroll, _detailsScroll })
                NativeReplayUi.SetRect((RectTransform)scroll.transform, Vector2.zero, Vector2.one, new Vector2(14, 12), new Vector2(-12, -62));
            var name = ui.CreateText(window.transform, "LCReplay", 29);
            NativeReplayUi.SetRect(name.rectTransform, new Vector2(0.02f, 0.062f), new Vector2(0.32f, 0.108f), Vector2.zero, Vector2.zero);
            _statusText = ui.CreateText(window.transform, "Automatic recording / automatic saving", 19);
            NativeReplayUi.SetRect(_statusText.rectTransform, new Vector2(0.02f, 0.007f), new Vector2(0.975f, 0.057f), Vector2.zero, Vector2.zero);
            _refreshButton = BottomButton(window.transform, "[Refresh]", 0.51f, 0.615f, () => RefreshRequested?.Invoke());
            _folderButton = BottomButton(window.transform, "[Folder]", 0.63f, 0.735f, () => { if (_day != null) OpenFolderRequested?.Invoke(_day.DirectoryPath); });
            BottomButton(window.transform, "[Close]", 0.75f, 0.855f, RequestClose);
            _playButton = BottomButton(window.transform, "[Play]", 0.87f, 0.975f, PlayDay);
        }

        private RectTransform Pane(Transform parent, string name, Vector2 min, Vector2 max)
        {
            var pane = _ui!.CreatePanel(parent, name, NativeReplayUi.Gray);
            NativeReplayUi.SetRect(pane.rectTransform, min, max, Vector2.zero, Vector2.zero);
            return pane.rectTransform;
        }

        private TextMeshProUGUI PaneTitle(Transform parent, string title)
        {
            var label = _ui!.CreateText(parent, title, 29);
            NativeReplayUi.SetRect(label.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(18, -54), new Vector2(-12, -14));
            label.enableWordWrapping = false;
            return label;
        }

        private Button BottomButton(Transform parent, string label, float left, float right, Action action)
        {
            var button = _ui!.CreateButton(parent, label, action);
            NativeReplayUi.SetRect((RectTransform)button.transform, new Vector2(left, 0.065f), new Vector2(right, 0.108f), Vector2.zero, Vector2.zero);
            button.GetComponentInChildren<TextMeshProUGUI>().alignment = TextAlignmentOptions.Center;
            return button;
        }

        private void Rebuild()
        {
            if (_ui == null || _treeContent == null || _daysContent == null || _detailsContent == null) return;
            _dirty = false;
            var treePosition = _treeScroll!.verticalNormalizedPosition;
            var dayPosition = _daysScroll!.verticalNormalizedPosition;
            var detailPosition = _detailsScroll!.verticalNormalizedPosition;
            NativeReplayUi.Clear(_treeContent);
            NativeReplayUi.Clear(_daysContent);
            NativeReplayUi.Clear(_detailsContent);
            _partButtons.Clear();
            BuildTree(); BuildDays(); BuildDetails();
            Canvas.ForceUpdateCanvases();
            _treeScroll.verticalNormalizedPosition = treePosition;
            _daysScroll.verticalNormalizedPosition = dayPosition;
            _detailsScroll.verticalNormalizedPosition = detailPosition;
            UpdateControls();
        }

        private void BuildTree()
        {
            var content = _treeContent!;
            var top = 0f;
            if (_index.Runs.Count == 0) AddText(content, "No saved recordings.\n\nRecording starts automatically when you join a game.", ref top, 180, 25);
            foreach (var run in _index.Runs)
            {
                var expanded = _expandedRuns.Contains(run.Id);
                var row = AddRow(content, (expanded ? "− " : "+ ") + RunLabel(run), top, 57, () =>
                {
                    if (!_expandedRuns.Remove(run.Id)) _expandedRuns.Add(run.Id);
                    _dirty = true;
                });
                row.GetComponentInChildren<TextMeshProUGUI>().fontSize = 23;
                top += 65;
                if (!expanded) continue;
                foreach (var session in run.Sessions)
                {
                    var sessionRow = AddRow(content, "> " + SessionLabel(session) + "  (" + session.Days.Count + ")", top, 49,
                        () => SelectSession(session.Id), 18);
                    if (session.Id == _selectedSession) _ui!.AddBorder((RectTransform)sessionRow.transform, NativeReplayUi.Orange, 3);
                    top += 57;
                }
                top += 9;
            }
            content.sizeDelta = new Vector2(0, top + 8);
        }

        private void BuildDays()
        {
            var content = _daysContent!;
            var top = 0f;
            SetText(_daysTitle, _session == null ? "[Recordings]" : "[" + SessionLabel(_session) + " / Recordings]");
            if (_session == null || _session.Days.Count == 0)
                AddText(content, "Select a quota on the left.\n\nRecordings are grouped by the remaining quota and deadline.", ref top, 170, 27);
            else foreach (var day in _session.Days)
            {
                var row = AddRow(content, DayLabel(day), top, 100, () => SelectDay(day.Id));
                var title = row.GetComponentInChildren<TextMeshProUGUI>();
                NativeReplayUi.SetRect(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(17, 47), new Vector2(-14, -12));
                title.fontSize = 28;
                var summary = _ui!.CreateText(row.transform,
                    DateText(day.StartedUtc, "MM-dd HH:mm") + "  /  " + Duration(day.DurationSeconds) + "  /  " + (day.Segments.Any(IsRecording) ? "Recording" : string.IsNullOrWhiteSpace(day.Moon) ? DayStatus(day) : day.Moon), 23);
                NativeReplayUi.SetRect(summary.rectTransform, Vector2.zero, Vector2.one, new Vector2(17, 8), new Vector2(-14, -57));
                summary.enableWordWrapping = false;
                if (day.Id == _selectedDay) _ui.AddBorder((RectTransform)row.transform, NativeReplayUi.Orange, 4);
                top += 111;
            }
            content.sizeDelta = new Vector2(0, top + 8);
        }

        private void BuildDetails()
        {
            var content = _detailsContent!;
            var top = 0f;
            if (_day == null)
            {
                AddText(content, "Select a recording to see its details.\n\nChoose [Play] to watch with a free camera.", ref top, 300, 25);
                content.sizeDelta = new Vector2(0, top);
                return;
            }
            AddText(content, DayLabel(_day), ref top, 85, 30);
            if (_session?.IsQuotaGroup == true)
            {
                AddText(content, "Quota remaining  " + Number(_day.QuotaRemaining ?? _session.QuotaRemaining)
                    + "\nTarget  " + Number(_day.QuotaTarget ?? _session.QuotaTarget)
                    + "  /  Fulfilled  " + Number(_day.QuotaFulfilled ?? _session.QuotaFulfilled), ref top, 100, 22);
            }
            AddText(content, "Started\n" + DateText(_day.StartedUtc, "yyyy-MM-dd HH:mm"), ref top, 80);
            AddText(content, "Moon\n" + (string.IsNullOrWhiteSpace(_day.Moon) ? "Not recorded" : _day.Moon), ref top, 78);
            AddText(content, "Duration  " + Duration(_day.DurationSeconds) + "\nSize  " + Bytes(_day.Bytes), ref top, 78);
            AddText(content, _day.Segments.Any(IsRecording) ? "Recording / saving" : DayStatus(_day), ref top, 78);
            if (OpenFolderRequested != null) AddText(content, "[Folder] opens the save location\n[Play] starts at the beginning", ref top, 82, 22);
            if (_day.Segments.Count > 1)
            {
                AddText(content, "[Start from]", ref top, 53, 27);
                foreach (var segment in _day.Segments)
                {
                    var part = segment;
                    var button = AddRow(content, DateText(part.StartedUtc, "HH:mm:ss") + "  >", top, 45, () =>
                    {
                        _selectedPart = part.FilePath;
                        if (CanPlayPart(part)) PlayRequested?.Invoke(part.FilePath);
                    });
                    _partButtons.Add((button, part));
                    if (SamePath(part.FilePath, _selectedPart)) _ui!.AddBorder((RectTransform)button.transform, NativeReplayUi.Orange, 2);
                    top += 53;
                }
            }
            var error = _day.Segments.FirstOrDefault(part => !string.IsNullOrWhiteSpace(part.Error));
            if (error != null) AddText(content, "Needs attention\n" + error.Error, ref top, 180, 21);
            content.sizeDelta = new Vector2(0, top + 8);
        }

        private Button AddRow(RectTransform parent, string label, float top, float height, Action action, float indent = 0)
        {
            var row = _ui!.CreateButton(parent, label, action);
            NativeReplayUi.SetRect((RectTransform)row.transform, new Vector2(0, 1), Vector2.one, new Vector2(indent, -top - height), new Vector2(-3, -top));
            row.GetComponentInChildren<TextMeshProUGUI>().enableWordWrapping = false;
            return row;
        }

        private void AddText(RectTransform parent, string text, ref float top, float height, float size = 24)
        {
            var label = _ui!.CreateText(parent, text, size);
            NativeReplayUi.SetRect(label.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(7, -top - height), new Vector2(-8, -top));
            top += height;
        }

        private void UpdateControls()
        {
            if (_ui == null) return;
            if (_refreshButton != null) _refreshButton.interactable = !_isLoading;
            if (_folderButton != null) _folderButton.interactable = _day != null && OpenFolderRequested != null;
            var first = _day?.Segments.FirstOrDefault();
            if (_playButton != null) _playButton.interactable = first != null && CanPlayPart(first) && !_day!.Segments.Any(IsRecording);
            foreach (var pair in _partButtons) pair.Button.interactable = CanPlayPart(pair.Segment);
            var status = _isLoading ? "Loading recording..." : _status;
            if (string.IsNullOrWhiteSpace(status)) status = "Automatic recording / automatic saving    ·    F9 or Esc: close";
            if (!_canPlay && !_isLoading) status = "Recordings save automatically. Return to the main menu to play them.";
            if (_index.IsTruncated) status = "Only part of this large archive is shown.  " + status;
            else if (_index.Warnings.Count > 0) status = _index.Warnings.Count + " archive notices  ·  " + status;
            if (_recordingFailure.Length > 0) status = _recordingFailure + "  ·  F8: retry recording";
            SetText(_statusText, status);
        }

        private bool CanPlayPart(ArchiveSegment part) => _canPlay && !_isLoading && part.HeaderReadable && !IsRecording(part);
        private void PlayDay()
        {
            var first = _day?.Segments.FirstOrDefault();
            if (first != null && CanPlayPart(first) && !_day!.Segments.Any(IsRecording)) PlayRequested?.Invoke(first.FilePath);
        }

        private void SelectSession(string id)
        {
            _selectedSession = id;
            _selectedDay = "";
            ResolveSelection();
            _selectedDay = _session?.Days.FirstOrDefault()?.Id ?? "";
            ResolveSelection();
            _selectedPart = _day?.Segments.FirstOrDefault()?.FilePath ?? "";
            if (_daysScroll != null) _daysScroll.verticalNormalizedPosition = 1;
            if (_detailsScroll != null) _detailsScroll.verticalNormalizedPosition = 1;
            _dirty = true;
        }

        private void SelectDay(string id)
        {
            _selectedDay = id;
            ResolveSelection();
            _selectedPart = _day?.Segments.FirstOrDefault()?.FilePath ?? "";
            if (_detailsScroll != null) _detailsScroll.verticalNormalizedPosition = 1;
            _dirty = true;
        }

        private void ResolveSelection()
        {
            _run = null; _session = null; _day = null;
            foreach (var run in _index.Runs)
            foreach (var session in run.Sessions)
            {
                if (session.Id == _selectedSession) { _run = run; _session = session; }
                var day = session.Days.FirstOrDefault(candidate => candidate.Id == _selectedDay);
                if (day != null) { _run = run; _session = session; _selectedSession = session.Id; _day = day; return; }
            }
        }

        private void RequestClose() { Close(); CloseRequested?.Invoke(); }
        private bool IsRecording(ArchiveSegment segment) => SamePath(segment.FilePath, _recordingPath);
        private static bool SamePath(string a, string b) => a.Length != 0 && b.Length != 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static void SetText(TMP_Text? text, string value) { if (text != null && text.text != value) text.text = value; }
        private static string RunLabel(ArchiveRun run) => (run.Id == "legacy" || run.Status == "recovered") && !string.IsNullOrWhiteSpace(run.Label) ? run.Label : DateText(run.StartedUtc, "MM-dd HH:mm");
        private static bool IsPreparing(ArchiveDay day) => string.Equals(day.Status, "preparing", StringComparison.OrdinalIgnoreCase);
        private static string SessionLabel(ArchiveSession session) => session.IsQuotaGroup ? "Quota " + Number(session.QuotaRemaining) : "Session " + session.SessionNumber;
        private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        private string DayLabel(ArchiveDay day)
        {
            if (_session?.IsQuotaGroup == true || day.DeadlineDaysRemaining.HasValue || day.QuotaRemaining.HasValue || day.QuotaCycle.HasValue)
                return "Deadline: " + (day.DeadlineDaysRemaining.HasValue ? day.DeadlineDaysRemaining.Value.ToString(CultureInfo.InvariantCulture)
                    + (day.DeadlineDaysRemaining.Value == 1 ? " day" : " days") : "unknown") + (IsPreparing(day) ? " · Lobby" : "");
            var generatedLabel = string.Equals(day.Label, "Day " + day.DayNumber, StringComparison.OrdinalIgnoreCase)
                || day.Label.StartsWith("Day-" + day.DayNumber.ToString("D3", CultureInfo.InvariantCulture) + "-", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(day.Label) && !generatedLabel && (day.Status == "recovered" || day.Id.StartsWith("recovered:", StringComparison.Ordinal))) return day.Label;
            return (day.DayNumber > 0 ? "Day " + day.DayNumber : "Lobby / preparation") + (IsPreparing(day) ? " · Lobby" : "");
        }
        private static string DayStatus(ArchiveDay day) => IsPreparing(day) ? "Lobby · No expedition" : day.Segments.Count == 0 ? "No recording files" : day.Segments.Any(segment => !segment.HeaderReadable) ? "Some files need attention" : day.Segments.All(segment => segment.Status == "complete") ? "Saved" : "Recoverable recording";
        private static string DateText(DateTimeOffset value, string format) => value == default ? "Unknown date" : value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        private static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "00:00";
            var span = TimeSpan.FromSeconds(Math.Min(seconds, 31536000));
            return span.TotalHours >= 1 ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
        private static string Bytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? (bytes / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        public void Dispose() { if (_disposed) return; _disposed = true; Close(); }
    }
}
