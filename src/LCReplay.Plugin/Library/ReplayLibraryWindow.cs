using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private TextMeshProUGUI? _statusText, _daysTitle, _selectionText, _deletePrompt;
        private RectTransform? _loadingOverlay, _loadingFill;
        private RectTransform? _deleteOverlay;
        private TextMeshProUGUI? _loadingText;
        private Button? _playButton, _refreshButton, _folderButton, _confirmDeleteButton;
        private readonly List<(Button Button, ArchiveSegment Segment)> _partButtons = new List<(Button, ArchiveSegment)>();
        private readonly List<(Button Button, ArchiveDay[] Days)> _deleteTargets = new List<(Button, ArchiveDay[])>();
        private string _selectedDay = "", _selectedSession = "", _selectedPart = "", _recordingPath = "", _protectedDayId = "";
        private ArchiveRun? _run;
        private ArchiveSession? _session;
        private ArchiveDay? _day;
        private string _status = "", _recordingFailure = "";
        private bool _canPlay, _isLoading, _disposed, _dirty;
        private float _loadProgress;
        private string _loadStage = "";
        private ArchiveDay[]? _pendingDeleteDays;

        public bool IsOpen => _ui != null;
        public event Action? CloseRequested;
        public event Action? RefreshRequested;
        public event Action<string>? PlayRequested;
        public event Action<string>? OpenFolderRequested;
        public event Action<IReadOnlyList<ArchiveDay>>? DeleteRequested;
        public bool HasDeleteConfirmation => _pendingDeleteDays != null;

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
            _statusText = _daysTitle = _selectionText = _deletePrompt = null;
            _loadingOverlay = _loadingFill = null;
            _deleteOverlay = null;
            _loadingText = null;
            _playButton = _refreshButton = _folderButton = _confirmDeleteButton = null;
            _pendingDeleteDays = null;
            _partButtons.Clear();
            _deleteTargets.Clear();
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
                _session = _index.Runs.SelectMany(run => run.Sessions).FirstOrDefault(session => session.Days.Count > 0);
                _selectedSession = _session?.Id ?? "";
            }
            if (_day == null)
            {
                _day = _session?.Days.FirstOrDefault();
                _selectedDay = _day?.Id ?? "";
            }
            ResolveSelection();
            if (HasDeleteConfirmation) CancelDelete();
            if (_run != null) _expandedRuns.Add(_run.Id);
            if (_day != null && !_day.Segments.Any(segment => SamePath(segment.FilePath, _selectedPart)))
                _selectedPart = _day.Segments.FirstOrDefault()?.FilePath ?? "";
            _dirty = true;
        }

        /// <summary>Native canvases draw themselves; this compatibility method only updates state.</summary>
        public void DrawGui(string status, bool canPlay, bool isLoading, string recordingFailure = "",
            float loadProgress = 0, string loadStage = "", string protectedDayId = "")
        {
            _status = status ?? "";
            _recordingFailure = recordingFailure ?? "";
            _canPlay = canPlay;
            _isLoading = isLoading;
            if (_isLoading && HasDeleteConfirmation) CancelDelete();
            _loadProgress = Mathf.Clamp01(loadProgress);
            _loadStage = loadStage ?? "";
            _protectedDayId = protectedDayId ?? "";
            if (HasDeleteConfirmation && !CanDeleteDays(_pendingDeleteDays)) CancelDelete();
            if (_ui == null) return;
            if (_dirty) Rebuild();
            UpdateControls();
        }

        private void BuildWindow()
        {
            var ui = _ui!;
            var shade = ui.CreatePanel(ui.Rect, "Archive backdrop", NativeReplayUi.Black);
            NativeReplayUi.Stretch(shade.rectTransform);
            var window = ui.CreatePanel(ui.Rect, "Replay archive window", NativeReplayUi.Gray);
            NativeReplayUi.SetRect(window.rectTransform, new Vector2(0.045f, 0.07f), new Vector2(0.955f, 0.93f), Vector2.zero, Vector2.zero);
            ui.AddBorder(window.rectTransform, NativeReplayUi.Orange, 2);
            ui.AddTitleBar(window.rectTransform, "Replay Archive", 54);
            _refreshButton = TopButton(window.transform, "Refresh", 0.65f, 0.75f, () => RefreshRequested?.Invoke());
            _folderButton = TopButton(window.transform, "Folder", 0.76f, 0.86f,
                () => { if (_day != null) OpenFolderRequested?.Invoke(_day.DirectoryPath); });
            TopButton(window.transform, "Close", 0.87f, 0.98f, RequestClose);
            var tree = Pane(window.transform, "Runs and quotas", new Vector2(0.018f, 0.16f), new Vector2(0.260f, 0.90f));
            var days = Pane(window.transform, "Day recordings", new Vector2(0.272f, 0.16f), new Vector2(0.690f, 0.90f));
            var details = Pane(window.transform, "Recording details", new Vector2(0.702f, 0.16f), new Vector2(0.982f, 0.90f));
            PaneTitle(tree, "RUNS / QUOTAS");
            _daysTitle = PaneTitle(days, "RECORDINGS");
            PaneTitle(details, "DETAILS");
            _treeScroll = ui.CreateScroll(tree, "Saved quotas", out _treeContent);
            _daysScroll = ui.CreateScroll(days, "Recorded days", out _daysContent);
            _detailsScroll = ui.CreateScroll(details, "Recording details", out _detailsContent);
            foreach (var scroll in new[] { _treeScroll, _daysScroll, _detailsScroll })
                NativeReplayUi.SetRect((RectTransform)scroll.transform, Vector2.zero, Vector2.one, new Vector2(14, 12), new Vector2(-12, -62));
            _selectionText = ui.CreateText(window.transform, "Select a recording", 22, NativeReplayUi.White);
            NativeReplayUi.SetRect(_selectionText.rectTransform, new Vector2(0.02f, 0.069f), new Vector2(0.72f, 0.13f), Vector2.zero, Vector2.zero);
            _selectionText.enableWordWrapping = false;
            _statusText = ui.CreateText(window.transform, "Automatic recording / automatic saving", 19);
            NativeReplayUi.SetRect(_statusText.rectTransform, new Vector2(0.02f, 0.012f), new Vector2(0.80f, 0.063f), Vector2.zero, Vector2.zero);
            _playButton = BottomButton(window.transform, "PLAY", 0.82f, 0.975f, PlayDay, true);
            _loadingOverlay = ui.CreateWindow(window.transform, "Replay loading", "Loading Replay", 48);
            NativeReplayUi.SetRect(_loadingOverlay, new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                new Vector2(-360, -100), new Vector2(360, 100));
            _loadingText = ui.CreateText(_loadingOverlay, "Loading recording", 27, alignment: TextAlignmentOptions.Center);
            NativeReplayUi.SetRect(_loadingText.rectTransform, new Vector2(0, .55f), new Vector2(1, .9f),
                new Vector2(20, 0), new Vector2(-20, 0));
            var track = ui.CreatePanel(_loadingOverlay, "Progress track", NativeReplayUi.Gray);
            NativeReplayUi.SetRect(track.rectTransform, new Vector2(0, .25f), new Vector2(1, .42f),
                new Vector2(28, 0), new Vector2(-28, 0));
            var fill = ui.CreatePanel(track.transform, "Progress fill", NativeReplayUi.Orange);
            _loadingFill = fill.rectTransform;
            NativeReplayUi.SetRect(_loadingFill, Vector2.zero, new Vector2(0, 1), Vector2.zero, Vector2.zero);
            _loadingOverlay.gameObject.SetActive(false);

            var deleteShade = ui.CreatePanel(window.transform, "Delete confirmation backdrop", NativeReplayUi.Black);
            _deleteOverlay = deleteShade.rectTransform;
            NativeReplayUi.Stretch(_deleteOverlay);
            var confirmation = ui.CreateWindow(_deleteOverlay, "Delete confirmation", "Delete Recording?", 48);
            NativeReplayUi.SetRect(confirmation, new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                new Vector2(-410, -185), new Vector2(410, 185));
            _deletePrompt = ui.CreateText(confirmation.transform, "", 24);
            NativeReplayUi.Place(_deletePrompt.rectTransform, 34, 78, 752, 150);
            var warning = ui.CreateText(confirmation.transform, "This removes the replay and its index files. This cannot be undone.", 20, NativeReplayUi.White);
            NativeReplayUi.Place(warning.rectTransform, 34, 250, 752, 34);
            var cancel = ui.CreateButton(confirmation.transform, "Keep recording", CancelDelete);
            NativeReplayUi.Place((RectTransform)cancel.transform, 34, 306, 350, 46);
            _confirmDeleteButton = ui.CreateButton(confirmation.transform, "DELETE PERMANENTLY", DeleteConfirmed);
            NativeReplayUi.Place((RectTransform)_confirmDeleteButton.transform, 395, 306, 391, 46);
            _confirmDeleteButton.GetComponent<Image>().color = NativeReplayUi.Orange;
            _confirmDeleteButton.GetComponentInChildren<TextMeshProUGUI>().color = NativeReplayUi.White;
            _deleteOverlay.gameObject.SetActive(false);
        }

        private RectTransform Pane(Transform parent, string name, Vector2 min, Vector2 max)
        {
            var pane = _ui!.CreatePanel(parent, name, NativeReplayUi.Black);
            NativeReplayUi.SetRect(pane.rectTransform, min, max, Vector2.zero, Vector2.zero);
            _ui.AddBorder(pane.rectTransform, NativeReplayUi.HeaderColor, 2);
            return pane.rectTransform;
        }

        private TextMeshProUGUI PaneTitle(Transform parent, string title)
        {
            var label = _ui!.AddTitleBar((RectTransform)parent, title, 48);
            label.fontSize = 20;
            label.enableWordWrapping = false;
            return label;
        }

        private Button BottomButton(Transform parent, string label, float left, float right, Action action, bool filled = false)
        {
            var button = _ui!.CreateButton(parent, label, action, filled);
            NativeReplayUi.SetRect((RectTransform)button.transform, new Vector2(left, 0.03f), new Vector2(right, 0.13f), Vector2.zero, Vector2.zero);
            button.GetComponentInChildren<TextMeshProUGUI>().alignment = TextAlignmentOptions.Center;
            return button;
        }

        private Button TopButton(Transform parent, string label, float left, float right, Action action)
        {
            var button = _ui!.CreateButton(parent, label, action);
            NativeReplayUi.SetRect((RectTransform)button.transform, new Vector2(left, 1), new Vector2(right, 1),
                new Vector2(3, -47), new Vector2(-3, -7));
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
            _deleteTargets.Clear();
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
            var visibleRuns = _index.Runs.Where(item => item.Sessions.Any(group => group.Days.Count > 0)).ToArray();
            if (visibleRuns.Length == 0) AddText(content, "No saved recordings.\n\nRecording starts automatically when you join a game.", ref top, 180, 25);
            foreach (var run in visibleRuns)
            {
                var expanded = _expandedRuns.Contains(run.Id);
                var row = AddRow(content, (expanded ? "− " : "+ ") + RunLabel(run), top, 46, () =>
                {
                    if (!_expandedRuns.Remove(run.Id)) _expandedRuns.Add(run.Id);
                    _dirty = true;
                }, rightInset: 62);
                row.GetComponentInChildren<TextMeshProUGUI>().fontSize = 19;
                InlineDelete(content, top, 46,
                    run.Sessions.SelectMany(group => group.Days).ToArray(),
                    "Run " + RunLabel(run));
                top += 53;
                if (!expanded) continue;
                foreach (var session in run.Sessions.Where(group => group.Days.Count > 0))
                {
                    var sessionRow = AddRow(content, "> " + SessionLabel(session) + "  (" + session.Days.Count + ")", top, 42,
                        () => SelectSession(session.Id), 18, 62);
                    InlineDelete(content, top, 42, session.Days.ToArray(),
                        SessionLabel(session));
                    if (session.Id == _selectedSession) { sessionRow.GetComponent<Image>().color = NativeReplayUi.HeaderColor; _ui!.AddBorder((RectTransform)sessionRow.transform, NativeReplayUi.Orange, 2); }
                    top += 48;
                }
                top += 9;
            }
            content.sizeDelta = new Vector2(0, top + 8);
        }

        private void BuildDays()
        {
            var content = _daysContent!;
            var top = 0f;
            SetText(_daysTitle, _session == null ? "RECORDINGS" : SessionLabel(_session).ToUpperInvariant());
            if (_session == null || _session.Days.Count == 0)
                AddText(content, "Select a quota on the left.\n\nEach quota has Day 1, Day 2 and Day 3 recordings.", ref top, 170, 27);
            else foreach (var day in _session.Days)
            {
                var row = AddRow(content, DayLabel(day) + (day.BookmarkCount > 0 ? "  /  Bookmarks: " + day.BookmarkCount : ""), top, 76, () => SelectDay(day.Id), rightInset: 62);
                var title = row.GetComponentInChildren<TextMeshProUGUI>();
                NativeReplayUi.SetRect(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(12, 38), new Vector2(-10, -6));
                title.fontSize = 23;
                var summary = _ui!.CreateText(row.transform,
                    DateText(day.StartedUtc, "MM-dd HH:mm") + "   ·   " + Duration(day.DurationSeconds) + "   ·   " + Bytes(day.Bytes) + "   ·   " +
                    (day.Segments.Any(IsRecording) ? "RECORDING" : string.IsNullOrWhiteSpace(day.Moon) ? DayStatus(day) : day.Moon)
                    + (day.Members.Count == 0 ? "" : "   ·   " + string.Join(", ", day.Members.Take(4))
                        + (day.Members.Count > 4 ? " +" + (day.Members.Count - 4).ToString(CultureInfo.InvariantCulture) : "")), 18,
                    NativeReplayUi.White);
                NativeReplayUi.SetRect(summary.rectTransform, Vector2.zero, Vector2.one, new Vector2(12, 8), new Vector2(-14, -45));
                summary.enableWordWrapping = false;
                if (day.Id == _selectedDay) { row.GetComponent<Image>().color = NativeReplayUi.HeaderColor; _ui.AddBorder((RectTransform)row.transform, NativeReplayUi.Orange, 2); }
                InlineDelete(content, top, 76, day.Segments.Count > 0 ? new[] { day } : Array.Empty<ArchiveDay>(),
                    SessionLabel(_session) + " / " + DayLabel(day));
                top += 84;
            }
            content.sizeDelta = new Vector2(0, top + 8);
        }

        private void BuildDetails()
        {
            var content = _detailsContent!;
            var top = 0f;
            if (_day == null)
            {
                AddText(content, "Select a recording to see its details.\n\nUse PLAY below to watch it. The X beside a recording deletes it after confirmation.", ref top, 300, 25);
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
            AddText(content, "Members\n" + (_day.Members.Count == 0 ? "Not recorded" : string.Join(", ", _day.Members)), ref top,
                _day.Members.Count > 4 ? 112 : 80, 22);
            AddText(content, "Duration  " + Duration(_day.DurationSeconds) + "\nSize  " + Bytes(_day.Bytes), ref top, 78);
            AddText(content, "Bookmarks  " + _day.BookmarkCount.ToString(CultureInfo.InvariantCulture), ref top, 44, 22);
            AddText(content, _day.Segments.Any(IsRecording) ? "Recording / saving" : DayStatus(_day), ref top, 78);
            if (OpenFolderRequested != null) AddText(content, "PLAY watches from the start\nX beside an item opens delete confirmation", ref top, 82, 22);
            if (_day.Segments.Count > 1)
            {
                AddText(content, "[Start from]", ref top, 53, 27);
                foreach (var segment in _day.Segments)
                {
                    var part = segment;
                    var button = AddRow(content, DateText(part.StartedUtc, "HH:mm:ss") + "  /  Bookmarks: " + part.BookmarkCount + "  >", top, 45, () =>
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

        private Button AddRow(RectTransform parent, string label, float top, float height, Action action, float indent = 0, float rightInset = 3)
        {
            var row = _ui!.CreateButton(parent, label, action);
            NativeReplayUi.SetRect((RectTransform)row.transform, new Vector2(0, 1), Vector2.one, new Vector2(indent, -top - height), new Vector2(-rightInset, -top));
            row.GetComponentInChildren<TextMeshProUGUI>().enableWordWrapping = false;
            return row;
        }

        private void InlineDelete(RectTransform parent, float top, float height, ArchiveDay[] days, string label)
        {
            if (!days.Any(day => day.Segments.Count > 0)) return;
            var button = _ui!.CreateButton(parent, "X", () => ConfirmDelete(days, label));
            NativeReplayUi.SetRect((RectTransform)button.transform, Vector2.one, Vector2.one,
                new Vector2(-54, -top - height + 5), new Vector2(-5, -top - 5));
            button.GetComponent<Image>().color = NativeReplayUi.Orange;
            var text = button.GetComponentInChildren<TextMeshProUGUI>();
            text.color = NativeReplayUi.White;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 27;
            _deleteTargets.Add((button, days));
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
            foreach (var target in _deleteTargets) target.Button.interactable = CanDeleteDays(target.Days);
            if (_confirmDeleteButton != null) _confirmDeleteButton.interactable = CanDeleteDays(_pendingDeleteDays);
            SetText(_selectionText, _day == null ? "Select a recording" : "Selected  /  " + DayLabel(_day));
            var first = _day?.Segments.FirstOrDefault();
            if (_playButton != null) _playButton.interactable = first != null && CanPlayPart(first) && !_day!.Segments.Any(IsRecording);
            foreach (var pair in _partButtons) pair.Button.interactable = CanPlayPart(pair.Segment);
            var status = _isLoading ? "Loading recording..." : _status;
            if (string.IsNullOrWhiteSpace(status)) status = "Automatic recording / automatic saving    ·    F9 or Esc: close";
            if (!_canPlay && !_isLoading) status = "Recordings save automatically. Select a recording to manage it.";
            if (_index.IsTruncated) status = "Only part of this large archive is shown.  " + status;
            else if (_index.Warnings.Count > 0) status = _index.Warnings.Count + " archive notices  ·  " + status;
            if (_recordingFailure.Length > 0) status = _recordingFailure + "  ·  F8: retry recording";
            SetText(_statusText, status);
            if (_loadingOverlay != null)
            {
                _loadingOverlay.gameObject.SetActive(_isLoading);
                if (_loadingText != null) SetText(_loadingText,
                    (_loadStage.Length == 0 ? "Loading recording" : _loadStage) + "  " + Mathf.RoundToInt(_loadProgress * 100) + "%");
                if (_loadingFill != null) _loadingFill.anchorMax = new Vector2(_loadProgress, 1);
            }
        }

        private bool CanPlayPart(ArchiveSegment part) => _canPlay && !_isLoading && part.HeaderReadable && !IsRecording(part);
        private bool CanDeleteDays(IReadOnlyList<ArchiveDay>? days) => days != null &&
            days.Any(day => day.Segments.Count > 0) && !_isLoading && DeleteRequested != null &&
            days.All(day => day.Id != _protectedDayId && !day.Segments.Any(IsRecording));
        private void ConfirmDelete(ArchiveDay[] days, string label)
        {
            if (!CanDeleteDays(days)) return;
            _pendingDeleteDays = days;
            var recordingCount = days.Count(day => day.Segments.Count > 0);
            var fileCount = days.Sum(day => day.Segments.Count);
            var size = days.Sum(day => day.Bytes);
            var date = days.Length == 1 ? DateText(days[0].StartedUtc, "yyyy-MM-dd HH:mm") + "\n" : "";
            SetText(_deletePrompt, label + "\n" + date + recordingCount +
                (recordingCount == 1 ? " recording" : " recordings") + "   ·   " + fileCount +
                (fileCount == 1 ? " file" : " files") + "   ·   " + Bytes(size));
            if (_deleteOverlay != null) _deleteOverlay.gameObject.SetActive(true);
            UpdateControls();
        }
        private void DeleteConfirmed()
        {
            var days = _pendingDeleteDays;
            if (CanDeleteDays(days))
            {
                _isLoading = true;
                DeleteRequested?.Invoke(days!);
            }
            CancelDelete();
        }
        private void CancelDelete()
        {
            _pendingDeleteDays = null;
            if (_deleteOverlay != null) _deleteOverlay.gameObject.SetActive(false);
            UpdateControls();
        }
        public bool DismissDialog()
        {
            if (!HasDeleteConfirmation) return false;
            CancelDelete();
            return true;
        }
        private void PlayDay()
        {
            var first = _day?.Segments.FirstOrDefault();
            if (first != null && CanPlayPart(first) && !_day!.Segments.Any(IsRecording)) PlayRequested?.Invoke(first.FilePath);
        }

        private void SelectSession(string id)
        {
            CancelDelete();
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
            CancelDelete();
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

        private void RequestClose()
        {
            if (DismissDialog()) return;
            if (CloseRequested != null) CloseRequested();
            else Close();
        }
        private bool IsRecording(ArchiveSegment segment) => SamePath(segment.FilePath, _recordingPath);
        private static bool SamePath(string a, string b) => a.Length != 0 && b.Length != 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static void SetText(TMP_Text? text, string value) { if (text != null && text.text != value) text.text = value; }
        private static string RunLabel(ArchiveRun run) => !string.IsNullOrWhiteSpace(run.Label) &&
            (run.Id == "legacy" || run.Status == "recovered" || Path.GetFileName(run.DirectoryPath).StartsWith("Lobby-", StringComparison.OrdinalIgnoreCase))
                ? run.Label : DateText(run.StartedUtc, "MM-dd HH:mm");
        private static bool IsPreparing(ArchiveDay day) => string.Equals(day.Status, "preparing", StringComparison.OrdinalIgnoreCase);
        private static string SessionLabel(ArchiveSession session) => session.IsQuotaGroup ? "Quota " + Number(session.QuotaRemaining) : "Session " + session.SessionNumber;
        private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        private string DayLabel(ArchiveDay day)
        {
            if (_session?.IsQuotaGroup == true || day.DeadlineDaysRemaining.HasValue || day.QuotaRemaining.HasValue || day.QuotaCycle.HasValue)
                return "Day " + QuotaDay.Number(day.DeadlineDaysRemaining, day.DayNumber) + (IsPreparing(day) ? " · Lobby" : "");
            var generatedLabel = string.Equals(day.Label, "Day " + day.DayNumber, StringComparison.OrdinalIgnoreCase)
                || day.Label.StartsWith("Day-" + day.DayNumber.ToString("D3", CultureInfo.InvariantCulture) + "-", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(day.Label) && !generatedLabel && !day.Label.StartsWith("Day", StringComparison.OrdinalIgnoreCase) &&
                (day.Status == "recovered" || day.Id.StartsWith("recovered:", StringComparison.Ordinal))) return day.Label;
            return "Day " + QuotaDay.Number(null, day.DayNumber) + (IsPreparing(day) ? " · Lobby" : "");
        }
        private static string DayStatus(ArchiveDay day) => IsPreparing(day) ? "Lobby · No expedition" : day.Segments.Count == 0 ? "No recording files" : day.Segments.Any(segment => !segment.HeaderReadable) ? "Some files need attention" : day.Segments.All(segment => segment.Status == "complete") ? "Saved" : "Recoverable recording";
        private static string DateText(DateTimeOffset value, string format) => value == default ? "Unknown date" : value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        private static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "00:00";
            var span = TimeSpan.FromSeconds(Math.Min(seconds, 31536000));
            return span.TotalHours >= 1 ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
        private static string Bytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? (bytes / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : bytes >= 1024L * 1024 ? (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        public void Dispose() { if (_disposed) return; _disposed = true; Close(); }
    }
}
