using System.Buffers.Binary;
using System.Text;

namespace BlueMail.Scanner;

/// <summary>A file inside an archive as listed by its directory (content may not be available).</summary>
internal sealed record ArchiveEntry(string Name, long Size, bool Encrypted);

/// <summary>
/// Directory listing of archive formats the BCL cannot open (PROMPT 2.6.4 layer 3): names, sizes and the
/// encryption flag are enough to apply the blocked-type rules to their contents.
/// </summary>
internal static class ArchiveListing
{
    /// <summary>Largest tar member size taken seriously (1 TiB); bigger values are corrupt headers.</summary>
    private const long MaxTarEntryBytes = 1L << 40;

    public const int MaxEntries = 100_000;

    /// <summary>TAR (POSIX ustar and GNU long names).</summary>
    public static List<ArchiveEntry> Tar(Stream s, int maxEntries)
    {
        var list = new List<ArchiveEntry>();
        var header = new byte[512];
        string? longName = null;
        var zeroBlocks = 0;
        while (list.Count < maxEntries && ReadBlock(s, header))
        {
            if (header.All(b => b == 0))
            {
                if (++zeroBlocks == 2)
                {
                    break;
                }

                continue;
            }

            zeroBlocks = 0;
            var name = CString(header.AsSpan(0, 100));
            var prefix = header.AsSpan(257, 5).SequenceEqual("ustar"u8) ? CString(header.AsSpan(345, 155)) : "";
            var size = Octal(header.AsSpan(124, 12));
            if (size is < 0 or > MaxTarEntryBytes)
            {
                break; // impossible size (e.g. GNU base-256 garbage): stop listing, keep what was read (BUG-068)
            }

            var type = (char)header[156];
            var padded = (size + 511) / 512 * 512;
            if (type == 'L')
            {
                var data = new byte[Math.Min(size, 4096)];
                s.ReadExactly(data);
                longName = CString(data);
                Skip(s, padded - data.Length);
                continue;
            }

            var full = longName ?? (prefix.Length > 0 ? prefix + "/" + name : name);
            longName = null;
            if (type is '0' or '\0' or '7')
            {
                list.Add(new ArchiveEntry(full, size, false));
            }

            Skip(s, padded);
        }

        return list;
    }

    private static bool ReadBlock(Stream s, byte[] block)
    {
        var read = 0;
        while (read < block.Length)
        {
            var n = s.Read(block, read, block.Length - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static void Skip(Stream s, long count)
    {
        if (count <= 0)
        {
            return;
        }

        if (s.CanSeek)
        {
            s.Seek(Math.Min(count, s.Length - s.Position), SeekOrigin.Current);
            return;
        }

        var buf = new byte[8192];
        while (count > 0)
        {
            var n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n == 0)
            {
                return;
            }

            count -= n;
        }
    }

    private static string CString(ReadOnlySpan<byte> b)
    {
        var end = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? b : b[..end]);
    }

    private static long Octal(ReadOnlySpan<byte> b)
    {
        if ((b[0] & 0x80) != 0)
        {
            return (long)BinaryPrimitives.ReadUInt64BigEndian(b[4..]); // GNU base-256 size
        }

        long v = 0;
        var started = false;
        foreach (var c in b)
        {
            if (c is >= (byte)'0' and <= (byte)'7')
            {
                v = (v * 8) + (c - '0');
                started = true;
            }
            else if (started)
            {
                break; // terminator (space or NUL) after the digits
            }
        }

        return v;
    }

    /// <summary>Original file name stored in a gzip header (RFC 1952 FNAME), if any.</summary>
    public static string? GzipName(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 || head[0] != 0x1F || head[1] != 0x8B)
        {
            return null;
        }

        var flags = head[3];
        var p = 10;
        if ((flags & 0x04) != 0)
        {
            if (p + 2 > head.Length)
            {
                return null;
            }

            p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(head[p..]);
        }

        if ((flags & 0x08) == 0 || p >= head.Length)
        {
            return null;
        }

        var end = head[p..].IndexOf((byte)0);
        return end < 0 ? null : Encoding.Latin1.GetString(head.Slice(p, end));
    }

