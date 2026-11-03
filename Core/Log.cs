using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace Morgott.Preloader.Core
{
    /// <summary>Single BepInEx log source for the patcher; every hook error goes through <see cref="Guard"/>.</summary>
    internal static class Log
    {
        private static readonly ManualLogSource Source = Logger.CreateLogSource("Morgott.Preloader");
        private static readonly HashSet<string> Logged = new HashSet<string>();

        public static void Info(string message) => Source.LogInfo(message);

        public static void Warning(string message) => Source.LogWarning(message);

        public static void Error(string message) => Source.LogError(message);

        // Once per key and process: hot paths (every bundle load) must not flood the log with the same failure.
        public static void WarningOnce(string key, string message)
        {
            if (First(key)) Warning(message);
        }

        public static void InfoOnce(string key, string message)
        {
            if (First(key)) Info(message);
        }

        private static bool First(string key)
        {
            lock (Logged) return Logged.Add(key);
        }

        /// <summary>Runs <paramref name="action"/> and logs instead of throwing: the patcher must never break the game.</summary>
        public static void Guard(string where, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Error($"{where} failed: {e}");
            }
        }
    }
}
