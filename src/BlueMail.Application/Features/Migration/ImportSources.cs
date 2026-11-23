using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Net;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;

namespace BlueMail.Application.Features.Migration;

/// <summary>
/// Where imports may connect (anti-SSRF): public addresses on the IMAP ports only, unless configured otherwise for
/// test set-ups (BM_IMPORT_ALLOW_PRIVATE, BM_IMPORT_PORTS); extra pinned certificates from BM_IMPORT_TRUSTED_CERTS.
/// </summary>
public sealed class ImportOptions
{
    public bool AllowPrivate { get; init; }
    public IReadOnlySet<int> Ports { get; init; } = new HashSet<int> { 143, 993 };
    public IReadOnlyList<X509Certificate2> TrustedCertificates { get; init; } = [];

    public ImapClientOptions Client(string tls) => new()
    {
        Tls = tls switch { "STARTTLS" => ImapTls.StartTls, _ => ImapTls.Implicit },
        CertificateValidation = TrustedCertificates.Count == 0 ? null : ImapCertificates.WithPinned(TrustedCertificates),
    };

    /// <summary>Resolves the host and returns the address to connect to, or an error code.</summary>
    public async Task<(IPAddress? Address, string? Error)> CheckAsync(string host, int port, CancellationToken ct)
    {
        if (!Ports.Contains(port))
        {
            return (null, "import_port_not_allowed");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await System.Net.Dns.GetHostAddressesAsync(host, ct);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return (null, "import_host_not_found");
        }

        var usable = addresses.Where(a => AllowPrivate || SafeHttpFetcher.IsPublic(a)).ToList();
        return usable.Count == 0 ? (null, addresses.Length == 0 ? "import_host_not_found" : "import_host_not_allowed") : (usable[0], null);
    }
}

