using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LCReplay.Core.Archive
{
    /// <summary>The quota/deadline shown by the game when a recording starts. Null means unavailable.</summary>
    public sealed class ArchiveQuotaSnapshot
    {
        public int? Target { get; set; }
        public int? Fulfilled { get; set; }
        public int? DeadlineDaysRemaining { get; set; }
        public int? DeadlineDaysTotal { get; set; }
        public int? QuotaCycle { get; set; }
        public int? Remaining => Target.HasValue && Target.Value >= 0 && Fulfilled.HasValue && Fulfilled.Value >= 0
            ? (int)Math.Max(0L, (long)Target.Value - Fulfilled.Value) : (int?)null;
        public bool IsKnown => Remaining.HasValue && DeadlineDaysRemaining.HasValue && DeadlineDaysRemaining.Value >= 0;
    }

    public sealed class ArchiveIndex
    {
        public List<ArchiveRun> Runs { get; set; } = new List<ArchiveRun>();
        public List<string> Warnings { get; set; } = new List<string>();
        public bool IsTruncated { get; set; }

        /// <summary>Continue only inside the recorded day, including after a size-based rotation.</summary>
        public ArchiveSegment? FindNextSegment(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            foreach (var day in Runs.SelectMany(r => r.Sessions).SelectMany(s => s.Days))
            {
                var parts = day.Segments.OrderBy(s => s.Part).ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
                int index = parts.FindIndex(s => string.Equals(s.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return index + 1 < parts.Count ? parts[index + 1] : null;
            }
            return null;
        }
    }

    public abstract class ArchiveFolder
    {
        public string Id { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string Label { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset LastModifiedUtc { get; set; }
        public string Status { get; set; } = "recording";
        [JsonIgnore] public double DurationSeconds { get; set; }
        [JsonIgnore] public long Bytes { get; set; }
    }

    public sealed class ArchiveRun : ArchiveFolder
    {
        [JsonIgnore] public List<ArchiveSession> Sessions { get; set; } = new List<ArchiveSession>();
    }

    public sealed class ArchiveSession : ArchiveFolder
    {
        public string RunId { get; set; } = "";
        public int SessionNumber { get; set; }
        public string Perspective { get; set; } = "";
        public bool IsQuotaGroup { get; set; }
        public int? QuotaRemaining { get; set; }
        public int? QuotaTarget { get; set; }
        public int? QuotaFulfilled { get; set; }
        [JsonIgnore] public List<ArchiveDay> Days { get; set; } = new List<ArchiveDay>();
    }

    public sealed class ArchiveDay : ArchiveFolder
    {
        // New quota recordings live directly under the quota folder as
        // <remaining-deadline>.lcr. Empty means the legacy directory layout.
        public string RecordingStem { get; set; } = "";
        public string RunId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public int DayNumber { get; set; }
        public int? CampaignDay { get; set; }
        public string Moon { get; set; } = "";
        public int? QuotaRemaining { get; set; }
        public int? QuotaTarget { get; set; }
        public int? QuotaFulfilled { get; set; }
        public int? DeadlineDaysRemaining { get; set; }
        public int? DeadlineDaysTotal { get; set; }
        public int? QuotaCycle { get; set; }
        [JsonIgnore] public List<ArchiveSegment> Segments { get; set; } = new List<ArchiveSegment>();
    }

    public sealed class ArchiveSegment
    {
        public string Id { get; set; } = "";
        public string RunId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string DayId { get; set; } = "";
        public int Part { get; set; }
        public string FilePath { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset LastModifiedUtc { get; set; }
        public double DurationSeconds { get; set; }
        public long Bytes { get; set; }
        public string Status { get; set; } = "recording";
        public string Error { get; set; } = "";
        public bool HeaderReadable { get; set; } = true;
        [JsonIgnore] public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ArchiveScanLimits
    {
        public int MaxDirectories { get; set; } = 4096;
        public int MaxReplayFiles { get; set; } = 10000;
        public int MaxEntries { get; set; } = 50000;
        public int MaxWarnings { get; set; } = 100;
        public int MaxHeaderBytes { get; set; } = 256 * 1024;
        public int MaxHeaderReads { get; set; } = 256;
    }
}
