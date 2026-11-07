using Morgott.Preloader.Core;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class PatchOwnersTests
    {
        [Theory]
        [InlineData("morgott.valheim.preloader.worldgen", false, false)]
        [InlineData("morgott.valheim.preloader.worldgen", true, false)]
        [InlineData("morgott.valheim.performance.Terrain", false, false)]   // neutral prefix / finalizer
        [InlineData("morgott.valheim.performance.Terrain", true, true)]     // its transpiler still counts
        [InlineData("morgott.valheim.performance.Scene", false, true)]
        [InlineData("mod.valheim_plus", false, true)]
        public void Foreign(string owner, bool body, bool expected) => Assert.Equal(expected, PatchOwners.IsForeign(owner, body));
    }
}