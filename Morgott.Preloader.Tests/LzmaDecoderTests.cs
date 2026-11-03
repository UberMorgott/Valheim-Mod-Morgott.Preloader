using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Morgott.Preloader.BundleCache;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class LzmaDecoderTests
    {
        // Data\lines.lzma = liblzma (Python lzma, FORMAT_ALONE) output for Expected() with the 8 size bytes cut out:
        // 5 property bytes + range-coded stream ending in an end marker. Independent encoder = known-answer vector.
        private static byte[] Vector() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "lines.lzma"));

        private static byte[] Expected()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++)
            {
                sb.Append("line ").Append(i % 1500).Append(' ').Append(i % 7).Append('\n');
            }
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static byte[] Decode(byte[] packed, long size, out LzmaDecoder decoder)
        {
            var output = new MemoryStream();
            decoder = new LzmaDecoder(new MemoryStream(packed), packed.Length);
            decoder.Decode(size, (window, offset, count) => output.Write(window, offset, count));
            return output.ToArray();
        }

        [Fact]
        public void DecodesKnownVectorAcrossChunks()
        {
            byte[] expected = Expected();
            Assert.True(expected.Length > LzmaDecoder.ChunkSize); // exercises the window wrap + chunked sink
            byte[] actual = Decode(Vector(), expected.Length, out LzmaDecoder decoder);
            Assert.Equal("791069ed4fd861a185ac35d9cece53a6e4d0c4a9d309b582444fe26f05d5a131", Convert.ToHexStringLower(SHA256.HashData(actual)));
            Assert.Equal(expected, actual);
            Assert.True(decoder.EndsCleanly());
        }

        [Fact]
        public void MatchPastUnpackedSizeThrows() =>
            Assert.Throws<InvalidDataException>(() => Decode(Vector(), Expected().Length - 1, out _));

        [Fact]
        public void TruncatedInputThrows()
        {
            byte[] packed = Vector();
            Array.Resize(ref packed, packed.Length / 2);
            Assert.ThrowsAny<Exception>(() => Decode(packed, Expected().Length, out _));
        }

        [Fact]
        public void BadPropertiesThrow()
        {
            Assert.Throws<InvalidDataException>(() => new LzmaDecoder(new MemoryStream(new byte[] { 225, 0, 0, 1, 0, 0 }), 6));
            Assert.Throws<InvalidDataException>(() => new LzmaDecoder(new MemoryStream(new byte[4]), 4));
        }
    }
}
