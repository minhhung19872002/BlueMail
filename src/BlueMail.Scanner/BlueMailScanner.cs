using System.IO.Compression;
using System.Text;

namespace BlueMail.Scanner;

/// <summary>
/// BlueMail's own attachment scanner (PROMPT 2.6.4). It is not an antivirus: it blocks by file type, structure
/// and behaviour - the layer that stops most mail-borne malware - and reports a stable reason code for each
/// finding. Layers 1-6 are here; known-bad hashes and byte-pattern rules (layers 7-8) plug in as further
/// <see cref="IFileScanner"/>s through <see cref="CompositeScanner"/>.
/// </summary>
public sealed class BlueMailScanner(ScannerPolicy? policy = null) : IFileScanner
{
    private const int HeadBytes = 0x8800; // enough for the ISO 9660 signature at 0x8001

    public ScannerPolicy Policy { get; } = policy ?? ScannerPolicy.Default;

    public async Task<ScanResult> ScanAsync(Stream content, string? fileName, CancellationToken ct = default)
    {
        Stream seekable = content;
        FileStream? temp = null;
        if (!content.CanSeek)
        {
            // Spool to a temporary file: never hold a large attachment in memory (PROMPT 8.2).
            temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            await content.CopyToAsync(temp, ct);
            temp.Position = 0;
            seekable = temp;
        }

        try
        {
            var ctx = new Context(Policy, ct);
            var type = Scan(ctx, seekable, fileName ?? "", fileName ?? "", 0);
            var status = ctx.Findings.Count == 0 ? ScanStatus.Clean : ctx.Findings.Max(f => f.Severity);
            return new ScanResult(status, ctx.Findings, type);
        }
        finally
        {
            if (temp is not null)
            {
                await temp.DisposeAsync();
            }
        }
    }

    private sealed class Context(ScannerPolicy policy, CancellationToken ct)
    {
        public ScannerPolicy Policy { get; } = policy;
        public CancellationToken Ct { get; } = ct;
        public List<ScanFinding> Findings { get; } = [];
        public long Decompressed { get; set; }
        public int Entries { get; set; }
        public bool BombReported { get; set; }

        public void Add(string code, ScanStatus severity, string path, string? detail = null)
        {
            if (severity > ScanStatus.Clean && !Findings.Any(f => f.Code == code && f.Path == path))
            {
                Findings.Add(new ScanFinding(code, severity, path, detail));
            }
        }
    }

    /// <summary>Scans one file (top level or inside an archive). Returns the detected type.</summary>
    private string Scan(Context ctx, Stream s, string name, string path, int depth)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        s.Position = 0;
        var head = new byte[(int)Math.Min(HeadBytes, s.Length)];
        s.ReadExactly(head);
        s.Position = 0;
        var type = FileTypes.Detect(head, out var zipBased);
        CheckName(ctx, name, path);

        if (zipBased)
        {
            type = ScanZip(ctx, s, path, depth);
        }
        else if (type == FileTypes.Ole2)
        {
            type = ScanOle2(ctx, s, path);
        }

        CheckType(ctx, type, name, path);

        switch (type)
        {
            case FileTypes.Pdf:
                foreach (var f in PdfAnalyzer.Analyze(path, ReadUpTo(s, ctx.Policy.MaxInspectBytes), IsBlockedName, ctx.Policy.MaxInspectBytes))
                {
                    ctx.Add(f.Code, f.Severity, f.Path, f.Detail);
                }

                break;
            case FileTypes.Html or FileTypes.Svg:
                foreach (var f in HtmlAnalyzer.Analyze(path, DecodeText(ReadUpTo(s, ctx.Policy.MaxInspectBytes)), type == FileTypes.Svg))
                {
                    ctx.Add(f.Code, f.Severity, f.Path, f.Detail);
                }

                break;
            case FileTypes.Gzip:
                ScanGzip(ctx, s, name, path, head, depth);
                break;
            case FileTypes.Tar:
                foreach (var e in ArchiveListing.Tar(s, ctx.Policy.MaxArchiveEntries))
                {
                    CheckEntry(ctx, e, path);
                }

                break;
            case FileTypes.Rar:
                var rarEntries = ArchiveListing.Rar(s, ctx.Policy.MaxArchiveEntries, out var headersEncrypted);
                if (headersEncrypted)
                {
                    ctx.Add(ScanCodes.EncryptedArchive, ctx.Policy.EncryptedArchives, path, "file names are encrypted");
                }

                foreach (var e in rarEntries)
                {
                    CheckEntry(ctx, e, path);
                }

                break;
            case FileTypes.SevenZip:
                if (ArchiveListing.SevenZip(s, out var sevenEntries))
                {
                    foreach (var e in sevenEntries)
                    {
                        CheckEntry(ctx, e, path);
                    }
                }
                else
                {
                    ctx.Add(ScanCodes.ArchiveUnlistable, ctx.Policy.UnlistableArchives, path, "7z directory is LZMA-compressed");
                }

                break;
        }

