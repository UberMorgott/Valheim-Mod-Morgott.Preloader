using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Morgott.Preloader.Core;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// Epic Loot makes its shard items from the fixed <c>ShardType</c> colour list (<c>Shards.CreateAndLoadShardItems</c>,
    /// EL 0.15.6 Shards.cs:542): a colour whose shardstones.json definition is missing or has no Rarities is skipped with
    /// <c>EpicLoot.LogErrorForce</c> "no usable definition for shard colour '...'". Morgott.EpicLoot patch 96 removes the
    /// 18 plain colour gems on purpose (their effects became affixes), so those 18 lines are noise; the one LogErrorForce
    /// call of that method goes to <see cref="Skip"/>, which swallows only the <see cref="ShardColourFilter.Colours"/> and passes
    /// any other skip through to Epic Loot's own error. One info line names the swallowed colours.
    ///
    /// Timing: Epic Loot runs the method from its own Awake, right after the chainloader loads its assembly, so a plugin
    /// (which a hard Epic Loot dependency sorts after Epic Loot) is too late; this hooks the assembly load from
    /// <c>Finish</c>, before the chainloader, on clients and the dedicated server. By name only: EpicLoot.dll is not
    /// loaded yet and may be absent (then nothing happens).
    /// </summary>
    internal static class EpicLootShardColours
    {
        public const string HarmonyId = "morgott.valheim.preloader.epicloot";
        private const string AssemblyName = "EpicLoot";

        private static readonly List<string> Skipped = new List<string>();
        private static MethodInfo _logErrorForce;

        public static void Install() => AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name != AssemblyName) return;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            // Raised inside BepInEx's Assembly.LoadFile: an install failure must not fail Epic Loot's load.
            Log.Guard("EpicLoot shard colours", () =>
            {
                var target = AccessTools.Method(args.LoadedAssembly.GetType("EpicLoot.ShardStones.Shards"), "CreateAndLoadShardItems");
                _logErrorForce = AccessTools.Method(args.LoadedAssembly.GetType("EpicLoot.EpicLoot"), "LogErrorForce", new[] { typeof(string) });
                if (target == null || _logErrorForce == null)
                {
                    Log.Warning("EpicLoot shard colours: Shards.CreateAndLoadShardItems / EpicLoot.LogErrorForce not found; fix off.");
                    return;
                }
                new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(typeof(EpicLootShardColours), nameof(Report)),
                    transpiler: new HarmonyMethod(typeof(EpicLootShardColours), nameof(Transpiler)));
            });
        }

        /// <summary>The method's single <c>EpicLoot.LogErrorForce(string)</c> call -> <see cref="Skip"/>; any other count = unchanged.</summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var calls = code.Where(c => c.Calls(_logErrorForce)).ToList();
            if (calls.Count != 1)
            {
                Log.Warning($"EpicLoot shard colours: {calls.Count} LogErrorForce calls in CreateAndLoadShardItems (expected 1); fix off.");
                return code;
            }
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(EpicLootShardColours), nameof(Skip));
            return code;
        }

        private static void Skip(string message)
        {
            var colour = ShardColourFilter.Expected(message);
            if (colour != null) Skipped.Add(colour);
            else _logErrorForce.Invoke(null, new object[] { message });
        }

        // "[EpicLootFixes] ... shard colour" is the line the autotest 98a-gems2 no-log check looks for.
        private static void Report()
        {
            if (Skipped.Count == 0) return;
            Log.Info($"[EpicLootFixes] {Skipped.Count} Epic Loot shard colour(s) removed by Morgott.EpicLoot patch 96, no items made: {string.Join(", ", Skipped)}.");
            Skipped.Clear();
        }
    }
}
