using System;
using System.IO;
using System.Text;

namespace Morgott.Preloader.BundleCache
{
    /// <summary>
    /// Rewrites an LZMA UnityFS bundle as an uncompressed UnityFS bundle with managed code only (no Unity API, any
    /// thread). Layout read and written as <see cref="BundleHeader"/> documents it (AssetStudio BundleFile.cs reads
    /// the same): header strings and format version are kept, the block table is written uncompressed at the end of the file (Unity's own LZ4 copies use the same layout) with
    /// the source's 16-byte hash and its node directory byte for byte, and the data stream (the
    /// decompressed concatenation of all blocks, which node offsets index) is cut into stored blocks of
    /// <see cref="LzmaDecoder.ChunkSize"/>. Unity loads stored blocks without decompressing; the post-menu queue
    /// later replaces the copy with Unity's own LZ4 recompression (<see cref="CacheStore.UpgradePath"/>).
    /// </summary>
    internal static class BundleConverter
    {
        private const uint CompressionMask = 0x3F;
        private const uint CombinedDirectory = 0x40;
        private const uint TableAtEnd = 0x80;
        private const uint PaddingAtStart = 0x200;
        private const uint KnownFlags = CompressionMask | CombinedDirectory | TableAtEnd | PaddingAtStart;
        private const int BlockFlagsCompressionMask = 0x3F;

        /// <summary>Parsed source layout.</summary>
        public sealed class Source
        {
            public uint Version;
            public string PlayerVersion;
            public string EngineRevision;
            public uint Flags;
            public long DataStart;
            public byte[] Hash;
            public uint[] BlockUncompressed;
            public uint[] BlockCompressed;
            public ushort[] BlockFlags;
            public byte[] Directory; // node count + nodes, verbatim
            public long DataSize;
            public bool HasLzma;
        }

        /// <summary>Null when the stream is not a UnityFS bundle this converter handles (reason in <paramref name="problem"/>).</summary>
        public static Source Parse(Stream stream, out string problem)
        {
            problem = null;
            stream.Position = 0;
            var r = new Reader(stream);
            if (r.CString() != "UnityFS")
            {
                problem = "not UnityFS";
                return null;
            }
            var s = new Source { Version = r.UInt32(), PlayerVersion = r.CString(), EngineRevision = r.CString() };
            long size = r.Int64();
            uint packedTable = r.UInt32();
            uint table = r.UInt32();
            s.Flags = r.UInt32();
            if ((s.Flags & ~KnownFlags) != 0 || s.Version < 6 || s.Version > 8 || size != stream.Length || table > 64 << 20 || packedTable > 64 << 20)
            {
                problem = $"unsupported header (version {s.Version}, flags 0x{s.Flags:x}, size {size}/{stream.Length})";
                return null;
            }
            if (s.Version >= 7)
            {
                stream.Position = (stream.Position + 15) & ~15L;
            }
            long afterHeader = stream.Position;
            byte[] packed;
            if ((s.Flags & TableAtEnd) != 0)
            {
                stream.Position = stream.Length - packedTable;
                packed = r.Bytes((int)packedTable);
                stream.Position = afterHeader;
            }
            else
            {
                packed = r.Bytes((int)packedTable);
            }
            byte[] info;
            switch (s.Flags & CompressionMask)
            {
                case 0:
                    info = packed;
                    break;
                case 1:
                    using (var ms = new MemoryStream(packed))
                    {
                        info = DecodeLzma(ms, packedTable, table);
                    }
                    break;
                case 2:
                case 3:
                    info = Lz4Block.Decode(packed, (int)table);
                    break;
                default:
                    problem = "unknown table compression";
                    return null;
            }
            if ((s.Flags & PaddingAtStart) != 0)
            {
                stream.Position = (stream.Position + 15) & ~15L;
            }
            s.DataStart = stream.Position;

            s.Hash = new byte[16];
            Buffer.BlockCopy(info, 0, s.Hash, 0, 16);
            int count = Int32(info, 16);
            if (count <= 0 || 20 + (long)count * 10 > info.Length)
            {
                problem = "bad block count";
                return null;
            }
            s.BlockUncompressed = new uint[count];
            s.BlockCompressed = new uint[count];
            s.BlockFlags = new ushort[count];
            long packedSum = 0;
            for (int i = 0; i < count; i++)
            {
                int at = 20 + i * 10;
                s.BlockUncompressed[i] = (uint)Int32(info, at);
                s.BlockCompressed[i] = (uint)Int32(info, at + 4);
                s.BlockFlags[i] = (ushort)((info[at + 8] << 8) | info[at + 9]);
                s.DataSize += s.BlockUncompressed[i];
                packedSum += s.BlockCompressed[i];
                int c = s.BlockFlags[i] & BlockFlagsCompressionMask;
                if (c > 3)
                {
                    problem = "unknown block compression";
                    return null;
                }
                s.HasLzma |= c == 1;
            }
            if (s.DataStart + packedSum > stream.Length)
            {
                problem = "blocks run past the end of the file";
                return null;
            }
            int dirAt = 20 + count * 10;
            s.Directory = new byte[info.Length - dirAt];
            Buffer.BlockCopy(info, dirAt, s.Directory, 0, s.Directory.Length);
            problem = CheckDirectory(s.Directory, s.DataSize);
            return problem == null ? s : null;
        }

