using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// <c>WorldGenerator.FindLakes</c> (ValheimDecompiled-1.0.16 WorldGenerator.cs:275-291) runs on every world load and
    /// connect on the main thread: ~10k underwater grid points merged into lakes by <c>MergePoints</c> (:293-314), which
    /// scans the whole remaining list per merge step (<c>FindClosest</c> :316-333, O(n^2), 1.5 s here).
    /// The replacement keeps vanilla's list operations (take index 0, swap-remove the merged point) on an array window and
    /// finds the same closest point through a uniform grid: cell = range, the 5x5 cells around the point (anything
    /// outside is over one range away, rounding included), same <c>Vector2 ==</c> skip, same <c>Vector2.Distance</c>, same
    /// tie rule (vanilla's strict &lt; in list order = lowest slot wins). Output and emptied input equal vanilla's.
    /// Vanilla while another mod patches MergePoints / FindClosest / FindLakes, or for a non-finite range / coordinate.
    /// With <c>DumpLocations</c> the result is compared against a reverse-patched original MergePoints (same input + 8
    /// synthetic seeds).
    /// </summary>
    internal static class FastLakes
    {
        private static MethodInfo[] _guarded;
        private static bool _verify;
        private static long _lastTicks;

        public static void Install(Harmony harmony, bool verify)
        {
            MethodInfo mergePoints = AccessTools.DeclaredMethod(typeof(WorldGenerator), "MergePoints", new[] { typeof(List<Vector2>), typeof(float) });
            MethodInfo findClosest = AccessTools.DeclaredMethod(typeof(WorldGenerator), "FindClosest", new[] { typeof(List<Vector2>), typeof(Vector2), typeof(float) });
            MethodInfo findLakes = AccessTools.DeclaredMethod(typeof(WorldGenerator), "FindLakes");
            if (mergePoints == null || findClosest == null || findLakes == null || mergePoints.ReturnType != typeof(List<Vector2>))
            {
                Log.Warning("FastLakes: WorldGenerator.MergePoints / FindClosest / FindLakes not found (game update?), module off");
                return;
            }
            _guarded = new[] { mergePoints, findClosest, findLakes };
            _verify = verify;
            if (verify) harmony.CreateReversePatcher(mergePoints, new HarmonyMethod(AccessTools.Method(typeof(FastLakes), nameof(OriginalMergePoints)))).Patch();
            harmony.Patch(mergePoints, prefix: new HarmonyMethod(AccessTools.Method(typeof(FastLakes), nameof(Prefix))));
        }

        /// <summary>Reverse patch: the IL of the original WorldGenerator.MergePoints (diagnostics only).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<Vector2> OriginalMergePoints(WorldGenerator instance, List<Vector2> points, float range) =>
            throw new NotImplementedException("reverse patch stub");

        private static bool Prefix(WorldGenerator __instance, List<Vector2> points, float range, ref List<Vector2> __result, bool __runOriginal)
        {
            if (!__runOriginal || points == null) return true;
            foreach (MethodInfo method in _guarded)
            {
                string foreign = PatchGuard.Foreign(method);
                if (foreign == null) continue;
                Log.WarningOnce("fast-lakes-foreign:" + foreign, $"FastLakes: {foreign}, vanilla lake merge used");
                return true;
            }
            int candidates = points.Count;
            List<Vector2> input = _verify ? new List<Vector2>(points) : null;
            Stopwatch watch = Stopwatch.StartNew();
            List<Vector2> result = Merge(points, range);
            if (result == null) return true;
            _lastTicks = watch.ElapsedTicks;
            __result = result;
            Log.Info(string.Format(CultureInfo.InvariantCulture, "WorldGen: FastLakes merged {0} lake candidates into {1} lakes in {2:F1} ms",
                candidates, result.Count, _lastTicks * 1000.0 / Stopwatch.Frequency));
            if (_verify) Log.Guard("FastLakes verify", () => Verify(__instance, input, range, result));
            return false;
        }

        /// <summary>Same result as WorldGenerator.MergePoints; leaves <paramref name="points"/> empty like vanilla. Null =
        /// input not supported (range or a coordinate not finite): the caller runs vanilla on the untouched list.</summary>
        internal static List<Vector2> Merge(List<Vector2> points, float range)
        {
            if (!(range > 0f) || float.IsInfinity(range)) return null;
            int n = points.Count;
            var pts = new Vector2[n];
            var cellOf = new long[n];
            var cells = new Dictionary<long, List<int>>();
            for (int i = 0; i < n; i++)
            {
                Vector2 p = points[i];
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.y)) return null;
                pts[i] = p;
                long key = cellOf[i] = Key(Cell(p.x, range), Cell(p.y, range));
                if (!cells.TryGetValue(key, out List<int> slots)) cells[key] = slots = new List<int>();
                slots.Add(i);
            }
            var result = new List<Vector2>();
            int head = 0;
            int end = n;
            while (head < end)
            {
                // points[0] taken, RemoveAt(0).
                Vector2 vector = pts[head];
                cells[cellOf[head]].Remove(head);
                head++;
                while (head < end)
                {
                    int found = FindClosest(pts, cells, vector, range);
                    if (found == -1) break;
                    vector = (vector + pts[found]) * 0.5f;
                    // points[num] = points[last]; points.RemoveAt(last).
                    int last = --end;
                    cells[cellOf[found]].Remove(found);
                    if (found == last) continue;
                    cells[cellOf[last]].Remove(last);
                    pts[found] = pts[last];
                    cellOf[found] = cellOf[last];
                    cells[cellOf[found]].Add(found);
                }
                result.Add(vector);
            }
            points.Clear();
            return result;
        }

        /// <summary>WorldGenerator.FindClosest over the 5x5 cells around <paramref name="p"/>: minimal distance, lowest slot
        /// (= lowest list index) on ties, -1 if none closer than the range.</summary>
        private static int FindClosest(Vector2[] pts, Dictionary<long, List<int>> cells, Vector2 p, float maxDistance)
        {
            int result = -1;
            float best = 99999f;
            int cx = Cell(p.x, maxDistance);
            int cy = Cell(p.y, maxDistance);
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (!cells.TryGetValue(Key(cx + dx, cy + dy), out List<int> slots)) continue;
                    foreach (int slot in slots)
                    {
                        if (pts[slot] == p) continue;
                        float d = Vector2.Distance(p, pts[slot]);
                        if (d < maxDistance && (d < best || (d == best && slot < result)))
                        {
                            result = slot;
                            best = d;
                        }
                    }
                }
            }
            return result;
        }

        private static int Cell(float v, float size) => (int)Math.Floor(v / (double)size);

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>Diagnostics: the original MergePoints (reverse patch) on the same input and on synthetic lake candidates of
        /// 8 more seeds (vanilla FindLakes grid, a stand-in base height with the same water threshold) must give the same
        /// list, bit for bit.</summary>
        private static void Verify(WorldGenerator instance, List<Vector2> input, float range, List<Vector2> fast)
        {
            Stopwatch vanilla = Stopwatch.StartNew();
            List<Vector2> expected = OriginalMergePoints(instance, new List<Vector2>(input), range);
            vanilla.Stop();
            int bad = Compare(expected, fast);
            var probe = (WorldGenerator)FormatterServices.GetUninitializedObject(typeof(WorldGenerator));
            int seedsBad = 0;
            var rng = new System.Random(4242);
            for (int s = 0; s < 8; s++)
            {
                List<Vector2> candidates = SyntheticCandidates(rng.Next(-10000, 10000), rng.Next(-10000, 10000));
                List<Vector2> b = Merge(new List<Vector2>(candidates), range);
                if (b == null || Compare(OriginalMergePoints(probe, new List<Vector2>(candidates), range), b) != 0) seedsBad++;
            }
            string message = string.Format(CultureInfo.InvariantCulture,
                "FastLakes verify: {0} ({1} candidates -> {2} lakes, vanilla {3:F0} ms, fast {4:F1} ms; 8 synthetic seeds: {5} different)",
                bad == 0 && seedsBad == 0 ? "identical" : "DIFFERENT", input.Count, fast.Count, vanilla.Elapsed.TotalMilliseconds,
                _lastTicks * 1000.0 / Stopwatch.Frequency, seedsBad);
            if (bad == 0 && seedsBad == 0) Log.Info(message);
            else Log.Error(message + $"; first differing index {bad - 1}");
        }

        /// <summary>0 = equal; else 1 + first differing index (or the shorter length).</summary>
        private static int Compare(List<Vector2> a, List<Vector2> b)
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                if (Hash.Bits(a[i].x) != Hash.Bits(b[i].x) || Hash.Bits(a[i].y) != Hash.Bits(b[i].y)) return i + 1;
            }
            return a.Count == b.Count ? 0 : n + 1;
        }

        /// <summary>The FindLakes grid (WorldGenerator.cs:279-288) with Perlin noise at the given offsets as base height.</summary>
        private static List<Vector2> SyntheticCandidates(int offset0, int offset1)
        {
            var list = new List<Vector2>();
            for (float y = -10000f; y <= 10000f; y = (float)(y + 128.0))
            {
                for (float x = -10000f; x <= 10000f; x = (float)(x + 128.0))
                {
                    if (!(new Vector2(x, y).magnitude > 10000f) &&
                        Mathf.PerlinNoise((x + offset0) * 0.001f, (y + offset1) * 0.001f) * Mathf.PerlinNoise((x + offset1) * 0.002f, (y + offset0) * 0.002f) < 0.12f)
                        list.Add(new Vector2(x, y));
                }
            }
            return list;
        }
    }
}
