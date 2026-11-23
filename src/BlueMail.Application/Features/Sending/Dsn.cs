using System.Globalization;
using System.Text;
using BlueMail.Infrastructure.Localization;
using BlueMail.Mime;

namespace BlueMail.Application.Features.Sending;

public enum DsnAction { Failed, Delayed }

public sealed record DsnRecipient(string Address, DsnAction Action, string Status, string? RemoteMta, string DiagnosticCode, DateTimeOffset? LastAttempt);

/// <summary>
/// Delivery status notifications (RFC 3464, RFC 6522 multipart/report): a readable part in English and, when
/// different, the sender's language (PROMPT 2.4), the machine-readable message/delivery-status part, and the
/// headers of the original message (text/rfc822-headers, never the full content).
/// </summary>
public static class DsnBuilder
{
    public static byte[] Build(string reportingMta, string to, string? senderLocale, string originalSubject, string? originalMessageId,
        string originalHeaders, IReadOnlyList<DsnRecipient> recipients, DateTimeOffset arrival, string messageId, DateTimeOffset now)
    {
        var delayed = recipients.All(r => r.Action == DsnAction.Delayed);
        var cultures = new List<CultureInfo> { CultureInfo.GetCultureInfo("en") };
        if (senderLocale is not null)
        {
            try
            {
                var c = CultureInfo.GetCultureInfo(senderLocale);
                if (JsonStringLocalizerFactory.Resolve(c) != "en")
                {
                    cultures.Insert(0, c);
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }

        var human = new StringBuilder();
        foreach (var c in cultures)
        {
            var args = new Dictionary<string, object?> { ["subject"] = originalSubject, ["mta"] = reportingMta };
            human.Append(JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(delayed ? "dsn.delayed.intro" : "dsn.failed.intro", c), c, args)).Append("\r\n\r\n");
            foreach (var r in recipients)
            {
                human.Append("  ").Append(r.Address).Append(": ").Append(r.DiagnosticCode).Append("\r\n");
            }

            human.Append("\r\n").Append(JsonStringLocalizerFactory.Get(delayed ? "dsn.delayed.outro" : "dsn.failed.outro", c)).Append("\r\n\r\n");
        }

        var status = new StringBuilder();
        status.Append("Reporting-MTA: dns; ").Append(reportingMta).Append("\r\n");
        status.Append("Arrival-Date: ").Append(Rfc5322Date(arrival)).Append("\r\n");
        foreach (var r in recipients)
        {
            status.Append("\r\n");
            status.Append("Final-Recipient: rfc822; ").Append(r.Address).Append("\r\n");
            status.Append("Action: ").Append(r.Action == DsnAction.Failed ? "failed" : "delayed").Append("\r\n");
            status.Append("Status: ").Append(r.Status).Append("\r\n");
            if (r.RemoteMta is not null)
            {
                status.Append("Remote-MTA: dns; ").Append(r.RemoteMta).Append("\r\n");
            }

            status.Append("Diagnostic-Code: smtp; ").Append(OneLine(r.DiagnosticCode)).Append("\r\n");
            if (r.LastAttempt is { } t)
            {
                status.Append("Last-Attempt-Date: ").Append(Rfc5322Date(t)).Append("\r\n");
            }
        }

        var boundary = "bm-dsn-" + Guid.NewGuid().ToString("N");
        var subject = delayed ? "Delivery Status Notification (Delay)" : "Delivery Status Notification (Failure)";
        var sb = new StringBuilder();
        sb.Append("From: Mail Delivery System <MAILER-DAEMON@").Append(reportingMta).Append(">\r\n");
        sb.Append("To: ").Append(to).Append("\r\n");
        sb.Append("Subject: ").Append(subject).Append("\r\n");
        sb.Append("Date: ").Append(Rfc5322Date(now)).Append("\r\n");
        sb.Append("Message-ID: <").Append(messageId).Append(">\r\n");
        if (originalMessageId is not null)
        {
            sb.Append("In-Reply-To: <").Append(originalMessageId).Append(">\r\n");
            sb.Append("References: <").Append(originalMessageId).Append(">\r\n");
        }

        sb.Append("Auto-Submitted: auto-replied\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: multipart/report; report-type=delivery-status; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Base64Lines(Encoding.UTF8.GetBytes(human.ToString())));
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: message/delivery-status\r\n\r\n").Append(status);
        sb.Append("\r\n--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/rfc822-headers; charset=utf-8\r\n\r\n").Append(originalHeaders.TrimEnd()).Append("\r\n");
        sb.Append("--").Append(boundary).Append("--\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The header block of a raw message (up to the first empty line), at most 64 KB.</summary>
    public static string HeadersOf(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 65536));
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static string OneLine(string s) => s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Rfc5322Date(DateTimeOffset d) => d.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);

    private static string Base64Lines(byte[] data)
    {
        var b64 = Convert.ToBase64String(data);
        var sb = new StringBuilder();
        for (var i = 0; i < b64.Length; i += 76)
        {
            sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        }

        return sb.ToString();
    }
}
