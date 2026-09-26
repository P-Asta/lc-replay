using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LCReplay.Core
{
    public sealed class ReplayRecordingPart
    {
        public string FilePath { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public double Duration { get; set; }
        public double Offset { get; internal set; }
        public ReplayFileWindow? Window { get; set; }
    }

    /// <summary>A single recording clock across bounded storage parts. Gaps hold the previous final pose.</summary>
    public sealed class ReplayRecordingTimeline
    {
        public IReadOnlyList<ReplayRecordingPart> Parts { get; }
        public double Duration { get; }

        public ReplayRecordingTimeline(IEnumerable<ReplayRecordingPart> parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            var source = parts.ToList();
            if (source.Count == 0 || source.Count > 10000) throw new ArgumentException("A recording needs between 1 and 10000 parts.", nameof(parts));
            var result = new List<ReplayRecordingPart>(source.Count);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (source[0] == null) throw new ArgumentException("Invalid recording part.", nameof(parts));
            var start = source[0].StartedUtc;
            double end = 0;
            foreach (var part in source)
            {
                if (part == null || string.IsNullOrWhiteSpace(part.FilePath) ||
                    !paths.Add(Path.GetFullPath(part.FilePath) + (part.Window == null ? "" : "#window=" + part.Window.Number)) ||
                    double.IsNaN(part.Duration) || double.IsInfinity(part.Duration) || part.Duration < 0)
                    throw new ArgumentException("Invalid recording part.", nameof(parts));
                var offset = end;
                if (start != default && part.StartedUtc >= start)
                    offset = Math.Max(offset, (part.StartedUtc - start).TotalSeconds);
                var copy = new ReplayRecordingPart { FilePath = part.FilePath, StartedUtc = part.StartedUtc,
                    Duration = part.Duration, Offset = offset, Window = part.Window };
                result.Add(copy);
                end = offset + part.Duration;
                if (end > 7 * 24 * 60 * 60) throw new ArgumentException("Recording exceeds seven days.", nameof(parts));
            }
            Parts = result.AsReadOnly();
            Duration = end;
        }

        public int Locate(double time)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) throw new ArgumentOutOfRangeException(nameof(time));
            int lo = 0, hi = Parts.Count;
            while (lo < hi)
            {
                int middle = lo + (hi - lo) / 2;
                if (Parts[middle].Offset <= time) lo = middle + 1;
                else hi = middle;
            }
            return Math.Max(0, lo - 1);
        }

        public double LocalTime(int part, double time) => Math.Max(0, Math.Min(Parts[part].Duration, time - Parts[part].Offset));
    }
}
