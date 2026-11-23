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
    bool AutoSubmitted, bool Bulk, long Size)
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
            auto is not null && !auto.Equals("no", StringComparison.OrdinalIgnoreCase), precedence is "bulk" or "list" or "junk", m.Length);
    }

    private static MailboxAddress Clean(MailboxAddress a) => new(SearchText.Clean(a.Name), SearchText.Clean(a.Address)!);
}