    /// <summary>RAR 4.x and 5.x file headers. <paramref name="headersEncrypted"/> when names are not readable.</summary>
    public static List<ArchiveEntry> Rar(Stream s, int maxEntries, out bool headersEncrypted)
    {
        headersEncrypted = false;
        var sig = new byte[8];
        if (s.Read(sig, 0, 7) < 7)
        {
            return [];
        }

        if (sig[6] == 0x01)
        {
            s.ReadByte(); // RAR5 signature has an 8th byte (0x00)
            return Rar5(s, maxEntries, out headersEncrypted);
        }

        return Rar4(s, maxEntries, out headersEncrypted);
    }

    private static List<ArchiveEntry> Rar4(Stream s, int maxEntries, out bool headersEncrypted)
    {
        headersEncrypted = false;
        var list = new List<ArchiveEntry>();
        var head = new byte[7];
        while (list.Count < maxEntries && ReadExact(s, head))
        {
            var type = head[2];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(3));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(5));
            if (size < 7)
            {
                break;
            }

            var rest = new byte[size - 7];
            if (!ReadExact(s, rest))
            {
                break;
            }

            long addSize = 0;
            if (type == 0x73 && (flags & 0x0080) != 0)
            {
                headersEncrypted = true;
                break;
            }

            if ((flags & 0x8000) != 0 && rest.Length >= 4)
            {
                addSize = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            }