        /// <summary>Every node lies inside the data stream; null = fine.</summary>
        public static string CheckDirectory(byte[] dir, long dataSize)
        {
            if (dir.Length < 4)
            {
                return "no node directory";
            }
            int nodes = Int32(dir, 0);
            int at = 4;
            if (nodes <= 0 || nodes > dir.Length / 21)
            {
                return "bad node count";
            }
            for (int i = 0; i < nodes; i++)
            {
                if (at + 21 > dir.Length)
                {
                    return "node directory truncated";
                }
                long offset = Int64(dir, at);
                long size = Int64(dir, at + 8);
                if (offset < 0 || size < 0 || offset > dataSize || size > dataSize - offset)
                {
                    return "node outside the data";
                }
                int end = Array.IndexOf(dir, (byte)0, at + 20);
                if (end < 0)
                {
                    return "node path not terminated";
                }
                at = end + 1;
            }
            return null;
        }

        /// <summary>
        /// Writes the uncompressed copy of <paramref name="input"/> (a parsed source) to <paramref name="output"/>.
        /// <paramref name="progress"/> gets the data bytes written so far. Throws on any inconsistency.
        /// </summary>
        public static void Convert(Stream input, Source s, string output, Action<long> progress)
        {
            int chunk = LzmaDecoder.ChunkSize;
            long blocks = (s.DataSize + chunk - 1) / chunk;
            if (blocks > int.MaxValue / 10)
            {
                throw new InvalidDataException("bundle too large");
            }
            int tableLen = 16 + 4 + (int)blocks * 10 + s.Directory.Length;
            // Same layout as Unity's own RecompressAssetBundleAsync output in this cache (flags 0xC0: directory in
            // the block table, table uncompressed at the end of the file; data right after the 16-aligned header).
            const uint flags = CombinedDirectory | TableAtEnd;
            using (var file = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                var w = new Writer(file);
                w.CString("UnityFS");
                w.UInt32(s.Version);
                w.CString(s.PlayerVersion);
                w.CString(s.EngineRevision);
                long sizeAt = file.Position;
                w.Int64(0); // patched below
                w.UInt32((uint)tableLen);
                w.UInt32((uint)tableLen);
                w.UInt32(flags);
                if (s.Version >= 7)
                {
                    w.Pad16();
                }
                // Data: every source block decoded in order; output is written as it is produced.
                long written = 0;
                long blockAt = s.DataStart;
                for (int i = 0; i < s.BlockUncompressed.Length; i++)
                {
                    input.Position = blockAt;
                    long before = written;
                    switch (s.BlockFlags[i] & BlockFlagsCompressionMask)
                    {
                        case 0:
                            CopyExactly(input, file, s.BlockUncompressed[i]);
                            written += s.BlockUncompressed[i];
                            break;
                        case 1:
                            var lzma = new LzmaDecoder(input, s.BlockCompressed[i]);
                            lzma.Decode(s.BlockUncompressed[i], (buffer, offset, count) =>
                            {
                                file.Write(buffer, offset, count);
                                written += count;
                                progress?.Invoke(written);
                            });
                            if (!lzma.EndsCleanly())
                            {
                                throw new InvalidDataException("LZMA block does not end cleanly at its unpacked size");
                            }
                            break;
                        default:
                            byte[] packedBlock = new Reader(input).Bytes((int)s.BlockCompressed[i]);
                            byte[] plain = Lz4Block.Decode(packedBlock, (int)s.BlockUncompressed[i]);
                            file.Write(plain, 0, plain.Length);
                            written += plain.Length;
                            break;
                    }
                    if (written - before != s.BlockUncompressed[i])
                    {
                        throw new InvalidDataException("block decoded to a different size");
                    }
                    blockAt += s.BlockCompressed[i];
                    progress?.Invoke(written);
                }
                if (written != s.DataSize)
                {
                    throw new InvalidDataException("data size mismatch");
                }
                w.Bytes(s.Hash);
                w.UInt32((uint)blocks);
                long lastBlock = s.DataSize - (blocks - 1) * chunk;
                for (long i = 0; i < blocks; i++)
                {
                    uint len = (uint)(i == blocks - 1 ? lastBlock : chunk);
                    w.UInt32(len);
                    w.UInt32(len);
                    w.UInt16(0); // stored
                }
                w.Bytes(s.Directory);
                long total = file.Position;
                file.Position = sizeAt;
                w.Int64(total);
                file.Flush(true);
            }
        }

