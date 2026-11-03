using System;
using System.Collections.Generic;
using System.Reflection;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;

namespace Morgott.Preloader.Translations
{
    /// <summary>
    /// Vanilla <c>Localization.LoadCSV(TextAsset, language)</c> (assembly_guiutils Localization.cs:525-565) reads
    /// <c>file.text</c>, splits the whole CSV and calls <c>AddWord(key, text)</c> (:481) per row; <c>SetupLanguage</c>
    /// (:506) runs it for all 13 vanilla files, 8 times during startup here (English + the user language, again on
    /// every mod's re-setup). The rows only depend on the TextAsset content (immutable) and the language, so the first
    /// run of each (TextAsset instance, language) records the <c>AddWord</c> calls it makes and later runs replay them
    /// through the real <c>AddWord</c> instead of parsing again.
    /// Semantics stay vanilla: the live <c>m_translations</c> is filled by the same AddWord sequence (overlay order,
    /// <c>Clear()</c> on language switch and other mods' AddWord patches all behave as before), LoadCSV returns true as
    /// the original did, and every other mod's LoadCSV prefix/postfix still runs (Harmony runs postfixes when a prefix
    /// skips the original). Nothing is written to disk. Caching is bypassed while any mod transpiles LoadCSV or patches
    /// the parser helpers it would skip (DoQuoteLineSplit, StripCitations), or when another prefix already skipped it.
    /// </summary>
    internal static class LocalizationCache
    {
        private static readonly Dictionary<Key, KeyValuePair<string, string>[]> Rows = new Dictionary<Key, KeyValuePair<string, string>[]>();
        private static readonly string[] OwnIds = { PreloaderPatcher.LocalizationHarmonyId, "morgott.valheim.preloader.profiler" };

        private static MethodInfo _loadCsv;
        private static MethodInfo[] _skippedHelpers;
        private static Action<global::Localization, string, string> _addWord;
        private static int _hits;
        private static int _misses;

        /// <summary>AddWord calls of the LoadCSV run being recorded on this thread (null = not recording).</summary>
        [ThreadStatic] private static List<KeyValuePair<string, string>> _recording;
        [ThreadStatic] private static Key _recordingKey;

        private struct Key : IEquatable<Key>
        {
            public int Asset;
            public string Language;

            public bool Equals(Key other) => Asset == other.Asset && Language == other.Language;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => Asset * 397 ^ (Language?.GetHashCode() ?? 0);
        }

        /// <summary>After Chainloader.Initialize (touches assembly_guiutils types).</summary>
        public static void Install(Harmony harmony)
        {
            Type type = typeof(global::Localization);
            _loadCsv = AccessTools.DeclaredMethod(type, "LoadCSV", new[] { typeof(TextAsset), typeof(string) });
            MethodInfo addWord = AccessTools.DeclaredMethod(type, "AddWord", new[] { typeof(string), typeof(string) });
            _skippedHelpers = new[]
            {
                AccessTools.DeclaredMethod(type, "DoQuoteLineSplit"),
                AccessTools.DeclaredMethod(type, "StripCitations"),
            };
            if (_loadCsv == null || addWord == null || Array.IndexOf(_skippedHelpers, null) >= 0)
            {
                Log.Warning("LocalizationCache: Localization.LoadCSV/AddWord/DoQuoteLineSplit/StripCitations not found (game update?), module off");
                return;
            }
            _addWord = AccessTools.MethodDelegate<Action<global::Localization, string, string>>(addWord);
            harmony.Patch(addWord, prefix: new HarmonyMethod(AccessTools.Method(typeof(LocalizationCache), nameof(AddWordPrefix)), Priority.First));
            harmony.Patch(_loadCsv,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(LocalizationCache), nameof(LoadPrefix)), Priority.Last),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(LocalizationCache), nameof(LoadPostfix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(LocalizationCache), nameof(LoadFinalizer))));
            Lifecycle.MenuReady += () => Log.Info($"LocalizationCache: {_misses} CSV loads parsed, {_hits} replayed until the main menu");
        }

        /// <summary>Last prefix, so earlier prefixes that skip the original are seen through <c>__runOriginal</c>.</summary>
        private static bool LoadPrefix(global::Localization __instance, TextAsset file, string language, ref bool __result, bool __runOriginal)
        {
            _recording = null;
            if (!__runOriginal || file == null || language == null || OthersChangeParsing())
            {
                return true;
            }
            var key = new Key { Asset = file.GetInstanceID(), Language = language };
            KeyValuePair<string, string>[] rows;
            lock (Rows)
            {
                Rows.TryGetValue(key, out rows);
            }
            if (rows == null)
            {
                _misses++;
                _recording = new List<KeyValuePair<string, string>>();
                _recordingKey = key;
                return true;
            }
            _hits++;
            foreach (KeyValuePair<string, string> row in rows)
            {
                _addWord(__instance, row.Key, row.Value);
            }
            __result = true;
            return false;
        }

        private static void AddWordPrefix(string key, string text) => _recording?.Add(new KeyValuePair<string, string>(key, text));

        /// <summary>First postfix sees the original's own result: false (language column missing) is not cached, nor
        /// a run whose original a later Priority.Last prefix of another mod skipped.</summary>
        private static void LoadPostfix(bool __result, bool __runOriginal)
        {
            List<KeyValuePair<string, string>> recorded = _recording;
            _recording = null;
            if (recorded != null && recorded.Count > 0 && __result && __runOriginal)
            {
                lock (Rows)
                {
                    Rows[_recordingKey] = recorded.ToArray();
                }
            }
        }

        private static void LoadFinalizer() => _recording = null;

        private static bool OthersChangeParsing()
        {
            Patches loadCsv = Harmony.GetPatchInfo(_loadCsv);
            if (loadCsv != null && (Foreign(loadCsv.Transpilers) || Foreign(loadCsv.ILManipulators)))
            {
                Log.WarningOnce("loc-bypass-transpiler", "LocalizationCache: another mod transpiles Localization.LoadCSV, cache bypassed");
                return true;
            }
            foreach (MethodInfo helper in _skippedHelpers)
            {
                if (Harmony.GetPatchInfo(helper) is Patches p && (Foreign(p.Prefixes) || Foreign(p.Postfixes) || Foreign(p.Transpilers) ||
                                                                  Foreign(p.Finalizers) || Foreign(p.ILManipulators)))
                {
                    Log.WarningOnce("loc-bypass-" + helper.Name, $"LocalizationCache: another mod patches Localization.{helper.Name}, cache bypassed");
                    return true;
                }
            }
            return false;
        }

        private static bool Foreign(IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches)
            {
                if (Array.IndexOf(OwnIds, patch.owner) < 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
