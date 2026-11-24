using System.Text;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueMail.IntegrationTests;

/// <summary>S3 objects nothing refers to are deleted; anything still needed never is (BlobRefs).</summary>
[Collection(StackCollection.Name)]
public class BlobCollectionTests(StackFixture stack)
{
    private static async Task<(Guid Tenant, Guid Mailbox)> Mailbox(BlueMailDbContext db, string email)
    {
        var u = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.PrimaryEmail == email);
        return (u.TenantId, await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == u.Id).Select(m => m.Id).SingleAsync());
    }

    private static byte[] Raw(string subject) => new MessageBuilder
    {
        From = new MailboxAddress(null, "an@doitac.test"), To = [new MailboxAddress(null, "sofia@acme.test")], Subject = subject,
        MessageId = Guid.NewGuid().ToString("N") + "@doitac.test", TextBody = "blob " + subject + " " + Guid.NewGuid(),
    }.ToBytes();

    private static async Task<DeliveredMessage> Deliver(PgMailStore store, Guid tenant, Guid mailbox, byte[] raw)
    {
        var s = new MemoryStream(raw, writable: false);
        var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
        s.Position = 0;
        return (await store.DeliverAsync(new DeliveryRequest(tenant, s, facts, [new MailboxDelivery(mailbox, [SystemLabels.Inbox])], DateTimeOffset.UtcNow)))[0];
    }

    private static Task Age(BlueMailDbContext db, byte[] sha) => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE mail.blobs SET last_used_at = now() - interval '3 days' WHERE sha256 = {sha}");

    [Fact]
    public async Task Unreferenced_old_blobs_go_live_and_recent_ones_stay_and_content_can_come_back()
    {
        await using var scope = stack.ApiScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var store = sp.GetRequiredService<PgMailStore>();
        var blobs = sp.GetRequiredService<BlobStore>();
        var (tenant, mailbox) = await Mailbox(db, "sofia@acme.test");

        var goneRaw = Raw("expunged");
        var keptRaw = Raw("live");
        var freshRaw = Raw("fresh");
        var gone = await Deliver(store, tenant, mailbox, goneRaw);
        var kept = await Deliver(store, tenant, mailbox, keptRaw);
        var fresh = await Deliver(store, tenant, mailbox, freshRaw);
        var sha = async (byte[] raw) => await BlobStore.HashAsync(new MemoryStream(raw), default);
        var (goneSha, keptSha, freshSha) = (await sha(goneRaw), await sha(keptRaw), await sha(freshRaw));

        // "gone" and "fresh" are expunged; "gone" and "kept" have not been used for 3 days.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE mail.messages SET deleted_at = now(), expunged_at = now() WHERE tenant_id = {tenant} AND id = ANY({new[] { gone.MessageId, fresh.MessageId }})");
        await Age(db, goneSha);
        await Age(db, keptSha);

        // Run until nothing more is due (other tests may have left blobs too).
        while (await BlobRefs.CollectAsync(db, blobs, BlobRefs.Grace, 1000, NullLogger.Instance, default) > 0)
        {
        }

        (await blobs.ExistsAsync(tenant, goneSha, default)).Should().BeFalse("expunged and unused for longer than the grace time");
        (await db.Blobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.TenantId == tenant && b.Sha256 == goneSha)).CollectedAt.Should().NotBeNull();
        (await blobs.ExistsAsync(tenant, keptSha, default)).Should().BeTrue("a live message still refers to it");
        (await blobs.ExistsAsync(tenant, freshSha, default)).Should().BeTrue("used recently: a writer may be about to refer to it");

        // The same content arriving again is stored again and can be read.
        var again = await Deliver(store, tenant, mailbox, goneRaw);
        var raw = new MemoryStream();
        (await store.CopyRawAsync(PgMailStore.SystemAccess(tenant, mailbox), again.MessageId, raw)).Should().BeTrue();
        raw.ToArray().Should().Equal(goneRaw);
    }

    [Fact]
    public async Task A_row_whose_object_is_missing_is_uploaded_again()
    {
        await using var scope = stack.ApiScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var store = sp.GetRequiredService<PgMailStore>();
        var blobs = sp.GetRequiredService<BlobStore>();
        var (tenant, mailbox) = await Mailbox(db, "sofia@acme.test");

        var content = Raw("half collected");
        var first = await Deliver(store, tenant, mailbox, content);
        var sha = await BlobStore.HashAsync(new MemoryStream(content), default);
        await blobs.DeleteAsync(tenant, sha, default); // as if a collection deleted the object and then failed to commit

        await BlobRefs.EnsureAsync(db, blobs, tenant, sha, new MemoryStream(content), default);
        var raw = new MemoryStream();
        (await store.CopyRawAsync(PgMailStore.SystemAccess(tenant, mailbox), first.MessageId, raw)).Should().BeTrue();
        raw.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task A_writer_waits_for_a_running_collection_and_then_uploads_again()
    {
        await using var scope = stack.ApiScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BlueMailDbContext>();
        var blobs = sp.GetRequiredService<BlobStore>();
        var (tenant, _) = await Mailbox(db, "sofia@acme.test");
        var content = Encoding.UTF8.GetBytes("orphan " + Guid.NewGuid());
        var sha = await BlobStore.HashAsync(new MemoryStream(content), default);
        await BlobRefs.EnsureAsync(db, blobs, tenant, sha, new MemoryStream(content), default);
        await Age(db, sha);

        // The collector holds the row lock (its transaction is open) while the writer asks for the same blob.
        await using var collectorScope = stack.ApiScope();
        var cdb = collectorScope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        await using var tx = await cdb.Database.BeginTransactionAsync();
        (await cdb.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail.blobs SET collected_at = now() WHERE tenant_id = {tenant} AND sha256 = {sha}")).Should().Be(1);
        await blobs.DeleteAsync(tenant, sha, default);

        await using var writerScope = stack.ApiScope();
        var wdb = writerScope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var writer = BlobRefs.EnsureAsync(wdb, blobs, tenant, sha, new MemoryStream(content), default);
        await Task.Delay(500);
        writer.IsCompleted.Should().BeFalse("the writer waits on the row lock");
        await tx.CommitAsync();
        await writer;

        (await blobs.ExistsAsync(tenant, sha, default)).Should().BeTrue("the writer found the object gone and uploaded it again");
    }
}
