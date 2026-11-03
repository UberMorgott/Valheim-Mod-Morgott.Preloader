using System.IO;
using System.Text;

namespace Morgott.Preloader.Core
{
    /// <summary>Write-to-temp then replace, so a crash mid-write never leaves a truncated file behind.</summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, null);
            }
            catch (System.Exception e) when (e is IOException || e is System.PlatformNotSupportedException)
            {
                // Mono without ReplaceFile support: overwrite-copy is still whole-file, just not atomic.
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
    }
}