            if (type == 0x74 && rest.Length >= 25)
            {
                var unpacked = (long)BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(4));
                var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(19));
                var nameOffset = 25 + ((flags & 0x100) != 0 ? 8 : 0);
                if ((flags & 0x100) != 0)
                {
                    unpacked |= (long)BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(29)) << 32;
                }

                if (nameOffset + nameSize <= rest.Length)
                {
                    var nameBytes = rest.AsSpan(nameOffset, nameSize);
                    var zero = nameBytes.IndexOf((byte)0);
                    var name = Encoding.UTF8.GetString(zero >= 0 ? nameBytes[..zero] : nameBytes); // Unicode names keep a plain part before NUL
                    list.Add(new ArchiveEntry(name.Replace('\\', '/'), unpacked, (flags & 0x04) != 0));
                }
            }

            Skip(s, addSize);
        }

        return list;
    }

    private static List<ArchiveEntry> Rar5(Stream s, int maxEntries, out bool headersEncrypted)
    {
        headersEncrypted = false;
        var list = new List<ArchiveEntry>();
        var crc = new byte[4];
        while (list.Count < maxEntries && ReadExact(s, crc))
        {
            var headerSize = ReadVint(s);
            if (headerSize is null or 0 or > 2 * 1024 * 1024)
            {
                break;
            }

            var header = new byte[headerSize.Value];
            if (!ReadExact(s, header))
            {
                break;
            }

            var p = 0;
            var type = Vint(header, ref p);
            var flags = Vint(header, ref p);
            ulong extraSize = (flags & 0x01) != 0 ? Vint(header, ref p) : 0;
            ulong dataSize = (flags & 0x02) != 0 ? Vint(header, ref p) : 0;
            if (type == 4)
            {
                headersEncrypted = true; // archive encryption header: everything after is encrypted
                break;
            }

            if (type == 2)
            {
                var fileFlags = Vint(header, ref p);
                var unpacked = Vint(header, ref p);
                Vint(header, ref p); // attributes
                if ((fileFlags & 0x02) != 0)
                {
                    p += 4; // mtime
                }

                if ((fileFlags & 0x04) != 0)
                {
                    p += 4; // data CRC32
                }

                Vint(header, ref p); // compression info
                Vint(header, ref p); // host OS
                var nameLenRaw = Vint(header, ref p);
                if (p > header.Length || nameLenRaw > (ulong)(header.Length - p))
                {
                    break; // name length beyond the header: corrupt or hostile
                }

                var nameLen = (int)nameLenRaw;

                var name = Encoding.UTF8.GetString(header, p, nameLen);
                p += nameLen;
                var encrypted = false;
                var extraEnd = (int)Math.Min((ulong)header.Length, (ulong)p + extraSize);
                while (extraSize > 0 && p < extraEnd)
                {
                    var recSize = Vint(header, ref p);
                    var recStart = p;
                    var recType = Vint(header, ref p);
                    if (recType == 0x01)
                    {
                        encrypted = true;
                    }

                    p = (int)Math.Min((ulong)extraEnd, (ulong)recStart + recSize);
                }

                list.Add(new ArchiveEntry(name, (long)Math.Min(unpacked, long.MaxValue), encrypted));
            }

            Skip(s, (long)Math.Min(dataSize, long.MaxValue));
            if (type == 5)
            {
                break; // end of archive
            }
        }

        return list;
    }

    private static ulong? ReadVint(Stream s)
    {
        ulong v = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = s.ReadByte();
            if (b < 0)
            {
                return null;
            }

            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }
        }

        return null;
    }

    private static ulong Vint(byte[] b, ref int p)
    {
        ulong v = 0;
        for (var shift = 0; shift < 64 && p < b.Length; shift += 7)
        {
            var x = b[p++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0)
            {
                break;
            }
        }

        return v;
    }

    private static bool ReadExact(Stream s, byte[] buf)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    /// <summary>
    /// 7z: the directory is in the "next header". 7-Zip compresses it with LZMA by default (kEncodedHeader),
    /// which cannot be read without an LZMA decoder - not available in the BCL. Returns false in that case.
    /// For an uncompressed header (kHeader) the names are read from the FilesInfo kName property.
    /// </summary>
    public static bool SevenZip(Stream s, out List<ArchiveEntry> entries)
    {
        entries = [];
        var start = new byte[32];
        if (!ReadExact(s, start))
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadInt64LittleEndian(start.AsSpan(12));
        var size = BinaryPrimitives.ReadInt64LittleEndian(start.AsSpan(20));
        if (offset < 0 || size is <= 0 or > 16 * 1024 * 1024 || !s.CanSeek || 32 + offset + size > s.Length)
        {
            return false;
        }

        s.Position = 32 + offset;
        var header = new byte[size];
        if (!ReadExact(s, header) || header[0] != 0x01)
        {
            return false; // 0x17 = kEncodedHeader (LZMA-compressed directory)
        }

        // Find FilesInfo (0x05) followed by a kName (0x11) property; names are NUL-terminated UTF-16LE.
        for (var i = 1; i < header.Length - 2; i++)
        {
            if (header[i] != 0x11)
            {
                continue;
            }

            var p = i + 1;
            var len = SevenZipNumber(header, ref p);
            if (len is null || p + (long)len > header.Length || p >= header.Length || header[p] != 0)
            {
                continue;
            }

            var data = header.AsSpan(p + 1, (int)len - 1);
            var names = Encoding.Unicode.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (names.Length > 0)
            {
                entries = names.Select(n => new ArchiveEntry(n.Replace('\\', '/'), 0, false)).ToList();
                return true;
            }
        }

        return false;
    }

    private static ulong? SevenZipNumber(byte[] b, ref int p)
    {
        if (p >= b.Length)
        {
            return null;
        }

        var first = b[p++];
        var mask = 0x80;
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((first & mask) == 0)
            {
                var high = (ulong)(first & (mask - 1));
                return value | (high << (8 * i));
            }

            if (p >= b.Length)
            {
                return null;
            }

            value |= (ulong)b[p++] << (8 * i);
            mask >>= 1;
        }

        return value;
    }
}
