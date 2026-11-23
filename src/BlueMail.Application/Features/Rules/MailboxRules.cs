using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueMail.Dkim;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Configuration;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BlueMail.Application.Features.Rules;

/// <summary>Filter actions (PROMPT 6.8), stored as JSON in mail.filters.actions.</summary>
public sealed record FilterActions
{
    public bool SkipInbox { get; init; }
    public bool MarkRead { get; init; }
    public bool Star { get; init; }
    public IReadOnlyList<Guid> LabelIds { get; init; } = [];
    public string? ForwardTo { get; init; }
    public bool Trash { get; init; }
    public bool NeverSpam { get; init; }

    /// <summary>ALWAYS / NEVER / null.</summary>
    public string? Important { get; init; }

    /// <summary>PRIMARY / SOCIAL / PROMOTIONS / UPDATES / FORUMS / null.</summary>
    public string? Category { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static FilterActions Parse(string json) => JsonSerializer.Deserialize<FilterActions>(json, Json) ?? new FilterActions();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public bool IsEmpty => !SkipInbox && !MarkRead && !Star && LabelIds.Count == 0 && ForwardTo is null && !Trash && !NeverSpam && Important is null && Category is null;
}

/// <summary>What arrived, as the delivery path knows it.</summary>
public sealed record IncomingMessage(
    MessageFacts Facts,
    byte[] Raw,
    string? EnvelopeFrom,
    DateTimeOffset ReceivedAt,
    bool IsSpam,
    bool SpamIsFinal,
    string? AuthServId = null,
    string? AuthResults = null,
    ArcStatus ArcChain = ArcStatus.None);

/// <summary>Where one copy goes and what happens after it is stored.</summary>
public sealed record MailboxPlan(MailboxDelivery Delivery, IReadOnlyList<string> ForwardTo, bool Vacation, IReadOnlyList<Guid> MatchedFilters, bool Blocked);

/// <summary>
/// Server-side mailbox rules, run in the delivery path (PROMPT 2.1 step 8, 6.8) for mail from the Internet and
/// internal mail alike: blocked senders, filters (same query tree as search), automatic forwarding (verified
/// addresses only, SRS + ARC, loop-safe) and vacation replies (RFC 3834).
/// </summary>
public sealed class MailboxRules(BlueMailDbContext db, SystemMail mail, Srs srs, BmSettings settings, ILogger<MailboxRules> log)
{
    public const int MaxReceivedForForwarding = 25;
    public static readonly TimeSpan VacationInterval = TimeSpan.FromDays(4);
    public const string LoopHeader = "X-BlueMail-Loop";

    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRIMARY"] = SystemLabels.CategoryPrimary, ["SOCIAL"] = SystemLabels.CategorySocial, ["PROMOTIONS"] = SystemLabels.CategoryPromotions,
        ["UPDATES"] = SystemLabels.CategoryUpdates, ["FORUMS"] = SystemLabels.CategoryForums,
    };

    public async Task<MailboxPlan> PlanAsync(Guid tenant, Guid mailboxId, IReadOnlyList<string> deliveredTo, IncomingMessage m, CancellationToken ct)
    {
        var labels = new List<string>(m.IsSpam ? [SystemLabels.Spam] : [SystemLabels.Inbox, SystemLabels.CategoryPrimary]);
        var mailbox = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == mailboxId, ct);
        var userId = mailbox.OwnerKind == MailboxOwnerKind.User ? mailbox.OwnerId : (Guid?)null;
        var from = m.Facts.From?.Address.ToLowerInvariant();

        // 1. Blocked senders: always Spam; filters cannot pull it back.
        if (userId is { } uid && from is not null && await IsBlockedAsync(tenant, uid, from, ct))
        {
            return new MailboxPlan(new MailboxDelivery(mailboxId, [SystemLabels.Spam], DeliveredTo: deliveredTo), [], false, [], true);
        }

