namespace BlueMail.Scanner;

public enum ScanStatus { Clean = 0, Suspicious = 1, Malicious = 2 }

/// <summary>Stable reason codes; descriptions are translated by the caller ("scanner.&lt;code&gt;").</summary>
public static class ScanCodes
{
    public const string BlockedType = "blocked_type";
    public const string DoubleExtension = "double_extension";
    public const string BidiOverride = "bidi_override_in_name";
    public const string ExtensionMismatch = "extension_mismatch";
    public const string EncryptedArchive = "encrypted_archive";
    public const string ArchiveBomb = "archive_bomb";
    public const string ArchiveTooDeep = "archive_too_deep";
    public const string ArchiveUnreadable = "archive_unreadable";
    public const string ArchiveUnlistable = "archive_unlistable";
    public const string OfficeMacro = "office_macro";
    public const string OfficeRemoteTemplate = "office_remote_template";
    public const string OfficeExternalObject = "office_external_object";
    public const string OfficeDde = "office_dde";
    public const string PdfJavaScript = "pdf_javascript";
    public const string PdfLaunch = "pdf_launch";
    public const string PdfEmbeddedExecutable = "pdf_embedded_executable";
    public const string HtmlScript = "html_script";
    public const string HtmlSmuggling = "html_smuggling";
    public const string HtmlCredentialForm = "html_credential_form";
    public const string SvgScript = "svg_script";
    public const string KnownMalwareHash = "known_malware_hash";
    public const string PatternRule = "pattern_rule";
    public const string TooLarge = "too_large";
}

/// <summary>One reason for a verdict, with the path inside archives ("a.zip/b.docx/word/vbaProject.bin").</summary>
public sealed record ScanFinding(string Code, ScanStatus Severity, string Path, string? Detail = null);

public sealed record ScanResult(ScanStatus Status, IReadOnlyList<ScanFinding> Findings, string DetectedType)
{
    public static ScanResult Clean(string type) => new(ScanStatus.Clean, [], type);
}

/// <summary>
/// A file scanner. Several can be chained (<see cref="CompositeScanner"/>); the most severe verdict wins, so a
/// commercial engine can be added later without touching the callers (PROMPT 2.6.4).
/// </summary>
public interface IFileScanner
{
    Task<ScanResult> ScanAsync(Stream content, string? fileName, CancellationToken ct = default);
}

public sealed class CompositeScanner(IEnumerable<IFileScanner> scanners) : IFileScanner
{
    public async Task<ScanResult> ScanAsync(Stream content, string? fileName, CancellationToken ct = default)
    {
        var findings = new List<ScanFinding>();
        var status = ScanStatus.Clean;
        var type = "unknown";
        foreach (var s in scanners)
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            var r = await s.ScanAsync(content, fileName, ct);
            findings.AddRange(r.Findings);
            if (r.Status > status)
            {
                status = r.Status;
            }

            if (type == "unknown")
            {
                type = r.DetectedType;
            }
        }

        return new ScanResult(status, findings, type);
    }
}

/// <summary>Configurable policy (per tenant). Defaults follow Gmail's blocked list.</summary>
public sealed record ScannerPolicy
{
    public static readonly ScannerPolicy Default = new();

    /// <summary>Extensions blocked wherever they appear (also inside archives).</summary>
    public IReadOnlySet<string> BlockedExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ade", "adp", "apk", "appx", "appxbundle", "bat", "cab", "chm", "cmd", "com", "cpl", "diagcab", "diagcfg", "diagpack",
        "dll", "dmg", "ex", "ex_", "exe", "hta", "img", "ins", "iso", "isp", "jar", "jnlp", "js", "jse", "lib", "lnk", "mde", "msc",
        "msi", "msix", "msixbundle", "msp", "mst", "nsh", "pif", "ps1", "ps1xml", "ps2", "psc1", "psm1", "reg", "scf", "scr",
        "sct", "shb", "sys", "vb", "vbe", "vbs", "vhd", "vhdx", "vxd", "wsc", "wsf", "wsh", "xll", "xlam", "docm", "dotm", "xlsm",
        "xltm", "pptm", "potm", "ppam", "sldm", "elf", "sh", "command", "app", "pkg", "deb", "rpm", "run", "url", "website", "settingcontent-ms",
    };

    /// <summary>Detected content types blocked whatever the name says.</summary>
    public IReadOnlySet<string> BlockedTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FileTypes.WindowsExecutable, FileTypes.Elf, FileTypes.MachO, FileTypes.Lnk, FileTypes.Iso, FileTypes.Vhd, FileTypes.Msi, FileTypes.Chm, FileTypes.Jar, FileTypes.Cab,
    };

    public ScanStatus MacroDocuments { get; init; } = ScanStatus.Suspicious;
    public ScanStatus EncryptedArchives { get; init; } = ScanStatus.Suspicious;
    public ScanStatus UnlistableArchives { get; init; } = ScanStatus.Suspicious;

    public int MaxArchiveDepth { get; init; } = 3;
    public long MaxUncompressedBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxCompressionRatio { get; init; } = 100;
    public int MaxArchiveEntries { get; init; } = 10_000;

    /// <summary>Bytes of one file read for content analysis (PDF/HTML/Office parts).</summary>
    public int MaxInspectBytes { get; init; } = 32 * 1024 * 1024;
}
