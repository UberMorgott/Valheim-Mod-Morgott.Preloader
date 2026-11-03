using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// <c>WorldGenerator.Pregenerate</c> (ValheimDecompiled-1.0.16 WorldGenerator.cs:252-258) runs on every world load and
    /// connect, on the main thread: FindLakes, PlaceRivers (lake pairs, height checks along each pair), PlaceStreams twice
    /// (3000 random start/end searches each, one height sample per try) and RenderRivers after each placement (rasterises
    /// the rivers into <c>m_riverPoints</c> with one <c>Random.Range</c> width per point). The searches are the expensive
    /// part (~0.5 s here); their results are small: the lake list, the river list and the two stream lists.
    /// Cache: after a vanilla Pregenerate the lakes, the three river lists, the <c>Random.state</c> at the start of each of
    /// the three RenderRivers calls and the final one-grid river cache (<c>m_cachedRiverGrid</c> + a copy of
    /// <c>m_cachedRiverPoints</c>, which can hold a pre-stream array) are written to
    /// <c>BepInEx\Morgott.Preloader\cache\worldgen\pregen-&lt;key&gt;.bin</c>. On a hit, Pregenerate is replaced by: the lists
    /// assigned, the original RenderRivers called three times with the recorded states and the same lists / rules (so
    /// <c>m_riverPoints</c> is built by vanilla code, keys in the same insertion order, same widths), the river cache set,
    /// and <c>Random.state</c> left as it was at entry (vanilla restores it around every placement, FindLakes draws none).
    /// Key = SHA-256 of: format, Unity version, assembly_valheim MVID, and every int/float field of the generator at
    /// Pregenerate entry (version, offsets, river and stream seeds, version-dependent distances; all derived from the
    /// seed and world-gen version). The whole key is stored in the file and compared, and the payload carries its own
    /// SHA-256. Off (vanilla, nothing read or written) while any foreign patch is on the generator path (WorldGenerator,
    /// DUtils, FastNoise, BiomeHelpers): such a mod can change the output for the same key.
    /// </summary>
    internal static class PregenCache
    {
        private const string Magic = "FSPREGEN";
        private const int Format = 1;
        private const int MaxFiles = 32;

        private static MethodInfo _pregenerate;
        private static MethodInfo _renderRivers;
        private static Type _riverAdd;
        private static FieldInfo[] _keyFields;
        private static FieldInfo[] _stateFields;
        private static AccessTools.FieldRef<WorldGenerator, List<Vector2>> _lakes;
        private static AccessTools.FieldRef<WorldGenerator, List<WorldGenerator.River>> _rivers;
        private static AccessTools.FieldRef<WorldGenerator, List<WorldGenerator.River>> _streams;
        private static AccessTools.FieldRef<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>> _riverPoints;
        private static AccessTools.FieldRef<WorldGenerator, WorldGenerator.RiverPoint[]> _cachedPoints;
        private static AccessTools.FieldRef<WorldGenerator, Vector2i> _cachedGrid;
        private static string _dir;
        private static string _envKey;

        /// <summary>Capture of one vanilla Pregenerate (main thread).</summary>
        private sealed class Capture
        {
            public string Key;
            public string Path;
            public readonly List<(Random.State State, List<WorldGenerator.River> Rivers, int Rule)> Renders =
                new List<(Random.State, List<WorldGenerator.River>, int)>();
        }

        private static Capture _capture;
        private static bool _replaying;

        public static string LastResult = "";

        public static void Install(Harmony harmony)
        {
            _pregenerate = AccessTools.DeclaredMethod(typeof(WorldGenerator), "Pregenerate");
            _riverAdd = AccessTools.Inner(typeof(WorldGenerator), "RiverAdd");
            _renderRivers = _riverAdd == null ? null : AccessTools.DeclaredMethod(typeof(WorldGenerator), "RenderRivers",
                new[] { typeof(List<WorldGenerator.River>), _riverAdd });
            _stateFields = new[] { "s0", "s1", "s2", "s3" }.Select(n => AccessTools.Field(typeof(Random.State), n)).ToArray();
            if (_pregenerate == null || _renderRivers == null || _stateFields.Any(f => f == null || f.FieldType != typeof(int)) ||
                Enum.GetUnderlyingType(_riverAdd) != typeof(int))
            {
                Log.Warning("PregenCache: WorldGenerator.Pregenerate / RenderRivers / RiverAdd or Random.State fields not found (game or Unity update?), module off");
                return;
            }
            _lakes = AccessTools.FieldRefAccess<WorldGenerator, List<Vector2>>("m_lakes");
            _rivers = AccessTools.FieldRefAccess<WorldGenerator, List<WorldGenerator.River>>("m_rivers");
            _streams = AccessTools.FieldRefAccess<WorldGenerator, List<WorldGenerator.River>>("m_streams");
            _riverPoints = AccessTools.FieldRefAccess<WorldGenerator, Dictionary<Vector2i, WorldGenerator.RiverPoint[]>>("m_riverPoints");
            _cachedPoints = AccessTools.FieldRefAccess<WorldGenerator, WorldGenerator.RiverPoint[]>("m_cachedRiverPoints");
            _cachedGrid = AccessTools.FieldRefAccess<WorldGenerator, Vector2i>("m_cachedRiverGrid");
            _keyFields = typeof(WorldGenerator).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(float) || f.FieldType == typeof(bool) || f.FieldType == typeof(long))
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            _dir = System.IO.Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "cache", "worldgen");
            _envKey = "format " + Format + "|unity " + Application.unityVersion + "|valheim " + typeof(WorldGenerator).Module.ModuleVersionId.ToString("N");
            harmony.Patch(_pregenerate,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PregenCache), nameof(PregeneratePrefix)), Priority.Last),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(PregenCache), nameof(PregenerateFinalizer)), Priority.First));
            harmony.Patch(_renderRivers, prefix: new HarmonyMethod(AccessTools.Method(typeof(PregenCache), nameof(RenderRiversPrefix)), Priority.First));
        }

        private static string Key(WorldGenerator generator)
        {
            var sb = new StringBuilder(_envKey);
            foreach (FieldInfo f in _keyFields)
            {
                object v = f.GetValue(generator);
                string text = v is float fl ? BitConverter.ToUInt32(BitConverter.GetBytes(fl), 0).ToString("x8", CultureInfo.InvariantCulture)
                    : Convert.ToString(v, CultureInfo.InvariantCulture);
                sb.Append('|').Append(f.Name).Append(' ').Append(text);
            }
            return sb.ToString();
        }

        private static bool PregeneratePrefix(WorldGenerator __instance, bool __runOriginal)
        {
            _capture = null;
            if (!__runOriginal || __instance.m_world == null || __instance.m_world.m_menu)
            {
                return true;
            }
            string foreign = PatchGuard.Foreign(GeneratorPath.Methods);
            if (foreign != null)
            {
                Log.WarningOnce("pregen-cache-foreign:" + foreign, $"PregenCache: {foreign}, world generator pregeneration not cached");
                LastResult = "off (foreign patch)";
                return true;
            }
            string key = Key(__instance);
            string path = System.IO.Path.Combine(_dir, "pregen-" + Hex(Sha(Encoding.UTF8.GetBytes(key))).Substring(0, 24) + ".bin");
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (File.Exists(path) && TryRestore(__instance, key, path))
                {
                    LastResult = string.Format(CultureInfo.InvariantCulture, "hit ({0:F0} ms)", watch.Elapsed.TotalMilliseconds);
                    Touch(path);
                    LogEndState(__instance);
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"PregenCache: cannot use {path}, vanilla pregeneration: {e.Message}");
                // Back to the constructor's state (field initializers, WorldGenerator.cs:69-77) before vanilla runs.
                _riverPoints(__instance)?.Clear();
                _rivers(__instance) = new List<WorldGenerator.River>();
                _streams(__instance) = new List<WorldGenerator.River>();
                _lakes(__instance) = null;
                _cachedPoints(__instance) = null;
                _cachedGrid(__instance) = new Vector2i(-999999, -999999);
            }
            _capture = new Capture { Key = key, Path = path };
            LastResult = "miss";
            return true;
        }

        private static void RenderRiversPrefix(List<WorldGenerator.River> rivers, object[] __args)
        {
            if (_capture != null && !_replaying)
            {
                _capture.Renders.Add((Random.state, new List<WorldGenerator.River>(rivers), Convert.ToInt32(__args[1], CultureInfo.InvariantCulture)));
            }
        }

        private static void PregenerateFinalizer(WorldGenerator __instance, Exception __exception)
        {
            Capture capture = _capture;
            _capture = null;
            if (capture != null && __exception == null)
            {
                LogEndState(__instance);
            }
            if (capture == null || __exception != null || capture.Renders.Count != 3)
            {
                return;
            }
            // Snapshot on the main thread (the lists are not changed after Pregenerate); serialize + write off it.
            List<Vector2> lakes = new List<Vector2>(_lakes(__instance) ?? new List<Vector2>());
            Vector2i grid = _cachedGrid(__instance);
            WorldGenerator.RiverPoint[] cached = _cachedPoints(__instance);
            WorldGenerator.RiverPoint[] cachedCopy = cached == null ? null : (WorldGenerator.RiverPoint[])cached.Clone();
            bool riversIsFirst = ReferenceEquals(_rivers(__instance), null) ? false : SameRivers(_rivers(__instance), capture.Renders[0].Rivers);
            bool streamsIsSecond = ReferenceEquals(_streams(__instance), null) ? false : SameRivers(_streams(__instance), capture.Renders[1].Rivers);
            if (!riversIsFirst || !streamsIsSecond)
            {
                Log.Warning("PregenCache: Pregenerate did not end with the rendered river / stream lists (game update?), not cached");
                return;
            }
            ThreadPool.QueueUserWorkItem(_ => Log.Guard("PregenCache write", () => Write(capture, lakes, grid, cachedCopy)));
        }

        private static bool SameRivers(List<WorldGenerator.River> a, List<WorldGenerator.River> b) =>
            a.Count == b.Count && a.Zip(b, ReferenceEquals).All(x => x);

        private static void Write(Capture capture, List<Vector2> lakes, Vector2i grid, WorldGenerator.RiverPoint[] cached)
        {
            var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                w.Write(lakes.Count);
                foreach (Vector2 p in lakes)
                {
                    w.Write(p.x);
                    w.Write(p.y);
                }
                foreach ((Random.State state, List<WorldGenerator.River> rivers, int rule) in capture.Renders)
                {
                    foreach (FieldInfo f in _stateFields)
                    {
                        w.Write((int)f.GetValue(state));
                    }
                    w.Write(rule);
                    w.Write(rivers.Count);
                    foreach (WorldGenerator.River r in rivers)
                    {
                        w.Write(r.p0.x); w.Write(r.p0.y); w.Write(r.p1.x); w.Write(r.p1.y); w.Write(r.center.x); w.Write(r.center.y);
                        w.Write(r.widthMin); w.Write(r.widthMax); w.Write(r.curveWidth); w.Write(r.curveWavelength);
                    }
                }
                w.Write(grid.x);
                w.Write(grid.y);
                w.Write(cached != null);
                if (cached != null)
                {
                    w.Write(cached.Length);
                    foreach (WorldGenerator.RiverPoint p in cached)
                    {
                        w.Write(p.p.x); w.Write(p.p.y); w.Write(p.w); w.Write(p.w2);
                    }
                }
            }
            byte[] body = payload.ToArray();
            Directory.CreateDirectory(_dir);
            string temp = capture.Path + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
            using (var file = new BinaryWriter(File.Create(temp), Encoding.UTF8))
            {
                file.Write(Magic);
                file.Write(capture.Key);
                file.Write(body.Length);
                file.Write(body);
                file.Write(Sha(body));
            }
            if (File.Exists(capture.Path))
            {
                File.Delete(capture.Path);
            }
            File.Move(temp, capture.Path);
            Prune();
        }

        private static bool TryRestore(WorldGenerator generator, string key, string path)
        {
            byte[] body;
            using (var file = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (file.ReadString() != Magic || file.ReadString() != key)
                {
                    return false;
                }
                body = file.ReadBytes(file.ReadInt32());
                byte[] sha = file.ReadBytes(32);
                if (!Sha(body).SequenceEqual(sha))
                {
                    Log.Warning($"PregenCache: {path} is damaged (checksum), vanilla pregeneration");
                    return false;
                }
            }
            var lakes = new List<Vector2>();
            var renders = new List<(Random.State, List<WorldGenerator.River>, int)>();
            Vector2i grid;
            WorldGenerator.RiverPoint[] cached = null;
            using (var r = new BinaryReader(new MemoryStream(body)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    lakes.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
                }
                for (int k = 0; k < 3; k++)
                {
                    object state = default(Random.State);
                    foreach (FieldInfo f in _stateFields)
                    {
                        f.SetValue(state, r.ReadInt32());
                    }
                    int rule = r.ReadInt32();
                    int count = r.ReadInt32();
                    var rivers = new List<WorldGenerator.River>(count);
                    for (int i = 0; i < count; i++)
                    {
                        rivers.Add(new WorldGenerator.River
                        {
                            p0 = new Vector2(r.ReadSingle(), r.ReadSingle()),
                            p1 = new Vector2(r.ReadSingle(), r.ReadSingle()),
                            center = new Vector2(r.ReadSingle(), r.ReadSingle()),
                            widthMin = r.ReadSingle(),
                            widthMax = r.ReadSingle(),
                            curveWidth = r.ReadSingle(),
                            curveWavelength = r.ReadSingle(),
                        });
                    }
                    renders.Add(((Random.State)state, rivers, rule));
                }
                grid = new Vector2i(r.ReadInt32(), r.ReadInt32());
                if (r.ReadBoolean())
                {
                    cached = new WorldGenerator.RiverPoint[r.ReadInt32()];
                    for (int i = 0; i < cached.Length; i++)
                    {
                        cached[i] = new WorldGenerator.RiverPoint(new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadSingle());
                        cached[i].w2 = r.ReadSingle();
                    }
                }
            }
            if (_riverPoints(generator) == null || _riverPoints(generator).Count != 0)
            {
                return false;
            }
            // Vanilla order (WorldGenerator.cs:252-258): lakes, rivers (render), streams (render), deep-north streams (render).
            Random.State outer = Random.state;
            _replaying = true;
            try
            {
                _lakes(generator) = lakes;
                for (int k = 0; k < 3; k++)
                {
                    Random.state = renders[k].Item1;
                    _renderRivers.Invoke(generator, new[] { renders[k].Item2, Enum.ToObject(_riverAdd, renders[k].Item3) });
                }
                _rivers(generator) = renders[0].Item2;
                _streams(generator) = renders[1].Item2;
                _cachedGrid(generator) = grid;
                // Vanilla's cache usually holds the very array stored in m_riverPoints for that grid: keep that identity
                // when the contents match (a superseded pre-render array stays a separate copy, as in vanilla).
                if (cached != null && _riverPoints(generator).TryGetValue(grid, out WorldGenerator.RiverPoint[] current) && SamePoints(current, cached))
                {
                    cached = current;
                }
                _cachedPoints(generator) = cached;
            }
            finally
            {
                _replaying = false;
                Random.state = outer;
            }
            return true;
        }

        /// <summary>[WorldGen] DumpLocations: the state Pregenerate leaves (river cache included), on a hit and a miss alike.</summary>
        private static void LogEndState(WorldGenerator generator)
        {
            if (Verify)
            {
                Log.Guard("PregenCache state", () => Log.Info("PregenCache: state at pregeneration end " + LastResult + ": river cache " +
                    LocationsDump.RiverCacheHash(generator) + ", river points " + _riverPoints(generator).Count + " keys"));
            }
        }

        public static bool Verify;

        private static bool SamePoints(WorldGenerator.RiverPoint[] a, WorldGenerator.RiverPoint[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].p.x.Equals(b[i].p.x) || !a[i].p.y.Equals(b[i].p.y) || !a[i].w.Equals(b[i].w) || !a[i].w2.Equals(b[i].w2))
                {
                    return false;
                }
            }
            return true;
        }

        private static void Touch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (IOException)
            {
                // Read-only or locked: LRU order only.
            }
        }

        /// <summary>Keeps the <see cref="MaxFiles"/> most recently used files; removes temps of dead processes' writes.</summary>
        private static void Prune()
        {
            FileInfo[] files = new DirectoryInfo(_dir).GetFiles("pregen-*.bin").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            foreach (FileInfo f in files.Skip(MaxFiles))
            {
                f.Delete();
            }
            foreach (FileInfo f in new DirectoryInfo(_dir).GetFiles("pregen-*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
            {
                f.Delete();
            }
        }

        private static byte[] Sha(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(bytes);
            }
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
