using System;
using System.IO;
using System.Text;

namespace Morgott.Preloader.BundleCache;

internal enum BundleCompression
{
    /// <summary>Not a UnityFS archive or not parseable: never touch the load.</summary>
    Unknown,

    /// <summary>At least one data block is LZMA (or the block table itself is LZMA): recompressing to LZ4 pays.</summary>
    Lzma,

    /// <summary>All data blocks are stored uncompressed or LZ4/LZ4HC: already fast to load, nothing to cache.</summary>
    NotLzma,
}

/// <summary>
/// Reads the UnityFS header and block table to find the data-block compression. The header's own flags only
/// describe the block table (every bundle here, LZMA or LZ4, has an LZ4HC table: flags 0x43/0x243), so the table
/// is decompressed and each block's flags are checked. Layout (big-endian): "UnityFS\0", u32 format version,
/// two C strings (player version, engine revision), i64 file size, u32 compressed / uncompressed table size,
/// u32 flags (bits 0-5 table compression: 0 none, 1 LZMA, 2 LZ4, 3 LZ4HC; 0x80 table at the end of the file);
/// format 7+ aligns the table start to 16 bytes. Table: 16-byte hash, i32 block count, per block u32
/// uncompressed size, u32 compressed size, u16 flags (bits 0-5 = compression).
/// </summary>
internal static class BundleHeader
{
    public const int CompressionMask = 0x3F;
    public const uint TableAtEnd = 0x80;
    public const int MaxTableSize = 64 * 1024 * 1024;

    /// <summary>The fixed header fields; <see cref="Read"/> returns null when the magic is not "UnityFS".</summary>
    public sealed class Header
    {
        public uint Version;
        public string PlayerVersion;
        public string EngineRevision;
        public long Size;
        public uint PackedTable;
        public uint Table;
        public uint Flags;
    }

    public static Header Read(Stream s) => CString(s) != "UnityFS" ? null : new Header
    {
        Version = UInt32(s),
        PlayerVersion = CString(s),
        EngineRevision = CString(s),
        Size = Int64(s),
        PackedTable = UInt32(s),
        Table = UInt32(s),
        Flags = UInt32(s),
    };

    /// <summary>The still packed block table; leaves the stream right after the (aligned) header.</summary>
    public static byte[] ReadTable(Stream stream, Header h, long origin)
    {
        if (h.Version >= 7)
        {
            stream.Position = origin + ((stream.Position - origin + 15) & ~15L);
        }
        if ((h.Flags & TableAtEnd) == 0)
        {
            return Bytes(stream, (int)h.PackedTable);
        }
        long afterHeader = stream.Position;
        stream.Position = stream.Length - h.PackedTable;
        byte[] packed = Bytes(stream, (int)h.PackedTable);
        stream.Position = afterHeader;
        return packed;
    }

    /// <summary>Classifies the bundle at the stream's current position; restores the position afterwards.</summary>
    public static BundleCompression Classify(Stream stream) => Classify(stream, out _, out _);

    /// <summary>
    /// True when <paramref name="path"/> is a complete LZ4/uncompressed UnityFS file: readable block table, no
    /// LZMA block, and the header's total size equals the file length (catches a truncated or foreign file).
    /// </summary>
    public static bool IsCompleteCopy(string path) =>
        WithFile(path, false, f => Classify(f, out long size, out _) == BundleCompression.NotLzma && size == f.Length);

    /// <summary>
    /// The archive's directory: one "path size" line per node (the serialized files and resources inside the
    /// bundle), or null when the block table is LZMA or unreadable. Recompression rewrites blocks but keeps the
    /// nodes, so equal lists mean the copy holds the same files as the source. Restores the stream position.
    /// </summary>
    public static string Nodes(Stream stream)
    {
        Classify(stream, out _, out byte[] table);
        return table == null ? null : NodeList(table);
    }

    public static string Nodes(string path) => WithFile<string>(path, null, Nodes);

