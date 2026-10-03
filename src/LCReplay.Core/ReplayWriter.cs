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
    /// A false return means the record was NOT accepted; retry after backpressure clears.
    /// Dispose drains accepted records, writes an end marker, and closes the file. Check Error afterwards.
    /// </summary>
    public sealed class ReplayWriter : IDisposable
    {
        private readonly BlockingCollection<(ReplayRecord Record, long Bytes)> queue;
        private readonly Stream output;
        private FileStream? indexOutput;
        private BinaryWriter? indexWriter;
        private readonly Task worker;
        private readonly ReplayReadLimits limits = new ReplayReadLimits();
        private Exception? error;
        private long expandedBytes;
        private int disposed;
        private readonly object gate = new object();
        private readonly long maxQueuedBytes;
        private long queuedBytes;
        private double writtenDuration;
        private int writtenBookmarks;
        private long bulkWorldBytes;
        private TaskCompletionSource<bool>? progress;

        public Exception? Error => Volatile.Read(ref error);
        public long ExpandedBytes => Interlocked.Read(ref expandedBytes);
        public Exception? IndexError { get; private set; }
        public long QueuedBytes => Interlocked.Read(ref queuedBytes);
        public int QueuedCount => queue.Count;
        internal long BulkWorldBytes => Interlocked.Read(ref bulkWorldBytes);
        public double WrittenDuration { get { lock (gate) return writtenDuration; } }
        public int WrittenBookmarkCount => Volatile.Read(ref writtenBookmarks);

        public ReplayWriter(string path, ReplayHeader header, int capacity = 64, bool indexed = false, long maxQueuedBytes = 16L * 1024 * 1024)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxQueuedBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
            this.maxQueuedBytes = maxQueuedBytes;
            var first = new ReplayRecord { Kind = "header", Header = header };
            ReplayValidation.Record(first, limits);
            queue = new BlockingCollection<(ReplayRecord, long)>(capacity);
            output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
            try
            {
                if (indexed)
                {
                    try
                    {
                        indexOutput = new FileStream(Path.ChangeExtension(path, ".lci"), FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                            65536, FileOptions.SequentialScan);
                        indexWriter = new BinaryWriter(indexOutput, System.Text.Encoding.UTF8, true);
                        indexWriter.Write(ReplayFileIndex.SidecarMagic);
                    }
                    catch (IOException e) { DisableIndex(e); }
                    catch (UnauthorizedAccessException e) { DisableIndex(e); }
                }
                output.Write(ReplayFormat.Magic, 0, ReplayFormat.Magic.Length);
                WriteIndexed(first);
            }
            catch { indexWriter?.Dispose(); indexOutput?.Dispose(); output.Dispose(); queue.Dispose(); throw; }
            worker = StartWorker();
        }

        // Deterministic slow/full-disk tests use a seekable injected output.
        internal ReplayWriter(Stream output, ReplayHeader header, int capacity, long maxQueuedBytes = 64L * 1024 * 1024)
        {
            if (capacity <= 0 || maxQueuedBytes < 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.output = output; this.maxQueuedBytes = maxQueuedBytes;
            queue = new BlockingCollection<(ReplayRecord, long)>(capacity);
            try { output.Write(ReplayFormat.Magic, 0, ReplayFormat.Magic.Length); WriteIndexed(new ReplayRecord { Kind = "header", Header = header }); }
            catch { output.Dispose(); queue.Dispose(); throw; }
            worker = StartWorker();
        }

        private Task StartWorker() => Task.Factory.StartNew(() =>
        {
            // A dedicated thread lets compression yield CPU priority to gameplay
            // without changing the priority of a shared thread-pool worker.
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* Optional on unsupported hosts. */ }
            WriteLoop();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public bool TryWrite(ReplayRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Kind == "header" || record.Kind == "end")
                throw new ArgumentException("The writer owns header and end records.", nameof(record));
            return TryWrite(record, ReplayRecordMemory.Estimate(record));
        }

        internal bool TryWrite(ReplayRecord record, long bytes)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Kind == "header" || record.Kind == "end")
                throw new ArgumentException("The writer owns header and end records.", nameof(record));
            lock (gate)
            {
                if (disposed != 0 || Error != null || (queuedBytes != 0 && bytes > maxQueuedBytes - queuedBytes)) return false;
                // One valid large world may exceed the accounting budget by itself.
                // It prevents all other queued payloads until the worker releases it.
                try { if (!queue.TryAdd((record, bytes))) return false; }
                catch (InvalidOperationException) { return false; }
                Interlocked.Add(ref queuedBytes, bytes);
                return true;
            }
        }

        public Task<bool> WriteAsync(ReplayRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return WriteAsync(record, ReplayRecordMemory.Estimate(record));
        }

        internal async Task<bool> WriteAsync(ReplayRecord record, long bytes)
        {
            while (true)
            {
                Task changed;
                lock (gate)
                {
                    if (TryWrite(record, bytes)) return true;
                    if (disposed != 0 || Error != null) return false;
                    progress = progress ?? NewProgress(); changed = progress.Task;
                }
                await changed.ConfigureAwait(false);
            }
        }

        private static TaskCompletionSource<bool> NewProgress() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private void NotifyProgress()
        {
            lock (gate) { progress?.TrySetResult(true); progress = null; }
        }

        private void WriteLoop()
        {
            double duration = 0;
            double lastFrameTime = -1;
            try
            {
                var lastFlush = System.Diagnostics.Stopwatch.StartNew();
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    var record = item.Record;
                    try
                    {
                        if (record.Kind == "world" && item.Bytes >= 4L * 1024 * 1024)
                            Interlocked.Exchange(ref bulkWorldBytes, item.Bytes);
                        if (record.Kind == "frame")
                        {
                            if (record.Time < lastFrameTime) throw new InvalidDataException("Frame times must be nondecreasing.");
                            lastFrameTime = record.Time;
                        }
                        WriteIndexed(record);
                        duration = Math.Max(duration, record.Time);
                        lock (gate) writtenDuration = duration;
                        if (record.Event?.Category == "marker" && record.Event.Name == "bookmark") Interlocked.Increment(ref writtenBookmarks);
                        if (lastFlush.Elapsed.TotalSeconds >= 2) { output.Flush(); FlushIndex(false); lastFlush.Restart(); }
                    }
                    finally { Interlocked.Add(ref queuedBytes, -item.Bytes); Interlocked.Exchange(ref bulkWorldBytes, 0); NotifyProgress(); }
                }
                WriteIndexed(new ReplayRecord { Kind = "end", Time = duration });
                if (output is FileStream file) file.Flush(true); else output.Flush();
                FlushIndex(true);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, e, null);
                queue.CompleteAdding();
                // Release snapshots after failure, including large meshes/textures.
                while (queue.TryTake(out var abandoned)) Interlocked.Add(ref queuedBytes, -abandoned.Bytes);
                NotifyProgress();
            }
            finally
            {
                try { indexWriter?.Dispose(); indexOutput?.Dispose(); }
                catch (Exception e) { IndexError = e; }
                try { output.Dispose(); }
                catch (Exception e) { Interlocked.CompareExchange(ref error, e, null); }
            }
        }

        private void WriteIndexed(ReplayRecord record)
        {
            var offset = output.Position;
            int expanded;
            try { expanded = ReplayFormat.Write(output, record, limits); }
            catch
            {
                // A partial record must not hide the complete records before it.
                try { output.SetLength(offset); output.Position = offset; } catch { /* Reader also recovers torn tails. */ }
                throw;
            }
            Interlocked.Add(ref expandedBytes, expanded);
            if (indexWriter == null) return;
            try
            {
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
                indexWriter.Write(ReplayEventIndex.Code(ReplayEventIndex.Category(record.Event)));
                var key = System.Text.Encoding.UTF8.GetBytes(ReplayEventIndex.Key(record.Event));
                if (key.Length > ushort.MaxValue) throw new InvalidDataException("Event index descriptor is too long.");
                indexWriter.Write((ushort)key.Length);
                indexWriter.Write(key);
            }
            catch (IOException e) { DisableIndex(e); }
            catch (OutOfMemoryException e) { DisableIndex(e); }
        }

        private void FlushIndex(bool sync)
        {
            try { if (indexOutput != null) indexOutput.Flush(sync); }
            catch (IOException e) { DisableIndex(e); }
        }
        private void DisableIndex(Exception cause)
        {
            IndexError = cause;
            try { indexWriter?.Dispose(); indexOutput?.Dispose(); } catch { }
            indexWriter = null; indexOutput = null;
        }

        public void Dispose()
        {
            CompleteAsync().GetAwaiter().GetResult();
            // Keep the completed collection alive so a concurrent TryWrite safely returns false.
        }

        public Task CompleteAsync()
        {
            lock (gate) { if (Interlocked.Exchange(ref disposed, 1) == 0) queue.CompleteAdding(); }
            NotifyProgress();
            return worker;
        }
    }
}
