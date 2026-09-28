using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LCReplay.Core
{
    public static partial class ReplayReader
    {
        /// <summary>Reads only the first bounded record, without loading world or frame data.</summary>
        public static ReplayHeader ReadHeader(string path, int maxHeaderBytes = 256 * 1024)
        {
            if (maxHeaderBytes < 256 || maxHeaderBytes > 16 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                var magic = reader.ReadBytes(ReplayFormat.Magic.Length);
                if (!magic.SequenceEqual(ReplayFormat.Magic)) throw new InvalidDataException("Unrecognized or incomplete replay header.");
                if (input.Length - input.Position < 8) throw new InvalidDataException("Replay header length is incomplete.");
                int compressed = reader.ReadInt32(), expanded = reader.ReadInt32();
                if (compressed <= 0 || expanded <= 0 || compressed > maxHeaderBytes || expanded > maxHeaderBytes)
                    throw new InvalidDataException("Replay header exceeds the bounded index read limit.");
                if (input.Length - input.Position < compressed) throw new InvalidDataException("Replay header is incomplete.");
                byte[] payload = reader.ReadBytes(compressed);
                if (payload.Length != compressed) throw new InvalidDataException("Replay header was truncated while reading.");
                var record = ReplayFormat.Decode(payload, expanded);
                ReplayValidation.Record(record, new ReplayReadLimits());
                if (record.Kind != "header") throw new InvalidDataException("The first replay record must be a header.");
                return record.Header!;
            }
        }

        /// <summary>Reads complete records. A physically truncated final record is discarded with a warning.</summary>
        public static ReplaySession Read(string path, ReplayReadLimits? limits = null, CancellationToken cancellationToken = default,
            Action<double>? progress = null)
        {
            limits = limits ?? new ReplayReadLimits();
            ReplayValidation.Limits(limits);
            var session = new ReplaySession();
            cancellationToken.ThrowIfCancellationRequested();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                // Snapshot the length so an actively recording file has a stable, recoverable boundary.
                long fileLength = input.Length;
                if (fileLength > limits.MaxFileBytes) throw new InvalidDataException("Replay file exceeds configured size limit.");
                var magic = reader.ReadBytes(ReplayFormat.Magic.Length);
                if (magic.Length != ReplayFormat.Magic.Length) throw new InvalidDataException("Replay header is incomplete.");
                for (int i = 0; i < magic.Length; i++)
                    if (magic[i] != ReplayFormat.Magic[i]) throw new InvalidDataException("Unrecognized replay format.");
                int count = 0;
                long expandedTotal = 0;
                long entitiesTotal = 0;
                bool hasHeader = false;
                double lastFrameTime = -1;
                while (input.Position < fileLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long recordOffset = input.Position;
                    if (fileLength - input.Position < 8)
                    {
                        session.Warnings.Add("Recovered complete prefix: the final record length is truncated.");
                        break;
                    }
                    int compressedLength = reader.ReadInt32();
                    int expandedLength = reader.ReadInt32();
                    if (compressedLength <= 0 || compressedLength > limits.MaxCompressedRecordBytes ||
                        expandedLength <= 0 || expandedLength > limits.MaxUncompressedRecordBytes)
                        throw new InvalidDataException("Invalid or excessive record size at byte " + recordOffset + ".");
                    expandedTotal += expandedLength;
                    if (expandedTotal > limits.MaxTotalUncompressedBytes || ++count > limits.MaxRecords)
                        throw new InvalidDataException("Replay exceeds configured aggregate read limits.");
                    if (fileLength - input.Position < compressedLength)
                    {
                        session.Warnings.Add("Recovered complete prefix: the final compressed record is truncated.");
                        break;
                    }
                    var payload = reader.ReadBytes(compressedLength);
                    if (payload.Length != compressedLength)
                    {
                        session.Warnings.Add("Recovered complete prefix: the file was truncated while reading.");
                        break;
                    }
                    ReplayRecord record;
                    if ((count & 63) == 0) progress?.Invoke(Math.Min(1, (double)input.Position / fileLength));
                    try
                    {
                        record = ReplayFormat.Decode(payload, expandedLength, cancellationToken);
                        ReplayValidation.Record(record, limits);
                    }
                    catch (InvalidDataException e)
                    {
                        throw new InvalidDataException("Invalid replay record at byte " + recordOffset + ": " + e.Message, e);
                    }
                    if (!hasHeader)
                    {
                        if (record.Kind != "header") throw new InvalidDataException("The first record must be a header.");
                        session.Header = record.Header!;
                        session.Warnings.AddRange(session.Header.Warnings);
                        hasHeader = true;
                        continue;
                    }
                    session.Duration = Math.Max(session.Duration, record.Time);
                    switch (record.Kind)
                    {
                        case "header": throw new InvalidDataException("Duplicate replay header.");
                        case "frame":
                            if (record.Time < lastFrameTime) throw new InvalidDataException("Frame timestamps are out of order.");
                            lastFrameTime = record.Time;
                            entitiesTotal += record.Frame!.Entities.Count;
                            if (session.Frames.Count >= limits.MaxFrames || entitiesTotal > limits.MaxTotalEntitySnapshots)
                                throw new InvalidDataException("Replay exceeds configured frame/entity limits.");
                            session.Frames.Add(record.Frame);
                            break;
                        case "event":
                            if (session.Events.Count >= limits.MaxEvents) throw new InvalidDataException("Replay exceeds event limit.");
                            session.Events.Add(record.Event!);
                            break;
                        case "world":
                            if (session.Worlds.Count >= limits.MaxWorlds) throw new InvalidDataException("Replay exceeds world snapshot limit.");
                            session.Worlds.Add(record);
                            break;
                        case "end":
                            if (input.Position != fileLength) throw new InvalidDataException("Unexpected data after the replay end marker.");
                            session.IsComplete = true;
                            break;
                    }
                }
                if (!hasHeader) throw new InvalidDataException("No complete replay header was found.");
            }
            if (!session.IsComplete) session.Warnings.Add("Recording has no end marker; recovered data may omit its final moments.");
            session.Events = session.Events.OrderBy(item => item.Time).ToList();
            session.Worlds = session.Worlds.OrderBy(item => item.Time).ToList();
            progress?.Invoke(1);
            return session;
        }

        /// <summary>Finds a completed footer without decoding frame/texture payloads. Partial files use the bounded recovery reader.</summary>
        public static double ReadDuration(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = ReadHeader(path);
            if (header.Metadata.TryGetValue("singleFile", out var singleFile) && singleFile == "true")
                return IndexSingleFile(path, cancellationToken).Duration;
            var limits = new ReplayReadLimits();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                if (input.Length > limits.MaxFileBytes || !reader.ReadBytes(8).SequenceEqual(ReplayFormat.Magic))
                    throw new InvalidDataException("Invalid replay while indexing duration.");
                long last = -1, expandedTotal = 0;
                int compressed = 0, expanded = 0, records = 0;
                while (input.Length - input.Position >= 8)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    compressed = reader.ReadInt32(); expanded = reader.ReadInt32();
                    if (compressed <= 0 || compressed > limits.MaxCompressedRecordBytes || expanded <= 0 || expanded > limits.MaxUncompressedRecordBytes)
                        throw new InvalidDataException("Invalid replay record size while indexing duration.");
                    expandedTotal += expanded;
                    if (++records > limits.MaxRecords || expandedTotal > limits.MaxTotalUncompressedBytes)
                        throw new InvalidDataException("Replay index exceeds read limits.");
                    if (input.Length - input.Position < compressed) { last = -1; break; }
                    last = input.Position;
                    input.Position += compressed;
                }
                if (last >= 0 && input.Position == input.Length && expanded < 1024 && compressed < 1024)
                {
                    input.Position = last;
                    var footer = ReplayFormat.Decode(reader.ReadBytes(compressed), expanded);
                    if (footer.Kind == "end") { ReplayValidation.Record(footer, limits); return footer.Time; }
                }
            }
            return Read(path, limits, cancellationToken).Duration;
        }
    }
}
