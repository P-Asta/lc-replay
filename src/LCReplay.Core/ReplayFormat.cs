using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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

        internal static ReplayRecord Decode(byte[] compressed, int expandedLength)
        {
            var raw = new byte[expandedLength];
            using (var input = new MemoryStream(compressed, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < raw.Length)
                {
                    int read = gzip.Read(raw, offset, raw.Length - offset);
                    if (read == 0) throw new InvalidDataException("Compressed record is shorter than its declared expanded length.");
                    offset += read;
                }
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Compressed record exceeds its declared expanded length.");
            }
            try
            {
                return JsonConvert.DeserializeObject<ReplayRecord>(Utf8.GetString(raw), Settings)
                    ?? throw new InvalidDataException("Null JSON record.");
            }
            catch (JsonException e) { throw new InvalidDataException("Invalid replay JSON.", e); }
            catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid replay UTF-8.", e); }
        }
    }
}
