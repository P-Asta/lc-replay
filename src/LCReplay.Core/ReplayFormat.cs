using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace LCReplay.Core
{
    internal static class ReplayFormat
    {
        // Eight-byte signature followed by repeated [int32 compressed][int32 expanded][gzip UTF-8 JSON].
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("LCREPL01");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MaxDepth = 64,
            CheckAdditionalContent = true,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        internal static int Write(Stream destination, ReplayRecord record, ReplayReadLimits limits)
        {
            ReplayValidation.Record(record, limits);
            int expanded;
            using (var buffer = new MemoryStream())
            {
                using (var compressed = new LimitedWriteStream(buffer, limits.MaxCompressedRecordBytes))
                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
                using (var raw = new LimitedWriteStream(gzip, limits.MaxUncompressedRecordBytes))
                {
                    using (var text = new StreamWriter(raw, Utf8, 16384, true))
                    using (var json = new JsonTextWriter(text) { CloseOutput = false })
                        JsonSerializer.Create(Settings).Serialize(json, record);
                    expanded = checked((int)raw.Length);
                }
                using (var writer = new BinaryWriter(destination, Utf8, true))
                {
                    writer.Write(checked((int)buffer.Length));
                    writer.Write(expanded);
                    writer.Write(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                }
            }
            // FileStream buffers short records. The background writer flushes and syncs
            // on completion; flushing every sample makes gameplay compete with disk I/O.
            return expanded;
        }

        // Count UTF-8 bytes during serialization and stop before an oversized
        // record grows buffers. Neither the expanded JSON nor a UTF-16 copy is kept.
        private sealed class LimitedWriteStream : Stream
        {
            private readonly Stream target;
            private readonly long limit;
            private long length;
            internal LimitedWriteStream(Stream target, long limit) { this.target = target; this.limit = limit; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > limit - length) throw new InvalidDataException("Replay record exceeds size limit.");
                target.Write(buffer, offset, count); length += count;
            }
            public override void Flush() => target.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => length;
            public override long Position { get => length; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        internal static ReplayRecord Decode(byte[] compressed, int expandedLength, CancellationToken cancellation = default)
        {
            try
            {
                // World records can approach 96 MiB. Parsing the gzip stream avoids
                // retaining both that byte buffer and a second, UTF-16 copy while
                // constructing all of the mesh/texture arrays.
                using (var input = new MemoryStream(compressed, false))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var bounded = new ExpandedRecordStream(gzip, expandedLength, cancellation))
                using (var text = new StreamReader(bounded, Utf8, false, 16384))
                using (var json = new JsonTextReader(text))
                {
                    var record = JsonSerializer.Create(Settings).Deserialize<ReplayRecord>(json)
                        ?? throw new InvalidDataException("Null JSON record.");
                    bounded.VerifyComplete();
                    return record;
                }
            }
            catch (JsonException e) { throw new InvalidDataException("Invalid replay JSON.", e); }
            catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid replay UTF-8.", e); }
        }

        // Playback needs record boundaries and carry-state identifiers, not the
        // mesh/texture/entity arrays of every future window. Stop inflation at
        // that metadata; ReadWindow still decodes and validates the entire
        // selected record before exposing any payload to the viewer.
        internal static ReplayRecord ReadIndexRecord(Stream input, int compressedLength, int expandedLength,
            CancellationToken cancellation)
        {
            try
            {
                using var slice = new RecordSliceStream(input, compressedLength);
                using var gzip = new GZipStream(slice, CompressionMode.Decompress);
                using var bounded = new ExpandedRecordStream(gzip, expandedLength, cancellation);
                using var text = new StreamReader(bounded, Utf8, false, 1024);
                using var json = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double };
                var serializer = JsonSerializer.Create(Settings);
                serializer.CheckAdditionalContent = false; // Deserialize individual envelope fields; check the root below.
                var record = new ReplayRecord();
                var hasTime = false;
                if (!json.Read() || json.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("Invalid replay record envelope.");
                while (json.Read() && json.TokenType != JsonToken.EndObject)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (json.TokenType != JsonToken.PropertyName || json.Depth != 1)
                        throw new InvalidDataException("Invalid replay record envelope.");
                    var name = (string)json.Value!;
                    if (!json.Read()) throw new InvalidDataException("Incomplete replay record envelope.");
                    switch (name)
                    {
                        case "Kind": record.Kind = serializer.Deserialize<string>(json)!; break;
                        case "Time": record.Time = serializer.Deserialize<double>(json); hasTime = true; break;
                        case "Header": record.Header = serializer.Deserialize<ReplayHeader>(json); break;
                        case "Event": record.Event = serializer.Deserialize<ReplayEvent>(json); break;
                        case "Frame":
                            if (record.Kind == "frame" && hasTime && json.TokenType == JsonToken.StartObject)
                            {
                                record.Frame = new ReplayFrame { Time = record.Time };
                                return record;
                            }
                            record.Frame = serializer.Deserialize<ReplayFrame>(json);
                            break;
                        case "World":
                            if (record.Kind == "world" && hasTime && json.TokenType == JsonToken.StartObject)
                            {
                                record.World = new WorldSnapshot();
                                while (json.Read() && json.TokenType != JsonToken.EndObject)
                                {
                                    if (json.TokenType != JsonToken.PropertyName || json.Depth != 2)
                                        throw new InvalidDataException("Invalid indexed world metadata.");
                                    var property = (string)json.Value!;
                                    if (!json.Read()) throw new InvalidDataException("Incomplete indexed world metadata.");
                                    if (property == "CaptureSetId")
                                    {
                                        record.World.CaptureSetId = serializer.Deserialize<string>(json)!;
                                        return record;
                                    }
                                    json.Skip();
                                }
                            }
                            else record.World = serializer.Deserialize<WorldSnapshot>(json);
                            break;
                        default: json.Skip(); break;
                    }
                }
                if (json.TokenType != JsonToken.EndObject || json.Depth != 0 || json.Read())
                    throw new InvalidDataException("Incomplete or additional replay JSON.");
                bounded.VerifyComplete();
                return record;
            }
            catch (JsonException error) { throw new InvalidDataException("Invalid replay JSON.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid replay UTF-8.", error); }
        }

        // GZipStream may read ahead. Bound it to this compressed record without
        // allocating a compressed payload or closing the surrounding file.
        private sealed class RecordSliceStream : Stream
        {
            private readonly Stream input;
            private readonly int length;
            private int position;
            internal RecordSliceStream(Stream input, int length) { this.input = input; this.length = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = input.Read(buffer, offset, Math.Min(count, length - position));
                position += read;
                return read;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class ExpandedRecordStream : Stream
        {
            private readonly Stream input;
            private readonly int length;
            private readonly CancellationToken cancellation;
            private int position;
            internal ExpandedRecordStream(Stream input, int length, CancellationToken cancellation)
            { this.input = input; this.length = length; this.cancellation = cancellation; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                cancellation.ThrowIfCancellationRequested();
                if (count == 0) return 0;
                if (position == length) { VerifyComplete(); return 0; }
                var read = input.Read(buffer, offset, Math.Min(count, length - position));
                if (read == 0) throw new InvalidDataException("Compressed record is shorter than its declared expanded length.");
                position += read;
                return read;
            }
            internal void VerifyComplete()
            {
                cancellation.ThrowIfCancellationRequested();
                if (position != length) throw new InvalidDataException("Compressed record is shorter than its declared expanded length.");
                if (input.ReadByte() != -1) throw new InvalidDataException("Compressed record exceeds its declared expanded length.");
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
