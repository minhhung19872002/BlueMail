using System.Text;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

namespace BlueMail.IntegrationTests;

/// <summary>IMailStore.DeliverAsync against real PostgreSQL, Valkey and Garage S3 (PROMPT 2.5).</summary>
[Collection(StackCollection.Name)]
public class MailDeliveryTests(StackFixture stack)
{
    private async Task<(Guid Tenant, Guid Mailbox)> MailboxOf(string email)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.PrimaryEmail == email);
        var mb = await db.Mailboxes.IgnoreQueryFilters().SingleAsync(m => m.OwnerId == user.Id);
        return (user.TenantId, mb.Id);
    }

    private static MemoryStream Raw(string text) => new(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));

    private async Task<IReadOnlyList<DeliveredMessage>> Deliver(Guid tenant, Stream raw, params MailboxDelivery[] targets)
    {
        await using var scope = stack.ApiScope();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        raw.Position = 0;
        var facts = MessageFacts.Extract(MimeMessage.Parse(raw), raw);
        raw.Position = 0;
        return await store.DeliverAsync(new DeliveryRequest(tenant, raw, facts, targets, DateTimeOffset.UtcNow));
    }

    private async Task<T> Scalar<T>(string sql, params (string, object)[] ps)
    {
        await using var c = new NpgsqlConnection(stack.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v);
        }

        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Content_is_encrypted_on_s3_and_reads_back_whole_and_by_range()
    {
        var (tenant, mailbox) = await MailboxOf("hoa@bluemail.test");
        var big = new string('a', 70_000) + "MARKER" + new string('b', 70_000); // spans three 64 KiB chunks
        var raw = Raw($"From: x@doitac.test\nTo: hoa@bluemail.test\nSubject: s3 {Guid.NewGuid()}\nMessage-ID: <{Guid.NewGuid()}@t>\n\n{big}\n");
        var plain = raw.ToArray();
        await Deliver(tenant, raw, new MailboxDelivery(mailbox, [SystemLabels.Inbox]));

        await using var scope = stack.ApiScope();
        var blobs = scope.ServiceProvider.GetRequiredService<BlobStore>();
        var s3 = scope.ServiceProvider.GetRequiredService<Amazon.S3.IAmazonS3>();
        var sha = System.Security.Cryptography.SHA256.HashData(plain);

        using (var obj = await s3.GetObjectAsync(BlobStore.Bucket, BlobStore.ObjectKey(tenant, sha)))
        using (var ms = new MemoryStream())
        {
            await obj.ResponseStream.CopyToAsync(ms);
            Encoding.ASCII.GetString(ms.ToArray()).Should().NotContain("MARKER", "the stored object is ciphertext");
            ms.Length.Should().Be(ChunkedAead.CiphertextLength(plain.Length));
        }

        var whole = new MemoryStream();
        await blobs.ReadAsync(tenant, sha, whole);
        whole.ToArray().Should().Equal(plain);

        var offset = Encoding.ASCII.GetString(plain).IndexOf("MARKER", StringComparison.Ordinal);
        var part = new MemoryStream();
        await blobs.ReadAsync(tenant, sha, part, offset - 3, 12);
        Encoding.ASCII.GetString(part.ToArray()).Should().Be("aaaMARKERbbb");

        // Another tenant's key cannot open it (AAD binds the tenant id; the key differs as well).
        var (otherTenant, _) = await MailboxOf("olivia@acme.test");
        var act = () => blobs.ReadAsync(otherTenant, sha, new MemoryStream());
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Same_message_to_many_mailboxes_is_stored_once_and_threads_by_references()
    {
        var (tenant, minh) = await MailboxOf("minh@bluemail.test");
        var (_, tuan) = await MailboxOf("tuan@bluemail.test");
        var id = Guid.NewGuid().ToString("N");
        var first = Raw($"From: a@doitac.test\nTo: minh@bluemail.test\nSubject: Báo giá {id}\nMessage-ID: <{id}@doitac.test>\n\nxin chào\n");
        var d1 = await Deliver(tenant, first, new MailboxDelivery(minh, [SystemLabels.Inbox]), new MailboxDelivery(tuan, [SystemLabels.Inbox]));
        d1.Should().HaveCount(2);
        (await Scalar<long>("select count(*) from mail.blobs where sha256 = (select blob_sha256 from mail.messages where id = @m)", ("m", d1[0].MessageId)))
            .Should().Be(1);

        // A reply referencing the first joins its thread; a different subject does not matter.
        var reply = Raw($"From: b@doitac.test\nTo: minh@bluemail.test\nSubject: other words\nMessage-ID: <r{id}@doitac.test>\nIn-Reply-To: <{id}@doitac.test>\nReferences: <{id}@doitac.test>\n\nok\n");
        var d2 = await Deliver(tenant, reply, new MailboxDelivery(minh, [SystemLabels.Inbox]));
        d2[0].ThreadId.Should().Be(d1.Single(x => x.MailboxId == minh).ThreadId);

        // Without headers, "Re:/TL:" + the same normalized subject within the window joins too.
        var bySubject = Raw($"From: c@doitac.test\nTo: minh@bluemail.test\nSubject: TL: Re: Báo giá {id}\nMessage-ID: <s{id}@doitac.test>\n\nok\n");
        var d3 = await Deliver(tenant, bySubject, new MailboxDelivery(minh, [SystemLabels.Inbox]));
        d3[0].ThreadId.Should().Be(d2[0].ThreadId);

        // A new message with the same subject but no prefix starts a new conversation.
        var fresh = Raw($"From: c@doitac.test\nTo: minh@bluemail.test\nSubject: Báo giá {id}\nMessage-ID: <n{id}@doitac.test>\n\nnew\n");
        var d4 = await Deliver(tenant, fresh, new MailboxDelivery(minh, [SystemLabels.Inbox]));
        d4[0].ThreadId.Should().NotBe(d2[0].ThreadId);
    }

    [Fact]
    public async Task Parallel_deliveries_get_unique_increasing_uids_and_modseqs()
    {
        var (tenant, mailbox) = await MailboxOf("ngoc@bluemail.test");
        var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            Deliver(tenant, Raw($"From: p@doitac.test\nTo: ngoc@bluemail.test\nSubject: parallel {i}\nMessage-ID: <p{i}-{Guid.NewGuid()}@t>\n\nbody {i}\n"),
                new MailboxDelivery(mailbox, [SystemLabels.Inbox])))).ToList();
        var results = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();
        results.Select(r => r.Modseq).Should().OnlyHaveUniqueItems();

        await using var c = new NpgsqlConnection(stack.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            select ml.imap_uid from mail.message_labels ml join mail.labels l on l.id = ml.label_id
            where l.mailbox_id = @mb and l.system_key = 'INBOX' order by ml.imap_uid
            """, c);
        cmd.Parameters.AddWithValue("mb", mailbox);
        var uids = new List<long>();
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                uids.Add(rd.GetInt64(0));
            }
        }

        uids.Should().OnlyHaveUniqueItems().And.HaveCountGreaterThanOrEqualTo(24);
        (await Scalar<long>("select uid_next from mail.labels where mailbox_id = @mb and system_key = 'INBOX'", ("mb", mailbox))).Should().BeGreaterThan(uids.Max());
    }

    [Fact]
    public async Task Search_vector_matches_without_accents_and_cjk_substrings()
    {
        var (tenant, mailbox) = await MailboxOf("mei@bluemail.test");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var d = await Deliver(tenant, Raw($"From: a@doitac.test\nTo: mei@bluemail.test\nSubject: Hợp đồng {tag}\nMessage-ID: <{tag}@t>\nContent-Type: text/plain; charset=utf-8\n\n合同已经签署 请查收\n"),
            new MailboxDelivery(mailbox, [SystemLabels.Inbox]));
        var id = d[0].MessageId;

        async Task<bool> Matches(string q) => await Scalar<bool>(
            "select search_vector @@ plainto_tsquery('simple', public.f_unaccent(@q)) from mail.messages where id = @id", ("q", SearchText.Prepare(q)), ("id", id));

        (await Matches("hop dong")).Should().BeTrue("Vietnamese without diacritics");
        (await Matches("HỢP ĐỒNG")).Should().BeTrue("case-insensitive with diacritics");
        (await Matches("签署")).Should().BeTrue("CJK word inside a run without spaces");
        (await Matches("合同")).Should().BeTrue();
        (await Matches("không có")).Should().BeFalse();
    }

    [Fact]
    public async Task Delivery_publishes_a_change_event_and_refuses_a_mailbox_of_another_tenant()
    {
        var (tenant, mailbox) = await MailboxOf("yen@bluemail.test");
        var (_, foreign) = await MailboxOf("jonas@acme.test");
        await using var scope = stack.ApiScope();
        var valkey = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var got = new TaskCompletionSource<string>();
        await valkey.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ValkeyMailEvents.ChannelPrefix + mailbox.ToString("N")), (_, v) => got.TrySetResult(v!));

        await Deliver(tenant, Raw($"From: a@doitac.test\nTo: yen@bluemail.test\nSubject: ev\nMessage-ID: <{Guid.NewGuid()}@t>\n\nx\n"), new MailboxDelivery(mailbox, [SystemLabels.Inbox]));
        (await got.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Contain("message.added");

        var act = () => Deliver(tenant, Raw("From: a@doitac.test\nSubject: x\n\nx\n"), new MailboxDelivery(foreign, [SystemLabels.Inbox]));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
