using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LCReplay.Core
{
    /// <summary>
    /// Appends independent gzip records on a bounded background queue. After TryWrite succeeds,
    /// ownership transfers to this writer: callers must not mutate the record or nested objects.
    /// A false return means the record was NOT accepted; callers should stop recording visibly.
    /// Dispose drains accepted records, writes an end marker, and closes the file. Check Error afterwards.
    /// </summary>
    public sealed class ReplayWriter : IDisposable
    {
        private readonly BlockingCollection<ReplayRecord> queue;
        private readonly FileStream output;
        private readonly FileStream? indexOutput;
        private readonly BinaryWriter? indexWriter;
        private readonly Task worker;
        private readonly ReplayReadLimits limits = new ReplayReadLimits();
        private Exception? error;
        private long expandedBytes;
        private int disposed;

        public Exception? Error => Volatile.Read(ref error);
        public long ExpandedBytes => Interlocked.Read(ref expandedBytes);

        public ReplayWriter(string path, ReplayHeader header, int capacity = 256, bool indexed = false)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            var first = new ReplayRecord { Kind = "header", Header = header };
            ReplayValidation.Record(first, limits);
            queue = new BlockingCollection<ReplayRecord>(capacity);
            output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
            try
            {
                if (indexed)
                {
                    indexOutput = new FileStream(Path.ChangeExtension(path, ".lci"), FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                        65536, FileOptions.SequentialScan);
                    indexWriter = new BinaryWriter(indexOutput, System.Text.Encoding.UTF8, true);
                    indexWriter.Write(ReplayFileIndex.SidecarMagic);
                }
                output.Write(ReplayFormat.Magic, 0, ReplayFormat.Magic.Length);
                WriteIndexed(first);
            }
            catch { indexWriter?.Dispose(); indexOutput?.Dispose(); output.Dispose(); queue.Dispose(); throw; }
            worker = Task.Run((Action)WriteLoop);
        }

        public bool TryWrite(ReplayRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Kind == "header" || record.Kind == "end")
                throw new ArgumentException("The writer owns header and end records.", nameof(record));
            if (Volatile.Read(ref disposed) != 0 || Error != null) return false;
            try { return queue.TryAdd(record); }
            catch (InvalidOperationException) { return false; }
        }

        private void WriteLoop()
        {
            double duration = 0;
            double lastFrameTime = -1;
            try
            {
                foreach (var record in queue.GetConsumingEnumerable())
                {
                    if (record.Kind == "frame")
                    {
                        if (record.Time < lastFrameTime) throw new InvalidDataException("Frame times must be nondecreasing.");
                        lastFrameTime = record.Time;
                    }
                    WriteIndexed(record);
                    duration = Math.Max(duration, record.Time);
                }
                WriteIndexed(new ReplayRecord { Kind = "end", Time = duration });
                output.Flush(true);
                indexOutput?.Flush(true);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, e, null);
                queue.CompleteAdding();
            }
            finally
            {
                try { indexWriter?.Dispose(); indexOutput?.Dispose(); }
                catch (Exception e) { Interlocked.CompareExchange(ref error, e, null); }
                try { output.Dispose(); }
                catch (Exception e) { Interlocked.CompareExchange(ref error, e, null); }
            }
        }

        private void WriteIndexed(ReplayRecord record)
        {
            var offset = output.Position;
            var expanded = ReplayFormat.Write(output, record, limits);
            Interlocked.Add(ref expandedBytes, expanded);
            if (indexWriter == null) return;
            var compressed = checked((int)(output.Position - offset - 8));
            indexWriter.Write(offset);
            indexWriter.Write(compressed);
            indexWriter.Write(expanded);
            indexWriter.Write(record.Time);
            indexWriter.Write(ReplayFileIndex.KindCode(record.Kind));
            var set = System.Text.Encoding.UTF8.GetBytes(record.World?.CaptureSetId ?? "");
            if (set.Length > 96) throw new InvalidDataException("Capture-set index identifier is too long.");
            indexWriter.Write((byte)set.Length);
            indexWriter.Write(set);
            indexWriter.Write((byte)(record.Kind == "event" && record.Event?.Category == "visual" ? 1 : 0));
        }

        public void Dispose()
        {
            CompleteAsync().GetAwaiter().GetResult();
            // Keep the completed collection alive so a concurrent TryWrite safely returns false.
        }

        public Task CompleteAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) queue.CompleteAdding();
            return worker;
        }
    }
}
