using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BlueMail.Antispam;
using BlueMail.Dns;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.MailStore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlueMail.Application.Features.Antispam;

/// <summary>Bayes models in PostgreSQL (spam.bayes_tokens / bayes_totals): global, per tenant, per user.</summary>
public sealed class BayesStore(BlueMailDbContext db)
{
    public async Task<IReadOnlyList<IBayesModel>> LoadAsync(Guid tenantId, Guid? userId, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var ids = tokens.Select(Tokenizer.Id).Distinct().ToArray();
        var scopes = new List<(short Scope, Guid Id)> { ((short)BayesScope.Global, Guid.Empty), ((short)BayesScope.Tenant, tenantId) };
        if (userId is { } u)
        {
            scopes.Add(((short)BayesScope.User, u));
        }

        var models = new List<IBayesModel>();
        foreach (var (scope, id) in scopes)
        {
            var total = await db.BayesTotals.AsNoTracking().FirstOrDefaultAsync(t => t.Scope == scope && t.ScopeId == id, ct);
            if (total is null || total.SpamMessages < Bayes.MinTrainingPerClass || total.HamMessages < Bayes.MinTrainingPerClass)
            {
                continue; // not trained enough to say anything
            }

            var m = new InMemoryBayesModel((BayesScope)scope);
            m.SetTotals(total.SpamMessages, total.HamMessages);
            foreach (var t in await db.BayesTokens.AsNoTracking().Where(t => t.Scope == scope && t.ScopeId == id && ids.Contains(t.Token)).ToListAsync(ct))
            {
                m.Set(t.Token, new TokenCounts(t.Spam, t.Ham));
            }

            models.Add(m);
        }

        return models;
    }

