using System.Text;
using Morgott.Preloader.Core;
using Xunit;

namespace Morgott.Preloader.Tests
{
    // Hash feeds persisted cache keys (PregenCache file names, minimap fingerprint) and the determinism dumps: its text must not drift.
    public class HashTests
    {
        [Fact]
        public void BitsAreTheIeeePattern()
        {
            Assert.Equal(0x3f800000u, Hash.Bits(1f));
            Assert.Equal(0x80000000u, Hash.Bits(-0f));
            Assert.Equal("bf800000", Hash.Bits(-1f).ToString("x8"));
        }

        [Fact]
        public void Sha256HexIsLowercaseFips180Vector()
        {
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Hash.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
            Assert.Equal(Hash.Sha256Hex(new byte[0]), Hash.Hex(Hash.Sha256(new byte[0])));
        }
    }
}
