using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
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
    [BepInDependency(ReplayBookmarkInput.InputUtilsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ReplayPlugin : BaseUnityPlugin
    {
        public const string Guid = "pasta.replay";
        public const string Version = "0.25.41";
        private GUIStyle? overlayLabel;
        private GUIStyle? errorNoticeLabel;
        private ConfigEntry<bool> bones = null!, world = null!, chat = null!, disableInteriorCulling = null!, noShadow = null!, showDebugOverlay = null!, cinematicMove = null!, showFog = null!;
        private ConfigEntry<int> rate = null!, maxFields = null!, maxObjects = null!, maxVertices = null!;
        private ConfigEntry<float> replayResolution = null!, replayGamma = null!, cameraSpeed = null!;
        private ConfigEntry<string> folder = null!, extraTypes = null!;
        private ReplayBookmarkInput? bookmarkInput;
        private EventHooks? hooks;
        private Recorder? recorder;
        private ReplayViewer? viewer;
        private ReplayViewer? cachedViewer;
        private string? viewerPath, cachedViewerPath;
        private int viewerSceneHandle;
        private bool viewerConnected;
        private int cachedSceneHandle;
        private bool cachedConnected;
        private List<(string Path, long Length, DateTime ModifiedUtc)>? cachedFiles;
        private ReplayLibraryWindow? library;
        private ReplayMenuButton? menuButton;
        private ReplayPauseMenuButton? pauseMenuButton;
        private Harmony? inputHarmony;
        private static ReplayPlugin? activeInputGuard;
        private ReplayRuntime? runtime;
        private ReplayArchive? archive;
        private ArchiveIndex index = new ArchiveIndex();
        private ArchiveRun? run;
        private ArchiveSession? session;
        private ArchiveDay? day;
        private ArchiveSegment? segment;
        private IReadOnlyList<WorldSnapshot>? reusableWorld;
        private readonly List<Task<SaveResult>> pendingSaves = new List<Task<SaveResult>>();
        private readonly List<Task> pendingGroupSaves = new List<Task>();
        private DayLifecycle dayLifecycle = new DayLifecycle();
        private Component? sessionRound;
        private Component? deletedLobbyRound;
        private Task<ArchiveIndex>? scanning;
        private Task<LoadedRecording>? loading;
        private IEnumerator<float>? loadingPlayerAssets;
        private CancellationTokenSource? loadingCancellation;
        private bool rescanRequested, recordingBlocked, wasConnected, quitting;
        private bool checkpointRequested;
        private bool dayHadRecoveryGap;
        private int recordingRetryFailures;
        private float nextRecordingRetryAt;
        private bool loadCancelled;
        private bool closeLibraryAfterSceneCleanup;
        private bool shutdownCompleted;
        private bool inputReported, inputReadyReported;
        private Component? playbackPlayer;
        private bool savedMoveInput, savedLookInput;
        private LiveReplayInput? liveReplayInput;
        private int part;
        private float nextMemberScan;
        private string status = "Gameplay is recorded and saved automatically.";
        private string recordingFailure = "";
        private string errorNotice = "";
        private float errorNoticeUntil;
        private float errorNoticeStarted;
        private bool errorNoticePending;
        private string? loadingPath;
        private float loadingSpeed = 1f;
        private int loadingPercent;
        private string loadingStage = "";
        private string ReplayDirectory => Path.GetFullPath(Path.IsPathRooted(folder.Value) ? folder.Value : Path.Combine(Paths.BepInExRootPath, folder.Value));

        private void Awake()
        {
            // Keep existing profile settings when upgrading from the old plugin ID.
            var legacyConfigPath = Path.Combine(Paths.ConfigPath, "io.lcreplay.recorder.cfg");
            if (!File.Exists(Config.ConfigFilePath) && File.Exists(legacyConfigPath))
            {
                try
                {
                    File.Copy(legacyConfigPath, Config.ConfigFilePath, false);
                    Config.Reload();
                }
                catch (Exception exception)
                {
                    Logger.LogWarning($"Could not migrate legacy replay settings: {exception.Message}");
                }
            }
            activeInputGuard = this;
            rate = Config.Bind("Recording", "SampleRate", 10, new ConfigDescription("Automatic recording snapshots per second.", new AcceptableValueRange<int>(1, 60)));
            bones = Config.Bind("Recording", "CaptureBones", false,
                "Also record player and item bone poses on each sample. Enemy visual poses are always recorded after animation and procedural movement; other actors use installed Animator controllers when off.");
            world = Config.Bind("Recording", "CaptureWorld", true, "Save bounded geometry, textures and actor appearance for offline replay.");
            chat = Config.Bind("Recording", "CaptureChat", false, "Capture displayed text chat. Restart required.");
            maxFields = Config.Bind("Limits", "FieldsPerEntity", 192, new ConfigDescription("Primitive fields per component.", new AcceptableValueRange<int>(32, 512)));
            maxObjects = Config.Bind("Limits", "WorldObjects", 4000, new ConfigDescription("Render object budget per world snapshot.", new AcceptableValueRange<int>(100, 20000)));
            maxVertices = Config.Bind("Limits", "WorldVertices", 500000, new ConfigDescription("Unique mesh vertex budget per world snapshot; repeated room meshes share data.", new AcceptableValueRange<int>(1000, 1000000)));
            var captureSettingsVersion = Config.Bind("Compatibility", "CaptureSettingsVersion", 0, "Internal capture settings migration version.");
            if (captureSettingsVersion.Value < 4)
            {
                if (maxVertices.Value == 120000) maxVertices.Value = 500000;
                captureSettingsVersion.Value = 4;
            }
            if (captureSettingsVersion.Value < 5)
            {
                // Existing profiles were created with a true default. Apply the
                // lean recording profile once; users can opt back in afterward.
                bones.Value = false;
                captureSettingsVersion.Value = 5;
            }
            if (captureSettingsVersion.Value < 6)
            {
                captureSettingsVersion.Value = 6;
            }
            folder = Config.Bind("Storage", "ReplayDirectory", "replays", "Absolute folder, or relative to BepInEx. Restart after changing.");
            extraTypes = Config.Bind("Compatibility", "ExtraTrackedTypes", "", "Comma-separated full Unity Component names supplied by other mods.");
            replayResolution = Config.Bind("Playback", "ResolutionMultiplier", 1f,
                new ConfigDescription("Replay resolution relative to the HDLethalCompany vanilla baseline: 1 = 860x520. Independent of window size; UI stays sharp. Replaces RenderResolutionScale.",
                    new AcceptableValueRange<float>(ReplayGraphicsDefaults.MinResolutionMultiplier, ReplayGraphicsDefaults.MaxResolutionMultiplier)));
            var playbackSettingsVersion = Config.Bind("Compatibility", "PlaybackSettingsVersion", 0,
                "Internal playback defaults migration version.");
            replayGamma = Config.Bind("Playback", "Gamma", 1f,
                new ConfigDescription("Replay image gamma; 1.0 is neutral.", new AcceptableValueRange<float>(0.5f, 2f)));
            disableInteriorCulling = Config.Bind("Playback", "DisableInteriorCulling", false,
                "Show every recorded interior tile from both outside and inside. This can reduce playback performance.");
            noShadow = Config.Bind("Playback", "NoShadow", false,
                "Keep outside illumination inside the facility and disable replay light shadows.");
            showFog = Config.Bind("Playback", "ShowFog", true,
                "Render recorded HDRP fog, local fog and fog-named scenery in replay.");
            if (playbackSettingsVersion.Value < 3)
            {
                // Adopt the requested vanilla baseline once. Subsequent changes
                // in replay Settings survive reopening playback and restarting.
                replayResolution.Value = 1f;
                replayGamma.Value = 1f;
                disableInteriorCulling.Value = false;
                noShadow.Value = false;
                showFog.Value = true;
                playbackSettingsVersion.Value = 3;
                Config.Save();
            }
            cinematicMove = Config.Bind("Playback", "CinematicMove", false,
                "Use smooth acceleration and deceleration for the replay free camera. Press C in replay to toggle.");
            cameraSpeed = Config.Bind("Playback", "CameraSpeed", 8f,
                new ConfigDescription("Free camera speed in replay.", new AcceptableValueRange<float>(1f, 30f)));
            showDebugOverlay = Config.Bind("Debug", "ShowOverlay", false,
                "Show the recording status in the top-left corner during gameplay. Errors appear briefly in the top-right corner regardless of this setting.");
            var bookmarkKey = Config.Bind("Input", "BookmarkKey", KeyCode.BackQuote,
                "Recording bookmark key when InputUtils is absent. With InputUtils, change Add replay bookmark in the game's Controls menu.");
            bookmarkInput = new ReplayBookmarkInput(bookmarkKey, message => Logger.LogInfo(message));
            try
            {
                archive = new ReplayArchive(ReplayDirectory);
                Logger.LogInfo("Replay archive directory: " + archive.RootDirectory);
                library = new ReplayLibraryWindow();
                menuButton = new ReplayMenuButton(OpenLibrary, message => Logger.LogWarning(message), message => Logger.LogInfo(message));
                pauseMenuButton = new ReplayPauseMenuButton(OpenLibrary, message => Logger.LogWarning(message));
                InstallInputGuards();
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
                    else if (evt.Name == "SandSpiderAI.SpawnWebTrapClientRpc") recorder?.NoticeSpiderWeb();
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
                runtime = ReplayRuntime.Create(RunFrame, DrawUi, RunLateFrame,
                    () => Logger.LogInfo("Replay runtime active: independent persistent frame and UI driver."),
                    (stage, ex) =>
                    {
                        ShowErrorNotice("Replay runtime " + stage + " failed: " + ex.Message);
                        Logger.LogError("Replay runtime " + stage + " failed: " + ex);
                    });
                Logger.LogInfo("Replay runtime created after scene load: " + SceneManager.GetActiveScene().name + ".");
            }
            catch (Exception ex) { ReportRecordingFailure("Replay runtime initialization failed", ex); }
        }

        private void RunFrame()
        {
            if (quitting) return;
            NativePlayerAssetLoader.Tick();
            // A parked replay still owns a Unity scene. Keep it out of Netcode's
            // new-client scene list even when playback is no longer visible.
            if (viewer != null || cachedViewer != null || loading != null ||
                SceneAssetReplay.HasPendingLoads || ReplayViewer.HasPendingSceneCleanup || NativePlayerAssetLoader.RequiresIsolation)
                ReplayIsolation.EnsureNetworkExclusion();
            if (cachedViewer != null && (cachedConnected != GameAccess.Connected ||
                cachedSceneHandle != SceneManager.GetActiveScene().handle)) DiscardCachedViewer();
            if (closeLibraryAfterSceneCleanup && !SceneAssetReplay.HasPendingLoads &&
                !ReplayViewer.HasPendingSceneCleanup) CloseLibrary();
            var inputAvailable = ReplayInput.Available;
            if (!inputReported || (inputAvailable && !inputReadyReported))
            {
                Logger.LogInfo(inputAvailable ? "Replay keyboard input ready (F9 opens the archive)." : "Replay keyboard input is not ready; waiting for an Input System keyboard.");
                inputReported = true;
                inputReadyReported |= inputAvailable;
            }
            var connected = GameAccess.Connected;
            if (viewer != null && (viewerConnected != connected ||
                viewerSceneHandle != SceneManager.GetActiveScene().handle))
            {
                CloseViewer(false, false);
                status = "Replay closed after the live game changed scenes.";
                return;
            }
            if (!connected && wasConnected)
            {
                EndSession();
                recordingBlocked = false;
                recordingRetryFailures = 0;
                nextRecordingRetryAt = 0;
            }
            wasConnected = connected;
            CompleteSaves();
            ResumeRecordingWhenReady();
            CompleteScan();
            menuButton?.Tick(!connected && viewer == null && library?.IsOpen != true && loading == null);
            pauseMenuButton?.Tick(connected && viewer == null && library?.IsOpen != true && loading == null);
            if (viewer != null)
            {
                BlockLocalPlayerInput();
                if (viewer.CloseRequested || ReplayInput.WasPressed("Escape") || ReplayInput.WasPressed("F11"))
                { CloseViewer(true); return; }
                try { viewer.Tick(Time.unscaledDeltaTime); }
                catch (Exception ex) { Logger.LogError(ex); CloseViewer(true, false); status = "Playback error: " + ex.Message; ShowErrorNotice(status); }
                if (viewer?.Error != null)
                {
                    var error = viewer.Error;
                    Logger.LogError(error);
                    CloseViewer(true, false);
                    status = "A recording part could not be loaded: " + error.Message;
                    ShowErrorNotice(status);
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
                    nextRecordingRetryAt = 0;
                    recordingRetryFailures = 0;
                    if (recorder != null)
                    {
                        checkpointRequested = true;
                    }
                }
                catch (Exception ex) { RecoverRecording("Save checkpoint failed", ex); }
            }
            if (bookmarkInput?.WasPressed() == true) AddRecordingBookmark();
            if (ReplayInput.WasPressed("F11")) OpenLibrary();
        }

        private void RunLateFrame()
        {
            if (quitting) return;
            viewer?.RefreshCursor();
            // Enemy scripts finish procedural limbs and transformed forms in
            // LateUpdate. Sampling in Update instead records a partially
            // evaluated pose that the Animator can overwrite later that frame.
            if (hooks != null && archive != null && GameAccess.CanRecord)
            {
                try
                {
                    if (checkpointRequested)
                        recorder?.Event(new ReplayEvent { Category = "capture", Name = "manual-checkpoint" });
                    RecordAutomatically(viewer != null);
                }
                catch (Exception ex) { RecoverRecording("Automatic recording stopped", ex); }
            }
            checkpointRequested = false;
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
            if (deletedLobbyRound != null)
            {
                if (ReferenceEquals(round, deletedLobbyRound) && inShipPhase) return;
                deletedLobbyRound = null;
            }
            var quota = CurrentQuota();
            // TimeOfDay exists before the save is applied. Creating a quota
            // folder in that interval permanently labels the first day 0/2.
            if (session == null && (!quota.Target.HasValue || quota.Target.Value <= 0 ||
                !quota.DeadlineDaysRemaining.HasValue ||
                quota.DeadlineDaysRemaining.Value == 0 &&
                !(GameAccess.Read(GameAccess.Singleton("TimeOfDay"), "timeUntilDeadline") is float deadlineTime && deadlineTime > 0))) return;
            if (session == null)
            {
                var network = GameAccess.Singleton("GameNetworkManager");
                var lobbyName = (GameAccess.Read(network, "steamLobbyName") as string)?.Trim();
                if (string.IsNullOrWhiteSpace(lobbyName) && GameAccess.IsHost)
                    lobbyName = (GameAccess.Read(GameAccess.Read(network, "lobbyHostSettings"), "lobbyName") as string)?.Trim();
                if (string.IsNullOrWhiteSpace(lobbyName)) lobbyName = "Unknown lobby";
                if (run != null && !string.Equals(run.Label, lobbyName, StringComparison.Ordinal))
                { archive!.EndRun(run); run = null; }
                run = run ?? archive!.BeginRun(DateTimeOffset.UtcNow, lobbyName);
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
                archive!.EndDay(endedDay, recordingBlocked || dayHadRecoveryGap ? "interrupted" : "saved");
                dayHadRecoveryGap = false;
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
            if (Time.realtimeSinceStartup >= nextMemberScan)
            {
                nextMemberScan = Time.realtimeSinceStartup + 2f;
                if (archive!.UpdateMembers(day, CurrentMembers(round))) RefreshArchiveIfOpen();
            }
            var moon = CurrentMoon(round);
            if ((!dayLifecycle.HasReturned && moon != day.Moon) || transition != DayTransition.None)
                archive!.UpdateDay(day, dayLifecycle.HasReturned ? day.Moon : moon,
                    dayLifecycle.HasReturned ? "returned" : dayLifecycle.HasDeparted ? "exploring" : "preparing");
            if (recorder == null) StartSegment();
            if (recorder == null) return;
            if (transition != DayTransition.None)
                recorder.Event(new ReplayEvent { Category = "day", Name = transition.ToString(),
                    Data = new Dictionary<string, string> { ["day"] = dayLifecycle.DayNumber.ToString() } });
            recorder.Tick(checkpointRequested || transition == DayTransition.ReturnedToOrbit, playbackActive);
            if (recorder.Error != null)
            { var error = recorder.Error; RecoverRecording("Automatic saving stopped", error); return; }
            if (recorder.Duration >= 30) recordingRetryFailures = 0;
        }

        private void BeginDay(object? round, ArchiveQuotaSnapshot quota)
        {
            int? campaignDay = null;
            if (GameAccess.IsHost && GameAccess.Read(GameAccess.Read(round, "gameStats"), "daysSpent") is int days && days >= 0 && days < 1000000)
                campaignDay = days + 1;
            day = archive!.BeginQuotaDay(session!, dayLifecycle.DayNumber, DateTimeOffset.UtcNow, CurrentMoon(round), quota, campaignDay);
            nextMemberScan = 0f;
            dayHadRecoveryGap = false;
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
            if (remainingTime is float seconds && totalTime is float daySeconds)
                remainingDays = QuotaDay.Remaining(seconds, daySeconds, remainingDays);
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

        private static IEnumerable<string> CurrentMembers(object? round)
        {
            if (!(GameAccess.Read(round, "allPlayerScripts") is IEnumerable players)) yield break;
            foreach (var player in players)
            {
                if (player == null || !GameAccess.Bool(player, "isPlayerControlled")) continue;
                if (GameAccess.Read(player, "playerUsername") is string name && !string.IsNullOrWhiteSpace(name))
                    yield return name;
            }
        }

        private void StartSegment()
        {
            if (recorder != null || recordingBlocked || day == null || hooks == null) return;
            segment = archive!.AllocateSegment(day, ++part);
            if (day.Members.Count != 0) segment.Metadata["members"] = string.Join(", ", day.Members);
            part = segment.Part;
            try
            {
                recorder = new Recorder(day.DirectoryPath, rate.Value, bones.Value, world.Value, maxFields.Value, maxObjects.Value,
                    maxVertices.Value, extraTypes.Value, hooks, chat.Value, message => Logger.LogInfo(message), day.Id, part,
                    segment.FilePath, segment.Metadata, reusableWorld);
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
        private void AddRecordingBookmark()
        {
            if (recorder == null || segment == null || recorder.Error != null || viewer != null || loading != null || library?.IsOpen == true) return;
            var player = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController");
            if (GameAccess.Bool(player, "isTypingChat") || GameAccess.Bool(player, "inTerminalMenu") ||
                GameAccess.Find("QuickMenuManager").Any(menu => GameAccess.Bool(menu, "isMenuOpen"))) return;
            var before = recorder.BookmarkCount;
            recorder.Event(new ReplayEvent { Category = "marker", Name = "bookmark" });
            if (recorder.BookmarkCount == before) return;
            try { archive?.UpdateBookmarkCount(segment, recorder.BookmarkCount); }
            catch (Exception error) { Logger.LogWarning("Bookmark event was queued; count metadata update failed: " + error.Message); }
            status = "Bookmark " + recorder.BookmarkCount + " saved at " + TimeSpan.FromSeconds(recorder.Duration).ToString(@"mm\:ss") + ".";
            ShowBookmarkNotice(status);
            RefreshArchiveIfOpen();
        }

        private void StopSegment()
        {
            ReplayApi.EventSink = null;
            var stopped = recorder;
            var completed = segment;
            recorder = null; segment = null;
            library?.SetRecordingPath(null);
            if (stopped == null) return;
            reusableWorld = stopped.Error == null ? stopped.ReusableWorld : null;
            var flush = stopped.FinishAsync();
            var target = archive;
            pendingSaves.Add(Task.Run(async () =>
            {
                Exception? flushError = null;
                try { await flush.ConfigureAwait(false); }
                catch (Exception failure) { flushError = failure; }
                var error = stopped.Error ?? flushError;
                var savedDuration = stopped.RecordedDuration;
                var savedBookmarks = stopped.WrittenBookmarkCount;
                if (error != null)
                {
                    // FileStream can fail while flushing already buffered bytes.
                    // Archive only the prefix physically present after closing.
                    try
                    {
                        var recovered = ReplayReader.IndexSingleFile(stopped.FilePath, deferPayloadValidation: true);
                        savedDuration = recovered.Duration; savedBookmarks = recovered.Bookmarks.Count;
                    }
                    catch (Exception recoveryError) { Logger.LogWarning("Recording prefix metadata could not be refreshed; file is preserved: " + recoveryError.Message); }
                }
                try
                {
                    if (completed != null) target!.CompleteSegment(completed, savedDuration, error == null, error?.Message, savedBookmarks);
                }
                catch (Exception ex) { return new SaveResult(stopped.FilePath, savedDuration, ex, true); }
                return new SaveResult(stopped.FilePath, savedDuration, error, false);
            }));
            status = "Saving the completed replay in the background.";
        }
        private void CompleteSaves()
        {
            for (var i = pendingGroupSaves.Count - 1; i >= 0; i--)
            {
                var task = pendingGroupSaves[i];
                if (!task.IsCompleted) continue;
                pendingGroupSaves.RemoveAt(i);
                if (task.IsFaulted) ReportCompletedSaveFailure("Archive group save failed", task.Exception!.GetBaseException());
                RefreshArchiveIfOpen();
            }
            for (var i = pendingSaves.Count - 1; i >= 0; i--)
            {
                var task = pendingSaves[i];
                if (!task.IsCompleted) continue;
                pendingSaves.RemoveAt(i);
                if (task.IsFaulted) ReportCompletedSaveFailure("Recording file save failed", task.Exception!.GetBaseException());
                else
                {
                    var result = task.Result;
                    if (result.Error != null) ReportCompletedSaveFailure(result.MetadataError ?
                        "Archive metadata save failed (recording files are preserved)" : "Recording file save failed", result.Error);
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
            var closingDay = day;
            var closingSession = session;
            var target = archive;
            var closingStatus = recordingBlocked || dayHadRecoveryGap ? "interrupted" :
                dayLifecycle.HasDeparted ? "saved" : "preparing";
            var saves = pendingSaves.ToArray();
            // Atomic manifest publication may flush storage and retry sharing
            // violations. Never perform those waits on the disconnect frame.
            if (target != null && (closingDay != null || closingSession != null))
                pendingGroupSaves.Add(Task.Run(async () =>
                {
                    try { await Task.WhenAll(saves).ConfigureAwait(false); }
                    finally
                    {
                        if (closingDay != null) target.EndDay(closingDay, closingStatus);
                        if (closingSession != null) target.EndSession(closingSession);
                    }
                }));
            day = null; session = null; sessionRound = null; part = 0; dayHadRecoveryGap = false; dayLifecycle = new DayLifecycle();
            reusableWorld = null;
            RefreshArchiveIfOpen();
        }
        private void ReportRecordingFailure(string message, Exception error)
        {
            recordingBlocked = true;
            nextRecordingRetryAt = 0;
            recordingFailure = status = message + ": " + error.Message;
            ShowErrorNotice(status);
            Logger.LogError(message + ": " + error);
        }

        private void ReportCompletedSaveFailure(string message, Exception error)
        {
            if (IsDiskFull(error))
            {
                if (recorder != null)
                {
                    try { StopSegment(); }
                    catch (Exception finishError) { Logger.LogError("Recording cleanup also failed: " + finishError); }
                }
                ReportRecordingFailure(message, error);
                return;
            }
            // A prior segment or its replaceable manifest failed on the worker.
            // Keep the current capture and any scheduled retry alive.
            recordingFailure = status = message + ": " + error.Message + ". Automatic recording continues.";
            ShowErrorNotice(status);
            Logger.LogWarning(message + "; automatic recording continues: " + error);
        }

        private void RecoverRecording(string message, Exception error)
        {
            if (day != null) dayHadRecoveryGap = true;
            try { StopSegment(); }
            catch (Exception finishError)
            {
                Logger.LogError("Recording cleanup also failed: " + finishError);
                error = new AggregateException(error, finishError);
            }
            if (IsDiskFull(error)) { ReportRecordingFailure(message, error); return; }
            recordingBlocked = true;
            recordingRetryFailures = Math.Min(recordingRetryFailures + 1, 6);
            var delay = Math.Min(60f, (float)Math.Pow(2, recordingRetryFailures));
            nextRecordingRetryAt = Time.realtimeSinceStartup + delay;
            recordingFailure = status = message + ": " + error.Message + $". Retrying automatically in {delay:0} seconds.";
            ShowErrorNotice(status);
            Logger.LogWarning(message + "; replay prefix kept, automatic retry scheduled: " + error);
        }

        private void ResumeRecordingWhenReady()
        {
            if (!recordingBlocked || nextRecordingRetryAt <= 0 || Time.realtimeSinceStartup < nextRecordingRetryAt ||
                !GameAccess.CanRecord || hooks == null || archive == null) return;
            nextRecordingRetryAt = 0;
            recordingBlocked = false;
            recordingFailure = "";
            status = "Resuming automatic recording.";
            Logger.LogInfo(status);
        }

        private static bool IsDiskFull(Exception error)
        {
            if (error is AggregateException aggregate)
                return aggregate.InnerExceptions.Any(IsDiskFull);
            // Windows ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39)
            // and ERROR_DISK_QUOTA_EXCEEDED (1295).
            if (error is IOException)
            {
                var code = error.HResult & 0xffff;
                if (code == 112 || code == 39 || code == 1295) return true;
            }
            return error.InnerException != null && IsDiskFull(error.InnerException);
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
                Component? closedRound = null;
                if (run != null && day == null && recorder == null && pendingSaves.All(save => save.IsCompleted) && pendingGroupSaves.All(save => save.IsCompleted) &&
                    selected.Any(candidate => candidate.RunId == run.Id) &&
                    index.Runs.Where(candidate => candidate.Id == run.Id).SelectMany(candidate => candidate.Sessions)
                        .SelectMany(group => group.Days).Where(candidate => candidate.Segments.Count > 0)
                        .All(candidate => selected.Any(item => item.Id == candidate.Id)))
                {
                    closedRound = sessionRound;
                    EndSession();
                    archive.EndRun(run);
                    run = null;
                }
                var count = archive.DeleteRecordings(selected);
                DiscardCachedViewer();
                if (closedRound != null) deletedLobbyRound = closedRound;
                status = "Deleted " + count + (count == 1 ? " replay file." : " replay files.");
                Logger.LogInfo(status);
                RefreshArchive();
            }
            catch (Exception error)
            {
                status = "Could not delete the recording: " + error.Message;
                ShowErrorNotice(status);
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
            if (task.IsFaulted) { status = "Cannot read the archive: " + task.Exception?.GetBaseException().Message; ShowErrorNotice(status); Logger.LogWarning(status); }
            else
            {
                index = task.Result;
                library?.SetIndex(index);
                foreach (var warning in index.Warnings) Logger.LogWarning(warning);
            }
            if (rescanRequested) { rescanRequested = false; RefreshArchive(); }
        }
        private void OpenLibrary() => OpenLibrary(true);

        private void OpenLibrary(bool refresh)
        {
            if (library == null || viewer != null) return;
            library.Open();
            BlockLocalPlayerInput();
            library.SetRecordingPath(recorder?.FilePath);
            if (refresh) RefreshArchive();
        }
        private void CloseLibrary()
        {
            if (loading != null) { loadCancelled = true; loadingCancellation?.Cancel(); status = "Recording load cancelled."; }
            loadingPlayerAssets?.Dispose(); loadingPlayerAssets = null;
            // A menu replay may have loaded a moon scene. Do not expose Host or
            // Join while that scene or the parked viewer's scene is still in
            // Unity's loaded-scene list.
            DiscardCachedViewer();
            if (SceneAssetReplay.HasPendingLoads || ReplayViewer.HasPendingSceneCleanup)
            {
                closeLibraryAfterSceneCleanup = true;
                status = "Finishing replay scene cleanup...";
                return;
            }
            closeLibraryAfterSceneCleanup = false;
            library?.Close();
            if (viewer == null) RestoreLocalPlayerInput();
        }
        private void LoadReplay(string path, float speed = 1f)
        {
            if (loading != null || viewer != null || closeLibraryAfterSceneCleanup ||
                SceneAssetReplay.HasPendingLoads || ReplayViewer.HasPendingSceneCleanup) return;
            if (!GameAccess.NetworkStateKnown)
            { status = "Replay playback is unavailable until the game network is initialized."; return; }
            if (GameAccess.Connected && !ReplayIsolation.EnsureNetworkExclusion())
            { status = "Replay playback cannot be isolated from multiplayer scene synchronization."; ShowErrorNotice(status); return; }
            var fullPath = Path.GetFullPath(path);
            if (TryResumeCachedViewer(fullPath, speed)) return;
            DiscardCachedViewer();
            loadingPath = fullPath;
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
            // The native body assets and file decompression are independent.
            // Prepare them together so a fresh menu does not pay both waits
            // consecutively. Connected games already provide their own assets.
            if (!GameAccess.Connected && !PrefabAssetRegistry.ResolvePrefab("player"))
                loadingPlayerAssets = NativePlayerAssetLoader.Prepare().GetEnumerator();
            BlockLocalPlayerInput();
        }
        private void CompleteLoad(bool connected)
        {
            if (loadingPlayerAssets != null)
            {
                if (loadCancelled || loading == null || loading.IsCanceled || loading.IsFaulted)
                { loadingPlayerAssets.Dispose(); loadingPlayerAssets = null; }
                else
                {
                    try
                    {
                        if (loadingPlayerAssets.MoveNext())
                        {
                            if (loading.IsCompleted) Volatile.Write(ref loadingStage, "Loading player model");
                            return;
                        }
                    }
                    catch (Exception error)
                    {
                        // The viewer retains its normal asset lookup/error path.
                        Logger.LogWarning("Player model preparation failed: " + error.Message);
                    }
                    loadingPlayerAssets.Dispose(); loadingPlayerAssets = null;
                }
            }
            if (loading == null || !loading.IsCompleted) return;
            var task = loading; loading = null;
            if (loadCancelled || task.IsCanceled)
            {
                _ = task.Exception;
                loadingPath = null;
                status = "Recording load cancelled.";
                return;
            }
            if (task.IsFaulted) { status = "Recording file error: " + task.Exception?.GetBaseException().Message; ShowErrorNotice(status); OpenLibrary(); }
            else if (loadCancelled || !GameAccess.NetworkStateKnown)
            { status = "Playback cancelled."; }
            else
            {
                try
                {
                    // Loading is asynchronous; the player may have connected
                    // since LoadReplay began. Never create a replay scene in a
                    // live session unless Netcode can exclude it from joins.
                    if (GameAccess.Connected && !ReplayIsolation.EnsureNetworkExclusion())
                        throw new InvalidOperationException("Replay scene isolation is unavailable in this game version.");
                    library?.Close();
                    ReplayIsolation.PlaybackActive = true;
                    viewer = new ReplayViewer(task.Result.Session, task.Result.Timeline, task.Result.PartIndex,
                        replayResolution.Value, replayGamma.Value,
                        value => replayResolution.Value = value, value => replayGamma.Value = value,
                        disableInteriorCulling.Value, value => disableInteriorCulling.Value = value,
                        false, null,
                        cinematicMove.Value, cameraSpeed.Value,
                        value => cinematicMove.Value = value, value => cameraSpeed.Value = value,
                        showFog.Value, value => showFog.Value = value,
                        connected, noShadow.Value, value => noShadow.Value = value) { Speed = loadingSpeed };
                    viewerPath = loadingPath;
                    viewerSceneHandle = SceneManager.GetActiveScene().handle;
                    viewerConnected = GameAccess.Connected;
                    status = "Recording loaded.";
                    BlockLocalPlayerInput();
                    foreach (var warning in task.Result.Session.Warnings) Logger.LogWarning(warning);
                }
                catch (Exception ex) { Logger.LogError(ex); CloseViewer(true, false); status = "Cannot start playback: " + ex.Message; ShowErrorNotice(status); }
            }
            loadingPath = null;
        }
        private bool TryResumeCachedViewer(string path, float speed)
        {
            if (cachedViewer == null || cachedViewerPath == null || cachedFiles == null ||
                !string.Equals(cachedViewerPath, path, StringComparison.OrdinalIgnoreCase) ||
                cachedConnected != GameAccess.Connected || cachedSceneHandle != SceneManager.GetActiveScene().handle)
                return false;
            var current = CaptureFileStamps(cachedFiles.Select(file => file.Path));
            if (current == null || current.Count != cachedFiles.Count ||
                current.Where((file, index) => file != cachedFiles[index]).Any()) return false;
            var ready = cachedViewer;
            cachedViewer = null;
            cachedFiles = null;
            cachedViewerPath = null;
            cachedSceneHandle = 0;
            try
            {
                if (GameAccess.Connected && !ReplayIsolation.EnsureNetworkExclusion())
                    throw new InvalidOperationException("Replay scene isolation is unavailable in this game version.");
                library?.Close();
                ReplayIsolation.PlaybackActive = true;
                ready.Resume();
                ready.Speed = speed;
                viewer = ready;
                viewerPath = path;
                viewerSceneHandle = SceneManager.GetActiveScene().handle;
                viewerConnected = GameAccess.Connected;
                status = "Recording restored from memory.";
                BlockLocalPlayerInput();
                return true;
            }
            catch (Exception error)
            {
                Logger.LogWarning("Cached replay could not resume: " + error);
                ready.Dispose();
                ReplayIsolation.PlaybackActive = false;
                OpenLibrary();
                return false;
            }
        }

        private static List<(string Path, long Length, DateTime ModifiedUtc)>? CaptureFileStamps(IEnumerable<string> paths)
        {
            try
            {
                var result = new List<(string Path, long Length, DateTime ModifiedUtc)>();
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var file = new FileInfo(Path.GetFullPath(path));
                    file.Refresh();
                    if (!file.Exists) return null;
                    result.Add((file.FullName, file.Length, file.LastWriteTimeUtc));
                }
                return result.Count == 0 ? null : result;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is NotSupportedException) { return null; }
        }

        private void DiscardCachedViewer()
        {
            var old = cachedViewer;
            cachedViewer = null;
            cachedViewerPath = null;
            cachedSceneHandle = 0;
            cachedFiles = null;
            old?.Dispose();
        }

        private void CloseViewer(bool reopenLibrary, bool allowCache = true)
        {
            var closing = viewer;
            viewer = null;
            try
            {
                // A parked viewer keeps its Unity scene loaded. Even with
                // Netcode's synchronization filter, live game code can observe
                // that extra scene through SceneManager.sceneCount.
                if (closing != null && allowCache && !quitting && !GameAccess.Connected && closing.CanPark && viewerPath != null)
                {
                    var files = CaptureFileStamps(closing.SourceFiles);
                    if (files != null)
                    {
                        try
                        {
                            DiscardCachedViewer();
                            closing.Park();
                            cachedViewer = closing;
                            cachedViewerPath = viewerPath;
                            cachedFiles = files;
                            cachedConnected = GameAccess.Connected;
                            cachedSceneHandle = SceneManager.GetActiveScene().handle;
                            closing = null;
                        }
                        catch (Exception error) { Logger.LogWarning("Replay could not be cached: " + error); }
                    }
                }
                closing?.Dispose();
            }
            finally
            {
                viewerPath = null;
                viewerSceneHandle = 0;
                ReplayIsolation.PlaybackActive = false;
                // A failed scene/cache cleanup must still return live controls.
                RestoreLocalPlayerInput();
            }
            if (reopenLibrary && !quitting) OpenLibrary(cachedViewer == null);
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
            (liveReplayInput ??= new LiveReplayInput()).Block(player!);
        }

        private void RestoreLocalPlayerInput()
        {
            var restoring = liveReplayInput;
            liveReplayInput = null;
            try { restoring?.Dispose(); }
            finally
            {
                if (playbackPlayer)
                {
                    SetPlayerInputFlag(playbackPlayer!, "disableMoveInput", savedMoveInput);
                    SetPlayerInputFlag(playbackPlayer!, "disableLookInput", savedLookInput);
                }
                playbackPlayer = null;
            }
        }

        private void InstallInputGuards()
        {
            var playerType = GameAccess.Type("GameNetcodeStuff.PlayerControllerB");
            if (playerType == null) return;
            inputHarmony = new Harmony(Guid + ".input");
            var guard = new HarmonyMethod(typeof(ReplayPlugin), nameof(AllowPlayerAction));
            foreach (var method in AccessTools.GetDeclaredMethods(playerType))
                // Disable sends cancellation to release held item actions.
                // Blocking canceled callbacks leaves tools stuck after closing.
                if (method.Name.EndsWith("_performed", StringComparison.Ordinal))
                    inputHarmony.Patch(method, prefix: guard);
            var update = AccessTools.Method(playerType, "Update");
            if (update != null) inputHarmony.Patch(update, prefix: new HarmonyMethod(typeof(ReplayPlugin), nameof(ProtectPlayerUpdate)));
        }

        private static bool AllowPlayerAction(object __instance)
        {
            var owner = activeInputGuard;
            if (ReferenceEquals(owner, null) || owner.liveReplayInput == null)
                return true;
            var localPlayer = GameAccess.Read(GameAccess.Singleton("GameNetworkManager"), "localPlayerController");
            return !ReferenceEquals(__instance, localPlayer) &&
                !(__instance is Component component && component == owner.playbackPlayer);
        }

        private static void ProtectPlayerUpdate(object __instance)
        {
            var owner = activeInputGuard;
            if (ReferenceEquals(owner, null) || !owner.playbackPlayer || !ReferenceEquals(__instance, owner.playbackPlayer)) return;
            SetPlayerInputFlag(owner.playbackPlayer!, "disableMoveInput", true);
            SetPlayerInputFlag(owner.playbackPlayer!, "disableLookInput", true);
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
            catch (Exception ex) { status = "Cannot open the recording folder: " + ex.Message; ShowErrorNotice(status); Logger.LogWarning(status); }
        }
        private void DrawUi()
        {
            if (quitting) return;
            DrawErrorNotice();
            if (viewer != null) { viewer.DrawGui(); return; }
            if (!showDebugOverlay.Value) return;
            if (library?.IsOpen != true && recorder != null)
                DrawOverlay(new Rect(12, 12, 570, 32), $"AUTO REC / SAVING  |  Quota {day?.QuotaRemaining?.ToString() ?? "?"} / Deadline {day?.DeadlineDaysRemaining?.ToString() ?? "?"}  |  F9 Replays");
        }

        private void ShowErrorNotice(string message)
        {
            var detail = (message ?? "Unknown error").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (detail.Length > 200) detail = detail.Substring(0, 197) + "...";
            errorNotice = "LC Replay error\n" + detail;
            errorNoticePending = true;
        }

        private void ShowBookmarkNotice(string message)
        {
            // Use the same short top-right notification surface as errors.
            errorNotice = "LC Replay bookmark saved\n" + message;
            errorNoticePending = true;
        }

        private void DrawErrorNotice()
        {
            if (errorNotice.Length == 0) return;
            var width = Mathf.Min(640f, Screen.width - 24f);
            if (width <= 0) return;
            if (errorNoticePending)
            {
                errorNoticeStarted = Time.realtimeSinceStartup;
                errorNoticeUntil = errorNoticeStarted + 8f;
                errorNoticePending = false;
            }
            if (Time.realtimeSinceStartup >= errorNoticeUntil) return;
            if (errorNoticeLabel == null)
            {
                errorNoticeLabel = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                    wordWrap = true, clipping = TextClipping.Clip, padding = new RectOffset(12, 10, 5, 5)
                };
                errorNoticeLabel.normal.textColor = NativeReplayUi.White;
            }
            var now = Time.realtimeSinceStartup;
            var enter = Mathf.Clamp01((now - errorNoticeStarted) / .35f);
            var leave = Mathf.Clamp01((errorNoticeUntil - now) / .35f);
            var progress = Mathf.SmoothStep(0f, 1f, Mathf.Min(enter, leave));
            var x = Mathf.Lerp(Screen.width + 12f, Screen.width - width - 12f, progress);
            DrawOverlay(new Rect(x, 12f, width, 88f), errorNotice, errorNoticeLabel);
        }

        private void DrawOverlay(Rect rect, string message, GUIStyle? labelStyle = null)
        {
            var tint = GUI.color;
            GUI.color = NativeReplayUi.Orange;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = NativeReplayUi.Black;
            GUI.DrawTexture(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4), Texture2D.whiteTexture);
            GUI.color = tint;
            if (labelStyle == null && overlayLabel == null)
            {
                overlayLabel = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                    wordWrap = false, padding = new RectOffset(11, 8, 2, 2)
                };
                overlayLabel.normal.textColor = NativeReplayUi.White;
            }
            GUI.Label(rect, message, labelStyle ?? overlayLabel);
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
            loadingPlayerAssets?.Dispose(); loadingPlayerAssets = null;
            if (loading != null) _ = loading.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            SceneManager.sceneLoaded -= SceneLoaded;
            Application.quitting -= ApplicationQuitting;
            runtime?.Dispose(); runtime = null;
            EndSession();
            try { Task.WhenAll(pendingSaves.Cast<Task>().Concat(pendingGroupSaves)).GetAwaiter().GetResult(); CompleteSaves(); }
            catch (Exception ex) { Logger.LogError("Replay background save failed: " + ex); }
            try { if (run != null) archive?.EndRun(run); }
            catch (Exception ex) { Logger.LogError("Run metadata save failed: " + ex); }
            CloseViewer(false);
            DiscardCachedViewer();
            ReplayParticleAssets.ClearCachedTemplates();
            pauseMenuButton?.Dispose(); pauseMenuButton = null;
            menuButton?.Dispose(); menuButton = null;
            inputHarmony?.UnpatchSelf(); inputHarmony = null;
            if (ReferenceEquals(activeInputGuard, this)) activeInputGuard = null;
            library?.Dispose(); library = null;
            hooks?.Dispose(); hooks = null;
            bookmarkInput?.Dispose(); bookmarkInput = null;
        }
    }
}
