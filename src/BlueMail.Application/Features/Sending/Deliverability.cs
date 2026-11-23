namespace BlueMail.Application.Features.Sending;

/// <summary>
/// Warm-up of a new sending IP (PROMPT 2.4): the number of messages it may hand to other servers per UTC day rises
/// over about six weeks (the usual ramp mailbox providers recommend), then the cap is lifted.
/// </summary>
public static class WarmupSchedule
{
    private static readonly int[] Caps =
    [
        50, 100, 200, 300, 500, 750, 1_000, 1_500, 2_000, 3_000, 4_000, 5_000, 6_500, 8_000, 10_000, 12_500, 15_000, 18_000, 21_000, 25_000,
        30_000, 35_000, 40_000, 50_000, 60_000, 70_000, 80_000, 90_000, 100_000, 120_000, 140_000, 160_000, 180_000, 200_000, 250_000, 300_000,
        350_000, 400_000, 450_000, 500_000, 600_000, 700_000,
    ];

    public static int Days => Caps.Length;

    /// <summary>Daily cap for an IP whose warm-up started at <paramref name="started"/>; null = no cap (warm, or warm-up finished).</summary>
    public static int? CapOn(DateTimeOffset? started, DateTimeOffset now)
    {
        if (started is null)
        {
            return null;
        }

        var day = (int)Math.Floor((now.UtcDateTime.Date - started.Value.UtcDateTime.Date).TotalDays);
        return day < 0 ? Caps[0] : day >= Caps.Length ? null : Caps[day];
    }
}

/// <summary>SMTP TLS reporting result types (RFC 8460 4.3) used by the outbound queue.</summary>
public static class TlsFailures
{
    public const string StartTlsNotSupported = "starttls-not-supported";
    public const string CertificateHostMismatch = "certificate-host-mismatch";
    public const string ValidationFailure = "validation-failure";
    public const string TlsaInvalid = "tlsa-invalid";
    public const string DnssecInvalid = "dnssec-invalid";
    public const string StsPolicyInvalid = "sts-policy-invalid";
    public const string StsWebpkiInvalid = "sts-webpki-invalid";
}
