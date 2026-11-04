using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace Morgott.Preloader.Core
{
    /// <summary>BepInEx\config\Morgott.Preloader.cfg. A patcher has no BaseUnityPlugin.Config, so the file is opened directly.</summary>
    internal static class Config
    {
        public static ConfigEntry<bool> ProfilerEnabled { get; private set; }

        public static ConfigEntry<bool> ProfilerTimeModPatches { get; private set; }

        public static ConfigEntry<bool> BundleCacheEnabled { get; private set; }

        public static ConfigEntry<int> BundleCacheMaxSizeMB { get; private set; }

        public static ConfigEntry<bool> BundleCacheEagerFirstBuild { get; private set; }

        public static ConfigEntry<bool> ConfigSaveBatcherEnabled { get; private set; }

        public static ConfigEntry<bool> LocalizationCacheEnabled { get; private set; }

        public static ConfigEntry<bool> HarmonyBatchingEnabled { get; private set; }

        public static ConfigEntry<bool> DumpHarmonyState { get; private set; }

        public static ConfigEntry<bool> ShaderReplacerFixEnabled { get; private set; }

        public static ConfigEntry<bool> DumpModHotspots { get; private set; }

        public static ConfigEntry<bool> VneiIndexingEnabled { get; private set; }

        public static ConfigEntry<bool> EpicLootShardColours { get; private set; }

        public static ConfigEntry<bool> ParallelBiomeData { get; private set; }

        public static ConfigEntry<bool> EarlyReject { get; private set; }

        public static ConfigEntry<float> LoadingTimeBudget { get; private set; }

        public static ConfigEntry<bool> DumpLocations { get; private set; }

        public static ConfigEntry<bool> FastLakes { get; private set; }

        public static ConfigEntry<bool> PregenCache { get; private set; }

        public static ConfigEntry<bool> PrefetchBiomeData { get; private set; }

        public static ConfigEntry<bool> PrefetchSectors { get; private set; }

        public static ConfigEntry<bool> ParallelMinimap { get; private set; }

        public static ConfigEntry<bool> ClientMinimapCache { get; private set; }

        public static ConfigEntry<bool> SpikeProbe { get; private set; }

        public static ConfigEntry<float> SpikeThresholdMs { get; private set; }

        public static ConfigEntry<bool> ScriptBreakdown { get; private set; }

        /// <summary>Any [WorldGen] key that needs the WorldGen hooks.</summary>
        public static bool WorldGenAny => ParallelBiomeData.Value || ParallelMinimap.Value || ClientMinimapCache.Value || FastLakes.Value || PregenCache.Value || EarlyReject.Value || LoadingTimeBudget.Value > 0f || DumpLocations.Value;

        public static void Load()
        {
            var file = new ConfigFile(Path.Combine(Paths.ConfigPath, "morgott.valheim.preloader.cfg"), true);
            ProfilerEnabled = file.Bind("Profiler", "Enabled", false,
                "Record a startup trace (process start -> main menu) and write BepInEx\\Morgott.Preloader\\trace.json (Chrome trace) " +
                "and summary.txt; recording goes on to the first spawn in a world (trace-world.json, summary-world.txt). Costs about 0.5 s of startup (its Harmony hooks), so it is off for normal play; turn it on to " +
                "measure. Read once at launch.");
            ProfilerTimeModPatches = file.Bind("Profiler", "TimeModPatches", false,
                "Also time every mod prefix/postfix/finalizer on the profiled game methods (FejdStartup.Awake etc.) per owner. " +
                "Diagnostic only: hooking forces Mono to compile those methods early, which crashed the game once on a method " +
                "Mono could not compile that way; such a method is skipped automatically from the next launch on " +
                "(BepInEx\\Morgott.Preloader\\patch-probe.skip).");
            BundleCacheEnabled = file.Bind("BundleCache", "Enabled", true,
                "Serve LZMA asset bundles that mods embed in their DLLs from an LZ4 copy cached under " +
                "BepInEx\\Morgott.Preloader\\cache\\bundles (game folder). Copies are made in the background after the main menu; " +
                "the first launch loads the originals. Read once at launch.");
            BundleCacheMaxSizeMB = file.Bind("BundleCache", "MaxCacheSizeMB", 2048,
                new ConfigDescription("Local cache size cap. Least recently used copies beyond it are deleted after the main menu " +
                                      "(copies loaded in the current session are kept).",
                    new AcceptableValueRange<int>(64, 65536)));
            BundleCacheEagerFirstBuild = file.Bind("BundleCache", "EagerFirstBuild", true,
                "From the preloader on, convert mods' LZMA bundles that have no cached copy yet on a few background threads " +
                "(first launch after a mod update), so their loads hit the cache instead of decompressing on the main thread. " +
                "Needs Enabled. Applied at the next start.");
            ConfigSaveBatcherEnabled = file.Bind("ConfigSaveBatcher", "Enabled", true,
                "While the chainloader loads plugins, write each changed .cfg once at the end instead of on every Bind/value " +
                "change (BepInEx rewrites the whole file each time). Also flushed before a pending file is reloaded and on " +
                "process exit. Read once at launch.");
            LocalizationCacheEnabled = file.Bind("LocalizationCache", "Enabled", true,
                "Parse each vanilla localization CSV once per language and replay its rows on later loads (in memory, this " +
                "session only). Other mods' localization patches still run; switching language rebuilds the table exactly as " +
                "vanilla. Read once at launch.");
            HarmonyBatchingEnabled = file.Bind("HarmonyBatching", "Enabled", true,
                "Inside one Harmony.PatchAll(assembly) call, build each patched method's wrapper once at the end of that " +
                "call instead of once per patch. Patch registration is unchanged. Read once at launch.");
            DumpHarmonyState = file.Bind("Diagnostics", "DumpHarmonyState", false,
                "At the main menu write every Harmony-patched method with its prefix/postfix/transpiler/finalizer owners to " +
                "BepInEx\\Morgott.Preloader\\harmony-state.txt (Morgott.Preloader's own patches excluded), for diffing two setups.");
            ShaderReplacerFixEnabled = file.Bind("ModHotspots", "ShaderReplacer", true,
                "Run the ShaderReplacer helper embedded in blacks7ar mods (OreMines) with one shader lookup instead of one " +
                "per material. Same shader assignments in the same order; only the known slow version of the helper is " +
                "replaced (matched by its IL). Read once at launch.");
            VneiIndexingEnabled = file.Bind("ModHotspots", "VNEIIndexing", true,
                "Build VNEI's index a few ms per frame instead of in one ~1-1.5 s frame right after the player spawns: the " +
                "items (with their icon renders) during the loading screen, the recipes in the frames after the spawn (other " +
                "mods' recipe patches need the player). Same calls in the same order, same index and UI. Only VNEI 0.17.6's " +
                "exact code is replaced (matched by its IL). Read once at launch.");
            EpicLootShardColours = file.Bind("ModHotspots", "EpicLootShardColours", true,
                "Silence Epic Loot's 18 startup errors for the shard colours Morgott.EpicLoot patch 96 removes on purpose " +
                "(one info line names them instead); any other colour's error stays. Read once at launch.");
            DumpModHotspots = file.Bind("Diagnostics", "DumpModHotspots", false,
                "At the main menu write the state the ModHotspots replacements produce (e.g. every ShaderReplacer material " +
                "and its shader) to BepInEx\\Morgott.Preloader\\modhotspots-state.txt, for diffing a toggle off against on.");
            ParallelBiomeData = file.Bind("WorldGen", "ParallelBiomeData", true,
                "Compute the 2048x2048 biome/height map built on every world load and connect (AltBiomeWorldData." +
                "GenerateBiomePoints) on all CPU cores, same per-cell formula, identical result. Falls back to vanilla while another " +
                "mod patches the world generator methods it calls. Read once at launch.");
            EarlyReject = file.Bind("WorldGen", "EarlyReject", true,
                "New worlds: during location placement, run the cheap later checks of a candidate point (distance to similar " +
                "locations, vegetation, alt-biome) before the 10-sample terrain delta, and skip the samples for a candidate those " +
                "checks reject. Same random sequence and same placed locations as vanilla. Read once at launch.");
            LoadingTimeBudget = file.Bind("WorldGen", "LoadingTimeBudget", 0.25f,
                new ConfigDescription("Seconds of location generation per frame while a new world is generated (vanilla 0.1; the " +
                                      "intro/cinematic budget is left alone). 0 = vanilla. Read once at launch.",
                    new AcceptableValueRange<float>(0f, 2f)));
            DumpLocations = file.Bind("WorldGen", "DumpLocations", false,
                "Diagnostic: when locations are generated (or a world with generated locations loads) write every location " +
                "instance (prefab, zone, position as float bits) and the SHA-256 of the biome/height map to " +
                "BepInEx\\Morgott.Preloader\\diag\\locations-<seed>.txt, for diffing WorldGen off against on. Also logs world-gen timings, " +
                "and compares FastLakes against the original lake merge (costs its 1-2 s once per world load).");
            FastLakes = file.Bind("WorldGen", "FastLakes", true,
                "Merge the lake candidates of every world load and connect (WorldGenerator.MergePoints, 1-2 s on the main thread) " +
                "through a spatial grid instead of a full scan per merge step. Same lakes, bit for bit (same list order, distances " +
                "and tie rule). Falls back to vanilla while another mod patches the lake search. Read once at launch.");
            PregenCache = file.Bind("WorldGen", "PregenCache", true,
                "Keep the world generator's river and stream placement of each world (lakes, rivers, streams and the random state " +
                "of their rasterisation, a few hundred KB) in BepInEx\\Morgott.Preloader\\cache\\worldgen and rebuild the river data from " +
                "it on the next load / connect instead of searching again. Keyed by game build, Unity version and the generator's " +
                "seed-derived inputs; off while another mod patches the world generator. Read once at launch.");
            PrefetchBiomeData = file.Bind("WorldGen", "PrefetchBiomeData", true,
                "With ParallelBiomeData: start the biome/height map build on worker threads as soon as the world generator exists " +
                "(ZNet.Awake) instead of in ZNet.Start, so it runs while the rest of the main scene loads. Same map. Read once at launch.");
            PrefetchSectors = file.Bind("WorldGen", "PrefetchSectors", true,
                "With PrefetchBiomeData: also build the biome sectors (flood fill of the map into regions, their edges and " +
                "neighbours) on the prefetch worker; the main-thread part (discovered flags, alt biomes) still runs at the vanilla " +
                "moment. Same sectors. Read once at launch.");
            ParallelMinimap = file.Bind("WorldGen", "ParallelMinimap", true,
                "Build the world minimap (biome colours, forest mask, heights; Minimap.GenerateWorldMap, 4-5 s in one frame on a " +
                "first load and on every join to a server) on all CPU cores, same per-pixel calls, identical textures. Falls back to " +
                "vanilla while another mod patches the world generator or the minimap colour methods. Read once at launch.");
            ClientMinimapCache = file.Bind("WorldGen", "ClientMinimapCache", true,
                "On a client joining a server: keep the generated minimap textures in BepInEx\\Morgott.Preloader\\cache\\minimap\\<seed>_g<gen>_<fp> " +
                "(vanilla file format; vanilla caches them only for worlds hosted locally) and load them on the next join instead of " +
                "generating again. Keyed by world seed, world generator version, game / data versions and the minimap settings; " +
                "the newest 8 worlds are kept; off while another mod patches the world generator. Read once at launch.");
            SpikeProbe = file.Bind("Profiler", "SpikeProbe", false,
                "Record in-play frame times and, for every frame over SpikeThresholdMs, what ran in it: time per Unity player-loop " +
                "subsystem, object creation / zone / location / dungeon room / terrain / asset bundle / texture upload calls, GC " +
                "collections, release-player render and memory counters, first-seen shaders, player state. Writes " +
                "BepInEx\\Morgott.Preloader\\spikes.tsv and spike-summary.txt (every 30 s in play and at quit). Independent of Enabled. " +
                "Diagnostic only, off for normal play. Read once at launch.");
            SpikeThresholdMs = file.Bind("Profiler", "SpikeThresholdMs", 33f,
                new ConfigDescription("SpikeProbe: frames at least this long (ms) are recorded as spikes. Read once at launch.",
                    new AcceptableValueRange<float>(5f, 1000f)));
            ScriptBreakdown = file.Bind("Profiler", "ScriptBreakdown", false,
                "With SpikeProbe: time every mod patch method, plugin Update/FixedUpdate/LateUpdate/OnGUI/coroutine/Tick and the game's own " +
                "per-frame messages (Update/FixedUpdate/LateUpdate/CustomUpdate/UpdateAI), per method and owner, and split the spike frames' " +
                "script time by them. Installed at the main menu (a few seconds); adds a calibrated, reported cost to every wrapped call. Writes " +
                "BepInEx\\Morgott.Preloader\\script-breakdown.txt and the spikes.tsv column scriptTop. Diagnostic only. Read once at launch.");
        }
    }
}
