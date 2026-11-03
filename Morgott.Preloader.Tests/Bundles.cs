using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Morgott.Preloader.Tests
{
    /// <summary>Synthetic one-block, one-node UnityFS bundles (layout as BundleHeader documents it).</summary>
    internal static class Bundles
    {
        public const string NodePath = "CAB-test";

        /// <param name="tableFlags">Header flags; 0x80 = table at the end, low bits 2/3 = LZ4 table (encoded here).</param>
        public static byte[] Build(uint version, uint tableFlags, ushort blockFlags, byte[] block, uint unpacked)
        {
            var table = new List<byte>(new byte[16]); // hash
            BE(table, 1, 4);
            BE(table, unpacked, 4);
            BE(table, (ulong)block.Length, 4);
            BE(table, blockFlags, 2);
            BE(table, 1, 4); // nodes
            BE(table, 0, 8);
            BE(table, unpacked, 8);
            BE(table, 4, 4);
            table.AddRange(Encoding.ASCII.GetBytes(NodePath + "\0"));
            byte[] packed = (tableFlags & 0x3F) is 2 or 3 ? Lz4Literals(table.ToArray()) : table.ToArray();

            var b = new List<byte>(Encoding.ASCII.GetBytes("UnityFS\0"));
            BE(b, version, 4);
            b.AddRange(Encoding.ASCII.GetBytes("5.x.x\0" + "2022.3.0f1\0"));
            int sizeAt = b.Count;
            BE(b, 0, 8);
            BE(b, (ulong)packed.Length, 4);
            BE(b, (ulong)table.Count, 4);
            BE(b, tableFlags, 4);
            while (version >= 7 && b.Count % 16 != 0)
            {
                b.Add(0);
            }
            bool atEnd = (tableFlags & 0x80) != 0;
            b.AddRange(atEnd ? block : packed);
            b.AddRange(atEnd ? packed : block);
            byte[] bytes = b.ToArray();
            for (int i = 0; i < 8; i++)
            {
                bytes[sizeAt + i] = (byte)((ulong)bytes.Length >> (56 - 8 * i));
            }
            return bytes;
        }

        /// <summary>An LZ4 block holding <paramref name="data"/> as one literal run.</summary>
        public static byte[] Lz4Literals(byte[] data)
        {
            var o = new List<byte> { (byte)(System.Math.Min(data.Length, 15) << 4) };
            if (data.Length >= 15)
            {
                int rest = data.Length - 15;
                for (; rest >= 255; rest -= 255)
                {
                    o.Add(255);
                }
                o.Add((byte)rest);
            }
            o.AddRange(data);
            return o.ToArray();
        }

        public static string TempFile(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "mp-bundle-" + System.Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void BE(List<byte> o, ulong v, int bytes)
        {
            for (int i = bytes - 1; i >= 0; i--)
            {
                o.Add((byte)(v >> (8 * i)));
            }
        }
    }
}
