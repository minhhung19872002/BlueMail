using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BlueMail.Scanner;

/// <summary>
/// OLE2 / Compound File Binary (MS-CFB) directory reader: lists stream and storage names, which is enough to
/// see VBA projects (.doc/.xls/.ppt), MSI installers and embedded OLE packages (layer 4).
/// </summary>
internal static class CompoundFile
{
    private static readonly Guid MsiClsid = new("000C1084-0000-0000-C000-000000000046");

    public sealed record Directory(IReadOnlyList<string> Names, bool IsMsi);

    public static Directory? Read(byte[] data)
    {
        if (data.Length < 512 || !data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return null;
        }

        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E));
        if (sectorShift is not (9 or 12))
        {
            return null;
        }

        var sectorSize = 1 << sectorShift;
        var fatSectors = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x2C)), 109u);
        var firstDir = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x30));
        long SectorOffset(uint s) => (long)(s + 1) * sectorSize;

        var fat = new List<uint>();
        for (var i = 0; i < fatSectors; i++)
        {
            var fs = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x4C + (i * 4)));
            var off = SectorOffset(fs);
            if (off + sectorSize > data.Length)
            {
                break;
            }

            for (var j = 0; j < sectorSize; j += 4)
            {
                fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + j)));
            }
        }

        var names = new List<string>();
        var isMsi = false;
        var sector = firstDir;
        var guard = 0;
        while (sector < 0xFFFFFFFA && guard++ < 10_000)
        {
            var off = SectorOffset(sector);
            if (off + sectorSize > data.Length)
            {
                break;
            }

            for (var e = 0; e < sectorSize; e += 128)
            {
                var entry = data.AsSpan((int)off + e, 128);
                var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]);
                var type = entry[0x42];
                if (type == 0 || nameLen is < 2 or > 64)
                {
                    continue;
                }

                var name = Encoding.Unicode.GetString(entry[..(nameLen - 2)]);
                names.Add(name);
                if (type == 5 && new Guid(entry.Slice(0x50, 16)) == MsiClsid)
                {
                    isMsi = true;
                }
            }

            sector = sector < fat.Count ? fat[(int)sector] : 0xFFFFFFFE; // follow the chain; loops are bounded by the guard
        }

        return new Directory(names, isMsi);
    }

    public static bool HasMacros(Directory d) =>
        d.Names.Any(n => n.Equals("VBA", StringComparison.OrdinalIgnoreCase) || n.Equals("_VBA_PROJECT", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase) || n.Equals("Macros", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("VBA_PROJECT", StringComparison.OrdinalIgnoreCase));

    public static bool HasEmbeddedPackage(Directory d) => d.Names.Any(n => n.EndsWith("Ole10Native", StringComparison.Ordinal));
}

