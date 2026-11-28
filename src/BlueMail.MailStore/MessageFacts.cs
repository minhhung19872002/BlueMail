using System.Text.Json;
using BlueMail.Mime;

namespace BlueMail.MailStore;

/// <summary>One MIME part as stored in mail.messages.mime_structure (offsets allow fetching one part by range).</summary>
public sealed record PartInfo(string Path, string Type, string? Charset, string? Encoding, string? FileName, string? ContentId,
    bool Inline, bool Attachment, long BodyOffset, long BodyLength);

public sealed record AttachmentFact(string PartPath, string? FileName, string ContentType, long Size, string? ContentId, bool Inline);

/// <summary>Everything the store keeps about a message, computed once from the raw content (PROMPT 2.5).</summary>
public sealed record MessageFacts(
    string? RfcMessageId, string? InReplyTo, IReadOnlyList<string> References, string? Subject, string NormalizedSubject, bool SubjectHadPrefix,
    MailboxAddress? From, IReadOnlyList<MailboxAddress> To, IReadOnlyList<MailboxAddress> Cc, IReadOnlyList<MailboxAddress> Bcc,
    IReadOnlyList<MailboxAddress> ReplyTo, DateTimeOffset? Date, string Snippet, string BodyText, bool HasAttachments,
    IReadOnlyList<PartInfo> Parts, IReadOnlyList<AttachmentFact> Attachments, string? ListId, string? ListUnsubscribe, string? ListUnsubscribePost,
    bool AutoSubmitted, bool Bulk, long Size, short Priority = MessagePriority.Normal, string? DispositionNotificationTo = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MessageFacts Extract(MimeMessage m, Stream source)
    {
        var subject = SearchText.Clean(m.Subject);
        var normalized = SubjectNormalizer.Normalize(subject, out var hadPrefix);
        string body;
        try
        {
            body = m.BodyText(source);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.DecoderFallbackException)
        {
            body = "";
        }

        body = SearchText.Clean(body) ?? "";
        var parts = new List<PartInfo>();
        var attachments = new List<AttachmentFact>();
        foreach (var e in m.Root.Walk())
        {
            parts.Add(new PartInfo(e.PartPath, e.MediaType, e.Charset, e.TransferEncoding, SearchText.Clean(e.FileName), SearchText.Clean(e.ContentId),
                e.IsInline, e.IsAttachment, e.BodyOffset, e.BodyLength));
        }

        // Files: an attached message counts as one file (.eml); what is inside it belongs to that message, not this one.
        foreach (var e in m.Root.WalkOwn())
        {
            if (e != m.Root && e.IsAttachedMessage)
            {
                attachments.Add(new AttachmentFact(e.PartPath, SearchText.Clean(e.FileName) ?? "message.eml", e.MediaType, e.BodyLength, null, false));
                continue;
            }

            var isFile = e.IsLeaf && (e.IsAttachment || (e.FileName is not null && !e.MediaType.StartsWith("text/", StringComparison.Ordinal))
                                      || (e.ContentId is not null && e.MediaType.StartsWith("image/", StringComparison.Ordinal)));
            if (isFile)
            {
                attachments.Add(new AttachmentFact(e.PartPath, SearchText.Clean(e.FileName), e.MediaType, e.BodyLength, SearchText.Clean(e.ContentId),
                    e.IsInline || (e.ContentId is not null && !e.IsAttachment)));
            }
        }

        var precedence = m.Headers.Text("Precedence")?.ToLowerInvariant();
        var auto = m.Headers.Text("Auto-Submitted");
        return new MessageFacts(
            SearchText.Clean(m.MessageId), SearchText.Clean(m.InReplyTo), m.References.Select(r => SearchText.Clean(r)!).Take(100).ToList(),
            subject, normalized, hadPrefix,
            m.From.FirstOrDefault() is { } f ? Clean(f) : null, m.To.Select(Clean).ToList(), m.Cc.Select(Clean).ToList(), m.Bcc.Select(Clean).ToList(),
            m.ReplyTo.Select(Clean).ToList(), m.Date, SearchText.Snippet(body), body, attachments.Any(a => !a.Inline), parts, attachments,
            SearchText.Clean(m.Headers.Text("List-Id")), SearchText.Clean(m.Headers.Text("List-Unsubscribe")), SearchText.Clean(m.Headers.Text("List-Unsubscribe-Post")),
            auto is not null && !auto.Equals("no", StringComparison.OrdinalIgnoreCase), precedence is "bulk" or "list" or "junk", m.Length,
            MessagePriority.Of(m.Headers), ReadReceiptAddress(m));
    }

    /// <summary>
    /// Where the sender wants a read receipt (RFC 8098 2.1): the first Disposition-Notification-To address, never for a
    /// message that is itself a disposition notification (no MDN about an MDN).
    /// </summary>
    private static string? ReadReceiptAddress(MimeMessage m)
    {
        if (m.Root.MediaType == "multipart/report"
            && string.Equals(m.Root.ContentType["report-type"], "disposition-notification", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var to = AddressParser.Parse(m.Headers["Disposition-Notification-To"]?.Value()).FirstOrDefault()?.Address;
        return to is null || to.Length > 320 || !to.Contains('@', StringComparison.Ordinal) ? null : SearchText.Clean(to);
    }

    private static MailboxAddress Clean(MailboxAddress a) => new(SearchText.Clean(a.Name), SearchText.Clean(a.Address)!);
}

/// <summary>
/// The sender's importance, as Outlook and Thunderbird write it: <c>Importance: high|normal|low</c> (RFC 2156),
/// <c>X-Priority: 1..5</c> (1-2 high, 4-5 low) and <c>Priority: urgent|non-urgent</c> (RFC 2156). The first one present wins.
/// </summary>
public static class MessagePriority
{
    public const short High = 1;
    public const short Normal = 3;
    public const short Low = 5;

    public static short Of(HeaderList headers)
    {
        if (headers.Text("Importance")?.Trim().ToLowerInvariant() is { } importance)
        {
            return importance switch { "high" => High, "low" => Low, _ => Normal };
        }

        if (headers.Text("X-Priority")?.Trim() is { Length: > 0 } x && x[0] is >= '1' and <= '5')
        {
            return x[0] switch { '1' or '2' => High, '4' or '5' => Low, _ => Normal };
        }

        return headers.Text("Priority")?.Trim().ToLowerInvariant() switch { "urgent" => High, "non-urgent" => Low, _ => Normal };
    }

    /// <summary>Headers for a message we write (high/low only; normal writes nothing, like Outlook).</summary>
    public static IEnumerable<(string Name, string Value)> Headers(short priority) => priority switch
    {
        High => [("Importance", "high"), ("X-Priority", "1 (Highest)")],
        Low => [("Importance", "low"), ("X-Priority", "5 (Lowest)")],
        _ => [],
    };
}

public static class ReadReceipts
{
    /// <summary>IMAP keyword of RFC 3503: an MDN was sent (or declined) for this message, so no client asks again.</summary>
    public const string SentKeyword = "$MDNSent";
}
