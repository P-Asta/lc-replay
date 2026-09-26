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

        public ArchiveRun BeginRun(DateTimeOffset now)
        {
            lock (gate)
            {
                string id = Guid.NewGuid().ToString("N");
                var run = new ArchiveRun
                {
                    Id = id, Label = now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    DirectoryPath = Path.Combine(RootDirectory, "Run-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + ShortId(id)),
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

        /// <summary>Creates one collision-safe replay file directly below the quota folder.</summary>
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
                    Label = deadline.HasValue ? "Deadline " + deadline.Value.ToString(CultureInfo.InvariantCulture) + " days" : "Deadline unknown",
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
                    if (part != 1 || day.Segments.Count != 0)
                        throw new InvalidOperationException("A quota day has exactly one .lcr file.");
                    file = Path.Combine(day.DirectoryPath, day.RecordingStem + ".lcr");
                    if (File.Exists(file) || File.Exists(Path.ChangeExtension(file, ".json")))
                        throw new IOException("The allocated quota replay file already exists.");
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

        public void CompleteSegment(ArchiveSegment segment, double duration, bool successful, string? error)
        {
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
            lock (gate)
            {
                RequireOwned(segment);
                EnsureSafePath(segment.FilePath);
                segment.DurationSeconds = duration;
                segment.LastModifiedUtc = DateTimeOffset.UtcNow;
                segment.Bytes = File.Exists(segment.FilePath) ? new FileInfo(segment.FilePath).Length : 0;
                segment.Status = successful ? "complete" : "incomplete";
                segment.Error = Clip(error, 2048);
                activeSegments.Remove(segment.FilePath);
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

        private static string ShortId(string id) => id.Substring(0, 12);
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
    }
}
