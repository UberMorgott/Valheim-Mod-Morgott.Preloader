# Step 3.L1 invariants (FastStartup -> Morgott.Preloader)

Kept:
- BepInEx preloader patcher contract (TargetDLLs / Patch / Initialize / Finish), installed in `BepInEx\patchers`; no
  BepInPlugin, no Core / Jotunn reference (plan 5b exception: runs before Jotunn exists).
- Every module and its default: BundleCache (cache key, format v1, validation, LRU cap, eager first build),
  ConfigSaveBatcher, LocalizationCache, HarmonyBatching, ModHotspots (ShaderReplacer, VNEI indexing unchanged until IB.5),
  WorldGen (bit-identical outputs, vanilla fallback while another mod patches the generator, PregenCache / minimap cache
  formats), Diagnostics dumps.
- Startup profiler: trace.json / summary.txt / trace-world.json / summary-world.txt, "menu ready" / "first spawn" log
  line, TimeModPatches, SpikeProbe (spikes.tsv, spike-summary.txt, `SpikeProbe.Segment` / `Flush`), ScriptBreakdown
  (`ScriptProbe` members the harness reflects), crash skip-lists.

Intended changes:
- Assembly / namespace `Morgott.Preloader`, entry class `PreloaderPatcher`, cfg `morgott.valheim.preloader.cfg`
  (cfgport map `docs/cfgport-3.L1.map`: same sections and keys; installed cfg had only defaults), log source
  `Morgott.Preloader`, data dir `BepInEx\Morgott.Preloader\` (old `BepInEx\FastStartup\` is not migrated: one cold
  bundle / worldgen / minimap cache start), Harmony ids `morgott.valheim.preloader.*`.
- Removed (unused diagnostics / never shipped): `[Profiler] TimeSpawnWindow` (spawn-window + frame probe),
  `[Profiler] TimeUnityMessages`, the modpack prebuilt bundle cache (`pack\bundles`, `tools\build-pack-cache.ps1`).
- Consumers that must switch in the same release set: harness reflection `Morgott.Preloader.Profiling.SpikeProbe` /
  `ScriptProbe`, perf-audit cfg / output paths, mp.ps1 cache junction; pack mods.json entry
  `../patchers/Morgott.Preloader.dll` (old `../patchers/FastStartup.dll` disabled + removed), pack junk dir
  `Morgott.Preloader`, updater ready pattern `Morgott.Preloader] menu ready`.
- Verify (in game, not yet run): startup trace written, bundle cache cold then hit on the second start, dedicated server
  unaffected (client-only patcher).
