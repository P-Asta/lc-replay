using System;
using System.IO;
using System.IO.Compression;

namespace LCReplay.Core
{
    /// <summary>PNG encoding of owned RGBA bytes without calling Unity from a worker.</summary>
    public static class ReplayPng
    {
        private static readonly uint[] CrcTable = MakeCrcTable();
        public static byte[] EncodeRgba(byte[] pixels, int width, int height)
        {
            if (width < 1 || height < 1 || width > 2048 || height > 2048 ||
                pixels == null || pixels.Length < checked(width * height * 4))
                throw new ArgumentException("Invalid bounded RGBA image.");
            using var zlib = new MemoryStream();
            zlib.WriteByte(0x78); zlib.WriteByte(0x9c);
            ulong a = 1, b = 0;
            var row = new byte[width * 4 + 1]; // PNG filter zero, followed by RGBA.
            using (var deflate = new DeflateStream(zlib, CompressionLevel.Fastest, true))
                for (var y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(pixels, (height - 1 - y) * width * 4, row, 1, width * 4);
                    foreach (var value in row) { a += value; b += a; }
                    a %= 65521; b %= 65521;
                    deflate.Write(row, 0, row.Length);
                }
            Big(zlib, (uint)((b << 16) | a));
            using var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            using var header = new MemoryStream();
            Big(header, (uint)width); Big(header, (uint)height);
            header.Write(new byte[] { 8, 6, 0, 0, 0 }, 0, 5);
            Chunk(png, "IHDR", header.GetBuffer(), (int)header.Length);
            Chunk(png, "IDAT", zlib.GetBuffer(), (int)zlib.Length);
            Chunk(png, "IEND", Array.Empty<byte>(), 0);
            return png.ToArray();
        }
        private static void Chunk(Stream stream, string type, byte[] data, int length)
        {
            Big(stream, (uint)length);
            uint crc = uint.MaxValue;
            foreach (var character in type)
            { var value = (byte)character; stream.WriteByte(value); crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); }
            stream.Write(data, 0, length);
            for (var i = 0; i < length; i++) crc = CrcTable[(crc ^ data[i]) & 255] ^ (crc >> 8);
            Big(stream, crc ^ uint.MaxValue);
        }
        private static void Big(Stream stream, uint value)
        { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static uint[] MakeCrcTable()
        {
            var result = new uint[256];
            for (uint i = 0; i < result.Length; i++)
            { var crc = i; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; result[i] = crc; }
            return result;
        }
    }
}
