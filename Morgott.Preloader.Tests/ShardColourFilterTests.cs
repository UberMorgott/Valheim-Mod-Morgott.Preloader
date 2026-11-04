using Morgott.Preloader.ModHotspots;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class ShardColourFilterTests
    {
        private static string Message(string colour) =>
            "shardstones.json has no usable definition for shard colour '" + colour + "' (missing entry or empty Rarities); skipping its shard items.";

        [Fact]
        public void EighteenRemovedColoursAreSwallowed()
        {
            Assert.Equal(18, ShardColourFilter.Colours.Count);
            Assert.Equal("Red", ShardColourFilter.Expected(Message("Red")));
            Assert.Equal("Peach", ShardColourFilter.Expected(Message("Peach")));
        }

        [Fact]
        public void OtherSkipsStayErrors()
        {
            Assert.Null(ShardColourFilter.Expected(Message("Eikthyr")));
            Assert.Null(ShardColourFilter.Expected(Message("Firewalker")));
            Assert.Null(ShardColourFilter.Expected(Message("red")));
            Assert.Null(ShardColourFilter.Expected("no quotes at all"));
            Assert.Null(ShardColourFilter.Expected(null));
        }
    }
}