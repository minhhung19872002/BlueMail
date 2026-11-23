using System.Text.Json;
using System.Text.RegularExpressions;
using BlueMail.Application.Common;
using BlueMail.Application.Features.Rules;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Net;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.Mime;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Application.Features.Mail;

/// <summary>What a List-Unsubscribe header (RFC 2369) offers, and whether RFC 8058 one-click applies.</summary>
public sealed record UnsubscribeOptions(string? Https, string? Mailto, bool OneClick)
{
    public static UnsubscribeOptions Parse(string? header, string? post)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return new(null, null, false);
        }

        var uris = Regex.Matches(header, "<([^>]+)>", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value.Trim()).ToList();
        var https = uris.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var mailto = uris.FirstOrDefault(u => u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
        var oneClick = https is not null && post is not null && post.Replace(" ", "", StringComparison.Ordinal)
            .Contains("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase);
        return new(https, mailto, oneClick);
    }

    public bool Any => Https is not null || Mailto is not null;
}

/// <summary>DONE (handled by BlueMail) or OPEN_URL (the user must confirm on the sender's page).</summary>
public sealed record UnsubscribeResult(string Outcome, string? Url);

public sealed record UnsubscribeCommand(Guid MessageId, Guid? MailboxId) : IRequest<UnsubscribeResult>;

/// <summary>
/// "Unsubscribe" (PROMPT 6.8 phase 8): one-click POST when the sender supports RFC 8058 (DKIM-signed header, as
/// RFC 8058 requires), else a mail to the mailto: address from the user, else the https page for the user to open.
/// </summary>
public sealed class UnsubscribeHandler(MailboxOpener opener, PgMailStore store, BlueMailDbContext db, SafeHttpFetcher http, SystemMail mail, BmSettings settings)
    : IRequestHandler<UnsubscribeCommand, UnsubscribeResult>
{
    public async Task<UnsubscribeResult> Handle(UnsubscribeCommand c, CancellationToken ct)
    {
        var access = await opener.OpenAsync(c.MailboxId, ct);
        var m = await store.GetMessageAsync(access, c.MessageId, ct) ?? throw AppException.NotFound("message_not_found");
        JsonElement? stored = m.ListUnsubscribe is null ? null : JsonDocument.Parse(m.ListUnsubscribe).RootElement;
        var options = UnsubscribeOptions.Parse(stored?.GetProperty("header").GetString(),
            stored is { } s && s.TryGetProperty("post", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null);
        if (!options.Any)
        {
            throw AppException.BadRequest("no_unsubscribe");
        }

        // RFC 8058 4: one-click only when the List-Unsubscribe(-Post) headers are covered by a valid DKIM signature.
        var dkimPass = m.AuthResults is not null && JsonDocument.Parse(m.AuthResults).RootElement.TryGetProperty("dkim", out var d)
                       && d.ValueKind == JsonValueKind.Array && d.EnumerateArray().Any(x => x.GetProperty("result").GetString() == "pass");
        if (options.OneClick && dkimPass)
        {
            try
            {
                if (await http.PostOneClickUnsubscribeAsync(options.Https!, ct))
                {
                    return new UnsubscribeResult("DONE", null);
                }
            }
            catch (FetchException)
            {
                // fall through to mailto / the page
            }
        }

        if (options.Mailto is { } mailto && ParseMailto(mailto) is ({ } to, var query))
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == access.ActorUserId, ct);
            var bytes = new MessageBuilder
            {
                From = new MailboxAddress(user.DisplayName, user.PrimaryEmail),
                To = [new MailboxAddress(null, to)],
                Subject = query["subject"] ?? "unsubscribe",
                MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
                Date = DateTimeOffset.UtcNow,
                TextBody = query["body"] ?? "unsubscribe",
            }.ToBytes();
            var domain = user.PrimaryEmail[(user.PrimaryEmail.LastIndexOf('@') + 1)..];
            if (await mail.SendAsync(access.TenantId, user.Id, user.PrimaryEmail, to, await mail.SignAsync(access.TenantId, domain, bytes, ct), false, user.Locale, null, ct))
            {
                return new UnsubscribeResult("DONE", null);
            }
        }

        return options.Https is not null ? new UnsubscribeResult("OPEN_URL", options.Https) : throw AppException.BadRequest("no_unsubscribe");
    }

    /// <summary>mailto: URI (RFC 6068): address and the subject/body fields.</summary>
    public static (string? To, System.Collections.Specialized.NameValueCollection Query) ParseMailto(string uri)
    {
        var rest = uri["mailto:".Length..];
        var q = rest.IndexOf('?', StringComparison.Ordinal);
        var to = Uri.UnescapeDataString(q < 0 ? rest : rest[..q]).Split(',')[0].Trim();
        var query = System.Web.HttpUtility.ParseQueryString(q < 0 ? "" : rest[(q + 1)..]);
        return (System.Net.Mail.MailAddress.TryCreate(to, out _) ? to : null, query);
    }
}