    /// <summary>Adds (delta 1) or removes (delta -1) one message's tokens to a model, atomically per token.</summary>
    public async Task ApplyAsync(BayesScope scope, Guid scopeId, IEnumerable<string> tokens, bool spam, int delta, CancellationToken ct)
    {
        var ids = tokens.Select(Tokenizer.Id).Distinct().ToArray();
        var s = (short)scope;
        var (ds, dh) = spam ? (delta, 0) : (0, delta);
        var c = (NpgsqlConnection)db.Database.GetDbConnection();
        if (c.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO spam.bayes_tokens (scope, scope_id, token, spam, ham)
            SELECT @s, @id, t, GREATEST(@ds, 0), GREATEST(@dh, 0) FROM unnest(@ids) AS t
            ON CONFLICT (scope, scope_id, token) DO UPDATE
              SET spam = GREATEST(spam.bayes_tokens.spam + @ds, 0), ham = GREATEST(spam.bayes_tokens.ham + @dh, 0)
            """, c))
        {
            cmd.Parameters.AddWithValue("s", s);
            cmd.Parameters.AddWithValue("id", scopeId);
            cmd.Parameters.AddWithValue("ds", ds);
            cmd.Parameters.AddWithValue("dh", dh);
            cmd.Parameters.AddWithValue("ids", ids);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO spam.bayes_totals (scope, scope_id, spam_messages, ham_messages) VALUES (@s, @id, GREATEST(@ds, 0), GREATEST(@dh, 0))
            ON CONFLICT (scope, scope_id) DO UPDATE
              SET spam_messages = GREATEST(spam.bayes_totals.spam_messages + @ds, 0), ham_messages = GREATEST(spam.bayes_totals.ham_messages + @dh, 0)
            """, c))
        {
            cmd.Parameters.AddWithValue("s", s);
            cmd.Parameters.AddWithValue("id", scopeId);
            cmd.Parameters.AddWithValue("ds", ds);
            cmd.Parameters.AddWithValue("dh", dh);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

/// <summary>
/// Learning from "Report spam" / "Not spam" (PROMPT 6.9): the user's model learns fully, the tenant's model too
/// (it weighs less at classification). Reporting the same message twice does nothing; changing one's mind
/// removes the earlier lesson first.
/// </summary>
public sealed class SpamLearning(BlueMailDbContext db, PgMailStore store, BayesStore bayes)
{
    public async Task LearnAsync(MailboxAccess access, IReadOnlyCollection<Guid> messageIds, bool spam, CancellationToken ct)
    {
        foreach (var id in messageIds.Take(500))
        {
            var previous = await db.SpamTrainings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TenantId == access.TenantId && t.UserId == access.ActorUserId && t.MessageId == id, ct);
            if (previous?.Spam == spam)
            {
                continue;
            }

            var input = await SpamInputs.FromStoredAsync(store, access, id, ct);
            if (input is null)
            {
                continue;
            }

            var tokens = SpamTokens.Of(input).ToList();
            if (previous is not null)
            {
                await bayes.ApplyAsync(BayesScope.User, access.ActorUserId, tokens, previous.Spam, -1, ct);
                await bayes.ApplyAsync(BayesScope.Tenant, access.TenantId, tokens, previous.Spam, -1, ct);
                previous.Spam = spam;
                previous.LearnedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.SpamTrainings.Add(new SpamTraining { TenantId = access.TenantId, UserId = access.ActorUserId, MessageId = id, Spam = spam, LearnedAt = DateTimeOffset.UtcNow });
            }

            await bayes.ApplyAsync(BayesScope.User, access.ActorUserId, tokens, spam, 1, ct);
            await bayes.ApplyAsync(BayesScope.Tenant, access.TenantId, tokens, spam, 1, ct);
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>Builds scorer inputs from parsed or stored messages.</summary>
public static partial class SpamInputs
{
    public static (IReadOnlyList<(string Href, string Text)> Links, int Images) HtmlFacts(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return ([], 0);
        }

        var links = Anchor().Matches(html).Take(200)
            .Select(m => (WebUtility.HtmlDecode(m.Groups["href"].Value), WebUtility.HtmlDecode(Tag().Replace(m.Groups["text"].Value, " ")).Trim())).ToList();
        return (links, Img().Count(html));
    }

    /// <summary>Content-only input of a stored message (used for learning: connection facts do not matter to Bayes).</summary>
    public static async Task<SpamInput?> FromStoredAsync(PgMailStore store, MailboxAccess access, Guid messageId, CancellationToken ct)
    {
        var m = await store.GetMessageAsync(access, messageId, ct);
        if (m is null)
        {
            return null;
        }

        var content = await store.GetContentAsync(access, messageId, ct);
        var (links, images) = HtmlFacts(content?.Html);
        return new SpamInput
        {
            RemoteIp = IPAddress.None,
            FromAddress = m.From?.Address,
            FromName = m.From?.Name,
            Subject = m.Subject,
            TextBody = content?.Text ?? m.Snippet,
            HtmlBody = content?.Html,
            Images = images,
            Links = links,
        };
    }

    [GeneratedRegex("<a\\b[^>]*\\bhref\\s*=\\s*[\"']?(?<href>[^\"'\\s>]+)[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex("<img\\b", RegexOptions.IgnoreCase)]
    private static partial Regex Img();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();
}

/// <summary>DNS block list lookups for enabled sources (all off by default, docs/00 Z4).</summary>
public sealed class DnsblChecker(BlueMailDbContext db, DnsClient dns)
{
    public async Task<IReadOnlyList<(string Source, double Score)>> CheckAsync(Guid tenantId, IPAddress ip, string? fromDomain, IEnumerable<string> linkHosts,
        CancellationToken ct)
    {
        var sources = await db.DnsblSources.AsNoTracking().Where(s => s.Enabled && (s.TenantId == null || s.TenantId == tenantId)).ToListAsync(ct);
        var hits = new List<(string, double)>();
        foreach (var s in sources)
        {
            var names = s.Kind switch
            {
                ListKind.Ip => [ReverseName(ip) + "." + s.Zone],
                ListKind.Domain => fromDomain is null ? [] : new[] { fromDomain + "." + s.Zone },
                _ => linkHosts.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).Select(h => h + "." + s.Zone).ToArray(),
            };
            foreach (var name in names)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var (status, addrs) = await dns.LookupAddressesAsync(name, ipv6: false, cts.Token);
                    // 127.0.0.x answers mean "listed"; 127.255.255.x are error codes (e.g. Spamhaus "query refused").
                    if (status == DnsStatus.Ok && addrs.Any(a => a.GetAddressBytes() is [127, var b, _, _] && b != 255))
                    {
                        hits.Add((s.Name, s.Score));
                        break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // a slow list never delays mail
                }
            }
        }

        return hits;
    }

    public static string ReverseName(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}";
        }

        var hex = Convert.ToHexStringLower(ip.GetAddressBytes());
        return string.Join('.', hex.Reverse());
    }
}

/// <summary>Greylisting (PROMPT 2.3, optional per tenant): a new (network, sender, recipient) is told to retry after 5 minutes.</summary>
public sealed class Greylisting(BlueMailDbContext db)
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);

    public static string Network(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork ? $"{b[0]}.{b[1]}.{b[2]}.0/24" : new IPAddress([.. b[..8], .. new byte[8]]) + "/64";
    }

    /// <summary>True when the message may pass now.</summary>
    public async Task<bool> PassAsync(Guid tenantId, IPAddress ip, string sender, string rcpt, CancellationToken ct)
    {
        var net = Network(ip);
        var s = sender.ToLowerInvariant();
        var r = rcpt.ToLowerInvariant();
        var e = await db.Greylist.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.IpNetwork == net && g.Sender == s && g.Recipient == r, ct);
        var now = DateTimeOffset.UtcNow;
        if (e is null)
        {
            db.Greylist.Add(new GreylistEntry { TenantId = tenantId, IpNetwork = net, Sender = s, Recipient = r, FirstSeen = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // parallel delivery created it
            }

            return false;
        }

        if (e.PassedAt is not null)
        {
            return true;
        }

        if (now - e.FirstSeen < Delay)
        {
            return false;
        }

        if (now - e.FirstSeen > RetryWindow)
        {
            e.FirstSeen = now; // too late: start over
            await db.SaveChangesAsync(ct);
            return false;
        }

        e.PassedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
