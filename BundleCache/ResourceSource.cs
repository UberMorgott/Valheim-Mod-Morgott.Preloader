using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Morgott.Preloader.BundleCache
{
    /// <summary>
    /// A bundle embedded as a manifest resource in a loaded assembly. Identity = module MVID + resource name:
    /// the MVID changes with every build of the assembly (a fresh GUID, or a content hash for deterministic
    /// builds) and the resource bytes are part of that build, so equal identity means equal bytes. No hashing of
    /// the bundle is needed; a mod update is a new MVID, so its old cache copy is simply never hit again.
    /// </summary>
    internal sealed class ResourceSource
    {
        public ResourceSource(Assembly assembly, Guid mvid, string resourceName, long length)
        {
            Assembly = assembly;
            Mvid = mvid;
            ResourceName = resourceName;
            Length = length;
            FileName = mvid.ToString("N") + "-" + Fnv1a64(resourceName).ToString("x16") + ".bundle";
        }

        public Assembly Assembly { get; }

        public Guid Mvid { get; }

        public string ResourceName { get; }

        public long Length { get; }

        /// <summary>Cache file name: "&lt;mvid N&gt;-&lt;fnv64(resource name)&gt;.bundle". The MVID prefix lets
        /// maintenance drop copies of assemblies that are no longer loaded without any index.</summary>
        public string FileName { get; }

        public string Label => Assembly.GetName().Name + ":" + ResourceName;

        private static ulong Fnv1a64(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }
            return hash;
        }
    }

    /// <summary>
    /// Maps a stream passed to <c>AssetBundle.LoadFromStream</c> back to its manifest resource. Mono's
    /// <c>RuntimeAssembly.GetManifestResourceStream(string)</c> returns a
    /// <c>RuntimeAssembly+UnmanagedMemoryStreamForModule</c> (Unity mscorlib decompile line 228585/229007) that
    /// points into the loaded image and keeps the owning <c>Module</c> in its private field <c>module</c>. The
    /// resource name is found by matching the stream's start address against the assembly's own resource streams.
    /// </summary>
    internal static class ResourceResolver
    {
        public static readonly Type StreamType =
            typeof(Assembly).Assembly.GetType("System.Reflection.RuntimeAssembly+UnmanagedMemoryStreamForModule");

        private static readonly FieldInfo ModuleField = StreamType == null ? null : AccessTools.Field(StreamType, "module");
        private static readonly Dictionary<Assembly, Dictionary<long, string>> NamesByAddress =
            new Dictionary<Assembly, Dictionary<long, string>>();

        public static bool Available => ModuleField != null;

        /// <summary>Null when the stream is not an unread manifest resource stream of a loaded assembly.</summary>
        public static ResourceSource Resolve(Stream stream)
        {
            if (stream == null || stream.GetType() != StreamType || stream.Position != 0)
            {
                return null;
            }
            if (!(ModuleField.GetValue(stream) is Module module))
            {
                return null;
            }
            string name = NameOf(module.Assembly, StartAddress((UnmanagedMemoryStream)stream));
            return name == null ? null : new ResourceSource(module.Assembly, module.ModuleVersionId, name, stream.Length);
        }

        private static unsafe long StartAddress(UnmanagedMemoryStream stream) => (long)stream.PositionPointer - stream.Position;

        private static string NameOf(Assembly assembly, long address)
        {
            lock (NamesByAddress)
            {
                if (!NamesByAddress.TryGetValue(assembly, out Dictionary<long, string> names))
                {
                    names = new Dictionary<long, string>();
                    foreach (string name in assembly.GetManifestResourceNames())
                    {
                        using (Stream resource = assembly.GetManifestResourceStream(name))
                        {
                            if (resource is UnmanagedMemoryStream memory)
                            {
                                names[StartAddress(memory)] = name;
                            }
                        }
                    }
                    NamesByAddress[assembly] = names;
                }
                return names.TryGetValue(address, out string found) ? found : null;
            }
        }
    }
}
