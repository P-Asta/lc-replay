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
            var raw = Utf8.GetBytes(JsonConvert.SerializeObject(record, Settings));
            if (raw.Length > limits.MaxUncompressedRecordBytes) throw new InvalidDataException("Replay record exceeds expanded size limit.");
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
                compressed = buffer.ToArray();
            }
            if (compressed.Length > limits.MaxCompressedRecordBytes) throw new InvalidDataException("Replay record exceeds compressed size limit.");
            using (var writer = new BinaryWriter(destination, Utf8, true))
            {
                writer.Write(compressed.Length);
                writer.Write(raw.Length);
                writer.Write(compressed);
            }
            // FileStream buffers short records. The background writer flushes and syncs
            // on completion; flushing every sample makes gameplay compete with disk I/O.
            return raw.Length;
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
