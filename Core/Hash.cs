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
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }

        public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        public static string Sha256Hex(byte[] bytes) => Hex(Sha256(bytes));
    }
}