    public static FileStream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static T WithFile<T>(string path, T fallback, Func<FileStream, T> read)
    {
        try
        {
            using FileStream file = OpenShared(path);
            return read(file);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>Node table after the block table: i32 count, per node i64 offset, i64 size, u32 flags, C string path.</summary>
    private static string NodeList(byte[] table)
    {
        try
        {
            int at = 20 + Int32(table, 16) * 10;
            int count = Int32(table, at);
            at += 4;
            if (count < 0 || count > table.Length / 21)
            {
                return null;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                long size = Int64(table, at + 8);
                at += 20;
                int end = Array.IndexOf(table, (byte)0, at);
                if (end < 0)
                {
                    return null;
                }
                sb.Append(Encoding.UTF8.GetString(table, at, end - at)).Append(' ').Append(size).Append('\n');
                at = end + 1;
            }
            return sb.ToString();
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static BundleCompression Classify(Stream stream, out long size, out byte[] table)
    {
        size = -1;
        table = null;
        long origin = stream.Position;
        try
        {
            Header h = Read(stream);
            if (h == null)
            {
                return BundleCompression.Unknown;
            }
            size = h.Size;
            if (h.PackedTable > MaxTableSize || h.Table > MaxTableSize || h.Table < 20)
            {
                return BundleCompression.Unknown;
            }
            byte[] packed = ReadTable(stream, h, origin);
            switch (h.Flags & CompressionMask)
            {
                case 0:
                    table = packed;
                    break;
                case 1:
                    return BundleCompression.Lzma; // only LZMA bundles have an LZMA table; not decoded here
                case 2:
                case 3:
                    table = Lz4Block.Decode(packed, (int)h.Table);
                    break;
                default:
                    return BundleCompression.Unknown;
            }
            int count = Int32(table, 16);
            if (count < 0 || 20 + (long)count * 10 > table.Length)
            {
                return BundleCompression.Unknown;
            }
            for (int i = 0; i < count; i++)
            {
                if ((((table[20 + i * 10 + 8] << 8) | table[20 + i * 10 + 9]) & CompressionMask) == 1)
                {
                    return BundleCompression.Lzma;
                }
            }
            return BundleCompression.NotLzma;
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is IndexOutOfRangeException ||
                                  e is ArgumentException || e is EndOfStreamException)
        {
            table = null;
            return BundleCompression.Unknown;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // Big-endian primitives shared with BundleConverter.
    public static int Int32(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    public static long Int64(byte[] b, int at) => ((long)(uint)Int32(b, at) << 32) | (uint)Int32(b, at + 4);

    public static uint UInt32(Stream s) => (uint)Int32(Bytes(s, 4), 0);

    public static long Int64(Stream s) => Int64(Bytes(s, 8), 0);

    public static byte[] Bytes(Stream s, int count)
    {
        var buffer = new byte[count];
        for (int read = 0, n; read < count; read += n)
        {
            n = s.Read(buffer, read, count - read);
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }
        }
        return buffer;
    }

    /// <summary>Null-terminated ASCII, at most 256 bytes (header strings are short version numbers).</summary>
    public static string CString(Stream s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 256; i++)
        {
            int c = s.ReadByte();
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

/// <summary>LZ4 block format decoder (no frame header), enough for UnityFS block tables.</summary>
internal static class Lz4Block
{
    public static byte[] Decode(byte[] src, int outputSize)
    {
        var dst = new byte[outputSize];
        int s = 0;
        int d = 0;
        while (s < src.Length)
        {
            int token = src[s++];
            int literals = Length(src, ref s, token >> 4);
            Buffer.BlockCopy(src, s, dst, d, literals);
            s += literals;
            d += literals;
            if (s >= src.Length)
            {
                break; // the last sequence carries literals only
            }
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            int match = Length(src, ref s, token & 15) + 4;
            int from = d - offset;
            if (offset == 0 || from < 0 || d + match > dst.Length)
            {
                throw new InvalidDataException("corrupt LZ4 block");
            }
            for (int i = 0; i < match; i++)
            {
                dst[d++] = dst[from + i]; // byte by byte: source and target may overlap
            }
        }
        if (d != outputSize)
        {
            throw new InvalidDataException("LZ4 block size mismatch");
        }
        return dst;
    }

    /// <summary>A 4-bit length; 15 continues with bytes added until one is below 255.</summary>
    private static int Length(byte[] src, ref int s, int length)
    {
        if (length == 15)
        {
            int b;
            do
            {
                b = src[s++];
                length += b;
            }
            while (b == 255);
        }
        return length;
    }
}
