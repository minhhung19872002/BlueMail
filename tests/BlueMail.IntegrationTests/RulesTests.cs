using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Mail;
using BlueMail.Infrastructure.Persistence;
using BlueMail.IntegrationTests.Infrastructure;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using BlueMail.Mime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BlueMail.IntegrationTests;

/// <summary>Phase 7: search operators, filters in the delivery path, blocking, forwarding, vacation.</summary>
[Collection(StackCollection.Name)]
public partial class RulesTests(StackFixture stack)
{
    private async Task<(Guid Tenant, Guid Mailbox, Guid User)> MailboxOf(string email)
    {
        await using var scope = stack.ApiScope();
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.PrimaryEmail == email);
        return (user.TenantId, await db.Mailboxes.IgnoreQueryFilters().Where(m => m.OwnerId == user.Id).Select(m => m.Id).SingleAsync(), user.Id);
    }

    /// <summary>Delivers like BlueMail.Mx does after scoring: mailbox rules, store, then forwards and vacation.</summary>
    private async Task<Guid> Receive(string to, string raw, string? envelopeFrom = "sender@partner.test", bool spam = false)
    {
        var (tenant, mailbox, _) = await MailboxOf(to);
        await using var scope = stack.ApiScope();
        var bytes = Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n"));
        var s = new MemoryStream(bytes);
        var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
        var rules = scope.ServiceProvider.GetRequiredService<MailboxRules>();
        var incoming = new IncomingMessage(facts, bytes, envelopeFrom, DateTimeOffset.UtcNow, spam, false, "mx.bluemail.test", "spf=pass smtp.mailfrom=partner.test",
            Dkim.ArcStatus.None);
        var plan = await rules.PlanAsync(tenant, mailbox, [to], incoming, default);
        s.Position = 0;
        var id = (await scope.ServiceProvider.GetRequiredService<PgMailStore>()
            .DeliverAsync(new DeliveryRequest(tenant, s, facts, [plan.Delivery], incoming.ReceivedAt)))[0].MessageId;
        await rules.AfterDeliveryAsync(tenant, mailbox, plan, incoming, default);
        return id;
    }

    private async Task<HttpClient> Client(string email) => OidcLogin.ApiClient(stack, await OidcLogin.LoginAsync(stack, email, StackFixture.UserPassword));

    [Fact]
    public async Task Select_all_of_a_search_runs_as_a_background_job_with_progress()
    {
        // PROMPT 6.3 / 8.3: "select all N" beyond one request's limit becomes a bulk job over the list or search.
        var tag = "bulk" + Guid.NewGuid().ToString("N")[..8];
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await Receive("quang@bluemail.test", $"From: tin@partner.test\nTo: quang@bluemail.test\nSubject: {tag} {i}\nMessage-ID: <{tag}{i}@t>\n\nx\n"));
        }

        using var api = await Client("quang@bluemail.test");
        var job = await Data(await api.PostAsJsonAsync("/api/v1/mail/bulk-jobs", new { action = "star", scopeLabelId = (Guid?)null, query = $"subject:{tag}" }));
        job.GetProperty("status").GetString().Should().Be("QUEUED");
        var id = job.GetProperty("id").GetGuid();

        using var other = await Client("minh@bluemail.test");
        (await other.GetAsync($"/api/v1/mail/bulk-jobs/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a job is visible to its owner only");

        (await stack.Api.Services.GetRequiredService<Application.Features.Mail.BulkMailJobWorker>().RunOnceAsync(default)).Should().BeTrue();
        var status = await Data(await api.GetAsync($"/api/v1/mail/bulk-jobs/{id}"));
        status.GetProperty("status").GetString().Should().Be("DONE");
        status.GetProperty("total").GetInt32().Should().Be(3);
        status.GetProperty("done").GetInt32().Should().Be(3);
        foreach (var m in ids)
        {
            (await Labels(m)).Should().Contain(SystemLabels.Starred);
        }

        // At most a few waiting jobs per user; an unknown action is refused before anything is queued.
        (await api.PostAsJsonAsync("/api/v1/mail/bulk-jobs", new { action = "explode", query = tag })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        for (var i = 0; i < Application.Features.Mail.StartBulkJobHandler.MaxActivePerUser; i++)
        {
            await Data(await api.PostAsJsonAsync("/api/v1/mail/bulk-jobs", new { action = "markRead", query = $"subject:{tag}" }));
        }

        var refused = await api.PostAsJsonAsync("/api/v1/mail/bulk-jobs", new { action = "markRead", query = $"subject:{tag}" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("too_many_jobs");
        while (await stack.Api.Services.GetRequiredService<Application.Features.Mail.BulkMailJobWorker>().RunOnceAsync(default))
        {
        }

        (await Db(db => db.Messages.IgnoreQueryFilters().Where(m => ids.Contains(m.Id)).AllAsync(m => m.Seen))).Should().BeTrue();
    }

    private static async Task<JsonElement> Data(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<T> Db<T>(Func<BlueMailDbContext, Task<T>> f)
    {
        await using var scope = stack.ApiScope();
        return await f(scope.ServiceProvider.GetRequiredService<BlueMailDbContext>());
    }

    private Task<List<string?>> Labels(Guid messageId) => Db(db => (from ml in db.MessageLabels.IgnoreQueryFilters()
                                                                    join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
                                                                    where ml.MessageId == messageId select l.SystemKey ?? l.Name).ToListAsync());

    private Task<List<(string EnvelopeFrom, string Rcpt, byte[] Sha, Guid Tenant)>> Queued(string rcpt) => Db(async db =>
        (await (from m in db.OutboundMessages.IgnoreQueryFilters()
                join r in db.OutboundRecipients.IgnoreQueryFilters() on m.Id equals r.OutboundMessageId
                where r.Rcpt == rcpt
                orderby m.CreatedAt
                select new { m.EnvelopeFrom, r.Rcpt, m.BlobSha256, m.TenantId }).ToListAsync())
        .Select(x => (x.EnvelopeFrom, x.Rcpt, x.BlobSha256, x.TenantId)).ToList());

    private async Task<string> Blob(Guid tenant, byte[] sha)
    {
        await using var scope = stack.ApiScope();
        var ms = new MemoryStream();
        await scope.ServiceProvider.GetRequiredService<BlobStore>().ReadAsync(tenant, sha, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task Search_in_sql_and_the_filter_matcher_agree_on_every_operator()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var raws = new[]
        {
            $"From: \"Nguyễn Văn An\" <an@acme.test>\nTo: ngoc@bluemail.test\nCc: Hoa <hoa@bluemail.test>\nSubject: Hợp đồng số 12 {tag}\nMessage-ID: <a{tag}@t>\n\nKính gửi chị Ngọc, bên em gửi báo giá và hợp đồng.\n",
            $"From: news@shop.test\nTo: ngoc@bluemail.test\nList-Id: <news.shop.test>\nSubject: Khuyến mãi {tag}\nMessage-ID: <b{tag}@t>\n\nGiảm giá 50% cuối tuần\n",
            $"From: 田中 <tanaka@example.jp>\nTo: ngoc@bluemail.test\nSubject: 契約書 {tag}\nMessage-ID: <c{tag}@t>\nMIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=b\n\n--b\nContent-Type: text/plain; charset=utf-8\n\n合同已经签署 请查收\n--b\nContent-Type: application/pdf; name=\"BaoCao_Q4.pdf\"\nContent-Disposition: attachment; filename=\"BaoCao_Q4.pdf\"\n\n%PDF-1.4\n--b--\n",
            $"From: Łukasz <lukasz@firma.test>\nTo: ngoc@bluemail.test\nSubject: Zażółć gęślą jaźń {tag}\nMessage-ID: <d{tag}@t>\n\nStraße, café, naïve\n",
        };
        var (tenant, mailbox, user) = await MailboxOf("ngoc@bluemail.test");
        var subjects = new List<(Guid Id, SearchSubject Subject)>();
        foreach (var raw in raws)
        {
            var id = await Receive("ngoc@bluemail.test", raw);
            var bytes = Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n"));
            var s = new MemoryStream(bytes);
            var facts = MessageFacts.Extract(MimeMessage.Parse(s), s);
            var at = await Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == id).Select(m => m.ReceivedAt).SingleAsync());
            var size = await Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == id).Select(m => m.Size).SingleAsync());
            var system = await Db(db => (from ml in db.MessageLabels.IgnoreQueryFilters() join l in db.Labels.IgnoreQueryFilters() on ml.LabelId equals l.Id
                                         where ml.MessageId == id && l.SystemKey != null select l.SystemKey!).ToListAsync());
            subjects.Add((id, SearchSubject.FromFacts(facts, at, size, ["ngoc@bluemail.test"], system)));
        }

        var queries = new[]
        {
            "hop dong", "HỢP ĐỒNG", "\"bao gia va hop dong\"", "\"hop dong bao gia\"", "签署", "合同", "契約", "khuyen mai -giam", "{giam khuyen} 50",
            "from:an@acme", "from:nguyen", "from:tanaka", "to:me", "cc:hoa", "subject:\"hop dong\"", "subject:zazolc", "subject:(gesla jazn)",
            "filename:pdf", "filename:baocao_q4", "has:attachment", "-has:attachment", "list:shop", "rfc822msgid:a" + tag + "@t",
            "in:inbox", "category:primary", "is:unread", "larger:1", "smaller:1K", "lukasz OR tanaka", "strasse cafe naive", "deliveredto:me",
            "newer_than:1d", "older_than:1d",
        };
        var ids = subjects.Select(x => x.Id).ToHashSet();
        await using var scope = stack.ApiScope();
        var store = scope.ServiceProvider.GetRequiredService<PgMailStore>();
        var access = await store.OpenAsync(tenant, user, mailbox);
        var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
        await db.Database.OpenConnectionAsync();
        foreach (var q in queries)
        {
            // Only this test's four messages count (the mailbox has others from other tests).
            var node = SearchParser.Parse(q, SearchContext.Utc())!;
            var sql = (await store.SearchMessagesAsync(access, node, ["ngoc@bluemail.test"], null, 5000)).Select(x => x.Id).Where(ids.Contains).ToHashSet();
            var folder = await PgTextFolder.CreateAsync((NpgsqlConnection)db.Database.GetDbConnection(),
                subjects.SelectMany(x => SearchMatcher.TextsToFold(x.Subject, [node])), default);
            var matcher = new SearchMatcher(folder, ["ngoc@bluemail.test"]);
            var memory = subjects.Where(x => matcher.Matches(node, x.Subject)).Select(x => x.Id).ToHashSet();
            memory.Should().BeEquivalentTo(sql, $"query {q}: SQL and filter matcher must agree");
        }

        // A sanity anchor: the two agree on non-trivial results, not only on "nothing".
        (await store.SearchMessagesAsync(access, SearchParser.Parse("hop dong " + tag, SearchContext.Utc())!, [], null, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task Search_api_returns_matches_and_explains_broken_queries()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await Receive("hoa@bluemail.test", $"From: an@acme.test\nTo: hoa@bluemail.test\nSubject: Báo giá {tag}\nMessage-ID: <s{tag}@t>\n\nnội dung\n");
        using var api = await Client("hoa@bluemail.test");
        var page = await Data(await api.GetAsync($"/api/v1/mail/conversations?view=all&q={Uri.EscapeDataString("bao gia " + tag)}"));
        page.GetProperty("items").EnumerateArray().Should().ContainSingle();
        var empty = await Data(await api.GetAsync($"/api/v1/mail/conversations?view=all&q={Uri.EscapeDataString("in:spam " + tag)}"));
        empty.GetProperty("total").GetInt64().Should().Be(0);

        var bad = await api.GetAsync($"/api/v1/mail/conversations?view=all&q={Uri.EscapeDataString("(bao gia")}");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("code").GetString().Should().Be("search_unbalanced_parenthesis");
        problem.GetProperty("title").GetString().Should().Be("Dấu ngoặc tròn không khớp.", "errors come in the user's language (BUG-035)");
        var reversed = await api.GetAsync($"/api/v1/mail/conversations?view=all&q={Uri.EscapeDataString("after:2026/03/01 before:2026/02/01")}");
        (await reversed.Content.ReadAsStringAsync()).Should().Contain("search_date_range_reversed");
    }

    [Fact]
    public async Task Filters_act_at_delivery_and_can_be_applied_to_existing_mail()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var api = await Client("quang@bluemail.test");
        var old = await Receive("quang@bluemail.test", $"From: hoadon@evn.test\nTo: quang@bluemail.test\nSubject: Hoá đơn tháng 10 {tag}\nMessage-ID: <o{tag}@t>\n\nx\n");
        var labelId = (await Data(await api.PostAsJsonAsync("/api/v1/mail/labels", new { name = "Hoá đơn " + tag }))).GetGuid();

        (await api.PostAsJsonAsync("/api/v1/mail/filters", new { criteria = "from:(", actions = new { markRead = true } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.PostAsJsonAsync("/api/v1/mail/filters", new { criteria = "from:x", actions = new { forwardTo = "nobody@doitac.test" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "forwarding needs a verified address");
        var filterId = await Data(await api.PostAsJsonAsync("/api/v1/mail/filters", new
        {
            criteria = $"from:hoadon@evn.test subject:{tag}",
            actions = new { skipInbox = true, markRead = true, star = true, labelIds = new[] { labelId } },
        }));

        var fresh = await Receive("quang@bluemail.test", $"From: Hoá đơn EVN <hoadon@evn.test>\nTo: quang@bluemail.test\nSubject: Hoá đơn tháng 11 {tag}\nMessage-ID: <n{tag}@t>\n\nx\n");
        var labels = await Labels(fresh);
        labels.Should().Contain([SystemLabels.Starred, "Hoá đơn " + tag]).And.NotContain(SystemLabels.Inbox);
        (await Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == fresh))).Should().Match<Message>(m => m.Seen && m.Flagged);
        var other = await Receive("quang@bluemail.test", $"From: khac@evn.test\nTo: quang@bluemail.test\nSubject: Khác {tag}\nMessage-ID: <k{tag}@t>\n\nx\n");
        (await Labels(other)).Should().Contain(SystemLabels.Inbox, "a filter only acts on what it matches");

        // Existing mail: in the background, with progress.
        (await Data(await api.GetAsync($"/api/v1/mail/filters/matches?q={Uri.EscapeDataString($"from:hoadon@evn.test subject:{tag}")}"))).GetInt32().Should().Be(2);
        var job = await Data(await api.PostAsync($"/api/v1/mail/filters/{filterId.GetGuid()}/apply", null));
        (await stack.Api.Services.GetRequiredService<FilterJobWorker>().RunOnceAsync(default)).Should().BeTrue();
        var status = await Data(await api.GetAsync($"/api/v1/mail/filter-jobs/{job.GetProperty("id").GetGuid()}"));
        status.GetProperty("status").GetString().Should().Be("DONE");
        status.GetProperty("total").GetInt32().Should().Be(2);
        (await Labels(old)).Should().Contain("Hoá đơn " + tag).And.NotContain(SystemLabels.Inbox);

        // Gmail export / import round trip.
        var xml = await (await api.GetAsync("/api/v1/mail/filters/export")).Content.ReadAsStringAsync();
        xml.Should().Contain("hasTheWord").And.Contain("Hoá đơn " + tag);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(xml)), "file", "mailFilters.xml" } };
        var imported = await Data(await api.PostAsync("/api/v1/mail/filters/import", form));
        imported.GetProperty("imported").GetInt32().Should().BeGreaterThan(0);
        var all = await Data(await api.GetAsync("/api/v1/mail/filters"));
        all.EnumerateArray().Count(f => f.GetProperty("criteria").GetString()!.Contains(tag, StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Blocked_sender_goes_to_spam_and_never_spam_filters_cannot_pull_it_back()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var api = await Client("tuan@bluemail.test");
        (await api.PostAsJsonAsync("/api/v1/mail/blocked", new { addressOrDomain = "not an address" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Data(await api.PostAsJsonAsync("/api/v1/mail/blocked", new { addressOrDomain = "@spammy-" + tag + ".test" }));
        await Data(await api.PostAsJsonAsync("/api/v1/mail/filters", new { criteria = "spammy", actions = new { neverSpam = true } }));
        var id = await Receive("tuan@bluemail.test", $"From: x@spammy-{tag}.test\nTo: tuan@bluemail.test\nSubject: spammy {tag}\nMessage-ID: <b{tag}@t>\n\nx\n");
        (await Labels(id)).Should().Contain(SystemLabels.Spam).And.NotContain(SystemLabels.Inbox);

        // never spam does rescue a message the scorer put in Spam.
        var scored = await Receive("tuan@bluemail.test", $"From: y@other-{tag}.test\nTo: tuan@bluemail.test\nSubject: spammy {tag}\nMessage-ID: <c{tag}@t>\n\nx\n", spam: true);
        (await Labels(scored)).Should().Contain(SystemLabels.Inbox).And.NotContain(SystemLabels.Spam);
    }

    [Fact]
    public async Task Forwarding_needs_a_code_uses_srs_arc_and_stops_loops_and_admins_can_forbid_external()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var target = $"thao.{tag}@partner.test";
        using var api = await Client("thao@bluemail.test");
        var id = (await Data(await api.PostAsJsonAsync("/api/v1/mail/forwarding", new { targetEmail = target }))).GetGuid();
        (await api.PutAsJsonAsync("/api/v1/mail/forwarding/active", new { id, keepCopy = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "not verified yet");

        // The code went to the target address, in the user's language.
        var codeMail = (await Queued(target)).Single();
        codeMail.EnvelopeFrom.Should().BeEmpty("verification mail uses the null sender");
        var raw = Encoding.UTF8.GetBytes(await Blob(codeMail.Tenant, codeMail.Sha));
        var parsed = MimeMessage.Parse(new MemoryStream(raw));
        var code = CodeRegex().Match(parsed.BodyText(new MemoryStream(raw))).Value;
        code.Should().HaveLength(6);
        (await api.PostAsJsonAsync($"/api/v1/mail/forwarding/{id}/verify", new { code = code == "000000" ? "111111" : "000000" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.PostAsJsonAsync($"/api/v1/mail/forwarding/{id}/verify", new { code })).EnsureSuccessStatusCode();
        (await api.PutAsJsonAsync("/api/v1/mail/forwarding/active", new { id, keepCopy = false })).EnsureSuccessStatusCode();

        var msg = await Receive("thao@bluemail.test", $"From: an@acme.test\nTo: thao@bluemail.test\nSubject: fw {tag}\nMessage-ID: <f{tag}@t>\n\nxin chào\n", "an@acme.test");
        (await Labels(msg)).Should().Contain(SystemLabels.Trash, "keep copy off").And.NotContain(SystemLabels.Inbox);
        var forwarded = (await Queued(target)).Last();
        forwarded.EnvelopeFrom.Should().StartWith("SRS0=").And.EndWith("=acme.test=an@bluemail.test");
        var content = await Blob(forwarded.Tenant, forwarded.Sha);
        content.Should().StartWith("ARC-Seal:").And.Contain("X-BlueMail-Loop: ").And.Contain("Subject: fw " + tag);
        await using (var scope = stack.ApiScope())
        {
            scope.ServiceProvider.GetRequiredService<Srs>().Reverse(forwarded.EnvelopeFrom).Should().Be("an@acme.test");
        }

        // The same message coming back through this mailbox is not forwarded again.
        var loopHeader = Regex.Match(content, "X-BlueMail-Loop: [0-9a-f]+").Value;
        var before = (await Queued(target)).Count;
        await Receive("thao@bluemail.test", $"{loopHeader}\nFrom: an@acme.test\nTo: thao@bluemail.test\nSubject: loop {tag}\nMessage-ID: <l{tag}@t>\n\nx\n", "an@acme.test");
        (await Queued(target)).Count.Should().Be(before);

        // Administrators forbid forwarding outside the organisation: existing forwards stop, new external addresses are refused.
        using var admin = await Client("lan@bluemail.test");
        (await admin.PutAsJsonAsync("/api/v1/admin/policies/allow_external_forwarding", new { value = false })).EnsureSuccessStatusCode();
        try
        {
            await Receive("thao@bluemail.test", $"From: an@acme.test\nTo: thao@bluemail.test\nSubject: blocked {tag}\nMessage-ID: <x{tag}@t>\n\nx\n", "an@acme.test");
            (await Queued(target)).Count.Should().Be(before);
            (await api.PostAsJsonAsync("/api/v1/mail/forwarding", new { targetEmail = "other@partner.test" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/v1/admin/policies/allow_external_forwarding", new { value = (bool?)null })).EnsureSuccessStatusCode();
            await api.PutAsJsonAsync("/api/v1/mail/forwarding/active", new { id = (Guid?)null, keepCopy = true });
        }
    }

    [Fact]
    public async Task Vacation_replies_once_per_sender_and_never_to_lists_or_automatic_mail()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var sender = $"khach.{tag}@partner.test";
        using var api = await Client("somchai@bluemail.test");
        (await api.PutAsJsonAsync("/api/v1/mail/vacation", new { enabled = true, subject = "Out of office", html = "<p>I am away</p><script>x</script>" })).EnsureSuccessStatusCode();
        (await Data(await api.GetAsync("/api/v1/mail/vacation"))).GetProperty("html").GetString().Should().NotContain("script");

        await Receive("somchai@bluemail.test", $"From: {sender}\nTo: somchai@bluemail.test\nSubject: Hello {tag}\nMessage-ID: <v1{tag}@t>\n\nx\n", sender);
        await Receive("somchai@bluemail.test", $"From: {sender}\nTo: somchai@bluemail.test\nSubject: Again {tag}\nMessage-ID: <v2{tag}@t>\n\nx\n", sender);
        var replies = await Queued(sender);
        replies.Should().ContainSingle("one reply per sender per interval");
        replies[0].EnvelopeFrom.Should().BeEmpty("RFC 3834 3.3: null return path");
        var reply = await Blob(replies[0].Tenant, replies[0].Sha);
        reply.Should().Contain("Auto-Submitted: auto-replied").And.Contain("In-Reply-To: <v1" + tag + "@t>").And.Contain("Subject: Out of office");

        var list = $"list.{tag}@partner.test";
        await Receive("somchai@bluemail.test", $"From: {list}\nTo: somchai@bluemail.test\nList-Id: <l.partner.test>\nSubject: News\nMessage-ID: <v3{tag}@t>\n\nx\n", list);
        var auto = $"auto.{tag}@partner.test";
        await Receive("somchai@bluemail.test", $"From: {auto}\nTo: somchai@bluemail.test\nAuto-Submitted: auto-replied\nSubject: Re\nMessage-ID: <v4{tag}@t>\n\nx\n", auto);
        (await Queued(list)).Should().BeEmpty();
        (await Queued(auto)).Should().BeEmpty();
        await api.PutAsJsonAsync("/api/v1/mail/vacation", new { enabled = false, subject = "", html = "" });
    }

    [GeneratedRegex("\\b\\d{6}\\b")]
    private static partial Regex CodeRegex();
}
