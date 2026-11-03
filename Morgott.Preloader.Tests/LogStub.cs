namespace Morgott.Preloader.Core
{
    /// <summary>Test stand-in for the BepInEx-backed Core\Log.cs (the only Log call the linked sources make).</summary>
    internal static class Log
    {
        public static void WarningOnce(string key, string message)
        {
        }
    }
}
