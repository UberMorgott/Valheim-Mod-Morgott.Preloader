using Morgott.Preloader.Core;
using HarmonyLib;

namespace Morgott.Preloader.WorldGen
{
    /// <summary>
    /// <c>ZoneSystem.Update</c> (ValheimDecompiled-1.0.16 ZoneSystem.cs:1149-1159) sets <c>m_timeSlicedGenerationTimeBudget</c>
    /// every frame while the server has not generated its locations: a frame-rate budget during the intro / cinematic,
    /// 0.1 s otherwise; the location coroutine yields once a slice exceeds it. A postfix raises the 0.1 s case to the
    /// configured budget and puts vanilla's value back once that branch is left. Placement does not depend on where the
    /// coroutine yields: the generation RNG state is swapped around every yield (:1906-1911) and each type reseeds (:1876).
    /// </summary>
    internal static class LoadingTimeBudget
    {
        private static AccessTools.FieldRef<ZoneSystem, float> _budget;
        private static float _value;
        private static bool _raised;
        private static float _vanilla;

        public static void Install(Harmony harmony, float budget)
        {
            if (AccessTools.DeclaredField(typeof(ZoneSystem), "m_timeSlicedGenerationTimeBudget") == null)
            {
                Log.Warning("LoadingTimeBudget: ZoneSystem.m_timeSlicedGenerationTimeBudget not found (game update?), module off");
                return;
            }
            _budget = AccessTools.FieldRefAccess<ZoneSystem, float>("m_timeSlicedGenerationTimeBudget");
            _value = budget;
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), "Update"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(LoadingTimeBudget), nameof(UpdatePostfix))));
        }

        private static void UpdatePostfix(ZoneSystem __instance)
        {
            // Same condition as the vanilla branch that writes 0.1 (ZoneSystem.cs:1145, 1149, 1151).
            bool vanillaBranch = ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected && ZNet.instance != null &&
                                 ZNet.instance.IsServer() && !__instance.LocationsGenerated &&
                                 !(TextViewer.IsShowingIntro() || CinematicsManager.IsPlaying());
            ref float budget = ref _budget(__instance);
            if (vanillaBranch)
            {
                if (budget != _value) _vanilla = budget;
                budget = _value;
                _raised = true;
            }
            else if (_raised)
            {
                _raised = false;
                if (__instance.LocationsGenerated && budget == _value) budget = _vanilla;
            }
        }
    }
}
