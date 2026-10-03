using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LCReplay.Core.Archive
{
    internal sealed class ArchiveScanner
    {
        private readonly string root;
        private readonly ArchiveScanLimits limits;
        private readonly HashSet<string> activeSegments, activeFolders;
        private readonly ArchiveIndex result = new ArchiveIndex();
        private int directories = 1, replayFiles, entries, headerReads;

        internal ArchiveScanner(string root, ArchiveScanLimits limits, HashSet<string> activeSegments, HashSet<string> activeFolders)
        {
            if (limits.MaxDirectories < 1 || limits.MaxReplayFiles < 1 || limits.MaxEntries < 1 || limits.MaxWarnings < 1 ||
                limits.MaxHeaderReads < 1 || limits.MaxHeaderBytes < 256 || limits.MaxHeaderBytes > 16 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(limits));
            this.root = root; this.limits = limits; this.activeSegments = activeSegments; this.activeFolders = activeFolders;
        }

        internal ArchiveIndex Scan()
        {
            if (!Directory.Exists(root)) return result;
            if (!Allowed(root)) return result;
            var top = ReadDirectory(root);
            foreach (string directory in top.Directories)
            {
                var run = ReadManifest<ArchiveRun>(Path.Combine(directory, "run.json")) ?? new ArchiveRun { Status = "recovered" };
                FillFolder(run, directory);
                var runEntries = ReadDirectory(directory);
                foreach (string sessionPath in runEntries.Directories)
                {
                    var session = ReadManifest<ArchiveSession>(Path.Combine(sessionPath, "quota.json"))
                        ?? ReadManifest<ArchiveSession>(Path.Combine(sessionPath, "session.json")) ?? new ArchiveSession { Status = "recovered" };
                    FillFolder(session, sessionPath); session.RunId = run.Id;
                    if (session.SessionNumber < 1) session.SessionNumber = Number(Path.GetFileName(sessionPath), "Session-");
                    var folderName = Path.GetFileName(sessionPath);
                    if (session.IsQuotaGroup || NumericDirectory(folderName, out var folderQuota))
                    {
                        session.IsQuotaGroup = true;
                        if (!session.QuotaRemaining.HasValue) session.QuotaRemaining = ParseNumericDirectory(folderName);
                        session.Label = "Quota " + (session.QuotaRemaining?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
                    }
                    var sessionEntries = ReadDirectory(sessionPath);
                    foreach (string dayPath in sessionEntries.Directories)
                    {
                        if (session.IsQuotaGroup)
                        {
                            var deadlineEntries = ReadDirectory(dayPath);
                            int? deadline = ParseNumericDirectory(Path.GetFileName(dayPath));
                            foreach (var clipPath in deadlineEntries.Directories) ReadDay(run, session, clipPath, deadline);
                            // An interrupted/external migration may leave clips directly
                            // in the deadline folder. They stay visible without being moved.
                            if (deadlineEntries.Files.Count > 0) ReadDay(run, session, dayPath, deadline, deadlineEntries);
                        }
                        else ReadDay(run, session, dayPath, null);
                    }
                    if (session.IsQuotaGroup)
                        foreach (var file in sessionEntries.Files) ReadFlatQuotaDay(run, session, file);
                    else AddLooseFiles(session, sessionEntries.Files);
                    FinalizeSession(session);
                    run.Sessions.Add(session);
                }
                if (runEntries.Files.Count > 0) run.Sessions.Add(LooseSession(run.Id, directory, runEntries.Files));
                FinalizeRun(run);
                result.Runs.Add(run);
            }
            if (top.Files.Count > 0)
            {
                var legacy = new ArchiveRun { Id = "legacy", DirectoryPath = root, Label = "Legacy recordings", Status = "recovered" };
                legacy.Sessions.Add(LooseSession(legacy.Id, root, top.Files));
                FinalizeRun(legacy);
                result.Runs.Add(legacy);
            }
            result.Runs = result.Runs.OrderBy(r => r.StartedUtc).ThenBy(r => r.DirectoryPath, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private void ReadDay(ArchiveRun run, ArchiveSession session, string path, int? deadline, DirectoryEntries? knownEntries = null)
        {
            var day = ReadManifest<ArchiveDay>(Path.Combine(path, "day.json")) ?? new ArchiveDay { Status = "recovered" };
            FillFolder(day, path); day.RunId = run.Id; day.SessionId = session.Id;
            if (day.DayNumber < 1) day.DayNumber = Number(Path.GetFileName(path), "Day-");
            if (session.IsQuotaGroup)
            {
                day.QuotaRemaining = day.QuotaRemaining ?? session.QuotaRemaining;
                day.DeadlineDaysRemaining = day.DeadlineDaysRemaining ?? deadline;
            }
            var entries = knownEntries ?? ReadDirectory(path);
            if (knownEntries == null && entries.Directories.Count > 0) Warn("Archive scan skipped folders below a recording clip: " + path);
            foreach (var file in entries.Files) day.Segments.Add(ReadSegment(file, day));
            if (session.IsQuotaGroup) day.Label = "Deadline " + (day.DeadlineDaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + (day.DeadlineDaysRemaining.HasValue ? " days" : "");
            FinalizeDay(day);
            session.Days.Add(day);
        }

        private void ReadFlatQuotaDay(ArchiveRun run, ArchiveSession session, string file)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var day = ReadManifest<ArchiveDay>(Path.Combine(session.DirectoryPath, stem + ".day.json"))
                ?? new ArchiveDay { Id = "recovered:" + file, Status = "recovered" };
            day.DirectoryPath = session.DirectoryPath;
            day.RecordingStem = stem;
            day.RunId = run.Id;
            day.SessionId = session.Id;
            day.QuotaRemaining = day.QuotaRemaining ?? session.QuotaRemaining;
            day.DeadlineDaysRemaining = day.DeadlineDaysRemaining ?? ParseNumericDirectory(stem.Split('-')[0]);
            day.Segments.Add(ReadSegment(file, day));
            if (day.Status == "recording" && !activeSegments.Contains(file)) day.Status = "interrupted";
            if (day.DayNumber < 1) day.DayNumber = 1;
            day.Label = "Deadline " + (day.DeadlineDaysRemaining?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                + (day.DeadlineDaysRemaining.HasValue ? " days" : "");
            FinalizeDay(day);
            session.Days.Add(day);
        }

        private ArchiveSession LooseSession(string runId, string directory, List<string> files)
        {
            var session = new ArchiveSession
            {
                Id = "recovered:" + directory, RunId = runId, DirectoryPath = directory,
                Label = "Recovered recordings", Status = "recovered", SessionNumber = 1
            };
            AddLooseFiles(session, files);
            FinalizeSession(session);
            return session;
        }

        private void AddLooseFiles(ArchiveSession session, List<string> files)
        {
            // Only explicit legacy header identities authorize chaining. Names can end in arbitrary digits.
            var groups = new Dictionary<string, ArchiveDay>(StringComparer.Ordinal);
            foreach (string file in files)
            {
                var day = new ArchiveDay
                {
                    Id = "recovered:" + file, RunId = session.RunId, SessionId = session.Id, DirectoryPath = Path.GetDirectoryName(file)!,
                    Label = Path.GetFileNameWithoutExtension(file), DayNumber = 1, Status = "recovered"
                };
                var segment = ReadSegment(file, day);
                if (segment.HeaderReadable && segment.Metadata.TryGetValue("recordingGroup", out var group) &&
                    !string.IsNullOrWhiteSpace(group) && group.Length <= 128 && group.All(c => char.IsLetterOrDigit(c) || c == '-') &&
                    segment.Metadata.TryGetValue("part", out var partText) &&
                    int.TryParse(partText, NumberStyles.None, CultureInfo.InvariantCulture, out int part) && part > 0)
                {
                    if (groups.TryGetValue(group, out var existing))
                    {
                        if (!existing.Segments.Any(s => s.Part == part))
                        {
                            segment.DayId = existing.Id;
                            existing.Segments.Add(segment);
                            continue;
                        }
                        Warn("Duplicate legacy recording group/part remains separate: " + file);
                    }
                    else
                    {
                        day.Id = "recovered-group:" + day.DirectoryPath + ":" + group;
                        day.Label = "Recording " + group;
                        segment.DayId = day.Id;
                        groups.Add(group, day);
                    }
                }
                day.Segments.Add(segment);
                session.Days.Add(day);
            }
            foreach (var day in session.Days) FinalizeDay(day);
        }

        private ArchiveSegment ReadSegment(string file, ArchiveDay day)
        {
            var segment = ReadManifest<ArchiveSegment>(Path.ChangeExtension(file, ".json"));
            bool recovered = segment == null;
            segment = segment ?? new ArchiveSegment { Status = "recovered", HeaderReadable = false };
            segment.FilePath = Path.GetFullPath(file);
            segment.Id = string.IsNullOrWhiteSpace(segment.Id) ? file : ReplayArchive.Clip(segment.Id, 2048);
            segment.RunId = day.RunId; segment.SessionId = day.SessionId; segment.DayId = day.Id;
            if (segment.Part < 1) segment.Part = Number(Path.GetFileNameWithoutExtension(file), "part-");
            segment.Error = ReplayArchive.Clip(segment.Error, 2048);
            try
            {
                var info = new FileInfo(file);
                segment.Bytes = info.Length;
                segment.LastModifiedUtc = info.LastWriteTimeUtc;
                if (segment.StartedUtc == default) segment.StartedUtc = info.CreationTimeUtc;
                if (recovered)
                {
                    if (++headerReads > limits.MaxHeaderReads)
                    {
                        segment.Status = "unindexed";
                        Limit("Header recovery limit reached; additional recordings are listed without header metadata.");
                    }
                    else
                    {
                        ReplayHeader header = ReplayReader.ReadHeader(file, limits.MaxHeaderBytes);
                        segment.HeaderReadable = true;
                        segment.Metadata = header.Metadata;
                        if (DateTimeOffset.TryParse(header.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started)) segment.StartedUtc = started.ToUniversalTime();
                        if (header.Metadata.TryGetValue("part", out var part) && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) segment.Part = number;
                        if (day.DayNumber < 1 && header.Metadata.TryGetValue("dayNumber", out var dayValue) && int.TryParse(dayValue, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0) day.DayNumber = number;
                        if (string.IsNullOrEmpty(day.Moon) && header.Metadata.TryGetValue("moon", out var moon)) day.Moon = ReplayArchive.Clip(moon, 512);
                        if (!day.CampaignDay.HasValue && header.Metadata.TryGetValue("campaignDay", out var campaign) && int.TryParse(campaign, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) day.CampaignDay = number;
                        day.QuotaRemaining = day.QuotaRemaining ?? MetadataNumber(header, "quotaRemaining");
                        day.QuotaTarget = day.QuotaTarget ?? MetadataNumber(header, "quotaTarget");
                        day.QuotaFulfilled = day.QuotaFulfilled ?? MetadataNumber(header, "quotaFulfilled");
                        day.DeadlineDaysRemaining = day.DeadlineDaysRemaining ?? MetadataNumber(header, "deadlineDaysRemaining");
                        day.DeadlineDaysTotal = day.DeadlineDaysTotal ?? MetadataNumber(header, "deadlineDaysTotal");
                        day.QuotaCycle = day.QuotaCycle ?? MetadataNumber(header, "quotaCycle");
                    }
                }
            }
            catch (Exception e) when (Recoverable(e))
            {
                segment.HeaderReadable = false; segment.Status = "unreadable"; segment.Error = ReplayArchive.Clip(e.Message, 2048);
                Warn("Could not index replay header " + file + ": " + e.Message);
            }
            if (!Finite(segment.DurationSeconds) || segment.DurationSeconds < 0) { segment.DurationSeconds = 0; Warn("Invalid duration in manifest: " + file); }
            if (segment.Status == "recording" && !activeSegments.Contains(file)) segment.Status = "interrupted";
            if (activeSegments.Contains(file)) segment.Status = "recording";
            if (segment.Part < 1) segment.Part = 1;
            if (segment.BookmarkCount < 0 || segment.BookmarkCount > 1000000)
            { segment.BookmarkCount = 0; Warn("Invalid bookmark count in manifest: " + file); }
            return segment;
        }

        private T? ReadManifest<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                if (!Allowed(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
                {
                    const int maximum = 128 * 1024;
                    if (stream.Length > maximum) throw new InvalidDataException("Manifest exceeds index read limit.");
                    var buffer = new char[maximum + 1];
                    int count = 0, read;
                    while (count < buffer.Length && (read = reader.Read(buffer, count, buffer.Length - count)) > 0) count += read;
                    if (count > maximum) throw new InvalidDataException("Manifest exceeds index read limit.");
                    T value = JsonConvert.DeserializeObject<T>(new string(buffer, 0, count), ReplayArchive.JsonSettings)
                        ?? throw new InvalidDataException("Manifest contains null.");
                    if (value is ArchiveFolder folder && (string.IsNullOrWhiteSpace(folder.Id) || folder.StartedUtc == default))
                        throw new InvalidDataException("Manifest has no archive identity or start time.");
                    if (value is ArchiveSession session && (session.SessionNumber < 1 || string.IsNullOrWhiteSpace(session.RunId)))
                        throw new InvalidDataException("Session manifest has invalid ancestry or sequence.");
                    if (value is ArchiveDay day && (day.DayNumber < 1 || string.IsNullOrWhiteSpace(day.SessionId) || string.IsNullOrWhiteSpace(day.RunId)))
                        throw new InvalidDataException("Day manifest has invalid ancestry or sequence.");
                    if (value is ArchiveSegment segment && (string.IsNullOrWhiteSpace(segment.Id) || segment.Part < 1 ||
                        string.IsNullOrWhiteSpace(segment.RunId) || string.IsNullOrWhiteSpace(segment.SessionId) || string.IsNullOrWhiteSpace(segment.DayId)))
                        throw new InvalidDataException("Segment manifest has invalid identity, ancestry or sequence.");
                    return value;
                }
            }
            catch (Exception e) when (Recoverable(e)) { Warn("Invalid archive manifest " + path + ": " + e.Message); return null; }
        }

        private DirectoryEntries ReadDirectory(string path)
        {
            var found = new DirectoryEntries();
            try
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(path))
                {
                    if (++entries > limits.MaxEntries) { Limit("Archive entry limit reached."); break; }
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(item); }
                    catch (Exception e) when (Recoverable(e)) { Warn("Could not inspect " + item + ": " + e.Message); continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { Warn("Skipped symbolic link or junction: " + item); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (++directories > limits.MaxDirectories) { Limit("Archive directory limit reached."); continue; }
                        found.Directories.Add(Path.GetFullPath(item));
                    }
                    else if (string.Equals(Path.GetExtension(item), ".lcr", StringComparison.OrdinalIgnoreCase))
                    {
                        if (++replayFiles > limits.MaxReplayFiles) { Limit("Replay file limit reached."); continue; }
                        found.Files.Add(Path.GetFullPath(item));
                    }
                }
            }
            catch (Exception e) when (Recoverable(e)) { Warn("Could not read archive folder " + path + ": " + e.Message); }
            found.Directories.Sort(StringComparer.OrdinalIgnoreCase);
            found.Files.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        private bool Allowed(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return true;
                Warn("Skipped symbolic link or junction: " + path);
            }
            catch (Exception e) when (Recoverable(e)) { Warn("Could not inspect " + path + ": " + e.Message); }
            return false;
        }

        private void FillFolder(ArchiveFolder folder, string directory)
        {
            folder.DirectoryPath = Path.GetFullPath(directory);
            folder.Id = string.IsNullOrWhiteSpace(folder.Id) ? directory : ReplayArchive.Clip(folder.Id, 2048);
            folder.Label = string.IsNullOrWhiteSpace(folder.Label) ? Path.GetFileName(directory) : ReplayArchive.Clip(folder.Label, 512);
            folder.Status = ReplayArchive.Clip(folder.Status, 64);
            if (folder.Status == "recording" && !activeFolders.Contains(directory)) folder.Status = "interrupted";
            try
            {
                var info = new DirectoryInfo(directory);
                folder.LastModifiedUtc = info.LastWriteTimeUtc;
                if (folder.StartedUtc == default) folder.StartedUtc = info.CreationTimeUtc;
            }
            catch (Exception e) when (Recoverable(e)) { Warn("Could not read folder dates: " + directory); }
        }

        private void FinalizeDay(ArchiveDay day)
        {
            day.Segments = day.Segments.OrderBy(s => s.Part).ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            day.DurationSeconds = day.Segments.Sum(s => s.DurationSeconds);
            if (day.Segments.Count > 0)
            {
                try
                {
                    // Rotation can pause while the writer drains. Match the playback
                    // clock, including those gaps, using only already-indexed manifests.
                    day.DurationSeconds = new ReplayRecordingTimeline(day.Segments.Select(segment => new ReplayRecordingPart
                    {
                        FilePath = segment.FilePath, StartedUtc = segment.StartedUtc, Duration = segment.DurationSeconds
                    })).Duration;
                }
                catch (ArgumentException error)
                {
                    // Malformed timestamps must not hide otherwise recoverable files.
                    Warn("Recording duration uses the sum of its parts because timeline metadata is invalid: " + day.DirectoryPath + ": " + error.Message);
                }
            }
            day.Bytes = day.Segments.Sum(s => s.Bytes);
            if (day.Segments.Count > 0)
            {
                if (day.StartedUtc == default) day.StartedUtc = day.Segments.Min(s => s.StartedUtc);
                day.LastModifiedUtc = Later(day.LastModifiedUtc, day.Segments.Max(s => s.LastModifiedUtc));
            }
            if (day.DayNumber < 1) day.DayNumber = 1;
            day.Moon = ReplayArchive.Clip(day.Moon, 512);
            day.Members = (day.Members ?? new List<string>()).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => ReplayArchive.Clip(name.Trim(), 64)).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
            day.QuotaRemaining = NonNegative(day.QuotaRemaining);
            day.QuotaTarget = NonNegative(day.QuotaTarget);
            day.QuotaFulfilled = NonNegative(day.QuotaFulfilled);
            day.DeadlineDaysRemaining = NonNegative(day.DeadlineDaysRemaining);
            day.DeadlineDaysTotal = NonNegative(day.DeadlineDaysTotal);
            day.QuotaCycle = NonNegative(day.QuotaCycle);
        }

        private static void FinalizeSession(ArchiveSession session)
        {
            session.Days = session.Days.OrderBy(d => d.StartedUtc).ThenBy(d => d.DayNumber)
                .ThenBy(d => d.RecordingStem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.DirectoryPath, StringComparer.OrdinalIgnoreCase).ToList();
            session.DurationSeconds = session.Days.Sum(d => d.DurationSeconds);
            session.Bytes = session.Days.Sum(d => d.Bytes);
            if (session.Days.Count > 0)
            {
                if (session.StartedUtc == default) session.StartedUtc = session.Days.Min(d => d.StartedUtc);
                session.LastModifiedUtc = Later(session.LastModifiedUtc, session.Days.Max(d => d.LastModifiedUtc));
            }
            session.Perspective = ReplayArchive.Clip(session.Perspective, 128);
            session.QuotaRemaining = NonNegative(session.QuotaRemaining);
            session.QuotaTarget = NonNegative(session.QuotaTarget);
            session.QuotaFulfilled = NonNegative(session.QuotaFulfilled);
        }

        private static void FinalizeRun(ArchiveRun run)
        {
            run.Sessions = run.Sessions.OrderBy(s => s.StartedUtc).ThenBy(s => s.SessionNumber)
                .ThenBy(s => s.DirectoryPath, StringComparer.OrdinalIgnoreCase).ToList();
            run.DurationSeconds = run.Sessions.Sum(s => s.DurationSeconds);
            run.Bytes = run.Sessions.Sum(s => s.Bytes);
            if (run.Sessions.Count > 0)
            {
                if (run.StartedUtc == default) run.StartedUtc = run.Sessions.Min(s => s.StartedUtc);
                run.LastModifiedUtc = Later(run.LastModifiedUtc, run.Sessions.Max(s => s.LastModifiedUtc));
            }
        }

        private static int Number(string name, string prefix)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            string digits = new string(name.Substring(prefix.Length).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0;
        }
        private static int? MetadataNumber(ReplayHeader header, string key) =>
            header.Metadata.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : (int?)null;
        private static int? NonNegative(int? value) => value.HasValue && value.Value >= 0 ? value : null;
        private static bool NumericDirectory(string name, out int? value)
        {
            string first = name.Split('-')[0];
            value = int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 0 ? number : (int?)null;
            return value.HasValue || string.Equals(first, "unknown", StringComparison.OrdinalIgnoreCase);
        }
        private static int? ParseNumericDirectory(string name) { NumericDirectory(name, out var value); return value; }
        private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Recoverable(Exception error) => error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is JsonException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException;
        private void Warn(string warning) { if (result.Warnings.Count < limits.MaxWarnings) result.Warnings.Add(warning); }
        private void Limit(string warning) { result.IsTruncated = true; if (!result.Warnings.Contains(warning)) Warn(warning); }
        private sealed class DirectoryEntries
        {
            internal readonly List<string> Directories = new List<string>();
            internal readonly List<string> Files = new List<string>();
        }
    }
}
