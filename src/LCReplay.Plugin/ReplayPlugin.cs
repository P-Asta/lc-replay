using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using LCReplay.Core;
using LCReplay.Core.Archive;
using LCReplay.Core.Lifecycle;
using LCReplay.Plugin.Capture;
using LCReplay.Plugin.Library;
using LCReplay.Plugin.Playback;
using LCReplay.Plugin.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin
{
    [BepInPlugin(Guid, "LC Replay", Version)]
    public sealed class ReplayPlugin : BaseUnityPlugin
    {
        public const string Guid = "io.lcreplay.recorder";
        public const string Version = "0.23.0";
        private GUIStyle? overlayLabel;
        private ConfigEntry<bool> bones = null!, world = null!, chat = null!, captureAudio = null!, captureVoiceChat = null!, disableInteriorCulling = null!, mutePlayerAudio = null!;
        private ConfigEntry<int> rate = null!, maxFields = null!, maxObjects = null!, maxVertices = null!;
        private ConfigEntry<float> replayResolution = null!, replayGamma = null!;
        private ConfigEntry<string> folder = null!, extraTypes = null!;
        private EventHooks? hooks;
        private Recorder? recorder;
        private ReplayViewer? viewer;
        private ReplayLibraryWindow? library;
        private ReplayMenuButton? menuButton;
        private ReplayRuntime? runtime;
        private ReplayArchive? archive;
        private ArchiveIndex index = new ArchiveIndex();
        private ArchiveRun? run;
        private ArchiveSession? session;
        private ArchiveDay? day;
        private ArchiveSegment? segment;
        private IReadOnlyList<WorldSnapshot>? reusableWorld;
        private readonly List<Task<SaveResult>> pendingSaves = new List<Task<SaveResult>>();
        private DayLifecycle dayLifecycle = new DayLifecycle();
        private readonly DateTimeOffset launchedAt = DateTimeOffset.Now;
        private Component? sessionRound;
        private Task<ArchiveIndex>? scanning;
        private Task<LoadedRecording>? loading;
        private CancellationTokenSource? loadingCancellation;
        private bool rescanRequested, recordingBlocked, wasConnected, quitting;
        private bool loadCancelled;
        private bool shutdownCompleted;
        private bool inputReported, inputReadyReported;
        private Component? playbackPlayer;
        private bool savedMoveInput, savedLookInput;
        private int part;
        private string status = "Gameplay is recorded and saved automatically.";
        private string recordingFailure = "";
        private string? loadingPath;
        private float loadingSpeed = 1f;
        private int loadingPercent;
        private string loadingStage = "";
        private string ReplayDirectory => Path.GetFullPath(Path.IsPathRooted(folder.Value) ? folder.Value : Path.Combine(Paths.BepInExRootPath, folder.Value));

        private void Awake()
        {
            rate = Config.Bind("Recording", "SampleRate", 10, new ConfigDescription("Automatic recording snapshots per second.", new AcceptableValueRange<int>(1, 60)));
            bones = Config.Bind("Recording", "CaptureBones", true, "Record up to 128 bone poses per actor.");
            world = Config.Bind("Recording", "CaptureWorld", true, "Save bounded geometry, textures and actor appearance for offline replay.");
            chat = Config.Bind("Recording", "CaptureChat", false, "Capture displayed text chat. Restart required.");
            captureAudio = Config.Bind("Recording", "CaptureAudio", true,
                "Record observed game sound effects, player footsteps and music. New recordings only.");
            captureVoiceChat = Config.Bind("Recording", "CaptureVoiceChat", false,
                "Also record player voice chat. This may store other players' conversations. New recordings only.");
            maxFields = Config.Bind("Limits", "FieldsPerEntity", 192, new ConfigDescription("Primitive fields per component.", new AcceptableValueRange<int>(32, 512)));
            maxObjects = Config.Bind("Limits", "WorldObjects", 4000, new ConfigDescription("Render object budget per world snapshot.", new AcceptableValueRange<int>(100, 20000)));
            maxVertices = Config.Bind("Limits", "WorldVertices", 500000, new ConfigDescription("Unique mesh vertex budget per world snapshot; repeated room meshes share data.", new AcceptableValueRange<int>(1000, 1000000)));
            var captureSettingsVersion = Config.Bind("Compatibility", "CaptureSettingsVersion", 0, "Internal capture settings migration version.");
            if (captureSettingsVersion.Value < 4)
            {
                if (maxVertices.Value == 120000) maxVertices.Value = 500000;
                captureSettingsVersion.Value = 4;
            }
            folder = Config.Bind("Storage", "ReplayDirectory", "replays", "Absolute folder, or relative to BepInEx. Restart after changing.");
            extraTypes = Config.Bind("Compatibility", "ExtraTrackedTypes", "", "Comma-separated full Unity Component names supplied by other mods.");
            replayResolution = Config.Bind("Playback", "RenderResolutionScale", 1f,
                new ConfigDescription("Replay scene resolution as a fraction of the window; UI remains sharp.", new AcceptableValueRange<float>(0.25f, 1f)));
            var playbackSettingsVersion = Config.Bind("Compatibility", "PlaybackSettingsVersion", 0,
                "Internal playback defaults migration version.");
            if (playbackSettingsVersion.Value < 2)
            {
                // Previous releases defaulted the whole replay image to half
                // resolution. Upgrade that exact default; preserve other choices.
                if (Math.Abs(replayResolution.Value - 0.5f) < 0.0001f) replayResolution.Value = 1f;
                playbackSettingsVersion.Value = 2;
            }
            replayGamma = Config.Bind("Playback", "Gamma", 1f,
                new ConfigDescription("Replay image gamma; 1.0 is neutral.", new AcceptableValueRange<float>(0.5f, 2f)));
            disableInteriorCulling = Config.Bind("Playback", "DisableInteriorCulling", false,
                "Show every recorded interior tile from both outside and inside. This can reduce playback performance.");
            mutePlayerAudio = Config.Bind("Playback", "MutePlayerAudio", false,
                "Mute all player-origin sounds in replay, including your own footsteps and equipment. New recordings carry source tags.");
            try
            {
                archive = new ReplayArchive(ReplayDirectory);
                Logger.LogInfo("Replay archive directory: " + archive.RootDirectory);
                library = new ReplayLibraryWindow();
                menuButton = new ReplayMenuButton(OpenLibrary, message => Logger.LogWarning(message), message => Logger.LogInfo(message));
                library.RefreshRequested += RefreshArchive;
                library.PlayRequested += path => LoadReplay(path);
                library.OpenFolderRequested += OpenFolder;
                library.DeleteRequested += DeleteRecording;
                library.CloseRequested += CloseLibrary;
                hooks = new EventHooks(evt =>
                {
                    recorder?.Event(evt);
                    if (evt.Name.Contains("OnShipLanded")) recorder?.RefreshEnvironmentAfterLanding();
                    else if (evt.Name.Contains("StartGame")) recorder?.MarkWorldDirty();
                }, chat.Value, message => Logger.LogWarning(message));
                Logger.LogInfo($"LC Replay {Version}: automatic quota/deadline archive; {hooks.Installed.Count} event hooks active.");
            }
            catch (Exception ex) { ReportRecordingFailure("Initialization failed", ex); }
            // Unity's first scene load can destroy even DontDestroyOnLoad objects made
            // during the BepInEx Application cctor. Managed event subscriptions survive.
            // Keep this managed coordinator alive and create its native driver afterwards.
            SceneManager.sceneLoaded += SceneLoaded;
            Application.quitting += ApplicationQuitting;
            Logger.LogInfo("Replay bootstrap ready; waiting for a loaded scene before creating the runtime.");
            if (SceneManager.GetActiveScene().isLoaded) EnsureRuntime();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) => EnsureRuntime();

        private void EnsureRuntime()
        {
            if (quitting || shutdownCompleted || runtime) return;
            try
            {
                runtime = ReplayRuntime.Create(RunFrame, DrawUi,
                    () => Logger.LogInfo("Replay runtime active: independent persistent frame and UI driver."),
                    (stage, ex) => Logger.LogError("Replay runtime " + stage + " failed: " + ex));
                Logger.LogInfo("Replay runtime created after scene load: " + SceneManager.GetActiveScene().name + ".");
            }
            catch (Exception ex) { ReportRecordingFailure("Replay runtime initialization failed", ex); }
        }

        private void RunFrame()
        {
            if (quitting) return;
            var inputAvailable = ReplayInput.Available;
            if (!inputReported || (inputAvailable && !inputReadyReported))
            {
                Logger.LogInfo(inputAvailable ? "Replay keyboard input ready (F9 opens the archive)." : "Replay keyboard input is not ready; waiting for an Input System keyboard.");
                inputReported = true;
                inputReadyReported |= inputAvailable;
            }
            var connected = GameAccess.Connected;
            if (!connected && wasConnected)
            {
                EndSession();
                recordingBlocked = false;
            }
            wasConnected = connected;
            CompleteSaves();
            CompleteScan();
            menuButton?.Tick(!connected && viewer == null && library?.IsOpen != true && loading == null);
            if (viewer != null)
            {
                if (hooks != null && archive != null && GameAccess.CanRecord)
                {
                    try { RecordAutomatically(true); }
                    catch (Exception ex) { StopSegment(); ReportRecordingFailure("Automatic recording stopped", ex); }
                }
                BlockLocalPlayerInput();
                if (viewer.CloseRequested || ReplayInput.WasPressed("Escape") || ReplayInput.WasPressed("F11"))
                { CloseViewer(true); return; }
                try { viewer.Tick(Time.unscaledDeltaTime); }
                catch (Exception ex) { Logger.LogError(ex); CloseViewer(true); status = "Playback error: " + ex.Message; }
                if (viewer?.Error != null)
                {
                    var error = viewer.Error;
                    Logger.LogError(error);
                    CloseViewer(true);
                    status = "A recording part could not be loaded: " + error.Message;
                }
                return;
            }
            CompleteLoad(connected);
            if (viewer != null) return;
            if (ReplayInput.WasPressed("F9"))
            {
                if (library?.IsOpen == true)
                {
                    if (!library.DismissDialog()) CloseLibrary();
                }
                else OpenLibrary();
            }
            if (library?.IsOpen == true)
            {
                library.Tick();
                BlockLocalPlayerInput();
                library.DrawGui(status, GameAccess.NetworkStateKnown,
                    loading != null || scanning != null, recordingFailure,
                    loading != null ? Volatile.Read(ref loadingPercent) / 100f : 0f,
                    loading != null ? Volatile.Read(ref loadingStage) : scanning != null ? "Scanning archive" : "",
                    day?.Id ?? "");
                if (ReplayInput.WasPressed("Escape") && !library.DismissDialog()) CloseLibrary();
            }
            if (ReplayInput.WasPressed("F8") && GameAccess.CanRecord && archive != null && hooks != null)
            {
                try
                {
                    if (recordingBlocked && day != null)
                    {
                        archive.EndDay(day, "interrupted");
                        day = null; segment = null; part = 0; reusableWorld = null;
                    }
                    recordingBlocked = false;
                    if (recorder != null)
                    {
                        recorder.Event(new ReplayEvent { Category = "capture", Name = "manual-checkpoint" });
                        recorder.Tick(true);
                    }
                }
                catch (Exception ex) { StopSegment(); ReportRecordingFailure("Save checkpoint failed", ex); }
            }
            if (ReplayInput.WasPressed("F10"))
            {
                recorder?.Event(new ReplayEvent { Category = "marker", Name = "bookmark" });
                status = recorder == null ? "No active recording." : "Bookmark saved automatically.";
            }
            if (ReplayInput.WasPressed("F11")) OpenLibrary();
            if (hooks != null && archive != null && GameAccess.CanRecord)
            {
                try { RecordAutomatically(); }
                catch (Exception ex) { StopSegment(); ReportRecordingFailure("Automatic recording stopped", ex); }
            }
        }

        private void RecordAutomatically(bool playbackActive = false)
        {
            if (recordingBlocked) return;
            var round = GameAccess.Singleton("StartOfRound");
            if (session != null && round is Component currentRound && currentRound != sessionRound)
                EndSession();
            if (recordingBlocked) return;
            if (!(GameAccess.Read(round, "inShipPhase") is bool inShipPhase))
                throw new InvalidOperationException("This game version does not expose round phase (inShipPhase).");
            var quota = CurrentQuota();
            // TimeOfDay exists before the save is applied. Creating a quota
            // folder in that interval permanently labels the first day 0/2.
            if (session == null && (!quota.Target.HasValue || quota.Target.Value <= 0 ||
                !quota.DeadlineDaysRemaining.HasValue ||
                quota.DeadlineDaysRemaining.Value == 0 &&
                !(GameAccess.Read(GameAccess.Singleton("TimeOfDay"), "timeUntilDeadline") is float deadlineTime && deadlineTime > 0))) return;
            if (session == null)
            {
                run = run ?? archive!.BeginRun(launchedAt);
                session = archive!.BeginQuota(run, DateTimeOffset.UtcNow, quota, GameAccess.IsHost ? "host-observed" : "client-observed");
                sessionRound = round as Component;
                dayLifecycle = new DayLifecycle();
            }
            var transition = dayLifecycle.Observe(inShipPhase);
            // The game's deadline display can update during an expedition. Do not
            // close the daily file when that value changes while entering a room.
            // A completed return to orbit is the only normal daily save boundary.
            if (day != null && transition == DayTransition.ReturnedToOrbit)
            {
                recorder?.Event(new ReplayEvent { Category = "day", Name = transition.ToString(),
                    Data = new Dictionary<string, string> { ["day"] = dayLifecycle.DayNumber.ToString() } });
                recorder?.Tick(true, playbackActive);
                StopSegment();
                var endedDay = day;
                day = null;
                part = 0;
                archive!.EndDay(endedDay, recordingBlocked ? "interrupted" : "saved");
                return;
            }
            // Keep the finished day closed while the crew remains in orbit.
            if (day == null && dayLifecycle.HasReturned) return;
            if (day == null && session.QuotaRemaining != quota.Remaining)
            {
                archive!.EndSession(session);
                session = archive.BeginQuota(run!, DateTimeOffset.UtcNow, quota, GameAccess.IsHost ? "host-observed" : "client-observed");
            }
            if (day == null) BeginDay(round, quota);
            if (day == null) return;
            var moon = CurrentMoon(round);
            if ((!dayLifecycle.HasReturned && moon != day.Moon) || transition != DayTransition.None)
                archive!.UpdateDay(day, dayLifecycle.HasReturned ? day.Moon : moon,
                    dayLifecycle.HasReturned ? "returned" : dayLifecycle.HasDeparted ? "exploring" : "preparing");
            if (recorder == null) StartSegment();
            if (recorder == null) return;
            if (transition != DayTransition.None)
                recorder.Event(new ReplayEvent { Category = "day", Name = transition.ToString(),
                    Data = new Dictionary<string, string> { ["day"] = dayLifecycle.DayNumber.ToString() } });
            recorder.Tick(transition == DayTransition.ReturnedToOrbit, playbackActive);
            if (recorder.Error != null)
            { var error = recorder.Error; StopSegment(); ReportRecordingFailure("Automatic saving stopped", error); return; }
        }

        private void BeginDay(object? round, ArchiveQuotaSnapshot quota)
        {
            int? campaignDay = null;
            if (GameAccess.IsHost && GameAccess.Read(GameAccess.Read(round, "gameStats"), "daysSpent") is int days && days >= 0 && days < 1000000)
                campaignDay = days + 1;
            day = archive!.BeginQuotaDay(session!, dayLifecycle.DayNumber, DateTimeOffset.UtcNow, CurrentMoon(round), quota, campaignDay);
            reusableWorld = null;
            archive.UpdateDay(day, day.Moon, dayLifecycle.HasReturned ? "returned" : dayLifecycle.HasDeparted ? "exploring" : "preparing");
            part = 0;
            RefreshArchiveIfOpen();
        }
        private static string CurrentMoon(object? round) => GameAccess.Scalar(GameAccess.Read(GameAccess.Read(round, "currentLevel"), "PlanetName")) ?? "Unknown moon";

        private static ArchiveQuotaSnapshot CurrentQuota()
        {
            var time = GameAccess.Singleton("TimeOfDay");
            int? remainingDays = NonnegativeInt(GameAccess.Read(time, "daysUntilDeadline"));
            var remainingTime = GameAccess.Read(time, "timeUntilDeadline");
            var totalTime = GameAccess.Read(time, "totalTime");
            if (remainingTime is float seconds && totalTime is float daySeconds &&
                seconds >= 0 && daySeconds > 0 && !float.IsNaN(seconds) && !float.IsInfinity(seconds) &&
                !float.IsNaN(daySeconds) && !float.IsInfinity(daySeconds))
                remainingDays = Math.Max(0, (int)Math.Ceiling(seconds / daySeconds));
            return new ArchiveQuotaSnapshot { Target = NonnegativeInt(GameAccess.Read(time, "profitQuota")),
                Fulfilled = NonnegativeInt(GameAccess.Read(time, "quotaFulfilled")),
                DeadlineDaysRemaining = remainingDays,
                DeadlineDaysTotal = NonnegativeInt(GameAccess.Read(GameAccess.Read(time, "quotaVariables"), "deadlineDaysAmount")),
                QuotaCycle = NonnegativeInt(GameAccess.Read(time, "timesFulfilledQuota")) };
        }

        private static int? NonnegativeInt(object? value)
        {
            if (value == null) return null;
            try { return Math.Max(0, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)); }
            catch { return null; }
        }

        private void StartSegment()
        {
            if (recorder != null || recordingBlocked || day == null || hooks == null) return;
            segment = archive!.AllocateSegment(day, ++part);
            part = segment.Part;
            try
            {
                recorder = new Recorder(day.DirectoryPath, rate.Value, bones.Value, world.Value, maxFields.Value, maxObjects.Value,
                    maxVertices.Value, extraTypes.Value, hooks, chat.Value, message => Logger.LogInfo(message), day.Id, part,
                    segment.FilePath, segment.Metadata, reusableWorld, captureAudio.Value, captureVoiceChat.Value);
                ReplayApi.EventSink = recorder.Event;
                recordingFailure = "";
                library?.SetRecordingPath(segment.FilePath);
                status = $"Quota {day.QuotaRemaining?.ToString() ?? "unknown"} / Deadline {day.DeadlineDaysRemaining?.ToString() ?? "unknown"} days - recording and saving automatically";
                Logger.LogInfo("Recording " + segment.FilePath);
                RefreshArchiveIfOpen();
            }
            catch (Exception ex)
            {
                try { archive.CompleteSegment(segment, 0, false, ex.Message); } catch (Exception metadataError) { Logger.LogWarning(metadataError); }
                segment = null;
                throw;
            }
        }
        private void StopSegment()
        {
            ReplayApi.EventSink = null;
            var stopped = recorder;
            var completed = segment;
            recorder = null; segment = null;
            library?.SetRecordingPath(null);
            if (stopped == null) return;
            reusableWorld = stopped.ReusableWorld;
            var flush = stopped.FinishAsync();
            var target = archive;
            pendingSaves.Add(Task.Run(async () =>
            {
                await flush.ConfigureAwait(false);
                var error = stopped.Error;
                try
                {
                    if (completed != null) target!.CompleteSegment(completed, stopped.RecordedDuration, error == null, error?.Message);
                }
                catch (Exception ex) { return new SaveResult(stopped.FilePath, stopped.RecordedDuration, ex, true); }
                return new SaveResult(stopped.FilePath, stopped.RecordedDuration, error, false);
            }));
            status = "Saving the completed replay in the background.";
        }
        private void CompleteSaves()
        {
            for (var i = pendingSaves.Count - 1; i >= 0; i--)
            {
                var task = pendingSaves[i];
                if (!task.IsCompleted) continue;
                pendingSaves.RemoveAt(i);
                if (task.IsFaulted) ReportRecordingFailure("Recording file save failed", task.Exception!.GetBaseException());
                else
                {
                    var result = task.Result;
                    if (result.Error != null) ReportRecordingFailure(result.MetadataError ? "Archive metadata save failed (recording files are preserved)" : "Recording file save failed", result.Error);
                    else
                    {
                        Logger.LogInfo($"Saved replay: {result.Path} ({result.Duration:F1} seconds).");
                        if (!recordingBlocked) status = "Recording saved automatically.";
                    }
                }
                RefreshArchiveIfOpen();
            }
        }
        private sealed class SaveResult
        {
            internal readonly string Path;
            internal readonly double Duration;
            internal readonly Exception? Error;
            internal readonly bool MetadataError;
            internal SaveResult(string path, double duration, Exception? error, bool metadataError)
            { Path = path; Duration = duration; Error = error; MetadataError = metadataError; }
        }
        private void EndSession()
        {
            StopSegment();
            try
            {
                if (day != null) archive?.EndDay(day, recordingBlocked ? "interrupted" : dayLifecycle.HasDeparted ? "saved" : "preparing");
                if (session != null) archive?.EndSession(session);
            }
            catch (Exception ex) { ReportRecordingFailure("Archive group save failed", ex); }
            finally { day = null; session = null; sessionRound = null; part = 0; dayLifecycle = new DayLifecycle(); }
            reusableWorld = null;
            RefreshArchiveIfOpen();
        }
        private void ReportRecordingFailure(string message, Exception error)
        {
            recordingBlocked = true;
            recordingFailure = status = message + ": " + error.Message;
            Logger.LogError(message + ": " + error);
        }

        private void RefreshArchiveIfOpen() { if (library?.IsOpen == true) RefreshArchive(); }
        private void DeleteRecording(IReadOnlyList<ArchiveDay> selected)
        {
            if (archive == null || library?.IsOpen != true || scanning != null || loading != null || viewer != null) return;
            var current = new HashSet<ArchiveDay>(index.Runs.SelectMany(run => run.Sessions).SelectMany(group => group.Days));
            if (selected.Count == 0 || selected.Any(candidate => !current.Contains(candidate)))
            { status = "The archive changed. Refresh and select the recording again."; RefreshArchive(); return; }
            if (day != null && selected.Any(candidate => day.Id == candidate.Id && day.RunId == candidate.RunId))
            { status = "The current day's recording cannot be deleted while it is active."; return; }
            try
            {
                var count = archive.DeleteRecordings(selected);
                status = "Deleted " + count + (count == 1 ? " replay file." : " replay files.");
                Logger.LogInfo(status);
                RefreshArchive();
            }
            catch (Exception error)
            {
                status = "Could not delete the recording: " + error.Message;
                Logger.LogWarning(status);
                RefreshArchive();
            }
        }
        private void RefreshArchive()
        {
            if (archive == null || quitting) return;
            if (scanning != null) { rescanRequested = true; return; }
            var source = archive;
            scanning = Task.Run(() => source.Scan());
        }
        private void CompleteScan()
        {
            if (scanning == null || !scanning.IsCompleted) return;
            var task = scanning; scanning = null;
            if (task.IsFaulted) { status = "Cannot read the archive: " + task.Exception?.GetBaseException().Message; Logger.LogWarning(status); }
            else
            {
                index = task.Result;
                library?.SetIndex(index);
                foreach (var warning in index.Warnings) Logger.LogWarning(warning);
            }
            if (rescanRequested) { rescanRequested = false; RefreshArchive(); }
        }
        private void OpenLibrary()
        {
            if (library == null || viewer != null) return;
            library.Open();
            BlockLocalPlayerInput();
            library.SetRecordingPath(recorder?.FilePath);
            RefreshArchive();
        }
        private void CloseLibrary()
        {
            if (loading != null) { loadCancelled = true; loadingCancellation?.Cancel(); status = "Recording load cancelled."; }
            library?.Close();
            if (viewer == null) RestoreLocalPlayerInput();
        }
        private void LoadReplay(string path, float speed = 1f)
        {
            if (loading != null || viewer != null) return;
            if (!GameAccess.NetworkStateKnown)
            { status = "Replay playback is unavailable until the game network is initialized."; return; }
            loadingPath = Path.GetFullPath(path);
            loadingSpeed = speed;
            loadCancelled = false;
            status = "Loading recording...";
            Volatile.Write(ref loadingPercent, 0);
            Volatile.Write(ref loadingStage, "Reading header");
            loadingCancellation?.Dispose();
            loadingCancellation = new CancellationTokenSource();
            var cancellation = loadingCancellation.Token;
            var sourceIndex = index;
            loading = Task.Run(() => LoadedRecording.Read(sourceIndex, path, cancellation, (stage, fraction) =>
            {
                Volatile.Write(ref loadingStage, stage);
                Volatile.Write(ref loadingPercent, Math.Max(0, Math.Min(99, (int)Math.Round(fraction * 100))));
            }), cancellation);
        }
        private void CompleteLoad(bool connected)
        {
            if (loading == null || !loading.IsCompleted) return;
            var task = loading; loading = null;
            if (loadCancelled || task.IsCanceled)
            {
                _ = task.Exception;
                loadingPath = null;
                status = "Recording load cancelled.";
                return;
            }
            if (task.IsFaulted) { status = "Recording file error: " + task.Exception?.GetBaseException().Message; OpenLibrary(); }
            else if (loadCancelled || !GameAccess.NetworkStateKnown)
            { status = "Playback cancelled."; }
            else
            {
                try
                {
                    library?.Close();
                    ReplayIsolation.PlaybackActive = true;
                    viewer = new ReplayViewer(task.Result.Session, task.Result.Timeline, task.Result.PartIndex,
                        replayResolution.Value, replayGamma.Value,
                        value => replayResolution.Value = value, value => replayGamma.Value = value,
                        disableInteriorCulling.Value, value => disableInteriorCulling.Value = value,
                        mutePlayerAudio.Value, value => mutePlayerAudio.Value = value) { Speed = loadingSpeed };
                    status = "Recording loaded.";
                    BlockLocalPlayerInput();
                    foreach (var warning in task.Result.Session.Warnings) Logger.LogWarning(warning);
                }
                catch (Exception ex) { Logger.LogError(ex); CloseViewer(true); status = "Cannot start playback: " + ex.Message; }
            }
            loadingPath = null;
        }
        private void CloseViewer(bool reopenLibrary)
        {
            try { viewer?.Dispose(); }
            finally { viewer = null; ReplayIsolation.PlaybackActive = false; }
            if (reopenLibrary && !quitting) OpenLibrary();
            else RestoreLocalPlayerInput();
        }

        private void BlockLocalPlayerInput()
        {
            var player = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController") as Component;
            if (!player) return;
            if (playbackPlayer != player)
            {
                RestoreLocalPlayerInput();
                playbackPlayer = player;
                savedMoveInput = GameAccess.Bool(player, "disableMoveInput");
                savedLookInput = GameAccess.Bool(player, "disableLookInput");
            }
            SetPlayerInputFlag(player!, "disableMoveInput", true);
            SetPlayerInputFlag(player!, "disableLookInput", true);
        }

        private void RestoreLocalPlayerInput()
        {
            if (playbackPlayer)
            {
                SetPlayerInputFlag(playbackPlayer!, "disableMoveInput", savedMoveInput);
                SetPlayerInputFlag(playbackPlayer!, "disableLookInput", savedLookInput);
            }
            playbackPlayer = null;
        }

        private static void SetPlayerInputFlag(Component player, string name, bool value)
        {
            try { player.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(player, value); }
            catch { /* Other game versions may not expose this optional input flag. */ }
        }
        private void OpenFolder(string path)
        {
            try
            {
                var target = Path.GetFullPath(path);
                var root = ReplayDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only folders inside the replay archive can be opened.");
                if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The recording folder does not exist.");
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch (Exception ex) { status = "Cannot open the recording folder: " + ex.Message; Logger.LogWarning(status); }
        }
        private void DrawUi()
        {
            if (quitting) return;
            if (viewer != null) { viewer.DrawGui(); return; }
            if (library?.IsOpen != true)
            {
                if (recordingFailure.Length > 0)
                    DrawOverlay(new Rect(12, 12, Math.Min(900, Screen.width - 24), 54), "LC Replay: " + recordingFailure +
                        (hooks == null || archive == null ? "\nRestart the game after resolving the initialization error." : "\nF9: details / F8: retry"));
                else if (recorder != null)
                    DrawOverlay(new Rect(12, 12, 570, 32), $"AUTO REC / SAVING  |  Quota {day?.QuotaRemaining?.ToString() ?? "?"} / Deadline {day?.DeadlineDaysRemaining?.ToString() ?? "?"}  |  F9 Replays");
            }
        }

        private void DrawOverlay(Rect rect, string message)
        {
            var tint = GUI.color;
            GUI.color = NativeReplayUi.Orange;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = NativeReplayUi.Black;
            GUI.DrawTexture(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4), Texture2D.whiteTexture);
            GUI.color = tint;
            if (overlayLabel == null)
            {
                overlayLabel = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                    wordWrap = false, padding = new RectOffset(11, 8, 2, 2)
                };
                overlayLabel.normal.textColor = NativeReplayUi.White;
            }
            GUI.Label(rect, message, overlayLabel);
        }
        private void ApplicationQuitting() { quitting = true; Shutdown(); }
        private void OnApplicationQuit() => ApplicationQuitting();
        private void OnDestroy()
        {
            if (quitting) Shutdown();
            else Logger.LogInfo("Replay bootstrap object retired during scene initialization; managed scene hooks remain active.");
        }
        private void Shutdown()
        {
            if (shutdownCompleted) return;
            shutdownCompleted = true;
            loadingCancellation?.Cancel();
            loadingCancellation?.Dispose();
            loadingCancellation = null;
            if (loading != null) _ = loading.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            SceneManager.sceneLoaded -= SceneLoaded;
            Application.quitting -= ApplicationQuitting;
            runtime?.Dispose(); runtime = null;
            EndSession();
            try { Task.WhenAll(pendingSaves).GetAwaiter().GetResult(); CompleteSaves(); }
            catch (Exception ex) { Logger.LogError("Replay background save failed: " + ex); }
            try { if (run != null) archive?.EndRun(run); }
            catch (Exception ex) { Logger.LogError("Run metadata save failed: " + ex); }
            CloseViewer(false);
            menuButton?.Dispose(); menuButton = null;
            library?.Dispose(); library = null;
            hooks?.Dispose(); hooks = null;
        }
    }
}