/// <summary>Office Open XML parts (layer 4): macros, external template/object relationships, DDE fields.</summary>
internal static partial class OfficeOpenXml
{
    public static bool IsMacroPart(string name) =>
        name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) || name.EndsWith("vbaData.xml", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<ScanFinding> AnalyzeRelationships(string path, string xml)
    {
        foreach (Match m in RelationshipRegex().Matches(xml))
        {
            var rel = m.Value;
            if (!rel.Contains("TargetMode=\"External\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rel.Contains("/attachedTemplate", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ScanFinding(ScanCodes.OfficeRemoteTemplate, ScanStatus.Malicious, path, Target(rel));
            }
            else if (rel.Contains("/oleObject", StringComparison.OrdinalIgnoreCase) || rel.Contains("/frame", StringComparison.OrdinalIgnoreCase)
                     || rel.Contains("/subDocument", StringComparison.OrdinalIgnoreCase))
            {
                yield return new ScanFinding(ScanCodes.OfficeExternalObject, ScanStatus.Suspicious, path, Target(rel));
            }
        }
    }

    public static IEnumerable<ScanFinding> AnalyzeDocumentPart(string path, string xml)
    {
        if (DdeRegex().IsMatch(xml) || xml.Contains("<ddeLink", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ScanFinding(ScanCodes.OfficeDde, ScanStatus.Malicious, path);
        }
    }

    private static string? Target(string rel)
    {
        var m = TargetRegex().Match(rel);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("<Relationship\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex RelationshipRegex();

    [GeneratedRegex("Target=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex TargetRegex();

    // DDE / DDEAUTO field codes in Word (w:instrText or fldSimple w:instr) - also split across runs after tag stripping.
    [GeneratedRegex("(?:instr(?:Text)?[^>]*>|w:instr=\")\\s*DDE(?:AUTO)?\\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdeRegex();
}

/// <summary>PDF actions (layer 5). Names are de-obfuscated (#xx escapes) and Flate streams are inflated.</summary>
internal static partial class PdfAnalyzer
{
    public static List<ScanFinding> Analyze(string path, byte[] pdf, Func<string, bool> isBlockedName, int maxInflate)
    {
        var findings = new List<ScanFinding>();
        var texts = new List<string> { Encoding.Latin1.GetString(pdf) };
        foreach (var inflated in InflateStreams(pdf, maxInflate))
        {
            texts.Add(Encoding.Latin1.GetString(inflated));
        }

        var all = NormalizeNames(string.Join('\n', texts));
        var hasJs = JsRegex().IsMatch(all);
        var hasOpenAction = all.Contains("/OpenAction", StringComparison.Ordinal) || all.Contains("/AA", StringComparison.Ordinal);
        if (hasJs)
        {
            findings.Add(new ScanFinding(ScanCodes.PdfJavaScript, hasOpenAction ? ScanStatus.Malicious : ScanStatus.Suspicious, path,
                hasOpenAction ? "script runs automatically on open" : null));
        }

        if (all.Contains("/Launch", StringComparison.Ordinal))
        {
            findings.Add(new ScanFinding(ScanCodes.PdfLaunch, ScanStatus.Malicious, path));
        }

        if (all.Contains("/EmbeddedFile", StringComparison.Ordinal))
        {
            foreach (Match m in FileSpecRegex().Matches(all))
            {
                var name = m.Groups[1].Value;
                if (isBlockedName(name))
                {
                    findings.Add(new ScanFinding(ScanCodes.PdfEmbeddedExecutable, ScanStatus.Malicious, path + "/" + name));
                }
            }
        }

        return findings;
    }

    /// <summary>"/J#61vaScript" -> "/JavaScript" (PDF 7.3.5 name escapes, a common obfuscation).</summary>
    internal static string NormalizeNames(string s) => NameEscapeRegex().Replace(s, m =>
        ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static List<byte[]> InflateStreams(byte[] pdf, int maxTotal)
    {
        var result = new List<byte[]>();
        var total = 0;
        var span = pdf.AsSpan();
        var pos = 0;
        while (total < maxTotal)
        {
            var s = span[pos..].IndexOf("stream"u8);
            if (s < 0)
            {
                break;
            }

            s += pos;
            var dictStart = Math.Max(0, s - 512);
            var dict = Encoding.Latin1.GetString(span[dictStart..s]);
            var start = s + 6;
            if (start < span.Length && span[start] == '\r')
            {
                start++;
            }

            if (start < span.Length && span[start] == '\n')
            {
                start++;
            }

            var e = span[start..].IndexOf("endstream"u8);
            if (e < 0)
            {
                break;
            }

            pos = start + e + 9;
            if (!dict.Contains("FlateDecode", StringComparison.Ordinal) && !dict.Contains("/Fl ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = pdf.AsSpan(start, e).ToArray();
            byte[]? inflated = null;
            try
            {
                using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                var buf = new byte[16 * 1024];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0 && ms.Length < maxTotal - total)
                {
                    ms.Write(buf, 0, n);
                }

                inflated = ms.ToArray();
            }
            catch (InvalidDataException)
            {
                // corrupt stream: ignore
            }

            if (inflated is not null)
            {
                total += inflated.Length;
                result.Add(inflated);
            }
        }

        return result;
    }

    [GeneratedRegex("#([0-9A-Fa-f]{2})")]
    private static partial Regex NameEscapeRegex();

    [GeneratedRegex("/(JavaScript|JS)\\b")]
    private static partial Regex JsRegex();

    [GeneratedRegex("/(?:UF|F)\\s*\\(([^)]{1,255})\\)")]
    private static partial Regex FileSpecRegex();
}

/// <summary>HTML and SVG attachments (layer 6).</summary>
internal static partial class HtmlAnalyzer
{
    public static List<ScanFinding> Analyze(string path, string text, bool isSvg)
    {
        var findings = new List<ScanFinding>();
        var lower = text.ToLowerInvariant();
        var hasScript = lower.Contains("<script", StringComparison.Ordinal) || EventHandlerRegex().IsMatch(text) || lower.Contains("javascript:", StringComparison.Ordinal);
        if (isSvg && hasScript)
        {
            findings.Add(new ScanFinding(ScanCodes.SvgScript, ScanStatus.Malicious, path));
            return findings;
        }

        // HTML smuggling: a large embedded payload assembled into a Blob/data URL and handed to the user as a download.
        var bigBlob = BigBase64Regex().IsMatch(text);
        var assembles = lower.Contains("atob(", StringComparison.Ordinal) || lower.Contains("new blob", StringComparison.Ordinal) || lower.Contains("uint8array", StringComparison.Ordinal);
        var delivers = lower.Contains(".download", StringComparison.Ordinal) || lower.Contains("download=", StringComparison.Ordinal) || lower.Contains("mssaveoropenblob", StringComparison.Ordinal)
                       || lower.Contains("createobjecturl", StringComparison.Ordinal);
        if (bigBlob && assembles && delivers)
        {
            findings.Add(new ScanFinding(ScanCodes.HtmlSmuggling, ScanStatus.Malicious, path));
        }

        // Credential phishing page: a form posting a password field to another site.
        if (lower.Contains("type=\"password\"", StringComparison.Ordinal) || lower.Contains("type='password'", StringComparison.Ordinal) || lower.Contains("type=password", StringComparison.Ordinal))
        {
            var action = FormActionRegex().Match(text);
            if (action.Success && action.Groups[1].Value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new ScanFinding(ScanCodes.HtmlCredentialForm, ScanStatus.Malicious, path, action.Groups[1].Value));
            }
        }

        if (hasScript && findings.Count == 0)
        {
            findings.Add(new ScanFinding(ScanCodes.HtmlScript, ScanStatus.Suspicious, path));
        }

        return findings;
    }

    [GeneratedRegex("\\son[a-z]{3,20}\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();

    [GeneratedRegex("[A-Za-z0-9+/]{4000,}={0,2}")]
    private static partial Regex BigBase64Regex();

    [GeneratedRegex("<form\\b[^>]*\\baction\\s*=\\s*[\"']?([^\"'\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FormActionRegex();
}
