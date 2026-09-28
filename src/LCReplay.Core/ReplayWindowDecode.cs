using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LCReplay.Core
{
    public static partial class ReplayReader
    {
        private sealed class PendingRecord
        {
            internal ReplayFileIndex.Entry Entry = null!;
            internal byte[]? Payload;
            internal ReplayRecord? Record;
        }

        private static IEnumerable<ReplayRecord> ReadWindowRecords(ReplayFileIndex index, IEnumerable<int> positions,
            ReplayReadLimits limits, CancellationToken cancellation)
        {
            // Bound simultaneous compressed buffers and decoded work. The finished
            // records are still yielded in physical order for equal-time events.
            const long batchLimit = 96L * 1024 * 1024;
            var batch = new List<PendingRecord>(128);
            long expandedTotal = 0, batchBytes = 0;
            using (var input = new FileStream(index.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                var reuseWorlds = index.ReuseWorldPayloads && index.IsComplete;
                if (reuseWorlds && (input.Length != index.FileLength ||
                    File.GetLastWriteTimeUtc(index.FilePath).Ticks != index.LastWriteUtcTicks))
                    throw new InvalidDataException("Replay changed after its index was created.");
                foreach (var position in positions)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entry = index.Entries[position];
                    expandedTotal += entry.Expanded;
                    if (expandedTotal > limits.MaxTotalUncompressedBytes)
                        throw new InvalidDataException("Replay playback window exceeds memory budget.");
                    if (batch.Count != 0 && (batch.Count >= 128 || batchBytes + entry.Expanded > batchLimit))
                    {
                        DecodeBatch(batch, limits, reuseWorlds, cancellation);
                        foreach (var pending in batch) yield return pending.Record!;
                        batch.Clear(); batchBytes = 0;
                    }
                    input.Position = entry.Offset;
                    if (reader.ReadInt32() != entry.Compressed || reader.ReadInt32() != entry.Expanded)
                        throw new InvalidDataException("Replay changed after its index was created.");
                    var item = new PendingRecord { Entry = entry };
                    if (reuseWorlds && entry.Kind == "world" && entry.World != null && entry.World.TryGetTarget(out var world))
                    {
                        // Only the immutable payload is shared. Record.Time is local
                        // to each window and must never be reused or normalized twice.
                        item.Record = new ReplayRecord { Kind = "world", Time = entry.Time, World = world };
                    }
                    else
                    {
                        item.Payload = reader.ReadBytes(entry.Compressed);
                        if (item.Payload.Length != entry.Compressed)
                            throw new InvalidDataException("Indexed replay record was truncated.");
                        batchBytes += entry.Expanded;
                    }
                    batch.Add(item);
                }
                DecodeBatch(batch, limits, reuseWorlds, cancellation);
                foreach (var pending in batch) yield return pending.Record!;
            }
        }

        private static void DecodeBatch(List<PendingRecord> batch, ReplayReadLimits limits, bool reuseWorlds,
            CancellationToken cancellation)
        {
            void Decode(int number)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = batch[number];
                if (item.Record != null)
                {
                    // The opt-in contract is read-only, but validate again so a
                    // caller violating it cannot bypass the current read limits.
                    ReplayValidation.Record(item.Record, limits);
                    return;
                }
                var record = ReplayFormat.Decode(item.Payload!, item.Entry.Expanded, cancellation);
                item.Payload = null;
                ReplayValidation.Record(record, limits);
                if (record.Kind != item.Entry.Kind || record.Time != item.Entry.Time ||
                    (record.World?.CaptureSetId ?? "") != item.Entry.CaptureSetId)
                    throw new InvalidDataException("Replay record disagrees with its index.");
                item.Record = record;
                if (reuseWorlds && record.World != null)
                    item.Entry.World = new WeakReference<WorldSnapshot>(record.World);
            }
            var workers = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
            if (batch.Count < 2 || workers == 1)
            {
                for (var i = 0; i < batch.Count; i++) Decode(i);
                return;
            }
            try
            {
                Parallel.For(0, batch.Count, new ParallelOptions
                    { MaxDegreeOfParallelism = workers, CancellationToken = cancellation }, Decode);
            }
            catch (AggregateException error)
            {
                // Keep the public reader's existing exception contract: callers
                // handle InvalidDataException and cancellation, not worker wrappers.
                cancellation.ThrowIfCancellationRequested();
                ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
                throw;
            }
        }
    }
}