        // 2. Filters, in order; every matching filter applies (as Gmail).
        var filters = await db.Filters.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.TenantId == tenant && f.MailboxId == mailboxId && f.IsEnabled && f.DeletedAt == null).OrderBy(f => f.SortOrder).ThenBy(f => f.CreatedAt)
            .ToListAsync(ct);
        var seen = false;
        bool? important = null;
        var extra = new List<Guid>();
        var forwards = new List<string>();
        var matched = new List<Guid>();
        if (filters.Count > 0)
        {
            var owner = await new Mail.SearchContexts(db).ForMailboxAsync(tenant, mailboxId, null, ct);
            var parsed = new List<(MailFilter Filter, SearchNode Node)>();
            foreach (var f in filters)
            {
                try
                {
                    if (SearchParser.Parse(f.Criteria, new SearchContext(owner.Zone, m.ReceivedAt)) is { } node)
                    {
                        parsed.Add((f, node));
                    }
                }
                catch (SearchSyntaxException e)
                {
                    log.LogWarning("Filter {Filter} has an invalid query ({Code}); skipped", f.Id, e.Code);
                }
            }

            var subject = SearchSubject.FromFacts(m.Facts, m.ReceivedAt, m.Raw.LongLength, deliveredTo, labels);
            var folder = await PgTextFolder.CreateAsync(await OpenAsync(ct), SearchMatcher.TextsToFold(subject, parsed.Select(p => p.Node)), ct);
            var matcher = new SearchMatcher(folder, owner.Addresses);
            foreach (var (f, node) in parsed)
            {
                if (!matcher.Matches(node, subject))
                {
                    continue;
                }

                matched.Add(f.Id);
                var a = FilterActions.Parse(f.Actions);
                if (a.NeverSpam && labels.Contains(SystemLabels.Spam) && !m.SpamIsFinal)
                {
                    labels.Remove(SystemLabels.Spam);
                    labels.AddRange([SystemLabels.Inbox, SystemLabels.CategoryPrimary]);
                }

                if (a.SkipInbox)
                {
                    labels.Remove(SystemLabels.Inbox);
                }

                if (a.Trash)
                {
                    labels.RemoveAll(l => l is SystemLabels.Inbox or SystemLabels.Spam);
                    labels.Add(SystemLabels.Trash);
                }

                if (a.Star)
                {
                    labels.Add(SystemLabels.Starred);
                }

                if (a.Category is { } c && Categories.TryGetValue(c, out var cat))
                {
                    labels.RemoveAll(l => l.StartsWith("CATEGORY_", StringComparison.Ordinal));
                    labels.Add(cat);
                }

                seen |= a.MarkRead;
                important = a.Important switch { "ALWAYS" => true, "NEVER" => false, _ => important };
                extra.AddRange(a.LabelIds);
                if (a.ForwardTo is { } to && userId is not null)
                {
                    forwards.Add(to);
                }
            }
        }

        var spam = labels.Contains(SystemLabels.Spam);

        // 3. Automatic forwarding (never for spam, as Gmail).
        if (userId is { } owner2 && !spam)
        {
            var active = await db.ForwardingAddresses.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenant && x.UserId == owner2 && x.IsActive && x.VerifiedAt != null && x.DeletedAt == null, ct);
            if (active is not null)
            {
                forwards.Add(active.TargetEmail);
                if (!active.KeepCopy)
                {
                    labels.RemoveAll(l => l is SystemLabels.Inbox);
                    labels.Add(SystemLabels.Trash);
                }
            }
        }

        var verified = forwards.Count == 0 || userId is null ? [] : await VerifiedTargetsAsync(tenant, userId.Value, forwards, ct);
        var vacation = userId is not null && !spam && !labels.Contains(SystemLabels.Trash);
        var delivery = new MailboxDelivery(mailboxId, labels.Distinct().ToList(), seen, Important: important ?? false, ExtraLabelIds: extra.Distinct().ToList(),
            DeliveredTo: deliveredTo);
        return new MailboxPlan(delivery, verified, vacation, matched, false);
    }

    /// <summary>After the copy is stored: forwards and the vacation reply. Failures are logged, never fail the delivery.</summary>
    public async Task AfterDeliveryAsync(Guid tenant, Guid mailboxId, MailboxPlan plan, IncomingMessage m, CancellationToken ct)
    {
        if (plan.ForwardTo.Count == 0 && !plan.Vacation)
        {
            return;
        }

        var mailbox = await db.Mailboxes.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == mailboxId, ct);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstAsync(u => u.Id == mailbox.OwnerId, ct);
        foreach (var target in plan.ForwardTo)
        {
            try
            {
                await ForwardAsync(tenant, user.Id, user.PrimaryEmail, mailboxId, target, m, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Forwarding to {Target} failed for mailbox {Mailbox}", target, mailboxId);
            }
        }

        if (plan.Vacation)
        {
            try
            {
                await VacationAsync(tenant, user.Id, user.PrimaryEmail, user.Locale, plan.Delivery.DeliveredTo ?? [], m, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Vacation reply failed for mailbox {Mailbox}", mailboxId);
            }
        }
    }

    // ---------- blocked senders ----------

    public async Task<bool> IsBlockedAsync(Guid tenant, Guid userId, string from, CancellationToken ct)
    {
        var domain = from.Contains('@', StringComparison.Ordinal) ? from[(from.LastIndexOf('@') + 1)..] : from;
        var candidates = new[] { from, domain, "@" + domain };
        return await db.BlockedSenders.IgnoreQueryFilters().AnyAsync(b => b.TenantId == tenant && b.UserId == userId && b.DeletedAt == null
                                                                         && candidates.Contains(b.AddressOrDomain), ct);
    }

    // ---------- forwarding ----------

    private async Task<IReadOnlyList<string>> VerifiedTargetsAsync(Guid tenant, Guid userId, List<string> targets, CancellationToken ct)
    {
        var lower = targets.Select(t => t.ToLowerInvariant()).Distinct().ToList();
        var ok = await db.ForwardingAddresses.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenant && x.UserId == userId && x.VerifiedAt != null && x.DeletedAt == null && lower.Contains(x.TargetEmail))
            .Select(x => x.TargetEmail).ToListAsync(ct);
        var allowExternal = await db.MailPolicies.AsNoTracking().Where(p => p.TenantId == tenant).Select(p => (bool?)p.AllowExternalForwarding).FirstOrDefaultAsync(ct) ?? true;
        if (allowExternal)
        {
            return ok;
        }

        var domains = await db.Domains.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenant && d.DeletedAt == null).Select(d => d.Domain).ToListAsync(ct);
        return ok.Where(t => domains.Contains(t[(t.LastIndexOf('@') + 1)..])).ToList();
    }

    /// <summary>Token of a mailbox in the loop header: a forward never passes the same mailbox twice.</summary>
    public string LoopToken(Guid mailboxId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.ASCII.GetBytes(settings.MailHostname), mailboxId.ToByteArray()))[..16];

    public static int CountHeader(byte[] raw, string name)
    {
        // Only the header block, case-insensitive field names at line starts.
        var text = Encoding.Latin1.GetString(raw, 0, Math.Min(raw.Length, 256 * 1024));
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = end < 0 ? text : text[..end];
        var count = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    public static bool HeaderContains(byte[] raw, string name, string value)
    {
        var text = Encoding.Latin1.GetString(raw, 0, Math.Min(raw.Length, 256 * 1024));
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = (end < 0 ? text : text[..end]).Replace("\r\n\t", " ", StringComparison.Ordinal).Replace("\r\n ", " ", StringComparison.Ordinal);
        return head.Split("\r\n").Any(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) && l.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ForwardAsync(Guid tenant, Guid userId, string userEmail, Guid mailboxId, string target, IncomingMessage m, CancellationToken ct)
    {
        var token = LoopToken(mailboxId);
        if (CountHeader(m.Raw, "Received") > MaxReceivedForForwarding || HeaderContains(m.Raw, LoopHeader, token))
        {
            log.LogWarning("Forwarding loop detected for mailbox {Mailbox}; not forwarding to {Target}", mailboxId, target);
            return;
        }

        var ourDomain = userEmail[(userEmail.LastIndexOf('@') + 1)..];
        var envelope = srs.Forward(m.EnvelopeFrom ?? "", ourDomain);
        byte[] message = [.. Encoding.ASCII.GetBytes(LoopHeader + ": " + token + "\r\n"), .. m.Raw];
        message = m.AuthResults is not null
            ? await mail.ArcSealAsync(tenant, ourDomain, message, m.AuthServId ?? settings.MailHostname, m.AuthResults, m.ArcChain, ct)
            : await mail.SignAsync(tenant, ourDomain, message, ct); // internal mail has no signature of its own yet
        await mail.SendAsync(tenant, userId, envelope, target, message, notification: string.IsNullOrEmpty(envelope), null, m.Facts.RfcMessageId, ct);
    }

    // ---------- vacation (RFC 3834) ----------

    /// <summary>RFC 3834 section 2 and 3.1: whether an automatic reply may be sent at all, independent of the user's settings.</summary>
    public static bool MayAutoReply(MessageFacts f, string? envelopeFrom, IReadOnlyCollection<string> userAddresses, IReadOnlyCollection<string> deliveredTo)
    {
        if (string.IsNullOrEmpty(envelopeFrom) || f.AutoSubmitted || f.Bulk || f.ListId is not null || f.ListUnsubscribe is not null)
        {
            return false; // null return path, auto-submitted, Precedence bulk/list/junk, mailing lists
        }

        var sender = envelopeFrom.ToLowerInvariant();
        var local = sender[..Math.Max(0, sender.IndexOf('@'))];
        if (local is "mailer-daemon" or "postmaster" or "listserv" or "majordomo" or "noreply" or "no-reply" or "do-not-reply" or "donotreply"
            || local.StartsWith("owner-", StringComparison.Ordinal) || local.EndsWith("-request", StringComparison.Ordinal)
            || local.StartsWith("bounce", StringComparison.Ordinal))
        {
            return false;
        }

        if (userAddresses.Contains(sender))
        {
            return false;
        }

        // Personal: one of our addresses must be in To or Cc (not only a Bcc or a list expansion), RFC 3834 3.1.1.
        var personal = f.To.Concat(f.Cc).Select(a => a.Address.ToLowerInvariant()).ToHashSet();
        return userAddresses.Concat(deliveredTo.Select(d => d.ToLowerInvariant())).Any(personal.Contains);
    }

    private async Task VacationAsync(Guid tenant, Guid userId, string userEmail, string? locale, IReadOnlyList<string> deliveredTo, IncomingMessage m,
        CancellationToken ct)
    {
        var v = await db.Vacations.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.TenantId == tenant, ct);
        var now = DateTimeOffset.UtcNow;
        if (v is null || !v.Enabled || (v.StartAt is { } s && now < s) || (v.EndAt is { } e && now >= e))
        {
            return;
        }

        var addresses = await db.UserAliases.IgnoreQueryFilters().AsNoTracking().Where(a => a.UserId == userId && a.DeletedAt == null)
            .Select(a => a.Email.ToLower()).ToListAsync(ct);
        addresses.Add(userEmail.ToLowerInvariant());
        if (!MayAutoReply(m.Facts, m.EnvelopeFrom, addresses, deliveredTo))
        {
            return;
        }

        var sender = m.EnvelopeFrom!.ToLowerInvariant();
        var senderDomain = sender[(sender.LastIndexOf('@') + 1)..];
        if (v.OnlyOrganization && !await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenant && d.Domain == senderDomain && d.DeletedAt == null, ct))
        {
            return;
        }

        if (v.OnlyContacts && !await WroteToAsync(tenant, userId, sender, ct))
        {
            return;
        }

        // Once per sender per interval; atomic so two parallel deliveries reply once.
        var c = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO mail.vacation_replies (tenant_id, user_id, sender, last_replied_at) VALUES (@t, @u, @s, now())
            ON CONFLICT (user_id, sender) DO UPDATE SET last_replied_at = now()
              WHERE mail.vacation_replies.last_replied_at < now() - @interval
            RETURNING id
            """, c))
        {
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("s", sender);
            cmd.Parameters.AddWithValue("interval", VacationInterval);
            if (await cmd.ExecuteScalarAsync(ct) is null)
            {
                return;
            }
        }

        var subject = string.IsNullOrWhiteSpace(v.Subject) ? "Auto: " + (m.Facts.Subject ?? "") : v.Subject;
        // MessageBuilder adds the angle brackets.
        var refs = m.Facts.References.Concat(m.Facts.RfcMessageId is null ? [] : [m.Facts.RfcMessageId]).TakeLast(20).ToList();
        var name = await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        var reply = new MessageBuilder
        {
            From = new MailboxAddress(name, userEmail),
            To = [new MailboxAddress(null, m.EnvelopeFrom!)],
            Subject = subject,
            MessageId = MessageBuilder.NewMessageId(settings.MailHostname),
            Date = DateTimeOffset.UtcNow,
            InReplyTo = m.Facts.RfcMessageId,
            References = refs,
            HtmlBody = v.Html,
            TextBody = HtmlToText.Convert(v.Html),
            ExtraHeaders = [("Auto-Submitted", "auto-replied"), ("X-Auto-Response-Suppress", "All")],
        }.ToBytes();
        var signed = await mail.SignAsync(tenant, userEmail[(userEmail.LastIndexOf('@') + 1)..], reply, ct);
        // RFC 3834 3.3: the reply goes to the return path and carries a null return path itself.
        await mail.SendAsync(tenant, userId, "", m.EnvelopeFrom!, signed, notification: true, locale, null, ct);
    }

    private async Task<bool> WroteToAsync(Guid tenant, Guid userId, string address, CancellationToken ct)
    {
        var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM mail.messages m JOIN mail.mailboxes b ON b.id = m.mailbox_id
              JOIN mail.message_labels ml ON ml.tenant_id = m.tenant_id AND ml.message_id = m.id
              JOIN mail.labels l ON l.id = ml.label_id
              WHERE m.tenant_id = @t AND b.owner_id = @u AND l.system_key = 'SENT' AND m.expunged_at IS NULL
                AND EXISTS (SELECT 1 FROM jsonb_array_elements(m.to_addrs || m.cc_addrs || m.bcc_addrs) e WHERE lower(e->>'address') = @a))
            """, c);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("a", address);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = (NpgsqlConnection)db.Database.GetDbConnection();
        if (c.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return c;
    }
}
