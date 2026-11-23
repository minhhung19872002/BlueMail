using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace BlueMail.Scanner;

/// <summary>A SHA-256 value as four 64-bit words: 32 bytes per entry in a hash set (millions fit in &lt; 200 MB, docs/00 Z7).</summary>
public readonly record struct Sha256Value(ulong A, ulong B, ulong C, ulong D)
{
    public static Sha256Value From(ReadOnlySpan<byte> h) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(h), BinaryPrimitives.ReadUInt64BigEndian(h[8..]), BinaryPrimitives.ReadUInt64BigEndian(h[16..]), BinaryPrimitives.ReadUInt64BigEndian(h[24..]));

    public static bool TryParse(ReadOnlySpan<char> hex, out Sha256Value value)
    {
        value = default;
        if (hex.Length != 64)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[32];
        for (var i = 0; i < 32; i++)
        {
            if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]))
            {
                return false;
            }
        }

        value = From(b);
        return true;
    }

    public override string ToString() => $"{A:x16}{B:x16}{C:x16}{D:x16}";
}

/// <summary>
/// Known-malware SHA-256 list (layer 7, PROMPT 2.6.4; source MalwareBazaar, CC0 - docs/00 Z7). The list is
/// replaced atomically when a new download is loaded, so scans never see a half-built set.
/// </summary>
public sealed class MalwareHashList
{
    private volatile HashSet<Sha256Value> _set = [];

    public int Count => _set.Count;
    public DateTimeOffset? LoadedAt { get; private set; }

    public bool Contains(Sha256Value h) => _set.Contains(h);

    public void Replace(HashSet<Sha256Value> set)
    {
        _set = set;
        LoadedAt = DateTimeOffset.UtcNow;
    }

    public void Add(IEnumerable<Sha256Value> more)
    {
        var copy = new HashSet<Sha256Value>(_set);
        copy.UnionWith(more);
        _set = copy;
        LoadedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reads MalwareBazaar exports: plain text (one hex SHA-256 per line, "#" comments) or a ZIP containing such
    /// a text file. Lines that are not a SHA-256 are skipped and counted (a damaged download is visible in logs).
    /// </summary>
    public static (HashSet<Sha256Value> Set, int Rejected) Parse(Stream data)
    {
        var set = new HashSet<Sha256Value>();
        var rejected = 0;
        var head = new byte[4];
        var n = data.Read(head);
        data.Position = 0;
        if (n == 4 && head[0] == 'P' && head[1] == 'K')
        {
            using var zip = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var e in zip.Entries.Where(e => e.Length > 0 && e.Length < 4L * 1024 * 1024 * 1024))
            {
                using var s = e.Open();
                rejected += ReadLines(s, set);
            }
        }
        else
        {
            rejected += ReadLines(data, set);
        }

        return (set, rejected);
    }

    private static int ReadLines(Stream s, HashSet<Sha256Value> set)
    {
        var rejected = 0;
        using var reader = new StreamReader(s);
        while (reader.ReadLine() is { } line)
        {
            var t = line.AsSpan().Trim();
            if (t.IsEmpty || t[0] == '#')
            {
                continue;
            }

            if (Sha256Value.TryParse(t, out var h))
            {
                set.Add(h);
            }
            else
            {
                rejected++;
            }
        }

        return rejected;
    }
}

/// <summary>
/// Layer 7: the file (and each file inside a ZIP, one level deep) is compared with the known-malware list.
/// A match is MALICIOUS. An empty list (no Auth-Key configured) finds nothing; that state is logged by the loader.
/// </summary>
public sealed class KnownHashScanner(MalwareHashList list) : IFileScanner
{
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    public async Task<ScanResult> ScanAsync(Stream content, string? fileName, CancellationToken ct = default)
    {
        if (list.Count == 0)
        {
            return ScanResult.Clean("");
        }

        var findings = new List<ScanFinding>();
        await using var owned = content.CanSeek ? null : await Buffer(content, ct);
        var seekable = owned ?? content;
        seekable.Position = 0;
        var top = Sha256Value.From(await SHA256.HashDataAsync(seekable, ct));
        if (list.Contains(top))
        {
            findings.Add(new ScanFinding(ScanCodes.KnownMalwareHash, ScanStatus.Malicious, fileName ?? "", top.ToString()));
        }

        seekable.Position = 0;
        if (IsZip(seekable))
        {
            try
            {
                using var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var e in zip.Entries.Take(10_000).Where(e => e.Length is > 0 and <= MaxEntryBytes))
                {
                    ct.ThrowIfCancellationRequested();
                    await using var s = await e.OpenAsync(ct);
                    var h = Sha256Value.From(await SHA256.HashDataAsync(s, ct));
                    if (list.Contains(h))
                    {
                        findings.Add(new ScanFinding(ScanCodes.KnownMalwareHash, ScanStatus.Malicious, (fileName ?? "") + "/" + e.FullName, h.ToString()));
                    }
                }
            }
            catch (InvalidDataException)
            {
                // unreadable archives are reported by layer 3
            }
        }

        seekable.Position = 0;
        return findings.Count == 0 ? ScanResult.Clean("") : new ScanResult(ScanStatus.Malicious, findings, "");
    }

    internal static bool IsZip(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        var n = s.Read(b);
        s.Position = 0;
        return n == 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4;
    }

    internal static async Task<Stream> Buffer(Stream content, CancellationToken ct)
    {
        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await content.CopyToAsync(temp, ct);
        return temp;
    }
}

/// <summary>The scanner every BlueMail process uses: layers 1-6, the known-hash list (7) and the pattern rules (8).</summary>
public static class BlueMailScanners
{
    public static CompositeScanner Create(MalwareHashList? hashes = null, PatternRuleScanner? patterns = null, ScannerPolicy? policy = null) =>
        new([new BlueMailScanner(policy), new KnownHashScanner(hashes ?? new MalwareHashList()), patterns ?? new PatternRuleScanner()]);
}
