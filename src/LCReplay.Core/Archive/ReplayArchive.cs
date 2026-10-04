using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace LCReplay.Core.Archive
{
    /// <summary>Append-only replay hierarchy. Manifests are replaceable indexes; replay files remain the source of truth.</summary>
    public sealed class ReplayArchive
    {
        private readonly object gate = new object();
        private readonly Dictionary<object, string> owned = new Dictionary<object, string>();
        private readonly HashSet<string> activeSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> activeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16, CheckAdditionalContent = true, Formatting = Formatting.Indented
        };
        public string RootDirectory { get; }

        public ReplayArchive(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("An archive directory is required.", nameof(rootDirectory));
            RootDirectory = Path.GetFullPath(rootDirectory);
        }

        public ArchiveRun BeginRun(DateTimeOffset now, string? lobbyName = null)
        {
            lock (gate)
            {
                string id = Guid.NewGuid().ToString("N");
                var room = Clip(lobbyName?.Trim(), 80);
                if (room.Length == 0) room = "Unknown lobby";
                var resumed = string.IsNullOrWhiteSpace(lobbyName) ? null : ResumeRun(room, now);
                if (resumed != null) return resumed;
                var run = new ArchiveRun
                {
                    Id = id, Label = room,
                    DirectoryPath = Path.Combine(RootDirectory, "Lobby-" + SafeFolderName(room) + "-" + ShortId(id)),
                    StartedUtc = now.ToUniversalTime(), LastModifiedUtc = now.ToUniversalTime()
                };
                CreateFolder(run, "run.json");
                return run;
            }
        }

        public ArchiveSession BeginSession(ArchiveRun run, DateTimeOffset now, string label, string perspective)
        {
            lock (gate)
            {
                RequireOwned(run);
                int number = run.Sessions.Count + 1;
                string id = Guid.NewGuid().ToString("N");
                var session = new ArchiveSession
                {
                    Id = id, RunId = run.Id, SessionNumber = number, Label = Clip(label, 512), Perspective = Clip(perspective, 128),
                    DirectoryPath = Path.Combine(run.DirectoryPath, "Session-" + number.ToString("D3", CultureInfo.InvariantCulture) + "-" + ShortId(id)),
                    StartedUtc = now.ToUniversalTime(), LastModifiedUtc = now.ToUniversalTime()
                };
                if (session.Label.Length == 0) session.Label = "Session " + number;
                CreateFolder(session, "session.json");
                run.Sessions.Add(session);
                return session;
            }
        }

        /// <summary>Groups new recordings by remaining quota, reusing that numeric folder within this launch.</summary>
        public ArchiveSession BeginQuota(ArchiveRun run, DateTimeOffset now, ArchiveQuotaSnapshot quota, string perspective)
        {
            if (quota == null) throw new ArgumentNullException(nameof(quota));
            lock (gate)
            {
                RequireOwned(run);
                var existing = run.Sessions.FirstOrDefault(session => session.IsQuotaGroup && session.QuotaRemaining == quota.Remaining);
                if (existing == null)
                {
                    var quotaPath = Path.Combine(run.DirectoryPath, NumericFolder(quota.Remaining));
                    var manifestPath = Path.Combine(quotaPath, "quota.json");
                    if (File.Exists(manifestPath))
                    {
                        EnsureSafePath(manifestPath);
                        try
                        {
                            var loaded = JsonConvert.DeserializeObject<ArchiveSession>(File.ReadAllText(manifestPath), JsonSettings);
                            if (loaded != null && loaded.IsQuotaGroup && loaded.QuotaRemaining == quota.Remaining &&
                                loaded.RunId == run.Id && Guid.TryParseExact(loaded.Id, "N", out _))
                            {
                                loaded.DirectoryPath = quotaPath;
                                run.Sessions.Add(loaded);
                                owned.Add(loaded, quotaPath);
                                existing = loaded;
                            }
                        }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
                        { /* A damaged old manifest must not overwrite its quota folder. */ }
                    }
                }
                if (existing != null)
                {
                    RequireOwned(existing);
                    existing.Status = "recording";
                    existing.LastModifiedUtc = now.ToUniversalTime();
                    existing.QuotaTarget = NonNegative(quota.Target);
                    existing.QuotaFulfilled = NonNegative(quota.Fulfilled);
                    existing.Perspective = Clip(perspective, 128);
                    WriteManifest(Path.Combine(existing.DirectoryPath, "quota.json"), existing);
                    activeFolders.Add(existing.DirectoryPath);
                    return existing;
                }
                var group = new ArchiveSession
                {
                    Id = Guid.NewGuid().ToString("N"), RunId = run.Id, SessionNumber = run.Sessions.Count + 1,
                    IsQuotaGroup = true, QuotaRemaining = quota.Remaining,
                    QuotaTarget = NonNegative(quota.Target), QuotaFulfilled = NonNegative(quota.Fulfilled),
                    Label = "Quota " + NumericFolder(quota.Remaining), Perspective = Clip(perspective, 128),
                    DirectoryPath = Path.Combine(run.DirectoryPath, NumericFolder(quota.Remaining)),
                    StartedUtc = now.ToUniversalTime(), LastModifiedUtc = now.ToUniversalTime()
                };
                CreateFolder(group, "quota.json");
                run.Sessions.Add(group);
                return group;
            }
        }

        /// <summary>Creates a collision-safe replay day directly below the quota folder.</summary>
        public ArchiveDay BeginQuotaDay(ArchiveSession group, int dayNumber, DateTimeOffset now, string moon,
            ArchiveQuotaSnapshot quota, int? campaignDay = null)
        {
            if (quota == null) throw new ArgumentNullException(nameof(quota));
            if (dayNumber < 1) throw new ArgumentOutOfRangeException(nameof(dayNumber));
            lock (gate)
            {
                RequireOwned(group);
                if (!group.IsQuotaGroup || group.QuotaRemaining != quota.Remaining)
                    throw new ArgumentException("The recording quota must match its allocated quota folder.", nameof(group));
                string id = Guid.NewGuid().ToString("N");
                int? deadline = NonNegative(quota.DeadlineDaysRemaining);
                var day = new ArchiveDay
                {
                    Id = id, RunId = group.RunId, SessionId = group.Id, DayNumber = dayNumber, CampaignDay = campaignDay,
                    QuotaRemaining = quota.Remaining, QuotaTarget = NonNegative(quota.Target), QuotaFulfilled = NonNegative(quota.Fulfilled),
                    DeadlineDaysRemaining = deadline, DeadlineDaysTotal = NonNegative(quota.DeadlineDaysTotal), QuotaCycle = NonNegative(quota.QuotaCycle),
                    Label = "Day " + QuotaDay.Number(deadline, dayNumber),
                    Moon = Clip(moon, 512),
                    DirectoryPath = group.DirectoryPath,
                    StartedUtc = now.ToUniversalTime(), LastModifiedUtc = now.ToUniversalTime()
                };
                var baseStem = NumericFolder(deadline);
                for (var suffix = 1; ; suffix++)
                {
                    day.RecordingStem = suffix == 1 ? baseStem : baseStem + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                    var file = Path.Combine(day.DirectoryPath, day.RecordingStem + ".lcr");
                    var manifest = DayManifestPath(day);
                    EnsureSafePath(file);
                    EnsureCompatiblePath(file);
                    if (File.Exists(file) || File.Exists(Path.ChangeExtension(file, ".json")) ||
                        File.Exists(Path.ChangeExtension(file, ".lci")) || File.Exists(manifest) ||
                        group.Days.Any(existing => string.Equals(existing.RecordingStem, day.RecordingStem, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    try { WriteManifest(manifest, day, false); }
                    catch (IOException) when (File.Exists(manifest)) { continue; }
                    break;
                }
                owned.Add(day, day.DirectoryPath);
                group.Days.Add(day);
                return day;
            }
        }

        public ArchiveDay BeginDay(ArchiveSession session, int dayNumber, DateTimeOffset now, string moon, int? campaignDay = null)
        {
            if (dayNumber < 1) throw new ArgumentOutOfRangeException(nameof(dayNumber));
            lock (gate)
            {
                RequireOwned(session);
                string id = Guid.NewGuid().ToString("N");
                var day = new ArchiveDay
                {
                    Id = id, RunId = session.RunId, SessionId = session.Id, DayNumber = dayNumber,
                    CampaignDay = campaignDay, Label = "Day " + dayNumber, Moon = Clip(moon, 512),
                    DirectoryPath = Path.Combine(session.DirectoryPath, "Day-" + dayNumber.ToString("D3", CultureInfo.InvariantCulture) + "-" + ShortId(id)),
                    StartedUtc = now.ToUniversalTime(), LastModifiedUtc = now.ToUniversalTime()
                };
                CreateFolder(day, "day.json");
                session.Days.Add(day);
                return day;
            }
        }

        public ArchiveSegment AllocateSegment(ArchiveDay day, int part)
        {
            if (part < 1) throw new ArgumentOutOfRangeException(nameof(part));
            lock (gate)
            {
                RequireOwned(day);
                EnsureSafePath(day.DirectoryPath);
                string file;
                if (day.RecordingStem.Length != 0)
                {
                    // The ordinary day has one file. If recording fails after
                    // writing a readable prefix, a numbered continuation keeps
                    // that prefix and its day identity instead of retrying the
                    // already occupied filename forever.
                    if (day.Segments.Count != 0)
                    {
                        var latest = day.Segments.Max(segment => segment.Part);
                        if (part <= latest)
                        {
                            if (latest == int.MaxValue) throw new IOException("No free replay continuation number remains.");
                            part = latest + 1;
                        }
                    }
                    while (true)
                    {
                        file = Path.Combine(day.DirectoryPath, part == 1 ? day.RecordingStem + ".lcr" :
                            day.RecordingStem + "-p" + part.ToString(CultureInfo.InvariantCulture) + ".lcr");
                        EnsureSafePath(file);
                        EnsureCompatiblePath(file);
                        if (!File.Exists(file) && !File.Exists(Path.ChangeExtension(file, ".json")) &&
                            !File.Exists(Path.ChangeExtension(file, ".lci"))) break;
                        if (part == 1) throw new IOException("The allocated quota replay file already exists.");
                        if (part == int.MaxValue) throw new IOException("No free replay continuation number remains.");
                        part++;
                    }
                }
                else
                {
                do
                {
                    file = Path.Combine(day.DirectoryPath, "part-" + part.ToString("D4", CultureInfo.InvariantCulture) + ".lcr");
                    if (!File.Exists(file) && !File.Exists(Path.ChangeExtension(file, ".json")) && !day.Segments.Any(s => s.Part == part)) break;
                    if (part == int.MaxValue) throw new IOException("No free replay segment number remains.");
                    part++;
                } while (true);
                }
                var now = DateTimeOffset.UtcNow;
                var segment = new ArchiveSegment
                {
                    Id = Guid.NewGuid().ToString("N"), RunId = day.RunId, SessionId = day.SessionId, DayId = day.Id,
                    FilePath = file, Part = part, StartedUtc = now, LastModifiedUtc = now,
                    Metadata = new Dictionary<string, string>
                    {
                        ["runId"] = day.RunId, ["sessionId"] = day.SessionId, ["dayId"] = day.Id,
                        ["dayNumber"] = day.DayNumber.ToString(CultureInfo.InvariantCulture),
                        ["part"] = part.ToString(CultureInfo.InvariantCulture), ["moon"] = day.Moon
                    }
                };
                var session = owned.Keys.OfType<ArchiveSession>().First(s => s.Id == day.SessionId);
                segment.Metadata["sessionLabel"] = session.Label;
                segment.Metadata["perspective"] = session.Perspective;
                if (day.CampaignDay.HasValue) segment.Metadata["campaignDay"] = day.CampaignDay.Value.ToString(CultureInfo.InvariantCulture);
                if (session.IsQuotaGroup) segment.Metadata["archiveLayout"] = "quota-deadline";
                AddMetadata(segment.Metadata, "quotaRemaining", day.QuotaRemaining);
                AddMetadata(segment.Metadata, "quotaTarget", day.QuotaTarget);
                AddMetadata(segment.Metadata, "quotaFulfilled", day.QuotaFulfilled);
                AddMetadata(segment.Metadata, "deadlineDaysRemaining", day.DeadlineDaysRemaining);
                AddMetadata(segment.Metadata, "deadlineDaysTotal", day.DeadlineDaysTotal);
                AddMetadata(segment.Metadata, "quotaCycle", day.QuotaCycle);
                WriteManifest(Path.ChangeExtension(file, ".json"), segment);
                owned.Add(segment, segment.FilePath);
                activeSegments.Add(file);
                day.Segments.Add(segment);
                return segment;
            }
        }

        public void CompleteSegment(ArchiveSegment segment, double duration, bool successful, string? error, int? bookmarkCount = null)
        {
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
            lock (gate)
            {
                RequireOwned(segment);
                EnsureSafePath(segment.FilePath);
                segment.DurationSeconds = duration;
                if (bookmarkCount.HasValue) segment.BookmarkCount = Math.Max(0, bookmarkCount.Value);
                segment.LastModifiedUtc = DateTimeOffset.UtcNow;
                segment.Bytes = File.Exists(segment.FilePath) ? new FileInfo(segment.FilePath).Length : 0;
                segment.Status = successful ? "complete" : "incomplete";
                segment.Error = Clip(error, 2048);
                activeSegments.Remove(segment.FilePath);
                WriteManifest(Path.ChangeExtension(segment.FilePath, ".json"), segment);
            }
        }

        public void UpdateBookmarkCount(ArchiveSegment segment, int count)
        {
            if (count < 0 || count > 1000000) throw new ArgumentOutOfRangeException(nameof(count));
            lock (gate)
            {
                RequireOwned(segment);
                EnsureSafePath(segment.FilePath);
                segment.BookmarkCount = count;
                WriteManifest(Path.ChangeExtension(segment.FilePath, ".json"), segment);
            }
        }

        public void UpdateDay(ArchiveDay day, string moon, string status)
        {
            lock (gate)
            {
                RequireOwned(day);
                day.Moon = Clip(moon, 512); day.Status = Clip(status, 64); day.LastModifiedUtc = DateTimeOffset.UtcNow;
                WriteManifest(DayManifestPath(day), day);
            }
        }

        public bool UpdateMembers(ArchiveDay day, IEnumerable<string> names)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            lock (gate)
            {
                RequireOwned(day);
                var merged = day.Members.Concat(names).Select(name => Clip(name?.Trim(), 64))
                    .Where(name => name.Length != 0 && !IsPlaceholderMember(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
                if (merged.Count == day.Members.Count && merged.SequenceEqual(day.Members)) return false;
                day.Members = merged;
                day.LastModifiedUtc = DateTimeOffset.UtcNow;
                WriteManifest(DayManifestPath(day), day);
                return true;
            }
        }

        public void EndDay(ArchiveDay day, string status)
        {
            lock (gate)
            {
                RequireOwned(day);
                day.Status = Clip(status, 64); day.LastModifiedUtc = DateTimeOffset.UtcNow;
                if (day.RecordingStem.Length == 0) activeFolders.Remove(day.DirectoryPath);
                WriteManifest(DayManifestPath(day), day);
            }
        }

        public void EndSession(ArchiveSession session)
        {
            lock (gate)
            {
                RequireOwned(session);
                session.Status = "complete"; session.LastModifiedUtc = DateTimeOffset.UtcNow;
                activeFolders.Remove(session.DirectoryPath);
                WriteManifest(Path.Combine(session.DirectoryPath, session.IsQuotaGroup ? "quota.json" : "session.json"), session);
            }
        }

        /// <summary>Marks a normal application shutdown after its session and writer have closed.</summary>
        public void EndRun(ArchiveRun run)
        {
            lock (gate)
            {
                RequireOwned(run);
                run.Status = "complete"; run.LastModifiedUtc = DateTimeOffset.UtcNow;
                activeFolders.Remove(run.DirectoryPath);
                WriteManifest(Path.Combine(run.DirectoryPath, "run.json"), run);
            }
        }

        /// <summary>Safe on a worker thread; indexes bounded headers and manifests, never world/frame payloads.</summary>
        public ArchiveIndex Scan(ArchiveScanLimits? limits = null)
        {
            HashSet<string> segments, folders;
            lock (gate)
            {
                segments = new HashSet<string>(activeSegments, StringComparer.OrdinalIgnoreCase);
                folders = new HashSet<string>(activeFolders, StringComparer.OrdinalIgnoreCase);
            }
            return new ArchiveScanner(RootDirectory, limits ?? new ArchiveScanLimits(), segments, folders).Scan();
        }

        /// <summary>Deletes one indexed recording and its own sidecars.</summary>
        public int DeleteRecording(ArchiveDay recording) => DeleteRecordings(new[] { recording });

        /// <summary>Deletes the recordings in a selected archive folder after validating every path.</summary>
        public int DeleteRecordings(IReadOnlyCollection<ArchiveDay> recordings)
        {
            if (recordings == null) throw new ArgumentNullException(nameof(recordings));
            if (recordings.Count == 0 || recordings.Count > 10000)
                throw new ArgumentException("Select at least one bounded recording to delete.", nameof(recordings));
            lock (gate)
            {
                var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = new List<string>();
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var recording in recordings)
                {
                    if (recording == null) throw new ArgumentException("A selected recording is null.", nameof(recordings));
                    var directory = Path.GetFullPath(recording.DirectoryPath);
                    EnsureSafePath(directory);
                    directories.Add(directory);
                    if (recording.Segments.Count == 0)
                    {
                        var emptyManifest = DayManifestPath(recording);
                        if (File.Exists(emptyManifest) && DayManifestMatches(emptyManifest, recording.Id)) files.Add(emptyManifest);
                    }
                    foreach (var segment in recording.Segments)
                    {
                        var file = Path.GetFullPath(segment.FilePath);
                        EnsureSafePath(file);
                        if (!string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(Path.GetExtension(file), ".lcr", StringComparison.OrdinalIgnoreCase) ||
                            !selected.Add(file))
                            throw new IOException("The selected recording contains an invalid or duplicate replay path.");
                        if (activeSegments.Contains(file)) throw new InvalidOperationException("The selected recording is still being saved.");
                        if (!File.Exists(file)) throw new FileNotFoundException("The selected recording changed; refresh the archive before deleting.", file);
                        files.Add(file);
                        files.Add(Path.ChangeExtension(file, ".lci"));
                        files.Add(Path.ChangeExtension(file, ".json"));
                        if (recording.RecordingStem.Length != 0)
                        {
                            var expectedStem = segment.Part == 1 ? recording.RecordingStem :
                                recording.RecordingStem + "-p" + segment.Part.ToString(CultureInfo.InvariantCulture);
                            if (!string.Equals(Path.GetFileNameWithoutExtension(file), expectedStem, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("The selected recording stem does not match its replay file.");
                            files.Add(Path.Combine(directory, recording.RecordingStem + ".day.json"));
                        }
                    }
                }
                if (selected.Count == 0) throw new InvalidOperationException("The selected archive item has no replay files to delete.");
                foreach (var recording in recordings.Where(day => day.RecordingStem.Length == 0))
                {
                    var directory = Path.GetFullPath(recording.DirectoryPath);
                    if (string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) continue;
                    var dayManifest = Path.Combine(directory, "day.json");
                    if (File.Exists(dayManifest) &&
                        Directory.EnumerateFiles(directory, "*.lcr", SearchOption.TopDirectoryOnly)
                            .All(file => selected.Contains(Path.GetFullPath(file))) &&
                        DayManifestMatches(dayManifest, recording.Id))
                        files.Add(dayManifest);
                }
                foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase)) EnsureSafePath(file);
                foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(file)) File.Delete(file);
                var cleanup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in directories)
                    for (string? current = directory; current != null &&
                        !string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase);
                        current = Path.GetDirectoryName(current))
                        cleanup.Add(current);
                // A failed or abandoned preparation may leave a metadata-only quota
                // sibling. Include bounded siblings so the lobby can disappear when
                // its final actual replay is deleted.
                foreach (var runDirectory in cleanup.Where(path => File.Exists(Path.Combine(path, "run.json"))).ToArray())
                    if (!activeFolders.Contains(runDirectory) && Directory.Exists(runDirectory))
                        foreach (var sibling in Directory.EnumerateDirectories(runDirectory).Take(4096))
                        {
                            try { EnsureSafePath(sibling); cleanup.Add(sibling); }
                            catch (IOException) { /* Never follow a sibling junction or unsafe path. */ }
                            catch (UnauthorizedAccessException) { /* Leave unrelated protected folders alone. */ }
                        }
                foreach (var directory in cleanup.OrderByDescending(path => path.Length))
                {
                    EnsureSafePath(directory);
                    if (!Directory.Exists(directory) || activeFolders.Contains(directory)) continue;
                    PruneEmptyQuotaMetadata(directory);
                    var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
                    if (entries.Length == 1 && new[] { "quota.json", "session.json", "run.json" }
                        .Contains(Path.GetFileName(entries[0]), StringComparer.OrdinalIgnoreCase))
                    { EnsureSafePath(entries[0]); File.Delete(entries[0]); entries = Array.Empty<string>(); }
                    if (entries.Length == 0) Directory.Delete(directory);
                }
                return selected.Count;
            }
        }

        private void PruneEmptyQuotaMetadata(string directory)
        {
            var quotaManifest = Path.Combine(directory, "quota.json");
            if (!File.Exists(quotaManifest) || Directory.EnumerateDirectories(directory).Any() ||
                Directory.EnumerateFiles(directory, "*.lcr", SearchOption.TopDirectoryOnly).Any() ||
                activeSegments.Any(file => string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase))) return;
            var files = Directory.EnumerateFiles(directory).ToArray();
            if (files.Any(file => !string.Equals(file, quotaManifest, StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".day.json", StringComparison.OrdinalIgnoreCase))) return;
            ArchiveSession? quota;
            try
            {
                if (new FileInfo(quotaManifest).Length > 128 * 1024) return;
                quota = JsonConvert.DeserializeObject<ArchiveSession>(File.ReadAllText(quotaManifest), JsonSettings);
                if (quota == null || string.IsNullOrEmpty(quota.Id) || string.IsNullOrEmpty(quota.RunId)) return;
                foreach (var file in files.Where(file => !string.Equals(file, quotaManifest, StringComparison.OrdinalIgnoreCase)))
                {
                    if (new FileInfo(file).Length > 128 * 1024) return;
                    var day = JsonConvert.DeserializeObject<ArchiveDay>(File.ReadAllText(file), JsonSettings);
                    if (day == null || day.SessionId != quota.Id || day.RunId != quota.RunId ||
                        !string.Equals(day.RecordingStem + ".day.json", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) return;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is JsonException || error is System.Security.SecurityException) { return; }
            foreach (var file in files.Where(file => !string.Equals(file, quotaManifest, StringComparison.OrdinalIgnoreCase)))
            { EnsureSafePath(file); File.Delete(file); }
        }

        private static bool DayManifestMatches(string file, string id)
        {
            try
            {
                if (new FileInfo(file).Length > 128 * 1024) return false;
                var day = JsonConvert.DeserializeObject<ArchiveDay>(File.ReadAllText(file), JsonSettings);
                return day != null && string.Equals(day.Id, id, StringComparison.Ordinal);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is JsonException || error is System.Security.SecurityException) { return false; }
        }

        private void CreateFolder(ArchiveFolder folder, string manifest)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                EnsureSafePath(folder.DirectoryPath);
                // Unity's Windows Mono can report DirectoryNotFoundException for paths over MAX_PATH.
                // Check before creating directories, including the longer temporary leaf used by atomic saves.
                EnsureCompatiblePath(Path.Combine(folder.DirectoryPath, "~000000000000.tmp"));
                if (Directory.Exists(folder.DirectoryPath) || File.Exists(folder.DirectoryPath))
                {
                    NewFolderIdentity(folder);
                    continue;
                }
                Directory.CreateDirectory(folder.DirectoryPath);
                string manifestPath = Path.Combine(folder.DirectoryPath, manifest);
                try
                {
                    // File.Move claims a new manifest without overwriting a concurrent directory allocation.
                    WriteManifest(manifestPath, folder, false);
                }
                catch (IOException) when (File.Exists(manifestPath))
                {
                    NewFolderIdentity(folder);
                    continue;
                }
                owned.Add(folder, folder.DirectoryPath);
                activeFolders.Add(folder.DirectoryPath);
                return;
            }
            throw new IOException("Could not allocate an unused archive folder.");
        }

        private ArchiveRun? ResumeRun(string room, DateTimeOffset now)
        {
            if (!Directory.Exists(RootDirectory)) return null;
            var prefix = "Lobby-" + SafeFolderName(room);
            ArchiveRun? best = null;
            foreach (var directory in Directory.EnumerateDirectories(RootDirectory, prefix + "*", SearchOption.TopDirectoryOnly).Take(256))
            {
                var leaf = Path.GetFileName(directory);
                if (!string.Equals(leaf, prefix, StringComparison.OrdinalIgnoreCase) &&
                    !leaf.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
                var manifest = Path.Combine(directory, "run.json");
                if (!File.Exists(manifest)) continue;
                EnsureSafePath(manifest);
                try
                {
                    var candidate = JsonConvert.DeserializeObject<ArchiveRun>(File.ReadAllText(manifest), JsonSettings);
                    if (candidate == null || !string.Equals(candidate.Label, room, StringComparison.OrdinalIgnoreCase) ||
                        !Guid.TryParseExact(candidate.Id, "N", out _)) continue;
                    candidate.DirectoryPath = directory;
                    if (best == null || candidate.LastModifiedUtc > best.LastModifiedUtc) best = candidate;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
                { /* Ignore an incomplete or unrelated folder. */ }
            }
            if (best == null) return null;
            best.Status = "recording";
            best.LastModifiedUtc = now.ToUniversalTime();
            owned.Add(best, best.DirectoryPath);
            activeFolders.Add(best.DirectoryPath);
            WriteManifest(Path.Combine(best.DirectoryPath, "run.json"), best);
            return best;
        }

        private static string ShortId(string id) => id.Substring(0, 12);
        private static string SafeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch) ? '_' : ch).ToArray())
                .Trim(' ', '.');
            if (cleaned.Length == 0) cleaned = "Unknown lobby";
            if (cleaned.Length > 48) cleaned = cleaned.Substring(0, 48).TrimEnd(' ', '.');
            // Windows device names are reserved even when the name is otherwise legal.
            var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
            return reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase) ? "Room_" + cleaned : cleaned;
        }
        private static string DayManifestPath(ArchiveDay day) => Path.Combine(day.DirectoryPath,
            day.RecordingStem.Length == 0 ? "day.json" : day.RecordingStem + ".day.json");
        private static int? NonNegative(int? value) => value.HasValue && value.Value >= 0 ? value : null;
        private static string NumericFolder(int? value) => NonNegative(value)?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        private static void AddMetadata(Dictionary<string, string> metadata, string key, int? value)
        { if (value.HasValue) metadata[key] = value.Value.ToString(CultureInfo.InvariantCulture); }

        private static void NewFolderIdentity(ArchiveFolder folder)
        {
            string name = Path.GetFileName(folder.DirectoryPath);
            folder.Id = Guid.NewGuid().ToString("N");
            folder.DirectoryPath = Path.Combine(Path.GetDirectoryName(folder.DirectoryPath)!,
                (name.LastIndexOf('-') < 0 ? name + "-" : name.Substring(0, name.LastIndexOf('-') + 1)) + ShortId(folder.Id));
        }

        private static void EnsureCompatiblePath(string path)
        {
            if (path.Length >= 260)
                throw new PathTooLongException("Replay archive path exceeds Unity Mono's Windows path limit. Choose a shorter ReplayDirectory: " + path);
        }

        private void RequireOwned(object item)
        {
            if (item == null || !owned.TryGetValue(item, out var originalPath)) throw new ArgumentException("Only archive entries created by this ReplayArchive can be changed.");
            string currentPath = item is ArchiveFolder folder ? folder.DirectoryPath : ((ArchiveSegment)item).FilePath;
            if (!string.Equals(originalPath, currentPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("An archive entry's allocated path cannot be changed.");
        }

        private void EnsureSafePath(string path)
        {
            string full = Path.GetFullPath(path);
            string root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Archive path escaped the recording root.");
            for (string? current = full; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Archive paths may not traverse symbolic links or junctions.");
                if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        private void WriteManifest(string path, object value, bool replaceExisting = true)
        {
            EnsureSafePath(path);
            EnsureCompatiblePath(path);
            string temporary = "";
            bool ownsTemporary = false;
            try
            {
                byte[] data = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value, JsonSettings));
                FileStream? output = null;
                for (int attempt = 0; attempt < 16 && output == null; attempt++)
                {
                    // A short sibling keeps atomic replacement on the same volume without appending 37 characters.
                    temporary = Path.Combine(Path.GetDirectoryName(path)!, "~" + ShortId(Guid.NewGuid().ToString("N")) + ".tmp");
                    EnsureCompatiblePath(temporary);
                    try { output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None); ownsTemporary = true; }
                    catch (IOException) when (File.Exists(temporary)) { }
                }
                if (output == null) throw new IOException("Could not allocate an unused archive manifest temporary file.");
                using (output)
                {
                    output.Write(data, 0, data.Length); output.Flush(true);
                }
                PublishManifest(temporary, path, replaceExisting);
            }
            finally
            {
                // A scanner or antivirus can briefly hold the temporary file too. Cleanup
                // must not turn a successful save into a failure or mask the original error.
                if (ownsTemporary)
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            }
        }

        private static void PublishManifest(string temporary, string path, bool replaceExisting)
        {
            if (!replaceExisting) { File.Move(temporary, path); return; }
            // Windows ReplaceFile can report ERROR_UNABLE_TO_REMOVE_REPLACED (1175)
            // while an indexer/antivirus holds a short-lived handle without delete sharing.
            // Keep the already-flushed sibling and retry atomically for at most 960 ms.
            // Never delete/truncate the destination as a fallback: the old index must
            // survive a persistent lock, denied access, disk failure or process crash.
            var delays = new[] { 10, 20, 40, 80, 160, 250, 400 };
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    return;
                }
                catch (IOException) when (attempt < delays.Length && File.Exists(temporary) && Directory.Exists(Path.GetDirectoryName(path)))
                {
                    Thread.Sleep(delays[attempt]);
                }
            }
        }

        internal static string Clip(string? value, int maximum) => value == null ? "" : value.Length <= maximum ? value : value.Substring(0, maximum);

        internal static bool IsPlaceholderMember(string name)
        {
            const string prefix = "Player #";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length) return false;
            for (var index = prefix.Length; index < name.Length; index++)
                if (name[index] < '0' || name[index] > '9') return false;
            return true;
        }
    }
}
