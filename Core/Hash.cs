using System;
using System.Security.Cryptography;

namespace Morgott.Preloader.Core
{
    /// <summary>Bit-exact float and SHA-256 helpers for cache keys and determinism dumps (Unity-free, unit tested).</summary>
    internal static class Hash
    {
        public static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);

        public static byte[] Sha256(byte[] bytes)
        {
#if NET
            return SHA256.HashData(bytes);
#else
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes);
#endif
        }

        public static string Hex(byte[] bytes) =>
#if NET
            Convert.ToHexStringLower(bytes);
#else
            BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
#endif

        public static string Sha256Hex(byte[] bytes) => Hex(Sha256(bytes));
    }
}
