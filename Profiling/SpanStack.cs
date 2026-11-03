using System.Collections.Generic;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Start timestamps of nested hooked calls on the current thread. Used instead of Harmony <c>__state</c>: on
    /// Valheim methods that carry two of our patch pairs plus other mods' patches, HarmonyX 2.9 handed the postfix
    /// a zeroed <c>__state</c> (observed in-game: prefix stored a timestamp, postfix received 0). Push in a prefix,
    /// pop in the closing hook; an enclosing finalizer calls <see cref="TruncateTo"/> to drop entries whose
    /// closing postfix was skipped by an exception. 0 = not recording.
    /// </summary>
    internal sealed class SpanStack
    {
        private readonly List<long> _starts = new List<long>();

        public int Count => _starts.Count;

        public void Push() => _starts.Add(StartupTrace.Recording ? StartupTrace.Now() : 0);

        /// <summary>Drops entries whose closing hook never ran (an exception skipped it).</summary>
        public void TruncateTo(int count)
        {
            if (count >= 0 && count < _starts.Count) _starts.RemoveRange(count, _starts.Count - count);
        }

        public long Pop()
        {
            int last = _starts.Count - 1;
            if (last < 0) return 0;
            long start = _starts[last];
            _starts.RemoveAt(last);
            return start;
        }
    }
}
