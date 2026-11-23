using BlueMail.Domain.Common;

namespace BlueMail.Domain.Admin;

public enum MailDirection { Inbound, Outbound, Internal }

public enum MailLogStatus { Delivered, Rejected, Deferred, Bounced, Quarantined, Queued }

/// <summary>Message tracking (PROMPT 7.6): one row per recipient per hop. Never contains message content.</summary>
public class MailLogEntry : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public MailDirection Direction { get; set; }
    public string? RfcMessageId { get; set; }
    public string? EnvelopeFrom { get; set; }
    public string Rcpt { get; set; } = "";
    public long Size { get; set; }
    public MailLogStatus Status { get; set; }
    public string? RemoteIp { get; set; }
    public string? RemoteHost { get; set; }
    public string? SmtpResponse { get; set; }
    public double? SpamScore { get; set; }

    /// <summary>JSON: SPF/DKIM/DMARC/ARC results, scanner findings, delivery target.</summary>
    public string? Details { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