/// <summary>How source folders, flags and labels become BlueMail labels (PROMPT 7.7).</summary>
public static class ImportMapping
{
    private static readonly Dictionary<string, string> SystemByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INBOX"] = SystemLabels.Inbox,
        ["Sent"] = SystemLabels.Sent, ["Sent Items"] = SystemLabels.Sent, ["Sent Messages"] = SystemLabels.Sent, ["Sent Mail"] = SystemLabels.Sent,
        ["Đã gửi"] = SystemLabels.Sent, ["Thư đã gửi"] = SystemLabels.Sent,
        ["Drafts"] = SystemLabels.Drafts, ["Draft"] = SystemLabels.Drafts, ["Thư nháp"] = SystemLabels.Drafts, ["Nháp"] = SystemLabels.Drafts,
        ["Trash"] = SystemLabels.Trash, ["Deleted Items"] = SystemLabels.Trash, ["Deleted Messages"] = SystemLabels.Trash, ["Bin"] = SystemLabels.Trash,
        ["Thùng rác"] = SystemLabels.Trash,
        ["Junk"] = SystemLabels.Spam, ["Junk E-mail"] = SystemLabels.Spam, ["Junk Email"] = SystemLabels.Spam, ["Spam"] = SystemLabels.Spam,
        ["Bulk Mail"] = SystemLabels.Spam, ["Thư rác"] = SystemLabels.Spam,
    };

    /// <summary>The labels for a folder: (system label or null, user label path or null). Archive / All Mail give neither.</summary>
    public static (string? System, IReadOnlyList<string>? UserPath) Folder(ImapFolder folder)
    {
        switch (folder.SpecialUse)
        {
            case "\\SENT": return (SystemLabels.Sent, null);
            case "\\DRAFTS": return (SystemLabels.Drafts, null);
            case "\\JUNK": return (SystemLabels.Spam, null);
            case "\\TRASH": return (SystemLabels.Trash, null);
            case "\\FLAGGED": return (SystemLabels.Starred, null);
            case "\\IMPORTANT": return (SystemLabels.Important, null);
            case "\\ARCHIVE" or "\\ALL": return (null, null);
        }

        var parts = folder.Delimiter is { Length: > 0 } d ? folder.Name.Split(d) : [folder.Name];
        if (parts.Length == 1 && SystemByName.TryGetValue(parts[0], out var system))
        {
            return (system, null);
        }

        // "INBOX/Projects" on servers that nest under INBOX: a label "Projects".
        if (parts.Length > 1 && parts[0].Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            parts = parts[1..];
        }

        // Gmail's own folders other than All Mail/Spam/Trash are not imported (their mail is in All Mail with labels).
        return (null, parts);
    }

    /// <summary>Gmail labels from X-GM-LABELS (or X-Gmail-Labels of a Takeout mbox) to system labels and user label paths.</summary>
    public static (List<string> System, List<IReadOnlyList<string>> User) GmailLabels(IEnumerable<string> labels)
    {
        var system = new List<string>();
        var user = new List<IReadOnlyList<string>>();
        foreach (var raw in labels)
        {
            var l = raw.Trim();
            var mapped = l.ToUpperInvariant() switch
            {
                "\\INBOX" or "INBOX" => SystemLabels.Inbox,
                "\\SENT" or "SENT" => SystemLabels.Sent,
                "\\DRAFT" or "\\DRAFTS" or "DRAFT" or "DRAFTS" => SystemLabels.Drafts,
                "\\IMPORTANT" or "IMPORTANT" => SystemLabels.Important,
                "\\STARRED" or "STARRED" => SystemLabels.Starred,
                "\\SPAM" or "SPAM" => SystemLabels.Spam,
                "\\TRASH" or "TRASH" => SystemLabels.Trash,
                _ => null,
            };
            if (mapped is not null)
            {
                system.Add(mapped);
            }
            else if (l.Length > 0 && !l.StartsWith('\\') && !IsTakeoutState(l))
            {
                user.Add(l.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return (system, user);
    }

    /// <summary>Google Takeout writes read state and categories as labels: "Opened", "Unread", "Archived", "Category Updates".</summary>
    private static bool IsTakeoutState(string l) =>
        l is "Opened" or "Unread" or "Archived" || l.StartsWith("Category ", StringComparison.Ordinal);

    /// <summary>IMAP flags: seen, starred, answered, forwarded, keywords (other than system flags), deleted.</summary>
    public static (bool Seen, bool Flagged, bool Answered, bool Forwarded, bool Deleted, List<string> Keywords) Flags(IEnumerable<string> flags)
    {
        bool seen = false, flagged = false, answered = false, forwarded = false, deleted = false;
        var keywords = new List<string>();
        foreach (var f in flags)
        {
            switch (f.ToUpperInvariant())
            {
                case "\\SEEN": seen = true; break;
                case "\\FLAGGED": flagged = true; break;
                case "\\ANSWERED": answered = true; break;
                case "$FORWARDED" or "\\FORWARDED": forwarded = true; break;
                case "\\DELETED": deleted = true; break;
                case "\\RECENT" or "\\DRAFT": break;
                default:
                    if (!f.StartsWith('\\') && f.Length <= 64 && keywords.Count < 20)
                    {
                        keywords.Add(f);
                    }

                    break;
            }
        }

        return (seen, flagged, answered, forwarded, deleted, keywords);
    }

    /// <summary>
    /// State written by other mail programs into mbox files: Gmail Takeout (X-Gmail-Labels), mutt/Pine (Status, X-Status)
    /// and Thunderbird (X-Mozilla-Status bit flags).
    /// </summary>
    public static (bool Seen, bool Flagged, bool Answered, bool Forwarded, List<string> System, List<IReadOnlyList<string>> User, bool HasLabels) MboxState(MimeMessage m)
    {
        bool seen = true, flagged = false, answered = false, forwarded = false;
        var system = new List<string>();
        var user = new List<IReadOnlyList<string>>();
        var hasLabels = false;
        if (m.Headers.Text("X-Gmail-Labels") is { } gmail)
        {
            var labels = SplitLabels(gmail);
            var (s, u) = GmailLabels(labels);
            system.AddRange(s);
            user.AddRange(u);
            seen = !labels.Contains("Unread");
            flagged = s.Contains(SystemLabels.Starred);
            hasLabels = true;
        }
        else if (m.Headers.Text("X-Mozilla-Status") is { } moz && int.TryParse(moz.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
        {
            seen = (bits & 0x0001) != 0;
            answered = (bits & 0x0002) != 0;
            flagged = (bits & 0x0004) != 0;
            forwarded = (bits & 0x1000) != 0;
        }
        else if (m.Headers.Text("Status") is { } status)
        {
            seen = status.Contains('R', StringComparison.Ordinal);
            var x = m.Headers.Text("X-Status") ?? "";
            answered = x.Contains('A', StringComparison.Ordinal);
            flagged = x.Contains('F', StringComparison.Ordinal);
        }

        if (flagged && !system.Contains(SystemLabels.Starred))
        {
            system.Add(SystemLabels.Starred);
        }

        return (seen, flagged, answered, forwarded, system, user, hasLabels);
    }

    /// <summary>Comma-separated labels; a label containing a comma is quoted.</summary>
    public static List<string> SplitLabels(string value)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                list.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            list.Add(sb.ToString().Trim());
        }

        return list.Where(l => l.Length > 0).ToList();
    }
}
