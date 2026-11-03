using System;
using System.IO;
using System.Linq;
using System.Text;
using Morgott.Preloader.BundleCache;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class BundleHeaderTests
    {
        private static readonly byte[] Plain = Encoding.ASCII.GetBytes("serialized file bytes, 34 of them!");

        private static byte[] Lz4Bundle(uint version = 7, uint tableFlags = 0x43) =>
            Bundles.Build(version, tableFlags, 2, Bundles.Lz4Literals(Plain), (uint)Plain.Length);

        private static BundleCompression Classify(byte[] bytes) => BundleHeader.Classify(new MemoryStream(bytes));

        [Theory]
        [InlineData(6u, 0x42u)] // LZ4 table after the header, no alignment
        [InlineData(7u, 0x43u)] // aligned header
        [InlineData(7u, 0xC0u)] // uncompressed table at the end (Unity's own LZ4 copies)
        public void Lz4BlocksAreNotLzmaAndAComplete(uint version, uint flags)
        {
            byte[] bytes = Lz4Bundle(version, flags);
            Assert.Equal(BundleCompression.NotLzma, Classify(bytes));
            Assert.Equal(Bundles.NodePath + " " + Plain.Length + "\n", BundleHeader.Nodes(new MemoryStream(bytes)));
            string path = Bundles.TempFile(bytes);
            try
            {
                Assert.True(BundleHeader.IsCompleteCopy(path));
                Assert.Equal(BundleHeader.Nodes(new MemoryStream(bytes)), BundleHeader.Nodes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LzmaBlockOrLzmaTableIsLzma()
        {
            Assert.Equal(BundleCompression.Lzma, Classify(Bundles.Build(7, 0x43, 1, new byte[20], 100)));
            Assert.Equal(BundleCompression.Lzma, Classify(Bundles.Build(7, 0x41, 2, new byte[20], 100)));
            string path = Bundles.TempFile(Bundles.Build(7, 0x43, 1, new byte[20], 100));
            try
            {
                Assert.False(BundleHeader.IsCompleteCopy(path)); // an LZMA file is never a servable copy
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TruncatedOrForeignCopiesAreRejected()
        {
            byte[] bytes = Lz4Bundle();
            foreach (byte[] bad in new[] { bytes.Take(bytes.Length - 3).ToArray(), bytes.Concat(new byte[5]).ToArray() })
            {
                string path = Bundles.TempFile(bad);
                try
                {
                    Assert.False(BundleHeader.IsCompleteCopy(path)); // header size != file length
                }
                finally
                {
                    File.Delete(path);
                }
            }
            Assert.False(BundleHeader.IsCompleteCopy(Path.Combine(Path.GetTempPath(), "mp-missing-" + Guid.NewGuid().ToString("N"))));
        }

        [Fact]
        public void MalformedHeadersAreUnknown()
        {
            byte[] bytes = Lz4Bundle();
            Assert.Equal(BundleCompression.Unknown, Classify(Array.Empty<byte>()));
            Assert.Equal(BundleCompression.Unknown, Classify(bytes.Take(30).ToArray())); // cut inside the header
            Assert.Equal(BundleCompression.Unknown, Classify(bytes.Take(60).ToArray())); // cut inside the table
            byte[] web = (byte[])bytes.Clone();
            web[5] = (byte)'W'; // "UnityWS"
            Assert.Equal(BundleCompression.Unknown, Classify(web));
            Assert.Equal(BundleCompression.Unknown, Classify(Encoding.ASCII.GetBytes(new string('U', 300)))); // no string terminator
            Assert.Equal(BundleCompression.Unknown, Classify(Bundles.Build(7, 0x44, 2, new byte[4], 4))); // unknown table compression
            Assert.Null(BundleHeader.Nodes(new MemoryStream(web)));
        }

        [Fact]
        public void ClassifyReadsFromAndRestoresTheCurrentPosition()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3 }.Concat(Lz4Bundle()).ToArray()) { Position = 3 };
            Assert.Equal(BundleCompression.NotLzma, BundleHeader.Classify(stream)); // alignment is relative to the bundle start
            Assert.Equal(3, stream.Position);
        }
    }

    public class Lz4BlockTests
    {
        [Fact]
        public void DecodesLiteralsAndOverlappingMatches()
        {
            // "abc" + match(offset 3, length 9) + final literal "!".
            byte[] src = { 0x35, (byte)'a', (byte)'b', (byte)'c', 3, 0, 0x10, (byte)'!' };
            Assert.Equal("abcabcabcabc!", Encoding.ASCII.GetString(Lz4Block.Decode(src, 13)));
        }

        [Fact]
        public void DecodesExtendedLengths()
        {
            byte[] data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(); // 15 + 255 + 30 literal bytes
            Assert.Equal(data, Lz4Block.Decode(Bundles.Lz4Literals(data), data.Length));
            // "x" + match(offset 1, length 4 + 15 + 1) = 21 times 'x'.
            Assert.Equal(new string('x', 21), Encoding.ASCII.GetString(Lz4Block.Decode(new byte[] { 0x1F, (byte)'x', 1, 0, 1 }, 21)));
        }

        [Fact]
        public void CorruptBlocksThrow()
        {
            Assert.Throws<InvalidDataException>(() => Lz4Block.Decode(new byte[] { 0x10, (byte)'a', 0, 0, 0x10, (byte)'b' }, 7)); // offset 0
            Assert.Throws<InvalidDataException>(() => Lz4Block.Decode(new byte[] { 0x10, (byte)'a', 5, 0 }, 10)); // before the start
            Assert.Throws<InvalidDataException>(() => Lz4Block.Decode(new byte[] { 0x20, (byte)'a', (byte)'b' }, 3)); // size mismatch
        }
    }
}