        private static byte[] DecodeLzma(Stream input, long packed, uint size)
        {
            var result = new byte[size];
            int at = 0;
            new LzmaDecoder(input, packed).Decode(size, (b, o, c) =>
            {
                Buffer.BlockCopy(b, o, result, at, c);
                at += c;
            });
            return result;
        }

        private static void CopyExactly(Stream from, Stream to, long count)
        {
            var buffer = new byte[1 << 16];
            while (count > 0)
            {
                int n = from.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (n <= 0)
                {
                    throw new EndOfStreamException();
                }
                to.Write(buffer, 0, n);
                count -= n;
            }
        }

        private static int Int32(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        private static long Int64(byte[] b, int at) => ((long)(uint)Int32(b, at) << 32) | (uint)Int32(b, at + 4);

        private sealed class Reader
        {
            private readonly Stream _s;

            public Reader(Stream s) => _s = s;

            public byte[] Bytes(int count)
            {
                var buffer = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = _s.Read(buffer, read, count - read);
                    if (n <= 0)
                    {
                        throw new EndOfStreamException();
                    }
                    read += n;
                }
                return buffer;
            }

            public uint UInt32() => (uint)Int32(Bytes(4), 0);

            public long Int64() => BundleConverter.Int64(Bytes(8), 0);

            public string CString()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 256; i++)
                {
                    int c = _s.ReadByte();
                    if (c < 0)
                    {
                        throw new EndOfStreamException();
                    }
                    if (c == 0)
                    {
                        return sb.ToString();
                    }
                    sb.Append((char)c);
                }
                throw new InvalidDataException("header string too long");
            }
        }

        private sealed class Writer
        {
            private readonly Stream _s;

            public Writer(Stream s) => _s = s;

            public void Bytes(byte[] b) => _s.Write(b, 0, b.Length);

            public void CString(string text)
            {
                Bytes(Encoding.ASCII.GetBytes(text));
                _s.WriteByte(0);
            }

            public void UInt16(ushort v)
            {
                _s.WriteByte((byte)(v >> 8));
                _s.WriteByte((byte)v);
            }

            public void UInt32(uint v) => Bytes(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

            public void Int64(long v)
            {
                UInt32((uint)(v >> 32));
                UInt32((uint)v);
            }

            public void Pad16()
            {
                while ((_s.Position & 15) != 0)
                {
                    _s.WriteByte(0);
                }
            }
        }
    }
}
