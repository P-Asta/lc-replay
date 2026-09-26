using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LCReplay.Core
{
    /// <summary>A bounded, read-only index for one physical day file. Windows are playback memory budgets, not disk chunks.</summary>
    public sealed class ReplayFileIndex
    {
        internal static readonly byte[] SidecarMagic = Encoding.ASCII.GetBytes("LCIX0001");
        internal static byte KindCode(string kind) => kind switch
        {
            "header" => 0, "frame" => 1, "event" => 2, "world" => 3, "end" => 4,
            _ => throw new InvalidDataException("Unknown indexed replay record kind.")
        };
        internal static string KindName(byte code) => code switch
        {
            0 => "header", 1 => "frame", 2 => "event", 3 => "world", 4 => "end",
            _ => throw new InvalidDataException("Unknown replay sidecar record kind.")
        };
        internal sealed class Entry
        {
            internal long Offset;
            internal int Compressed, Expanded;
            internal string Kind = "", CaptureSetId = "";
            internal double Time;
        }

        internal readonly List<Entry> Entries = new List<Entry>();
        public string FilePath { get; internal set; } = "";
        public ReplayHeader Header { get; internal set; } = new ReplayHeader();
        public IReadOnlyList<ReplayFileWindow> Windows { get; internal set; } = Array.Empty<ReplayFileWindow>();
        public double Duration { get; internal set; }
        public bool IsComplete { get; internal set; }
        public int FrameCount => Entries.Count(entry => entry.Kind == "frame");
        public int EventCount => Entries.Count(entry => entry.Kind == "event");
        public int WorldCount => Entries.Count(entry => entry.Kind == "world");
        internal long FileLength;
    }

    public sealed class ReplayFileWindow
    {
        public ReplayFileIndex Index { get; internal set; } = null!;
        public double Start { get; internal set; }
        public double End { get; internal set; }
        public int Number { get; internal set; }
        public double Duration => End - Start;
    }

    public static partial class ReplayReader
    {
        private const long MaxSingleFileBytes = 16L * 1024 * 1024 * 1024;
        private const long MaxSingleFileExpandedBytes = 24L * 1024 * 1024 * 1024;

        /// <summary>Scans independent records without retaining their decoded frame/world payloads.</summary>
        public static ReplayFileIndex IndexSingleFile(string path, CancellationToken cancellationToken = default,
            long windowExpandedBytes = 180L * 1024 * 1024)
        {
            if (windowExpandedBytes < 1024 || windowExpandedBytes > 360L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(windowExpandedBytes));
            var sidecar = TryReadSidecar(path, cancellationToken, windowExpandedBytes);
            if (sidecar != null) return sidecar;
            var limits = new ReplayReadLimits();
            var index = new ReplayFileIndex { FilePath = Path.GetFullPath(path) };
            var starts = new List<double> { 0 };
            long windowBytes = 0, expandedTotal = 0;
            int frames = 0;
            double lastFrame = -1;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                index.FileLength = input.Length;
                if (input.Length > MaxSingleFileBytes || !reader.ReadBytes(8).SequenceEqual(ReplayFormat.Magic))
                    throw new InvalidDataException("Invalid or oversized single-file replay.");
                while (input.Position < index.FileLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index.Entries.Count >= 2000000) throw new InvalidDataException("Single-file replay record limit exceeded.");
                    var offset = input.Position;
                    if (index.FileLength - offset < 8) break; // Recover a physically truncated final record.
                    int compressed = reader.ReadInt32(), expanded = reader.ReadInt32();
                    if (compressed <= 0 || compressed > limits.MaxCompressedRecordBytes ||
                        expanded <= 0 || expanded > limits.MaxUncompressedRecordBytes)
                        throw new InvalidDataException("Invalid single-file record size at byte " + offset + ".");
                    expandedTotal += expanded;
                    if (expandedTotal > MaxSingleFileExpandedBytes)
                        throw new InvalidDataException("Single-file replay expanded-size limit exceeded.");
                    if (index.FileLength - input.Position < compressed) break;
                    var payload = reader.ReadBytes(compressed);
                    if (payload.Length != compressed) break;
                    var record = ReplayFormat.Decode(payload, expanded);
                    ReplayValidation.Record(record, limits);
                    if (index.Entries.Count == 0)
                    {
                        if (record.Kind != "header") throw new InvalidDataException("Single-file replay has no header.");
                        index.Header = record.Header!;
                    }
                    else if (record.Kind == "header") throw new InvalidDataException("Duplicate replay header.");
                    var entry = new ReplayFileIndex.Entry { Offset = offset, Compressed = compressed, Expanded = expanded,
                        Kind = record.Kind, Time = record.Time, CaptureSetId = record.World?.CaptureSetId ?? "" };
                    if (record.Kind == "frame")
                    {
                        if (record.Time < lastFrame) throw new InvalidDataException("Frame timestamps are out of order.");
                        lastFrame = record.Time;
                        if (++frames > 1000000) throw new InvalidDataException("Single-file frame limit exceeded.");
                        if (windowBytes >= windowExpandedBytes && record.Time > starts[starts.Count - 1])
                        { starts.Add(record.Time); windowBytes = 0; }
                    }
                    if (record.Kind == "frame" || record.Kind == "event") windowBytes += expanded;
                    if (record.Kind == "end")
                    {
                        if (input.Position != index.FileLength) throw new InvalidDataException("Unexpected data after replay end marker.");
                        index.IsComplete = true;
                    }
                    index.Duration = Math.Max(index.Duration, record.Time);
                    index.Entries.Add(entry);
                }
            }
            if (index.Entries.Count == 0) throw new InvalidDataException("Single-file replay has no complete header.");
            starts.RemoveAll(time => time >= index.Duration && time > 0);
            var windows = new List<ReplayFileWindow>();
            for (var i = 0; i < starts.Count; i++)
                windows.Add(new ReplayFileWindow { Index = index, Number = i, Start = starts[i],
                    End = i + 1 < starts.Count ? starts[i + 1] : index.Duration });
            index.Windows = windows.AsReadOnly();
            return index;
        }

        private static ReplayFileIndex? TryReadSidecar(string path, CancellationToken cancellationToken, long windowExpandedBytes)
        {
            var sidecarPath = Path.ChangeExtension(path, ".lci");
            if (!File.Exists(sidecarPath)) return null;
            try
            {
                var index = new ReplayFileIndex { FilePath = Path.GetFullPath(path), Header = ReadHeader(path) };
                index.FileLength = new FileInfo(path).Length;
                if (index.FileLength > MaxSingleFileBytes) throw new InvalidDataException("Single-file replay is too large.");
                using (var input = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                using (var reader = new BinaryReader(input, Encoding.UTF8, true))
                {
                    if (input.Length > 256L * 1024 * 1024 || !reader.ReadBytes(8).SequenceEqual(ReplayFileIndex.SidecarMagic))
                        return null;
                    long expectedOffset = 8, expandedTotal = 0;
                    double lastFrame = -1;
                    int frames = 0;
                    while (input.Position < input.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (index.Entries.Count >= 2000000 || input.Length - input.Position < 26) return null;
                        var offset = reader.ReadInt64();
                        var compressed = reader.ReadInt32();
                        var expanded = reader.ReadInt32();
                        var time = reader.ReadDouble();
                        var kind = ReplayFileIndex.KindName(reader.ReadByte());
                        var length = reader.ReadByte();
                        if (length > 96 || input.Length - input.Position < length) return null;
                        var set = Encoding.UTF8.GetString(reader.ReadBytes(length));
                        if (offset != expectedOffset || compressed <= 0 || compressed > 48 * 1024 * 1024 ||
                            expanded <= 0 || expanded > 96 * 1024 * 1024 || double.IsNaN(time) ||
                            double.IsInfinity(time) || time < 0 || time > 7 * 24 * 60 * 60 ||
                            offset > index.FileLength - 8 - compressed ||
                            index.Entries.Count == 0 && kind != "header" ||
                            index.Entries.Count != 0 && kind == "header") return null;
                        if (kind == "frame")
                        {
                            if (time < lastFrame || ++frames > 1000000) return null;
                            lastFrame = time;
                        }
                        expectedOffset = offset + 8L + compressed;
                        expandedTotal += expanded;
                        if (expandedTotal > MaxSingleFileExpandedBytes) return null;
                        index.Duration = Math.Max(index.Duration, time);
                        index.Entries.Add(new ReplayFileIndex.Entry { Offset = offset, Compressed = compressed,
                            Expanded = expanded, Time = time, Kind = kind, CaptureSetId = set });
                    }
                    index.IsComplete = index.Entries.Count != 0 && index.Entries[index.Entries.Count - 1].Kind == "end" &&
                        expectedOffset == index.FileLength;
                    if (!index.IsComplete) return null;
                }
                var starts = new List<double> { 0 };
                long windowBytes = 0;
                foreach (var entry in index.Entries)
                {
                    if (entry.Kind == "frame" && windowBytes >= windowExpandedBytes && entry.Time > starts[starts.Count - 1])
                    { starts.Add(entry.Time); windowBytes = 0; }
                    if (entry.Kind == "frame" || entry.Kind == "event") windowBytes += entry.Expanded;
                }
                starts.RemoveAll(time => time >= index.Duration && time > 0);
                var windows = new List<ReplayFileWindow>();
                for (var i = 0; i < starts.Count; i++)
                    windows.Add(new ReplayFileWindow { Index = index, Number = i, Start = starts[i],
                        End = i + 1 < starts.Count ? starts[i + 1] : index.Duration });
                index.Windows = windows.AsReadOnly();
                return index;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is EndOfStreamException)
            { cancellationToken.ThrowIfCancellationRequested(); return null; }
        }

        /// <summary>Loads one spectator window from the indexed day while carrying its latest world and boundary frame.</summary>
        public static ReplaySession ReadWindow(ReplayFileWindow window, CancellationToken cancellationToken = default)
        {
            if (window == null || window.Index == null || window.Number < 0 ||
                window.Number >= window.Index.Windows.Count || !ReferenceEquals(window.Index.Windows[window.Number], window))
                throw new ArgumentException("The replay window is not part of its file index.", nameof(window));
            var index = window.Index;
            var entries = index.Entries;
            var selected = new HashSet<int>();
            int priorFrame = -1, priorWorld = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Time < window.Start)
                {
                    if (entry.Kind == "frame") priorFrame = i;
                    if (entry.Kind == "world") priorWorld = i;
                    continue;
                }
                if (entry.Time < window.End || window.Number == index.Windows.Count - 1 && entry.Time <= window.End)
                    if (entry.Kind == "frame" || entry.Kind == "event" || entry.Kind == "world") selected.Add(i);
            }
            if (priorFrame >= 0) selected.Add(priorFrame);
            if (priorWorld >= 0)
            {
                var set = entries[priorWorld].CaptureSetId;
                for (var i = 0; i <= priorWorld; i++)
                    if (entries[i].Kind == "world" && (set.Length == 0 ? i == priorWorld : entries[i].CaptureSetId == set))
                        selected.Add(i);
            }
            var limits = new ReplayReadLimits();
            var session = new ReplaySession { Header = index.Header, Duration = window.Duration,
                IsComplete = index.IsComplete && window.Number == index.Windows.Count - 1 };
            session.Warnings.AddRange(index.Header.Warnings);
            if (!index.IsComplete) session.Warnings.Add("Recording has no end marker; recovered data may omit its final moments.");
            long expandedTotal = 0, entitiesTotal = 0;
            using (var input = new FileStream(index.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
                foreach (var position in selected.OrderBy(value => value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = entries[position];
                    expandedTotal += entry.Expanded;
                    if (expandedTotal > limits.MaxTotalUncompressedBytes) throw new InvalidDataException("Replay playback window exceeds memory budget.");
                    input.Position = entry.Offset;
                    if (reader.ReadInt32() != entry.Compressed || reader.ReadInt32() != entry.Expanded)
                        throw new InvalidDataException("Replay changed after its index was created.");
                    var payload = reader.ReadBytes(entry.Compressed);
                    if (payload.Length != entry.Compressed) throw new InvalidDataException("Indexed replay record was truncated.");
                    var record = ReplayFormat.Decode(payload, entry.Expanded);
                    ReplayValidation.Record(record, limits);
                    record.Time = Math.Max(0, record.Time - window.Start);
                    if (record.Kind == "frame")
                    {
                        record.Frame!.Time = record.Time;
                        entitiesTotal += record.Frame.Entities.Count;
                        if (session.Frames.Count >= limits.MaxFrames || entitiesTotal > limits.MaxTotalEntitySnapshots)
                            throw new InvalidDataException("Replay playback window exceeds entity budget.");
                        session.Frames.Add(record.Frame);
                    }
                    else if (record.Kind == "event")
                    {
                        record.Event!.Time = record.Time;
                        if (session.Events.Count >= limits.MaxEvents) throw new InvalidDataException("Replay playback window exceeds event budget.");
                        session.Events.Add(record.Event);
                    }
                    else if (record.Kind == "world")
                    {
                        if (session.Worlds.Count >= limits.MaxWorlds) throw new InvalidDataException("Replay playback window exceeds world budget.");
                        session.Worlds.Add(record);
                    }
                }
            session.Worlds = session.Worlds.OrderBy(record => record.Time).ToList();
            session.Events = session.Events.OrderBy(record => record.Time).ToList();
            return session;
        }
    }
}
