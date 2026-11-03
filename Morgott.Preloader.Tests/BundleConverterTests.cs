using System.IO;
using System.Linq;
using Morgott.Preloader.BundleCache;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class BundleConverterTests
    {
        [Fact]
        public void LzmaBundleBecomesACompleteStoredCopy()
        {
            byte[] expected = LzmaDecoderTests.Expected();
            byte[] bundle = Bundles.Build(7, 0x43, 1, LzmaDecoderTests.Vector(), (uint)expected.Length);
            BundleConverter.Source source = BundleConverter.Parse(new MemoryStream(bundle), out string problem);
            Assert.Null(problem);
            Assert.True(source.HasLzma);
            Assert.Equal(BundleCompression.Lzma, BundleHeader.Classify(new MemoryStream(bundle)));

            string output = Path.Combine(Path.GetTempPath(), "mp-convert-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                long progress = 0;
                BundleConverter.Convert(new MemoryStream(bundle), source, output, n => progress = n);
                Assert.Equal(expected.Length, progress);
                Assert.True(BundleHeader.IsCompleteCopy(output));
                Assert.Equal(BundleHeader.Nodes(new MemoryStream(bundle)), BundleHeader.Nodes(output));
                using (FileStream file = File.OpenRead(output))
                {
                    BundleConverter.Source copy = BundleConverter.Parse(file, out problem);
                    Assert.Null(problem);
                    Assert.False(copy.HasLzma);
                    Assert.True(copy.BlockFlags.All(f => f == 0));
                    Assert.Equal((expected.Length + LzmaDecoder.ChunkSize - 1) / LzmaDecoder.ChunkSize, copy.BlockFlags.Length); // stored ChunkSize blocks
                    Assert.Equal(source.Hash, copy.Hash);
                    Assert.Equal(source.Directory, copy.Directory);
                    file.Position = copy.DataStart;
                    Assert.Equal(expected, BundleHeader.Bytes(file, expected.Length));
                }
            }
            finally
            {
                File.Delete(output);
            }
        }

        [Fact]
        public void UnsupportedHeadersAreRefused()
        {
            byte[] good = Bundles.Build(7, 0x43, 2, Bundles.Lz4Literals(new byte[8]), 8);
            Assert.NotNull(BundleConverter.Parse(new MemoryStream(good), out _));
            Assert.Null(BundleConverter.Parse(new MemoryStream(Bundles.Build(9, 0x43, 2, Bundles.Lz4Literals(new byte[8]), 8)), out string problem));
            Assert.StartsWith("unsupported header", problem);
            Assert.Null(BundleConverter.Parse(new MemoryStream(good.Concat(new byte[1]).ToArray()), out problem)); // size != length
            Assert.StartsWith("unsupported header", problem);
            Assert.Null(BundleConverter.Parse(new MemoryStream(Bundles.Build(7, 0x43, 5, Bundles.Lz4Literals(new byte[8]), 8)), out problem));
            Assert.Equal("unknown block compression", problem);
        }
    }
}
