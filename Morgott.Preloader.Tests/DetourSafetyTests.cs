using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Morgott.Preloader.Profiling;
using Xunit;

namespace Morgott.Preloader.Tests
{
    public class DetourSafetyTests
    {
        private static int _n;
        private static readonly List<string> Items = new List<string> { "a" };

        private static IEnumerator Finite()
        {
            for (int i = 0; i < 3; i++)
            {
                yield return null;
                foreach (string s in Items) _n += s.Length;
            }
        }

        private static void PlainForeach()
        {
            foreach (string s in Items) _n += s.Length;
        }

        private static MethodBody MoveNextBody(IEnumerator it) =>
            it.GetType().GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetMethodBody();

        [Fact]
        public void FiniteCoroutineIsPatchable() => Assert.False(DetourSafety.EndsInHandler(MoveNextBody(Finite())));

        [Fact]
        public void PlainMethodWithTryIsPatchable() =>
            Assert.False(DetourSafety.EndsInHandler(typeof(DetourSafetyTests).GetMethod(nameof(PlainForeach), BindingFlags.Static | BindingFlags.NonPublic).GetMethodBody()));

        // Row 1: the lab's SkidbladnirLab AttackProbe+<Culled>d__22.MoveNext (endless loop, foreach after the yield; optimized
        // build): 157 IL bytes, finally at 0x8f..0x9d = the IL's last block (crashed ScriptProbe, sblab 20261123-230935).
        // A Debug build lays the same source out with code after the finally, so the shape is pinned by these numbers.
        [Theory]
        [InlineData(157, 0x8f, 14, true)]
        [InlineData(100, 80, 20, true)]
        [InlineData(100, 80, 19, false)]
        [InlineData(100, 0, 0, false)]
        public void HandlerReachingTheEnd(int ilLength, int offset, int length, bool expected) =>
            Assert.Equal(expected, DetourSafety.EndsInHandler(ilLength, new[] { (offset, length) }));

        [Fact]
        public void NoHandlers() => Assert.False(DetourSafety.EndsInHandler(10, new (int, int)[0]));
    }
}
