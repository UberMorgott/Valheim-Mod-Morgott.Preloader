using System;
using System.IO;
using System.Text;
using static Morgott.Preloader.BundleCache.BundleHeader;

namespace Morgott.Preloader.BundleCache;

/// <summary>
/// Rewrites an LZMA UnityFS bundle as an uncompressed UnityFS bundle with managed code only (no Unity API, any
/// thread). Layout as <see cref="BundleHeader"/> documents it (AssetStudio BundleFile.cs reads the same): header
/// strings and format version are kept, the block table is written uncompressed at the end of the file (Unity's
/// own LZ4 copies use the same layout) with the source's 16-byte hash and its node directory byte for byte, and
/// the data stream (the decompressed concatenation of all blocks, which node offsets index) is cut into stored
/// blocks of <see cref="LzmaDecoder.ChunkSize"/>. Unity loads stored blocks without decompressing; the post-menu
/// queue later replaces the copy with Unity's own LZ4 recompression (<see cref="CacheStore.UpgradePath"/>).
/// </summary>
internal static class BundleConverter
{
    private const uint CombinedDirectory = 0x40;
    private const uint PaddingAtStart = 0x200;
    private const uint KnownFlags = CompressionMask | CombinedDirectory | TableAtEnd | PaddingAtStart;

    /// <summary>Parsed source layout.</summary>
    public sealed class Source
    {
        public Header Header;
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
        stream.Position = 0;
        Header h = Read(stream);
        if (h == null)
        {
            problem = "not UnityFS";
            return null;
        }
        if ((h.Flags & ~KnownFlags) != 0 || h.Version < 6 || h.Version > 8 || h.Size != stream.Length || h.Table > MaxTableSize || h.PackedTable > MaxTableSize)
        {
            problem = $"unsupported header (version {h.Version}, flags 0x{h.Flags:x}, size {h.Size}/{stream.Length})";
            return null;
        }
        byte[] packed = ReadTable(stream, h, 0);
        byte[] info;
        switch (h.Flags & CompressionMask)
        {
            case 0:
                info = packed;
                break;
            case 1:
                info = new byte[h.Table];
                int at = 0;
                new LzmaDecoder(new MemoryStream(packed), h.PackedTable).Decode(h.Table, (b, o, c) =>
                {
                    Buffer.BlockCopy(b, o, info, at, c);
                    at += c;
                });
                break;
            case 2:
            case 3:
                info = Lz4Block.Decode(packed, (int)h.Table);
                break;
            default:
                problem = "unknown table compression";
                return null;
        }
        if ((h.Flags & PaddingAtStart) != 0)
        {
            stream.Position = (stream.Position + 15) & ~15L;
        }
        var s = new Source { Header = h, DataStart = stream.Position, Hash = new byte[16] };
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
            int c = s.BlockFlags[i] & CompressionMask;
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
    private static string CheckDirectory(byte[] dir, long dataSize)
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
        uint tableLen = (uint)(16 + 4 + (int)blocks * 10 + s.Directory.Length);
        using var file = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        PutCString(file, "UnityFS");
        Put32(file, s.Header.Version);
        PutCString(file, s.Header.PlayerVersion);
        PutCString(file, s.Header.EngineRevision);
        long sizeAt = file.Position;
        Put64(file, 0); // patched below
        Put32(file, tableLen);
        Put32(file, tableLen);
        // Same layout as Unity's own RecompressAssetBundleAsync output in this cache (flags 0xC0: directory in
        // the block table, table uncompressed at the end of the file; data right after the 16-aligned header).
        Put32(file, CombinedDirectory | TableAtEnd);
        while (s.Header.Version >= 7 && (file.Position & 15) != 0)
        {
            file.WriteByte(0);
        }
        // Data: every source block decoded in order; output is written as it is produced.
        long written = 0;
        long blockAt = s.DataStart;
        for (int i = 0; i < s.BlockUncompressed.Length; i++)
        {
            input.Position = blockAt;
            long before = written;
            switch (s.BlockFlags[i] & CompressionMask)
            {
                case 0:
                    var buffer = new byte[1 << 16];
                    for (long left = s.BlockUncompressed[i], n; left > 0; left -= n)
                    {
                        n = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                        if (n <= 0)
                        {
                            throw new EndOfStreamException();
                        }
                        file.Write(buffer, 0, (int)n);
                    }
                    written += s.BlockUncompressed[i];
                    break;
                case 1:
                    var lzma = new LzmaDecoder(input, s.BlockCompressed[i]);
                    lzma.Decode(s.BlockUncompressed[i], (b, offset, count) =>
                    {
                        file.Write(b, offset, count);
                        written += count;
                        progress?.Invoke(written);
                    });
                    if (!lzma.EndsCleanly())
                    {
                        throw new InvalidDataException("LZMA block does not end cleanly at its unpacked size");
                    }
                    break;
                default:
                    byte[] plain = Lz4Block.Decode(Bytes(input, (int)s.BlockCompressed[i]), (int)s.BlockUncompressed[i]);
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
        file.Write(s.Hash, 0, s.Hash.Length);
        Put32(file, (uint)blocks);
        long lastBlock = s.DataSize - (blocks - 1) * chunk;
        for (long i = 0; i < blocks; i++)
        {
            uint len = (uint)(i == blocks - 1 ? lastBlock : chunk);
            Put32(file, len);
            Put32(file, len);
            file.WriteByte(0); // u16 flags 0: stored
            file.WriteByte(0);
        }
        file.Write(s.Directory, 0, s.Directory.Length);
        long total = file.Position;
        file.Position = sizeAt;
        Put64(file, total);
        file.Flush(true);
    }

    private static void PutCString(Stream s, string text)
    {
        byte[] b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
        s.WriteByte(0);
    }

    private static void Put32(Stream s, uint v) => s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }, 0, 4);

    private static void Put64(Stream s, long v)
    {
        Put32(s, (uint)(v >> 32));
        Put32(s, (uint)v);
    }
}
