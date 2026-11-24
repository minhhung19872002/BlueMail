using System.Globalization;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Audit;
using BlueMail.Domain.Identity;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Localization;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BlueMail.Application.Features.Security;

/// <summary>
/// Mail BlueMail sends about an account (PROMPT 13 security): the "new sign-in" alert when the web is opened from a
/// browser and network not seen before, and the forgotten-password request, which goes to the organisation's
/// administrators (accounts have no recovery address; administrators reset passwords). Notices are delivered to the
/// mailbox, never block the sign-in, and the answer to a forgotten-password request never tells whether the address exists.
/// </summary>
public sealed class AccountNotices(BlueMailDbContext db, SystemMail mail, BmSettings settings, AuditWriter audit, ILogger<AccountNotices> log)
{
    /// <summary>How far back a browser or network counts as known.</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromDays(180);

    /// <summary>One forgotten-password notice per account in this time.</summary>
    public static readonly TimeSpan ResetRequestInterval = TimeSpan.FromHours(1);

    public const string ResetRequestAction = "auth.password_reset_request";

    /// <summary>
    /// True when the user has signed in before, but never (in <see cref="KnownFor"/>) from this browser or this
    /// address. Call before the current sign-in is recorded.
    /// </summary>
    public async Task<bool> IsNewAsync(Guid userId, string? ip, string? userAgent, CancellationToken ct)
    {
        if (ip is null && userAgent is null)
        {
            return false; // nothing to compare
        }

        var since = DateTimeOffset.UtcNow - KnownFor;
        var previous = db.LoginEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.UserId == userId && e.Result == LoginResult.Success && e.Protocol == LoginProtocol.Web && e.OccurredAt > since);
        if (!await previous.AnyAsync(ct))
        {
            return false; // the first sign-in of an account is not news to its owner
        }

        return !await previous.AnyAsync(e => (ip != null && e.Ip == ip) || (userAgent != null && e.UserAgent == userAgent), ct);
    }

    /// <summary>Sends the new sign-in alert to the user's own mailbox; failures are logged, never thrown.</summary>
    public async Task SendNewSignInAsync(User user, string? ip, string? device, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(user.Locale ?? "en");
            var zone = TimeZoneInfo.TryFindSystemTimeZoneById(user.TimeZone ?? "UTC", out var z) ? z : TimeZoneInfo.Utc;
            var local = TimeZoneInfo.ConvertTime(at, zone);
            var args = new Dictionary<string, object?>
            {
                ["email"] = user.PrimaryEmail,
                ["device"] = string.IsNullOrEmpty(device) ? "?" : device,
                ["ip"] = ip ?? "?",
                ["time"] = local.ToString("f", culture) + " (" + zone.Id + ")",
            };
            await NotifyAsync(user, "account.newSignIn", culture, args, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "New sign-in alert for {User} not sent", user.Id);
        }
    }

    /// <summary>
    /// "Forgot password": tells the administrators of the account's organisation (at most once per
    /// <see cref="ResetRequestInterval"/>). Does nothing for unknown or inactive addresses; the caller answers the same.
    /// </summary>
    public async Task RequestPasswordResetAsync(string address, string? ip, string? userAgent, CancellationToken ct)
    {
        // Same normalisation as sign-in (EmailLookupNormalizer): parsed address (A-label domain, NFC), lower case.
        var normalized = new Infrastructure.Identity.EmailLookupNormalizer().NormalizeName(address);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.DeletedAt == null && u.Status == UserStatus.Active, ct);
        if (user is null)
        {
            return;
        }

        var since = DateTimeOffset.UtcNow - ResetRequestInterval;
        var recent = await db.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == ResetRequestAction && a.UserId == user.Id && a.OccurredAt > since, ct);
        await audit.WriteAsync(new AuditEntry(ResetRequestAction, AuditResult.Success, user.TenantId, user.Id, user.PrimaryEmail, ip, userAgent, "user", user.Id.ToString()), ct);
        if (recent)
        {
            return;
        }

        var admins = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == user.TenantId && u.IsTenantAdmin && u.Id != user.Id && u.DeletedAt == null && u.Status == UserStatus.Active)
            .ToListAsync(ct);
        foreach (var admin in admins)
        {
            var culture = CultureInfo.GetCultureInfo(admin.Locale ?? "en");
            await NotifyAsync(admin, "account.resetRequest", culture,
                new Dictionary<string, object?> { ["name"] = user.DisplayName, ["email"] = user.PrimaryEmail, ["ip"] = ip ?? "?" }, ct);
        }

        if (admins.Count == 0)
        {
            log.LogWarning("Password reset requested for {User}, but the organisation has no other active administrator", user.Id);
        }
    }

    private async Task NotifyAsync(User to, string key, CultureInfo culture, Dictionary<string, object?> args, CancellationToken ct)
    {
        var bytes = new MessageBuilder
        {
            From = new MailboxAddress("BlueMail", "no-reply@" + settings.MailHostname),
            To = [new MailboxAddress(to.DisplayName, to.PrimaryEmail)],
            Subject = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".subject", culture), culture, args),
            MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
            Date = DateTimeOffset.UtcNow,
            TextBody = JsonStringLocalizerFactory.Format(JsonStringLocalizerFactory.Get(key + ".body", culture), culture, args),
            ExtraHeaders = [("Auto-Submitted", "auto-generated")],
        }.ToBytes();
        await mail.SendAsync(to.TenantId, null, "", to.PrimaryEmail, bytes, notification: true, to.Locale, null, ct);
    }
}