        return type;
    }

    private void CheckName(Context ctx, string name, string path)
    {
        if (name.Length == 0)
        {
            return;
        }

        if (FileTypes.HasBidiControl(name))
        {
            ctx.Add(ScanCodes.BidiOverride, ScanStatus.Malicious, path, "right-to-left override hides the real extension");
        }

        var exts = FileTypes.Extensions(name.Replace("‮", "", StringComparison.Ordinal));
        if (exts.Count == 0)
        {
            return;
        }

        if (Policy.BlockedExtensions.Contains(exts[0]))
        {
            ctx.Add(ScanCodes.BlockedType, ScanStatus.Malicious, path, "." + exts[0]);
            if (exts.Count > 1 && !Policy.BlockedExtensions.Contains(exts[1]))
            {
                ctx.Add(ScanCodes.DoubleExtension, ScanStatus.Malicious, path, "." + exts[1] + "." + exts[0]);
            }
        }
    }

    private void CheckType(Context ctx, string type, string name, string path)
    {
        if (Policy.BlockedTypes.Contains(type))
        {
            ctx.Add(ScanCodes.BlockedType, ScanStatus.Malicious, path, type);
            return;
        }

        var exts = FileTypes.Extensions(name);
        if (exts.Count > 0 && type is not (FileTypes.Text or FileTypes.Unknown or FileTypes.Html or FileTypes.Svg) && !FileTypes.ExtensionFits(type, exts[0]))
        {
            ctx.Add(ScanCodes.ExtensionMismatch, ScanStatus.Suspicious, path, $"content is {type}, name says .{exts[0]}");
        }
    }

    private void CheckEntry(Context ctx, ArchiveEntry e, string archivePath)
    {
        var p = archivePath + "/" + e.Name;
        if (++ctx.Entries > Policy.MaxArchiveEntries)
        {
            if (!ctx.BombReported)
            {
                ctx.BombReported = true;
                ctx.Add(ScanCodes.ArchiveBomb, ScanStatus.Malicious, archivePath, "too many entries");
            }

            return;
        }

        CheckName(ctx, Path.GetFileName(e.Name), p);
        if (e.Encrypted)
        {
            ctx.Add(ScanCodes.EncryptedArchive, Policy.EncryptedArchives, archivePath, "password-protected entries cannot be inspected");
        }
    }

    private bool IsBlockedName(string name)
    {
        var exts = FileTypes.Extensions(name);
        return exts.Count > 0 && Policy.BlockedExtensions.Contains(exts[0]);
    }

    private string ScanZip(Context ctx, Stream s, string path, int depth)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) // ZLibException is an IOException (BUG-073)
        {
            ctx.Add(ScanCodes.ArchiveUnreadable, ScanStatus.Suspicious, path);
            return FileTypes.Zip;
        }

        using (zip)
        {
            IReadOnlyCollection<ZipArchiveEntry> entries;
            try
            {
                entries = zip.Entries; // the central directory is read lazily and may be corrupt
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) // ZLibException is an IOException (BUG-073)
            {
                ctx.Add(ScanCodes.ArchiveUnreadable, ScanStatus.Suspicious, path);
                return FileTypes.Zip;
            }

            var isOoxml = entries.Any(e => e.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            var isJar = entries.Any(e => e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
                        && entries.Any(e => e.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase));
            var type = isJar ? FileTypes.Jar : isOoxml ? FileTypes.Ooxml : FileTypes.Zip;
            if (isJar)
            {
                return type;
            }

            if (depth >= Policy.MaxArchiveDepth)
            {
                ctx.Add(ScanCodes.ArchiveTooDeep, ScanStatus.Suspicious, path, $"nested deeper than {Policy.MaxArchiveDepth}");
                return type;
            }

            foreach (var e in entries)
            {
                if (e.FullName.EndsWith('/'))
                {
                    continue; // directory
                }

                var entryPath = path + "/" + e.FullName;
                CheckEntry(ctx, new ArchiveEntry(e.FullName, e.Length, e.IsEncrypted), path);
                if (ctx.BombReported)
                {
                    break;
                }

                if (e.CompressedLength > 0 && e.Length > 1024 * 1024 && e.Length / e.CompressedLength > Policy.MaxCompressionRatio)
                {
                    ctx.BombReported = true;
                    ctx.Add(ScanCodes.ArchiveBomb, ScanStatus.Malicious, entryPath, $"compression ratio {e.Length / e.CompressedLength}:1");
                    break;
                }

                if (e.IsEncrypted)
                {
                    continue;
                }

                if (isOoxml && OfficeOpenXml.IsMacroPart(e.FullName))
                {
                    ctx.Add(ScanCodes.OfficeMacro, Policy.MacroDocuments, entryPath);
                    continue;
                }

                var inner = Extract(ctx, e, entryPath);
                if (inner is null)
                {
                    break; // budget exhausted (bomb reported)
                }

                using (inner)
                {
                    if (isOoxml && e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var f in OfficeOpenXml.AnalyzeRelationships(entryPath, DecodeText(inner.ToArray())))
                        {
                            ctx.Add(f.Code, f.Severity, f.Path, f.Detail);
                        }

                        continue;
                    }

                    if (isOoxml && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var f in OfficeOpenXml.AnalyzeDocumentPart(entryPath, DecodeText(inner.ToArray())))
                        {
                            ctx.Add(f.Code, f.Severity, f.Path, f.Detail);
                        }

                        continue;
                    }

                    // Every other member is scanned as a file in its own right (nested archives, documents, executables).
                    Scan(ctx, inner, e.Name, entryPath, depth + 1);
                }
            }

            return type;
        }
    }

    /// <summary>Decompresses one entry, counting real output bytes against the budget (headers can lie).</summary>
    private MemoryStream? Extract(Context ctx, ZipArchiveEntry e, string entryPath)
    {
        var ms = new MemoryStream();
        try
        {
            using var src = e.Open();
            var buf = new byte[81920];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                ctx.Decompressed += n;
                if (ctx.Decompressed > Policy.MaxUncompressedBytes)
                {
                    ctx.BombReported = true;
                    ctx.Add(ScanCodes.ArchiveBomb, ScanStatus.Malicious, entryPath, "expands beyond " + Policy.MaxUncompressedBytes + " bytes");
                    ms.Dispose();
                    return null;
                }

                if (ms.Length < Policy.MaxInspectBytes)
                {
                    ms.Write(buf, 0, (int)Math.Min(n, Policy.MaxInspectBytes - ms.Length));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            // corrupt data, unsupported compression method (e.g. deflate64, lzma) or truncated entry
            ctx.Add(ScanCodes.ArchiveUnreadable, ScanStatus.Suspicious, entryPath, ex.GetType().Name);
        }

        ms.Position = 0;
        return ms;
    }

    private string ScanOle2(Context ctx, Stream s, string path)
    {
        var dir = CompoundFile.Read(ReadUpTo(s, Policy.MaxInspectBytes));
        if (dir is null)
        {
            return FileTypes.Ole2;
        }

        if (dir.IsMsi)
        {
            return FileTypes.Msi;
        }

        if (CompoundFile.HasMacros(dir))
        {
            ctx.Add(ScanCodes.OfficeMacro, Policy.MacroDocuments, path);
        }

        if (CompoundFile.HasEmbeddedPackage(dir))
        {
            ctx.Add(ScanCodes.OfficeExternalObject, ScanStatus.Suspicious, path, "embedded OLE package");
        }

        return FileTypes.Ole2;
    }

    private void ScanGzip(Context ctx, Stream s, string name, string path, byte[] head, int depth)
    {
        var innerName = ArchiveListing.GzipName(head) ?? (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? name[..^4] + ".tar"
            : name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : "");
        if (depth >= Policy.MaxArchiveDepth)
        {
            ctx.Add(ScanCodes.ArchiveTooDeep, ScanStatus.Suspicious, path);
            return;
        }

        var ms = new MemoryStream();
        try
        {
            using var gz = new GZipStream(s, CompressionMode.Decompress, leaveOpen: true);
            var buf = new byte[81920];
            int n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0)
            {
                ctx.Decompressed += n;
                if (ctx.Decompressed > Policy.MaxUncompressedBytes)
                {
                    ctx.Add(ScanCodes.ArchiveBomb, ScanStatus.Malicious, path, "gzip expands beyond the limit");
                    return;
                }

                if (ms.Length < Policy.MaxInspectBytes)
                {
                    ms.Write(buf, 0, (int)Math.Min(n, Policy.MaxInspectBytes - ms.Length));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) // ZLibException is an IOException (BUG-073)
        {
            ctx.Add(ScanCodes.ArchiveUnreadable, ScanStatus.Suspicious, path);
            return;
        }

        using (ms)
        {
            ms.Position = 0;
            Scan(ctx, ms, innerName, path + "/" + (innerName.Length > 0 ? innerName : "(gzip content)"), depth + 1);
        }
    }

    private static byte[] ReadUpTo(Stream s, int max)
    {
        s.Position = 0;
        var len = (int)Math.Min(s.Length, max);
        var buf = new byte[len];
        s.ReadExactly(buf);
        s.Position = 0;
        return buf;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
