using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// The world minimap (forest mask, biome colours, heights; ValheimDecompiled-1.0.20 Minimap.cs) is built in one
    /// main-thread frame when <c>Minimap.Update</c> first sees a world generator (:655-666): cache
    /// (<c>TryLoadMinimapTextureData</c> :765), else <c>GenerateWorldMap</c> (:1950, 4.2-4.6 s at 2048 px).
    /// <para><b>ParallelMinimap</b>: <c>GenerateWorldMap</c> with the pixel loop split by rows over all cores, each pixel
    /// through the vanilla methods (<c>GetBiome</c>, <c>GetBiomeHeight</c>, private <c>GetPixelColor</c> / <c>GetMaskColor</c>):
    /// pure functions of seed and serialized colours, so the arrays are vanilla's bit for bit. Upload, log line and the
    /// vanilla disk save stay on the main thread in vanilla order.</para>
    /// <para><b>ClientMinimapCache</b>: vanilla sets the cache paths only in <c>Minimap.Start</c> when <c>ZNet.World</c> exists
    /// (:569-581, host); a remote client regenerates on every join and its save no-ops (:1991). On a client the paths point
    /// to <c>BepInEx\Morgott.Preloader\cache\minimap\&lt;seed&gt;_g&lt;gen&gt;_&lt;fp&gt;\</c> (vanilla names and format) and the
    /// vanilla load body runs without the host-save version check. Fingerprint = game / data versions, texture and pixel
    /// size, colours. Newest <see cref="KeepWorlds"/> worlds kept.</para>
    /// Both fall back to vanilla while another mod patches the generator path or the minimap colour / build methods.
    /// </summary>
    internal static class MinimapGen
    {
        private const int KeepWorlds = 8;
        private const int Schema = 1;
        private const string MetaName = "cacheMinimapMeta";

        /// <summary>Cache file names, in the order of <see cref="_paths"/> (mask, biome, height, meta).</summary>
        private static readonly string[] CacheNames = { "cacheMinimapMask", "cacheMinimapBiome", "cacheMinimapHeight", MetaName };

        private static string _root;
        private static MethodInfo _generate;
        private static MethodInfo _tryLoad;
        private static List<MethodBase> _guarded;

        private static Func<Minimap, Heightmap.Biome, Color> _pixelColor;
        private static Func<Minimap, float, float, float, Heightmap.Biome, Color> _maskColor;
        private static Action<Minimap, Color32[], Color32[], float[]> _save;
        private static AccessTools.FieldRef<Minimap, Texture2D> _maskTexture;
        private static AccessTools.FieldRef<Minimap, Texture2D> _heightTexture;
        private static AccessTools.FieldRef<Minimap, Color> _mistlandsColor;
        private static AccessTools.FieldRef<Minimap, string> _saveDirectory;
        private static AccessTools.FieldRef<Minimap, string>[] _paths;
        private static Color _forestColor;
        private static Color _noForestColor;

        /// <summary>Minimap whose cache paths point at our client entry (vanilla's are empty on a client).</summary>
        private static Minimap _owner;

        public static void Install(Harmony harmony, bool parallel, bool cache)
        {
            _generate = AccessTools.DeclaredMethod(typeof(Minimap), "GenerateWorldMap", Type.EmptyTypes);
            _tryLoad = AccessTools.DeclaredMethod(typeof(Minimap), "TryLoadMinimapTextureData", new[] { typeof(int) });
            MethodInfo pixel = AccessTools.DeclaredMethod(typeof(Minimap), "GetPixelColor", new[] { typeof(Heightmap.Biome) });
            MethodInfo mask = AccessTools.DeclaredMethod(typeof(Minimap), "GetMaskColor", new[] { typeof(float), typeof(float), typeof(float), typeof(Heightmap.Biome) });
            MethodInfo save = AccessTools.DeclaredMethod(typeof(Minimap), "SaveMapTextureDataToDisk", new[] { typeof(Color32[]), typeof(Color32[]), typeof(float[]) });
            FieldInfo forest = AccessTools.DeclaredField(typeof(Minimap), "s_forestColor");
            FieldInfo noForest = AccessTools.DeclaredField(typeof(Minimap), "s_noForestColor");
            string[] pathFields = { "m_cachedMinimapMaskTexturePath", "m_cachedMinimapBiomeTexturePath", "m_cachedMinimapHeightTexturePath", "m_cachedMinimapMetaPath" };
            if (_generate == null || _tryLoad == null || pixel == null || mask == null || save == null || forest == null || noForest == null ||
                pathFields.Concat(new[] { "m_forestMaskTexture", "m_heightTexture", "m_mistlandsColor", "m_saveDirectory" })
                    .Any(f => AccessTools.DeclaredField(typeof(Minimap), f) == null))
            {
                Log.Warning("MinimapGen: Minimap.GenerateWorldMap / TryLoadMinimapTextureData / colour or cache members not found (game update?), module off");
                return;
            }
            _pixelColor = AccessTools.MethodDelegate<Func<Minimap, Heightmap.Biome, Color>>(pixel);
            _maskColor = AccessTools.MethodDelegate<Func<Minimap, float, float, float, Heightmap.Biome, Color>>(mask);
            _save = AccessTools.MethodDelegate<Action<Minimap, Color32[], Color32[], float[]>>(save);
            _maskTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
            _heightTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");
            _mistlandsColor = AccessTools.FieldRefAccess<Minimap, Color>("m_mistlandsColor");
            _saveDirectory = AccessTools.FieldRefAccess<Minimap, string>("m_saveDirectory");
            _paths = pathFields.Select(f => AccessTools.FieldRefAccess<Minimap, string>(f)).ToArray();
            _forestColor = (Color)forest.GetValue(null);
            _noForestColor = (Color)noForest.GetValue(null);
            _root = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "cache", "minimap");
            // Code deciding the map besides the generator: the colour methods and Utils.SmoothStep (Mistlands mask).
            _guarded = new List<MethodBase>(GeneratorPath.Methods) { pixel, mask };
            _guarded.AddRange(AccessTools.GetDeclaredMethods(typeof(Utils)).Where(m => m.Name == nameof(Utils.SmoothStep)));
            if (parallel)
                harmony.Patch(_generate, prefix: new HarmonyMethod(AccessTools.Method(typeof(MinimapGen), nameof(GeneratePrefix)), Priority.Last));
            if (cache)
                harmony.Patch(_tryLoad, prefix: new HarmonyMethod(AccessTools.Method(typeof(MinimapGen), nameof(TryLoadPrefix)), Priority.Last));
        }

        /// <summary>Replacing prefix, vanilla GenerateWorldMap (Minimap.cs:1950-1987) with the pixel loop on all cores.</summary>
        private static bool GeneratePrefix(Minimap __instance, bool __runOriginal)
        {
            WorldGenerator generator = WorldGenerator.instance;
            if (!__runOriginal || generator == null || ZNet.World == null) return true;
            // A patch that appeared after the paths were taken (e.g. before a ForceRegen): its map must not be saved under our key.
            if (ReferenceEquals(_owner, __instance)) DisownIfForeign(__instance);
            string foreign = PatchGuard.Foreign(_generate, bodyOnly: true) ?? PatchGuard.Foreign(_guarded);
            if (foreign != null)
            {
                Log.WarningOnce("minimap-parallel-foreign:" + foreign, $"MinimapGen: {foreign}, vanilla minimap generation used");
                return true;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            int size = __instance.m_textureSize;
            float pixelSize = __instance.m_pixelSize;
            int half = size / 2;
            float halfPixel = pixelSize / 2f;
            var biomePixels = new Color32[size * size];
            var maskPixels = new Color32[size * size];
            var heightColors = new Color[size * size];
            var heights = new float[size * size];
            // Vanilla :1953. Not for our client entry: it deletes the cache of a local world with the server's world name.
            if (!ReferenceEquals(_owner, __instance)) Minimap.DeleteMapTextureData(ZNet.World.m_name);
            ParallelBiomeData.NormalizeRiverCache(generator);
            try
            {
                Parallel.For(0, size, i =>
                {
                    float wy = (float)(i - half) * pixelSize + halfPixel;
                    for (int j = 0; j < size; j++)
                    {
                        float wx = (float)(j - half) * pixelSize + halfPixel;
                        Heightmap.Biome biome = generator.GetBiome(wx, wy);
                        float biomeHeight = generator.GetBiomeHeight(biome, wx, wy, out Color _);
                        int index = i * size + j;
                        biomePixels[index] = _pixelColor(__instance, biome);
                        maskPixels[index] = _maskColor(__instance, wx, wy, biomeHeight, biome);
                        heightColors[index].r = biomeHeight;
                        heights[index] = biomeHeight;
                    }
                });
            }
            catch (Exception e)
            {
                Log.Warning($"MinimapGen: parallel minimap build failed, vanilla rerun: {(e as AggregateException)?.InnerException ?? e}");
                return true;
            }
            long computeMs = stopwatch.ElapsedMilliseconds;
            Upload(__instance, maskPixels, biomePixels, heightColors);
            ZLog.Log("Generating new world minimap done [" + stopwatch.ElapsedMilliseconds + "ms]");
            Log.Info($"MinimapGen: parallel minimap {size}x{size} in {stopwatch.ElapsedMilliseconds} ms (pixels {computeMs} ms)");
            if (FileHelpers.LocalStorageSupport == LocalStorageSupport.Supported) _save(__instance, maskPixels, biomePixels, heights);
            return false;
        }

        private static void Upload(Minimap minimap, Color32[] mask, Color32[] biome, Color[] height)
        {
            Texture2D maskTexture = _maskTexture(minimap);
            maskTexture.SetPixels32(mask);
            maskTexture.Apply();
            minimap.m_mapTexture.SetPixels32(biome);
            minimap.m_mapTexture.Apply();
            Texture2D heightTexture = _heightTexture(minimap);
            heightTexture.SetPixels(height);
            heightTexture.Apply();
        }

        /// <summary>Remote client only (vanilla left the cache paths empty): point them at our per-world entry and load it
        /// with the vanilla body (Minimap.cs:765-803) minus the host save-file version check.</summary>
        private static bool TryLoadPrefix(Minimap __instance, int worldSeed, ref bool __result, bool __runOriginal)
        {
            World world = ZNet.World;
            bool owned = ReferenceEquals(_owner, __instance);
            if (!__runOriginal || world == null || ZNet.instance == null || ZNet.instance.IsServer() || (!owned && !string.IsNullOrEmpty(_paths[0](__instance))) ||
                FileHelpers.LocalStorageSupport != LocalStorageSupport.Supported)
                return true;
            // Any foreign patch on the load, the build (a postfix could change the textures after the save) or the pixel
            // path: the cached files would not be what that mod's game shows.
            if (DisownIfForeign(owned ? __instance : null)) return true;
            string dir;
            try
            {
                dir = Path.Combine(_root, string.Format(CultureInfo.InvariantCulture, "{0}_g{1}_{2}", world.m_seed, world.m_worldGenVersion, Fingerprint(__instance)));
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                Log.Warning($"MinimapGen: client minimap cache unavailable, vanilla: {e.Message}");
                return true;
            }
            _owner = __instance;
            SetPaths(__instance, dir);
            __result = Load(__instance, worldSeed);
            Log.Info($"MinimapGen: client minimap cache {(__result ? "hit" : "miss")} {Path.GetFileName(dir)}");
            if (__result) Log.Guard("MinimapGen touch", () => File.SetLastWriteTimeUtc(_paths[3](__instance), DateTime.UtcNow));
            Trim(dir);
            return false;
        }

        /// <summary>True on a foreign patch on the load / build / pixel path; then <paramref name="owned"/> (if any) goes back to
        /// the client's vanilla state (no cache paths: vanilla regenerates and saves nothing).</summary>
        private static bool DisownIfForeign(Minimap owned)
        {
            string foreign = PatchGuard.Foreign(_tryLoad) ?? PatchGuard.Foreign(_generate) ?? PatchGuard.Foreign(_guarded);
            if (foreign == null) return false;
            Log.WarningOnce("minimap-cache-foreign:" + foreign, $"MinimapGen: {foreign}, client minimap not cached");
            if (owned != null)
            {
                _owner = null;
                SetPaths(owned, null);
            }
            return true;
        }

        /// <summary>Minimap.Start :576-580 (same "dir/name" format); null clears them.</summary>
        private static void SetPaths(Minimap minimap, string dir)
        {
            _saveDirectory(minimap) = dir;
            for (int i = 0; i < _paths.Length; i++) _paths[i](minimap) = dir == null ? null : dir + "/" + CacheNames[i];
        }

        /// <summary>Vanilla TryLoadMinimapTextureData body without <c>Version.World.CheatCleanSlate != ZNet.World.m_worldVersion</c>.</summary>
        private static bool Load(Minimap minimap, int worldSeed)
        {
            string[] paths = _paths.Select(p => p(minimap)).ToArray();
            if (!paths.All(File.Exists)) return false;
            try
            {
                byte[] value = File.ReadAllBytes(paths[3]);
                if (BitConverter.ToInt32(value, 0) != worldSeed)
                {
                    ZLog.LogWarning("Minimap seed is wrong! Will re-generate minimap.");
                    return false;
                }
                var version = (global::Version.CachedMinimap)BitConverter.ToInt32(value, 4);
                if (version != global::Version.CachedMinimap.Original)
                {
                    ZLog.Log("Minimap version changed from: " + version + " to " + global::Version.CachedMinimap.Original + ", will re-generate.");
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            int count = minimap.m_textureSize * minimap.m_textureSize;
            try
            {
                // Decoded before any texture is touched: a truncated file (vanilla writes are not atomic) must not leave
                // one texture from the cache and the next regenerated.
                Color32[] mask = Utils.CompressedBufferToColors(File.ReadAllBytes(paths[0]));
                Color32[] biome = Utils.CompressedBufferToColors(File.ReadAllBytes(paths[1]));
                Color[] height = Utils.CompressedHalfBufferToRedChannel(File.ReadAllBytes(paths[2]));
                if (mask.Length != count || biome.Length != count || height.Length != count)
                {
                    Log.Warning($"MinimapGen: cached minimap size {mask.Length}/{biome.Length}/{height.Length} != {count}, regenerated");
                    return false;
                }
                Upload(minimap, mask, biome, height);
            }
            catch (Exception ex)
            {
                ZLog.LogWarning("Error loading minimap files: " + ex.Message + " Stacktrace: " + ex.StackTrace);
                return false;
            }
            ZLog.Log("Loading minimap textures done [" + stopwatch.ElapsedMilliseconds + "ms]");
            return true;
        }

        /// <summary>128 bits (32 hex) of SHA-256 over everything besides seed + generator version that shapes the cached files.</summary>
        private static string Fingerprint(Minimap minimap)
        {
            var text = new StringBuilder();
            text.Append(Schema).Append('|').Append(typeof(Minimap).Assembly.ManifestModule.ModuleVersionId).Append('|')
                .Append(typeof(Utils).Assembly.ManifestModule.ModuleVersionId).Append('|').Append(global::Version.CurrentVersion).Append('|').Append(global::Version.c_networkVersion).Append('|')
                .Append((int)global::Version.c_biomeDataVersion).Append('|').Append((int)global::Version.c_CachedMinimapVersion).Append('|')
                .Append(Application.unityVersion).Append('|').Append(minimap.m_textureSize).Append('|')
                .Append(Bits(minimap.m_pixelSize));
            foreach (Color c in new[] { minimap.m_meadowsColor, minimap.m_ashlandsColor, minimap.m_blackforestColor, minimap.m_deepnorthColor,
                         minimap.m_heathColor, minimap.m_swampColor, minimap.m_mountainColor, _mistlandsColor(minimap), _forestColor, _noForestColor })
                text.Append('|').Append(Bits(c.r)).Append(',').Append(Bits(c.g)).Append(',').Append(Bits(c.b)).Append(',').Append(Bits(c.a));
            return Hash.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString())).Substring(0, 32);
        }

        // Signed: the key text has always held the decimal int32 of the float bits.
        private static int Bits(float value) => unchecked((int)Hash.Bits(value));

        /// <summary>Keeps the newest <see cref="KeepWorlds"/> client entries (by meta write time, touched on every hit; an
        /// entry without meta by its folder time) and never <paramref name="current"/>.</summary>
        private static void Trim(string current)
        {
            Log.Guard("MinimapGen trim", () =>
            {
                foreach (DirectoryInfo old in new DirectoryInfo(_root).GetDirectories()
                             .OrderByDescending(d => File.Exists(Path.Combine(d.FullName, MetaName)) ? File.GetLastWriteTimeUtc(Path.Combine(d.FullName, MetaName)) : d.LastWriteTimeUtc)
                             .Skip(KeepWorlds))
                {
                    if (!string.Equals(old.FullName.TrimEnd('\\', '/'), Path.GetFullPath(current).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        old.Delete(true);
                }
            });
        }
    }
}
