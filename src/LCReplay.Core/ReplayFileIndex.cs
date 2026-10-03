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
        internal static readonly byte[] SidecarMagic = Encoding.ASCII.GetBytes("LCIX0007");
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
            internal string Kind = "", CaptureSetId = "", EventCategory = "", EventKey = "";
            internal double Time;
            internal WeakReference<WorldSnapshot>? World;
        }

        internal readonly List<Entry> Entries = new List<Entry>();
        public string FilePath { get; internal set; } = "";
        public ReplayHeader Header { get; internal set; } = new ReplayHeader();
        public IReadOnlyList<ReplayFileWindow> Windows { get; internal set; } = Array.Empty<ReplayFileWindow>();
        public double Duration { get; internal set; }
        public bool IsComplete { get; internal set; }
        /// <summary>Share validated, read-only world payloads between windows while another loaded window still owns them.
        /// Callers enabling this must not modify the returned WorldSnapshot or its children.</summary>
        public bool ReuseWorldPayloads { get; set; }
        /// <summary>Carry prior actor state only for actors present in this window's frames.
        /// Intended for playback; leave disabled when inspecting complete historical state.</summary>
        public bool TrimInactiveActorState { get; set; }
        public int FrameCount => Entries.Count(entry => entry.Kind == "frame");
        public int EventCount => Entries.Count(entry => entry.Kind == "event");
        public int WorldCount => Entries.Count(entry => entry.Kind == "world");
        public IReadOnlyList<double> Bookmarks => Entries.Where(entry => entry.EventCategory == "bookmark")
            .Select(entry => entry.Time).OrderBy(time => time).ToArray();
        /// <summary>Physical offset of the latest world update at the requested file time.</summary>
        public long WorldRevisionAt(double time)
        {
            long revision = -1;
            foreach (var entry in Entries)
                if (entry.Kind == "world" && entry.Time <= time) revision = entry.Offset;
            return revision;
        }
        internal long FileLength;
        internal long LastWriteUtcTicks;
    }

    public sealed class ReplayFileWindow
    {
        public ReplayFileIndex Index { get; internal set; } = null!;
        public double Start { get; internal set; }
        public double End { get; internal set; }
        public int Number { get; internal set; }
        public double Duration => End - Start;
    }

    public sealed class ReplayPlayerDeath
    {
        public double Time { get; set; }
        public string EntityId { get; set; } = "";
        public string Name { get; set; } = "";
        public Vec3 Position { get; set; }
    }

    public static partial class ReplayReader
    {
        private const long MaxSingleFileBytes = 16L * 1024 * 1024 * 1024;
        private const long MaxSingleFileExpandedBytes = 24L * 1024 * 1024 * 1024;

        /// <summary>Read only death transitions and their adjacent frames from an indexed recording.</summary>
        public static IReadOnlyList<ReplayPlayerDeath> ReadPlayerDeaths(ReplayFileIndex index,
            CancellationToken cancellation = default)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            var deaths = new List<ReplayPlayerDeath>();
            var limits = new ReplayReadLimits();
            using var input = new FileStream(index.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            if (input.Length != index.FileLength || File.GetLastWriteTimeUtc(index.FilePath).Ticks != index.LastWriteUtcTicks)
                throw new InvalidDataException("Replay changed after its index was created.");
            ReplayRecord Decode(ReplayFileIndex.Entry entry)
            {
                cancellation.ThrowIfCancellationRequested();
                input.Position = entry.Offset;
                if (reader.ReadInt32() != entry.Compressed || reader.ReadInt32() != entry.Expanded)
                    throw new InvalidDataException("Replay changed after its index was created.");
                var payload = reader.ReadBytes(entry.Compressed);
                if (payload.Length != entry.Compressed) throw new InvalidDataException("Indexed replay record was truncated.");
                var record = ReplayFormat.Decode(payload, entry.Expanded, cancellation);
                ReplayValidation.Record(record, limits);
                return record;
            }
            var entries = index.Entries;
            var lastFramePosition = -1;
            for (var position = 0; position < entries.Count; position++)
            {
                var entry = entries[position];
                cancellation.ThrowIfCancellationRequested();
                if (entry.Kind == "frame") { lastFramePosition = position; continue; }
                if (entry.Kind != "event" || entry.EventCategory != "death") continue;
                var evt = Decode(entry).Event;
                if (evt?.Category != "state" || evt.Name != "isPlayerDead" ||
                    !evt.Data.TryGetValue("to", out var to) || !string.Equals(to, "True", StringComparison.OrdinalIgnoreCase) ||
                    lastFramePosition < 0) continue;
                EntitySnapshot? player = null;
                var examined = 0;
                for (var prior = lastFramePosition; prior >= 0 && examined < 64; prior--)
                {
                    if (entries[prior].Kind != "frame") continue;
                    examined++;
                    var candidate = Decode(entries[prior]).Frame?.Entities.Find(entity =>
                        entity.Id == evt.EntityId && entity.Kind == "player");
                    if (candidate == null) continue;
                    player ??= candidate;
                    if (!candidate.State.TryGetValue("isPlayerDead", out var dead) ||
                        !string.Equals(dead, "True", StringComparison.OrdinalIgnoreCase))
                    { player = candidate; break; }
                }
                if (player == null) continue;
                deaths.Add(new ReplayPlayerDeath { Time = evt.Time, EntityId = evt.EntityId,
                    Name = player.Name.Length != 0 ? player.Name : evt.Data.TryGetValue("entity", out var name) ? name : "",
                    Position = player.Position });
            }
            return deaths;
        }

        /// <summary>Scans independent records without retaining their decoded frame/world payloads.
        /// Playback may defer frame/world payload validation until ReadWindow to avoid decoding future sections.</summary>
        public static ReplayFileIndex IndexSingleFile(string path, CancellationToken cancellationToken = default,
            long windowExpandedBytes = 48L * 1024 * 1024, Action<double>? progress = null, long initialWindowExpandedBytes = 0,
            bool deferPayloadValidation = false)
        {
            if (windowExpandedBytes < 1024 || windowExpandedBytes > 360L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(windowExpandedBytes));
            if (initialWindowExpandedBytes < 0 || initialWindowExpandedBytes > windowExpandedBytes ||
                initialWindowExpandedBytes != 0 && initialWindowExpandedBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(initialWindowExpandedBytes));
            var firstBudget = initialWindowExpandedBytes == 0 ? windowExpandedBytes : initialWindowExpandedBytes;
            var sidecar = TryReadSidecar(path, cancellationToken, windowExpandedBytes, firstBudget, progress);
            if (sidecar != null) return sidecar;
            var limits = new ReplayReadLimits();
            var index = new ReplayFileIndex { FilePath = Path.GetFullPath(path), LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks };
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
                    ReplayRecord record;
                    if (deferPayloadValidation)
                    {
                        record = ReplayFormat.ReadIndexRecord(input, compressed, expanded, cancellationToken);
                        input.Position = offset + 8L + compressed;
                    }
                    else
                    {
                        var payload = reader.ReadBytes(compressed);
                        if (payload.Length != compressed) break;
                        record = ReplayFormat.Decode(payload, expanded, cancellationToken);
                    }
                    ReplayValidation.Record(record, limits);
                    if (index.Entries.Count == 0)
                    {
                        if (record.Kind != "header") throw new InvalidDataException("Single-file replay has no header.");
                        index.Header = record.Header!;
                    }
                    else if (record.Kind == "header") throw new InvalidDataException("Duplicate replay header.");
                    var entry = new ReplayFileIndex.Entry { Offset = offset, Compressed = compressed, Expanded = expanded,
                        Kind = record.Kind, Time = record.Time, CaptureSetId = record.World?.CaptureSetId ?? "",
                        EventCategory = ReplayEventIndex.Category(record.Event), EventKey = ReplayEventIndex.Key(record.Event) };
                    if (record.Kind == "frame")
                    {
                        if (record.Time < lastFrame) throw new InvalidDataException("Frame timestamps are out of order.");
                        lastFrame = record.Time;
                        if (++frames > 1000000) throw new InvalidDataException("Single-file frame limit exceeded.");
                        if (windowBytes >= (starts.Count == 1 ? firstBudget : windowExpandedBytes) && record.Time > starts[starts.Count - 1])
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
                    if ((index.Entries.Count & 255) == 0) progress?.Invoke(Math.Min(1, (double)input.Position / index.FileLength));
                }
            }
            if (index.Entries.Count == 0) throw new InvalidDataException("Single-file replay has no complete header.");
            starts.RemoveAll(time => time >= index.Duration && time > 0);
            var windows = new List<ReplayFileWindow>();
            for (var i = 0; i < starts.Count; i++)
                windows.Add(new ReplayFileWindow { Index = index, Number = i, Start = starts[i],
                    End = i + 1 < starts.Count ? starts[i + 1] : index.Duration });
            index.Windows = windows.AsReadOnly();
            TryPersistSidecar(index);
            progress?.Invoke(1);
            return index;
        }

        private static void TryPersistSidecar(ReplayFileIndex index)
        {
            if (!index.IsComplete) return;
            var target = Path.ChangeExtension(index.FilePath, ".lci");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    writer.Write(ReplayFileIndex.SidecarMagic);
                    foreach (var entry in index.Entries)
                    {
                        var set = Encoding.UTF8.GetBytes(entry.CaptureSetId);
                        var key = Encoding.UTF8.GetBytes(entry.EventKey);
                        if (set.Length > 96 || key.Length > ushort.MaxValue) return;
                        writer.Write(entry.Offset); writer.Write(entry.Compressed); writer.Write(entry.Expanded);
                        writer.Write(entry.Time); writer.Write(ReplayFileIndex.KindCode(entry.Kind));
                        writer.Write((byte)set.Length); writer.Write(set);
                        writer.Write(ReplayEventIndex.Code(entry.EventCategory));
                        writer.Write((ushort)key.Length); writer.Write(key);
                    }
                }
                if (File.Exists(target)) File.Replace(temporary, target, null, true);
                else File.Move(temporary, target);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException)
            { /* A read-only archive remains playable; only repeat indexing is slower. */ }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static ReplayFileIndex? TryReadSidecar(string path, CancellationToken cancellationToken, long windowExpandedBytes, long firstBudget,
            Action<double>? progress)
        {
            var sidecarPath = Path.ChangeExtension(path, ".lci");
            if (!File.Exists(sidecarPath)) return null;
            try
            {
                var index = new ReplayFileIndex { FilePath = Path.GetFullPath(path), Header = ReadHeader(path),
                    LastWriteUtcTicks = File.GetLastWriteTimeUtc(path).Ticks };
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
                        if (index.Entries.Count >= 2000000 || input.Length - input.Position < 28) return null;
                        var offset = reader.ReadInt64();
                        var compressed = reader.ReadInt32();
                        var expanded = reader.ReadInt32();
                        var time = reader.ReadDouble();
                        var kind = ReplayFileIndex.KindName(reader.ReadByte());
                        var length = reader.ReadByte();
                        if (length > 96 || input.Length - input.Position < length + 3) return null;
                        var set = Encoding.UTF8.GetString(reader.ReadBytes(length));
                        var categoryCode = reader.ReadByte();
                        var keyLength = reader.ReadUInt16();
                        if (categoryCode > 8 || categoryCode != 0 && kind != "event" ||
                            input.Length - input.Position < keyLength || (categoryCode >= 1 && categoryCode <= 5 || categoryCode == 8) && keyLength == 0 ||
                            (categoryCode == 0 || categoryCode == 6 || categoryCode == 7) && keyLength != 0) return null;
                        var eventKey = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
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
                            Expanded = expanded, Time = time, Kind = kind, CaptureSetId = set, EventKey = eventKey,
                            EventCategory = ReplayEventIndex.Name(categoryCode) });
                        if ((index.Entries.Count & 255) == 0) progress?.Invoke(Math.Min(1, (double)input.Position / input.Length));
                    }
                    index.IsComplete = index.Entries.Count != 0 && index.Entries[index.Entries.Count - 1].Kind == "end" &&
                        expectedOffset == index.FileLength;
                    if (!index.IsComplete) return null;
                }
                var starts = new List<double> { 0 };
                long windowBytes = 0;
                foreach (var entry in index.Entries)
                {
                    if (entry.Kind == "frame" && windowBytes >= (starts.Count == 1 ? firstBudget : windowExpandedBytes) && entry.Time > starts[starts.Count - 1])
                    { starts.Add(entry.Time); windowBytes = 0; }
                    if (entry.Kind == "frame" || entry.Kind == "event") windowBytes += entry.Expanded;
                }
                starts.RemoveAll(time => time >= index.Duration && time > 0);
                var windows = new List<ReplayFileWindow>();
                for (var i = 0; i < starts.Count; i++)
                    windows.Add(new ReplayFileWindow { Index = index, Number = i, Start = starts[i],
                        End = i + 1 < starts.Count ? starts[i + 1] : index.Duration });
                index.Windows = windows.AsReadOnly();
                progress?.Invoke(1);
                return index;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is EndOfStreamException)
            { cancellationToken.ThrowIfCancellationRequested(); return null; }
        }

        /// <summary>Loads one spectator window from the indexed day while carrying its latest world and boundary frame.
        /// The first window may also prepare the first complete map capture, through its interior record.</summary>
        public static ReplaySession ReadWindow(ReplayFileWindow window, CancellationToken cancellationToken = default,
            Action<double>? progress = null, bool preloadInitialMap = false)
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
            var setStart = 0.0;
            if (priorWorld >= 0)
            {
                var set = entries[priorWorld].CaptureSetId;
                // Carry only the current capture set's world updates.
                setStart = entries[priorWorld].Time;
                for (var i = 0; i <= priorWorld; i++)
                    if (entries[i].Kind == "world" && (set.Length == 0 ? i == priorWorld : entries[i].CaptureSetId == set))
                    {
                        selected.Add(i);
                        setStart = Math.Min(setStart, entries[i].Time);
                    }
            }
            var limits = new ReplayReadLimits();
            var session = new ReplaySession { Header = index.Header, Duration = window.Duration,
                IsComplete = index.IsComplete && window.Number == index.Windows.Count - 1 };
            session.Warnings.AddRange(index.Header.Warnings);
            if (!index.IsComplete) session.Warnings.Add("Recording has no end marker; recovered data may omit its final moments.");
            long entitiesTotal = 0, frameBytes = 0;
            HashSet<string>? activeActors = null;
            if (index.TrimInactiveActorState)
            {
                activeActors = new HashSet<string>(StringComparer.Ordinal);
                var frames = selected.Where(position => entries[position].Kind == "frame").OrderBy(position => position).ToArray();
                var readFrames = 0;
                foreach (var record in ReadWindowRecords(index, frames, limits, cancellationToken))
                {
                    record.Time = Math.Max(0, record.Time - window.Start);
                    record.Frame!.Time = record.Time;
                    entitiesTotal += record.Frame.Entities.Count;
                    if (session.Frames.Count >= limits.MaxFrames || entitiesTotal > limits.MaxTotalEntitySnapshots)
                        throw new InvalidDataException("Replay playback window exceeds entity budget.");
                    session.Frames.Add(record.Frame);
                    foreach (var actor in record.Frame.Entities) activeActors.Add(actor.Id);
                    if ((++readFrames & 15) == 0) progress?.Invoke(.5 * readFrames / Math.Max(1, frames.Length));
                }
                frameBytes = frames.Sum(position => (long)entries[position].Expanded);
                selected.ExceptWith(frames);
            }
            var latestState = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (entries[i].Time < window.Start && (entries[i].EventCategory != "visual" || entries[i].Time >= setStart))
                    foreach (var key in ReplayEventIndex.CarryKeys(entries[i].EventCategory, entries[i].EventKey, activeActors))
                        if (!latestState.TryGetValue(key, out var prior) || entries[i].Time >= entries[prior].Time)
                        {
                            // A transition just before the boundary still needs
                            // its preceding state for the short animation blend.
                            if (entries[i].EventCategory == "animation" && entries[i].Time >= window.Start - .5 &&
                                prior >= 0 && latestState.ContainsKey(key)) selected.Add(prior);
                            latestState[key] = i;
                        }
            }
            foreach (var prior in latestState.Values) selected.Add(prior);
            var windowExpandedBytes = frameBytes + selected.Sum(position => (long)entries[position].Expanded);
            if (windowExpandedBytes > limits.MaxTotalUncompressedBytes)
                throw new InvalidDataException("Replay playback window exceeds memory budget.");
            var readCount = 0;
            foreach (var record in ReadWindowRecords(index, selected.OrderBy(value => value), limits, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Stateful events carried from an earlier window need their
                // original relative age for animation phase reconstruction.
                var sourceTime = record.Time;
                record.Time = record.Kind == "event" ? record.Time - window.Start : Math.Max(0, record.Time - window.Start);
                if (record.Kind == "world") record.SourceTime = sourceTime;
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
                if ((++readCount & 15) == 0) progress?.Invoke(activeActors == null ? (double)readCount / Math.Max(1, selected.Count)
                    : .5 + .5 * readCount / Math.Max(1, selected.Count));
            }
            if (preloadInitialMap && window.Number == 0)
            {
                // The recorder writes exterior chunks first and the interior last.
                // A ship capture can already have an interior in this window, with
                // the facility in the next set. The sidecar carries set IDs but
                // not layers, so inspect at most one following set. Initial room
                // metadata may come from a carried previous world and cannot prove
                // that a later facility capture is unnecessary.
                const int maxPreloadedWorlds = 8;
                const long maxPreloadedExpandedBytes = 384L * 1024 * 1024;
                var firstWorld = -1;
                for (var position = 0; position < entries.Count; position++)
                    if (selected.Contains(position) && entries[position].Kind == "world")
                    { firstWorld = position; break; }
                if (firstWorld >= 0)
                {
                    var initialSet = entries[firstWorld].CaptureSetId;
                    var initialComplete = session.Worlds.Any(record =>
                        record.World?.CaptureSetId == initialSet && record.World.Layer == "interior");
                    var otherComplete = session.Worlds.Any(record =>
                        record.World?.CaptureSetId != initialSet && record.World?.Layer == "interior");
                    if (initialSet.Length != 0 && !otherComplete)
                    {
                        var preloadedWorlds = 0;
                        long preloadedBytes = 0;
                        var candidate = new List<ReplayRecord>();
                        var nextSet = "";
                        for (var position = firstWorld + 1; position < entries.Count; position++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var entry = entries[position];
                            if (entry.Kind != "world") continue;
                            if (entry.CaptureSetId.Length == 0) break;
                            if (entry.CaptureSetId != initialSet)
                            {
                                if (nextSet.Length == 0) { nextSet = entry.CaptureSetId; candidate.Clear(); }
                                else if (entry.CaptureSetId != nextSet) break;
                            }
                            else if (nextSet.Length != 0) break;
                            if (selected.Contains(position)) continue;
                            if (entry.CaptureSetId == initialSet && initialComplete) continue;
                            if (preloadedWorlds >= maxPreloadedWorlds ||
                                preloadedBytes + entry.Expanded > maxPreloadedExpandedBytes ||
                                windowExpandedBytes + preloadedBytes + entry.Expanded > limits.MaxTotalUncompressedBytes ||
                                session.Worlds.Count + candidate.Count >= limits.MaxWorlds) break;
                            var record = ReadWindowRecords(index, new[] { position }, limits, cancellationToken).Single();
                            record.SourceTime = record.Time;
                            record.Time = Math.Max(0, record.Time - window.Start);
                            candidate.Add(record);
                            preloadedWorlds++;
                            preloadedBytes += entry.Expanded;
                            if (record.World!.Layer == "interior")
                            {
                                session.Worlds.AddRange(candidate);
                                candidate.Clear();
                                if (entry.CaptureSetId != initialSet) break;
                                initialComplete = true;
                            }
                        }
                    }
                }
            }
            // The interior may be captured after doors were opened. Load one
            // nearby frame as a pose reference without adding future motion to
            // this playback window's frame timeline.
            foreach (var interior in session.Worlds.Where(record => record.World?.Layer == "interior" &&
                         record.World.Geometry.Any(geometry => geometry.IsMovingSceneRenderer && geometry.Name == "DoorMesh"))
                         .GroupBy(record => record.World!.CaptureSetId, StringComparer.Ordinal)
                         .Select(group => group.First()).Take(2))
            {
                var captureTime = interior.World!.CaptureCompletedAt > 0 ? interior.World.CaptureCompletedAt :
                    interior.SourceTime ?? interior.Time + window.Start;
                ReplayFileIndex.Entry? nearest = null;
                var distance = 5.0;
                foreach (var entry in entries)
                {
                    if (entry.Kind != "frame") continue;
                    var delta = Math.Abs(entry.Time - captureTime);
                    if (delta >= distance) continue;
                    distance = delta; nearest = entry;
                }
                if (nearest == null) continue;
                var position = entries.IndexOf(nearest);
                var reference = ReadWindowRecords(index, new[] { position }, limits, cancellationToken).Single().Frame;
                if (reference == null) continue;
                StoreDoorPoseReference(session, interior.World!.CaptureSetId, reference);
            }
            session.Worlds = session.Worlds.OrderBy(record => record.Time).ToList();
            session.Events = session.Events.OrderBy(record => record.Time).ToList();
            progress?.Invoke(1);
            return session;
        }

        private static void StoreDoorPoseReference(ReplaySession session, string captureSetId, ReplayFrame frame)
        {
            session.DoorPoseReferences[captureSetId] = frame.Entities
                .Where(entity => entity.Kind == "door")
                .Take(512).Select(entity => new EntitySnapshot
                { Id = entity.Id, Kind = entity.Kind, Position = entity.Position, Rotation = entity.Rotation })
                .ToList();
        }
    }
}
