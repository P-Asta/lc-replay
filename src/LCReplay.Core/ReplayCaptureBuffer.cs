using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LCReplay.Core
{
    /// <summary>Ordered, bounded overflow for one capture producer. Never blocks its thread on disk I/O.</summary>
    public sealed class ReplayCaptureBuffer
    {
        private readonly ReplayWriter writer;
        private readonly int capacity;
        private readonly long maxBytes;
        private readonly Queue<(ReplayRecord Record, long Bytes)> pending = new Queue<(ReplayRecord, long)>();
        private long pendingBytes;
        private bool completing;
        private bool throttled;
        public Exception? Error { get; private set; }
        public int PendingCount => pending.Count;
        public long PendingBytes => pendingBytes;
        public bool ShouldPauseCapture
        {
            get
            {
                var bulk = writer.BulkWorldBytes;
                if (bulk != 0)
                {
                    // A single exported map can exceed the writer's memory cap.
                    // Keep a small, ordered motion tail instead of treating the
                    // in-flight map alone as a reason to freeze all sampling.
                    var motionBytes = pendingBytes + Math.Max(0, writer.QueuedBytes - bulk);
                    var motionCount = pending.Count + writer.QueuedCount;
                    if (motionCount >= 48 || motionBytes >= 4L * 1024 * 1024) throttled = true;
                    else if (motionCount <= 12 && motionBytes <= 1024 * 1024) throttled = false;
                    return throttled;
                }
                // Resume at a lower watermark to avoid repeated allocation bursts.
                if (pending.Count != 0 || writer.QueuedCount >= 48 || writer.QueuedBytes >= 4L * 1024 * 1024) throttled = true;
                else if (writer.QueuedCount <= 12 && writer.QueuedBytes <= 1024 * 1024) throttled = false;
                return throttled;
            }
        }

        public ReplayCaptureBuffer(ReplayWriter writer, int capacity = 4096, long maxBytes = 8L * 1024 * 1024)
        {
            if (capacity < 1 || maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer)); this.capacity = capacity; this.maxBytes = maxBytes;
        }
        public bool TryWrite(ReplayRecord record)
        {
            if (completing || Error != null || writer.Error != null) return false;
            var bytes = ReplayRecordMemory.Estimate(record);
            if (pending.Count == 0 && writer.TryWrite(record, bytes)) return true;
            if (pending.Count >= capacity || (pendingBytes != 0 && bytes > maxBytes - pendingBytes))
            {
                Error = new IOException("Capture backlog exceeded its bounded memory budget; previously accepted recording data is preserved.");
                return false;
            }
            pending.Enqueue((record, bytes)); pendingBytes += bytes;
            return true;
        }
        public void Drain(int maxRecords = 16, double maxMilliseconds = .75)
        {
            if (completing) return;
            var start = Stopwatch.GetTimestamp();
            while (maxRecords-- > 0 && pending.Count != 0)
            {
                var next = pending.Peek();
                if (!writer.TryWrite(next.Record, next.Bytes)) break;
                var item = pending.Dequeue(); pendingBytes -= item.Bytes;
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= maxMilliseconds) break;
            }
        }
        public Task CompleteAsync() => CompleteAsync(null);
        public async Task CompleteAsync(IEnumerable<ReplayRecord>? trailingRecords)
        {
            completing = true;
            try
            {
                while (pending.Count != 0)
                {
                    var next = pending.Peek();
                    if (!await writer.WriteAsync(next.Record, next.Bytes).ConfigureAwait(false)) break;
                    var item = pending.Dequeue(); pendingBytes -= item.Bytes;
                }
                // One final producer may stream its already captured records
                // without filling another memory queue or blocking Unity.
                if (writer.Error == null && trailingRecords != null)
                    foreach (var record in trailingRecords)
                        if (!await writer.WriteAsync(record).ConfigureAwait(false)) break;
            }
            finally
            {
                pending.Clear(); pendingBytes = 0;
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}
