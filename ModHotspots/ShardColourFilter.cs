using System;
using System.Collections.Generic;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>Pure decision of <see cref="EpicLootShardColours"/> (unit-tested).</summary>
    internal static class ShardColourFilter
    {
        /// <summary>EL 0.15.6 <c>ShardType</c> 0-9, 30-34, 40-42: the colours Morgott.EpicLoot patch 96 removes. Boss /
        /// Firewalker / Stormcaller shards keep their definitions.</summary>
        internal static readonly HashSet<string> Colours = new HashSet<string>(StringComparer.Ordinal)
        {
            "Red", "Yellow", "Cyan", "Black", "Green", "Orange", "Pink", "Purple", "White", "Grey",
            "DarkGreen", "DarkPurple", "DarkRed", "DarkBlue", "Golden", "LightBlue", "LightGreen", "Peach"
        };

        /// <summary>The colour of Epic Loot's skip message (its first quoted name) when it is one of <see cref="Colours"/>, else null.</summary>
        internal static string Expected(string message)
        {
            var a = message?.IndexOf('\'') ?? -1;
            var b = a >= 0 ? message.IndexOf('\'', a + 1) : -1;
            var colour = b > a ? message.Substring(a + 1, b - a - 1) : null;
            return colour != null && Colours.Contains(colour) ? colour : null;
        }
    }
}