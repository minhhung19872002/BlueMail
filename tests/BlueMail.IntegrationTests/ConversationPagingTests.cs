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

    [Fact]
    public async Task Sorting_by_sender_subject_and_size_pages_every_conversation_once_and_big_lists_stay_by_date()
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var u = await db.Users.IgnoreQueryFilters().Where(x => x.PrimaryEmail == "kenji@bluemail.test").Select(x => new { x.Id, x.TenantId }).SingleAsync();
        var mailbox = await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == u.Id).Select(m => m.Id).SingleAsync();
        var tag = "sort-" + Guid.NewGuid().ToString("N")[..6];
        var access = PgMailStore.SystemAccess(u.TenantId, mailbox);
        var label = await store.CreateLabelAsync(access, tag, null, null, null);

        // 7 conversations: senders and subjects in a known order unrelated to time, bodies of growing size.
        string[] senders = ["Gia", "an", "Binh", "Ella", "Chi", "Dung", "Fay"];
        for (var i = 0; i < senders.Length; i++)
        {
            var subject = $"{tag} {(char)('z' - i)}";
            var raw = Encoding.UTF8.GetBytes($"From: {senders[i]} <s{i}@doitac.test>\r\nTo: kenji@bluemail.test\r\nSubject: {subject}\r\nMessage-ID: <{tag}-{i}@t>\r\n\r\n" +
                                              new string('x', 100 * ((i * 3 % 7) + 1)) + "\r\n");
            var facts = MessageFacts.Extract(MimeMessage.Parse(raw), new MemoryStream(raw));
            await store.DeliverAsync(new DeliveryRequest(u.TenantId, new MemoryStream(raw), facts, [new MailboxDelivery(mailbox, [], ExtraLabelIds: [label])],
                DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        async Task<(List<string> Senders, List<long> Sizes, List<string> Subjects, string Sort)> All(string sort, bool asc)
        {
            var senders = new List<string>();
            var sizes = new List<long>();
            var subjects = new List<string>();
            string? cursor = null;
            var applied = "";
            do
            {
                var page = await store.ListConversationsAsync(access, new ConversationQuery(MailView.Label, label, Cursor: cursor, Limit: 3, Sort: sort, Ascending: asc));
                applied = page.Sort;
                foreach (var i in page.Items)
                {
                    senders.Add(i.Participants[0].Name ?? i.Participants[0].Address);
                    subjects.Add(i.Subject ?? "");
                }

                cursor = page.NextCursor;
            }
            while (cursor is not null);

            foreach (var s in subjects)
            {
                var i = s[^1] - 'z';
                sizes.Add(-i);
            }

            return (senders, sizes, subjects, applied);
        }

        var byFrom = await All(ConversationSort.From, true);
        byFrom.Sort.Should().Be(ConversationSort.From);
        byFrom.Senders.Should().Equal("an", "Binh", "Chi", "Dung", "Ella", "Fay", "Gia");
        var bySubjectDesc = await All(ConversationSort.Subject, false);
        bySubjectDesc.Subjects.Should().BeInDescendingOrder(StringComparer.Ordinal).And.HaveCount(7).And.OnlyHaveUniqueItems();
        var bySize = await All(ConversationSort.Size, false);
        bySize.Senders.Should().HaveCount(7).And.OnlyHaveUniqueItems();
        var sizeOrder = Enumerable.Range(0, 7).OrderByDescending(i => (i * 3 % 7) + 1).Select(i => senders[i]).ToList();
        bySize.Senders.Should().Equal(sizeOrder, "largest conversation first");

        // Above the cap the list cannot be sorted in memory: newest first, and the caller is told.
        store.CountCap = 3;
        try
        {
            var page = await store.ListConversationsAsync(access, new ConversationQuery(MailView.Label, label, Limit: 3, Sort: ConversationSort.From, Ascending: true));
            page.Sort.Should().Be(ConversationSort.Date);
            page.Items.Select(i => i.Participants[0].Name).Should().Equal("Gia", "an", "Binh");
        }
        finally
        {
            store.CountCap = PgMailStore.DefaultCountCap;
        }
    }
}
