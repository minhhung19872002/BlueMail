using System.Text;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueMail.IntegrationTests;

/// <summary>
/// The conversation list has two ways to build a page (Phase 13, PROMPT 8.2): grouping every match in memory (up to
/// CountCap messages, exact total) and walking messages newest first (big lists). Both must give the same order, no
/// duplicates and no gaps across pages.
/// </summary>
[Collection(StackCollection.Name)]
public class ConversationPagingTests(StackFixture stack)
{
    [Fact]
    public async Task Both_paging_paths_list_every_conversation_once_in_the_same_order()
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var u = await db.Users.IgnoreQueryFilters().Where(x => x.PrimaryEmail == "kenji@bluemail.test").Select(x => new { x.Id, x.TenantId }).SingleAsync();
        var mailbox = await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == u.Id).Select(m => m.Id).SingleAsync();
        var tag = "paging-" + Guid.NewGuid().ToString("N")[..6];
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        var access = PgMailStore.SystemAccess(u.TenantId, mailbox);
        var label = await store.CreateLabelAsync(access, tag, null, null, null);

        // 12 conversations, 1-3 messages each, replies interleaved in time; two messages share a timestamp.
        var n = 0;
        for (var t = 0; t < 12; t++)
        {
            string? root = null;
            for (var k = 0; k <= t % 3; k++)
            {
                var id = $"<{tag}-{t}-{k}@t>";
                var when = start.AddMinutes((k * 40) + (t * 7) + (t == 5 ? 0 : n % 2)).AddSeconds(t == 6 && k == 0 ? 0 : n);
                var raw = Encoding.UTF8.GetBytes($"From: x@doitac.test\r\nTo: kenji@bluemail.test\r\nSubject: {(k > 0 ? "Re: " : "")}{tag} {t}\r\nMessage-ID: {id}\r\n" +
                                                  (root is null ? "" : $"In-Reply-To: {root}\r\nReferences: {root}\r\n") + "\r\nbody\r\n");
                var facts = MessageFacts.Extract(MimeMessage.Parse(raw), new MemoryStream(raw));
                await store.DeliverAsync(new DeliveryRequest(u.TenantId, new MemoryStream(raw), facts, [new MailboxDelivery(mailbox, [], ExtraLabelIds: [label])], when));
                root ??= id;
                n++;
            }
        }

        async Task<(List<Guid> Threads, long FirstTotal, bool Exact)> All(int cap)
        {
            store.CountCap = cap;
            var seen = new List<Guid>();
            string? cursor = null;
            long total = -1;
            var exact = true;
            do
            {
                var page = await store.ListConversationsAsync(access, new ConversationQuery(MailView.Label, label, Cursor: cursor, Limit: 5));
                if (total < 0)
                {
                    (total, exact) = (page.Total, page.TotalExact);
                }

                seen.AddRange(page.Items.Select(i => i.ThreadId));
                cursor = page.NextCursor;
            }
            while (cursor is not null);
            return (seen, total, exact);
        }

        var grouped = await All(PgMailStore.DefaultCountCap);
        var walked = await All(3);
        store.CountCap = PgMailStore.DefaultCountCap;

        grouped.Threads.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        (grouped.FirstTotal, grouped.Exact).Should().Be((12, true));
        walked.Threads.Should().Equal(grouped.Threads, "the newest-first walk pages exactly like the in-memory grouping");
        (walked.FirstTotal, walked.Exact).Should().Be((3, false), "above the cap the total is only a lower bound");
    }
}
