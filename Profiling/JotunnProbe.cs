using System;
using System.Linq;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.Profiling
{
    /// <summary>
    /// Optional Jotunn phases: prefab/item/piece/creature registration in the menu and on world load, their event
    /// invocations and mock reference fixing. Installed after Chainloader.Start, when Jotunn's managers are
    /// initialized: patching them earlier forces their static constructors before Jotunn is ready (observed:
    /// TypeInitializationException in <c>AssetManager</c>, which breaks Jotunn for the session). Members verified in
    /// the installed Jotunn 2.30.2 (ilspycmd); a missing one (other version) is skipped with a warning.
    /// <c>MockManager.FixReferences(object, int)</c> recurses through every member of the fixed object, so only its
    /// outermost call is recorded.
    /// </summary>
    internal static class JotunnProbe
    {
        private static readonly SpanStack Spans = new SpanStack();
        private static readonly SpanStack OuterOnlySpans = new SpanStack();
        private static int _outerOnlyDepth;

        // Instance methods (Jotunn.Managers.*), except MockManager (static; FixReferences recursive: outermost call only).
        // ItemManager/PieceManager/PrefabManager also declare static Harmony forwarders of the same names in their nested
        // Patches classes (different type). Callers: ObjectDB.CopyOtherDB / ObjectDB.Awake / ZNetScene.Awake / ZoneSystem.Start patches.
        private static readonly (string Type, string[] Methods)[] Targets =
        {
            ("PrefabManager", new[] { "InvokeOnVanillaObjectsAvailable", "RegisterAllToZNetScene", "InvokeOnPrefabsRegistered" }),
            ("ItemManager", new[]
            {
                "RegisterCustomDataFejd", "InvokeOnItemsRegisteredFejd", "RegisterCustomData", "InvokeOnVanillaItemsAvailable", "InvokeOnKitbashItemsAvailable",
                "RegisterCustomItems", "RegisterCustomRecipes", "RegisterCustomStatusEffects", "RegisterCustomItemConversions", "InvokeOnItemsRegistered",
            }),
            ("PieceManager", new[] { "RegisterCustomData", "LoadPieceTables", "RegisterInPieceTables", "InvokeOnPiecesRegistered" }),
            ("CreatureManager", new[] { "InvokeOnVanillaCreaturesAvailable", "FixReferences", "InvokeOnCreaturesRegistered" }),
            ("MockManager", new[] { "FixReferences", "FixQueuedMaterials" }),
        };

        public static void Install(Harmony harmony)
        {
            Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Jotunn");
            if (jotunn == null) return;
            int hooked = Targets.Sum(t => t.Methods.Sum(m => Hook(harmony, jotunn, "Jotunn.Managers." + t.Type, m, t.Type == "MockManager")));
            Log.Info($"Jotunn probe: {hooked}/{Targets.Sum(t => t.Methods.Length)} Jotunn methods hooked");
        }

        private static int Hook(Harmony harmony, Assembly assembly, string type, string method, bool isStatic)
        {
            bool outerOnly = isStatic && method == "FixReferences";
            BindingFlags flags = (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo[] found = assembly.GetType(type)?.GetMethods(flags).Where(m => m.Name == method && !m.IsGenericMethodDefinition).ToArray();
            if (found == null || found.Length != 1)
            {
                Log.Warning($"Jotunn probe: {type}.{method} not found or ambiguous ({found?.Length ?? 0} matches), skipped");
                return 0;
            }
            try
            {
                harmony.Patch(found[0],
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(JotunnProbe), outerOnly ? nameof(OuterPrefix) : nameof(Prefix)), Priority.First),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(JotunnProbe), outerOnly ? nameof(OuterFinalizer) : nameof(Finalizer)), Priority.Last));
                return 1;
            }
            catch (Exception e)
            {
                Log.Warning($"Jotunn probe: cannot hook {type}.{method}: {e.Message}");
                return 0;
            }
        }
        private static void Prefix() => Spans.Push();

        private static void Finalizer(MethodBase __originalMethod) => Record(__originalMethod, Spans.Pop());

        // Main thread only (Jotunn registration runs from Unity callbacks).
        private static void OuterPrefix()
        {
            if (_outerOnlyDepth++ == 0) OuterOnlySpans.Push();
        }

        private static void OuterFinalizer(MethodBase __originalMethod)
        {
            if (_outerOnlyDepth > 0 && --_outerOnlyDepth == 0) Record(__originalMethod, OuterOnlySpans.Pop());
        }

        private static void Record(MethodBase original, long start)
        {
            if (start != 0) StartupTrace.Complete("jotunn", original.DeclaringType?.Name + "." + original.Name, start, StartupTrace.Now());
        }
    }
}
