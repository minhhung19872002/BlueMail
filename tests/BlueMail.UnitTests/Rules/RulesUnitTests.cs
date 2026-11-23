using System.Text;
using BlueMail.Application.Features.Rules;
using BlueMail.Domain.Mail;
using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using BlueMail.Mime;

namespace BlueMail.UnitTests.Rules;

public class SrsTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Forward_and_reverse_round_trip_case_insensitively()
    {
        var now = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        var srs = new Srs(Key, () => now);
        var a = srs.Forward("An.Nguyen@Doitac.test", "bluemail.test");
        a.Should().StartWith("SRS0=").And.EndWith("=doitac.test=An.Nguyen@bluemail.test");
        srs.Reverse(a).Should().Be("An.Nguyen@doitac.test");
        srs.Reverse(a.ToUpperInvariant()[..a.LastIndexOf('@')] + "@bluemail.test").Should().NotBeNull("MTAs may change the case of the local part");
        srs.Forward("", "bluemail.test").Should().BeEmpty("a bounce stays a bounce");
    }

    [Fact]
    public void Forged_or_old_addresses_are_refused()
    {
        var now = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        var a = new Srs(Key, () => now).Forward("x@doitac.test", "bluemail.test");
        new Srs(Key, () => now.AddDays(Srs.MaxAgeDays)).Reverse(a).Should().NotBeNull();
        new Srs(Key, () => now.AddDays(Srs.MaxAgeDays + 1)).Reverse(a).Should().BeNull("too old");
        new Srs(Key.Reverse().ToArray(), () => now).Reverse(a).Should().BeNull("another server's key");
        new Srs(Key, () => now).Reverse(a.Replace("=x@", "=y@", StringComparison.Ordinal)).Should().BeNull("changed address, same hash");
        new Srs(Key, () => now).Reverse("SRS0=abcd=aa=doitac.test@bluemail.test").Should().BeNull("malformed");
        new Srs(Key, () => now).Reverse("lan@bluemail.test").Should().BeNull();
    }
}

public class GmailFilterXmlTests
{
    private const string Sample = """
        <?xml version='1.0' encoding='UTF-8'?><feed xmlns='http://www.w3.org/2005/Atom' xmlns:apps='http://schemas.google.com/apps/2006'>
        <title>Mail Filters</title><id>tag:mail.google.com,2008:filters:1</id><updated>2026-11-20T10:00:00Z</updated>
        <author><name>Lan</name><email>lan@gmail.com</email></author>
        <entry><category term='filter'></category><title>Mail Filter</title><id>tag:mail.google.com,2008:filter:1</id><updated>2026-11-20T10:00:00Z</updated><content></content>
          <apps:property name='from' value='hoadon@evn.com.vn OR billing@vnpt.vn'/><apps:property name='label' value='Hoá đơn'/>
          <apps:property name='shouldArchive' value='true'/><apps:property name='shouldMarkAsRead' value='true'/><apps:property name='sizeOperator' value='s_sl'/>
          <apps:property name='size' value='1'/><apps:property name='sizeUnit' value='s_smb'/></entry>
        <entry><category term='filter'></category><title>Mail Filter</title><id>x</id><updated>2026-11-20T10:00:00Z</updated><content></content>
          <apps:property name='subject' value='"bao gia"'/><apps:property name='doesNotHaveTheWord' value='nhap'/><apps:property name='shouldNeverSpam' value='true'/>
          <apps:property name='smartLabelToApply' value='^smartlabel_promo'/><apps:property name='shouldAlwaysMarkAsImportant' value='true'/></entry>
        <entry><category term='filter'></category><title>Mail Filter</title><id>y</id><updated>2026-11-20T10:00:00Z</updated><content></content>
          <apps:property name='hasTheWord' value='x'/></entry>
        <entry><category term='filter'></category><title>Mail Filter</title><id>z</id><updated>2026-11-20T10:00:00Z</updated><content></content>
          <apps:property name='shouldStar' value='true'/><apps:property name='futureThing' value='1'/></entry>
        </feed>
        """;

    private static (IReadOnlyList<PortableFilter> Filters, IReadOnlyList<(int Entry, string Code, string? Detail)> Problems) Load(string xml) =>
        GmailFilters.Import(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Gmail_export_becomes_queries_our_parser_accepts()
    {
        var (filters, problems) = Load(Sample);
        filters.Should().HaveCount(2);
        filters[0].Criteria.Should().Be("from:(hoadon@evn.com.vn OR billing@vnpt.vn) larger:1M");
        filters[0].LabelNames.Should().Equal("Hoá đơn");
        filters[0].Actions.Should().Match<FilterActions>(a => a.SkipInbox && a.MarkRead);
        filters[1].Criteria.Should().Be("subject:(\"bao gia\") -(nhap)");
        filters[1].Actions.Should().Match<FilterActions>(a => a.NeverSpam && a.Category == "PROMOTIONS" && a.Important == "ALWAYS");
        problems.Select(p => (p.Entry, p.Code)).Should().Equal((3, "no_action"), (4, "unknown_property"), (4, "no_criteria"));
        foreach (var f in filters)
        {
            SearchParser.Parse(f.Criteria, SearchContext.Utc()).Should().NotBeNull(f.Criteria);
        }
    }

    [Fact]
    public void Export_then_import_keeps_query_labels_and_actions()
    {
        var original = new PortableFilter("from:(an OR binh) has:attachment -is:read",
            new FilterActions { SkipInbox = true, Star = true, Important = "NEVER", Category = "UPDATES", ForwardTo = "x@doitac.test", Trash = false }, ["Dự án/Q4"]);
        var xml = GmailFilters.Export([original], "Lan", "lan@bluemail.test", DateTimeOffset.UtcNow);
        xml.Should().StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        var (back, problems) = Load(xml);
        problems.Should().BeEmpty();
        back.Should().ContainSingle();
        back[0].Criteria.Should().Be("(from:(an OR binh) has:attachment -is:read)");
        back[0].LabelNames.Should().Equal("Dự án/Q4");
        back[0].Actions.Should().BeEquivalentTo(original.Actions);
    }

    [Fact]
    public void Doctype_and_entities_are_refused()
    {
        var evil = "<?xml version='1.0'?><!DOCTYPE feed [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><feed xmlns='http://www.w3.org/2005/Atom'>&x;</feed>";
        var act = () => Load(evil);
        act.Should().Throw<System.Xml.XmlException>();
        var notGmail = () => Load("<rss/>");
        notGmail.Should().Throw<FormatException>();
    }
}

public class VacationRuleTests
{
    private static MessageFacts Facts(string raw)
    {
        var s = new MemoryStream(Encoding.UTF8.GetBytes(raw.ReplaceLineEndings("\r\n")));
        return MessageFacts.Extract(MimeMessage.Parse(s), s);
    }

    private static readonly string[] Me = ["lan@bluemail.test"];

    [Theory]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\nSubject: hi\n\nx\n", "a@doitac.test", true)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\nSubject: hi\n\nx\n", null, false)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\nAuto-Submitted: auto-replied\n\nx\n", "a@doitac.test", false)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\nAuto-Submitted: no\n\nx\n", "a@doitac.test", true)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\nPrecedence: bulk\n\nx\n", "a@doitac.test", false)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\nList-Id: <news.doitac.test>\n\nx\n", "a@doitac.test", false)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\n\nx\n", "MAILER-DAEMON@doitac.test", false)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\n\nx\n", "owner-list@doitac.test", false)]
    [InlineData("From: a@doitac.test\nTo: lan@bluemail.test\n\nx\n", "list-request@doitac.test", false)]
    [InlineData("From: a@doitac.test\nTo: everyone@doitac.test\n\nx\n", "a@doitac.test", false)]
    [InlineData("From: lan@bluemail.test\nTo: lan@bluemail.test\n\nx\n", "lan@bluemail.test", false)]
    public void Rfc3834_conditions(string raw, string? envelopeFrom, bool expected) =>
        MailboxRules.MayAutoReply(Facts(raw), envelopeFrom, Me, ["lan@bluemail.test"]).Should().Be(expected);

    [Fact]
    public void Headers_are_counted_in_the_header_block_only()
    {
        var raw = Encoding.ASCII.GetBytes("Received: a\r\nreceived: b\r\nX-BlueMail-Loop: abc\r\nSubject: s\r\n\r\nReceived: not a header\r\n");
        MailboxRules.CountHeader(raw, "Received").Should().Be(2);
        MailboxRules.HeaderContains(raw, "X-BlueMail-Loop", "abc").Should().BeTrue();
        MailboxRules.HeaderContains(raw, "X-BlueMail-Loop", "zzz").Should().BeFalse();
    }

    [Fact]
    public void Filter_actions_map_to_store_changes()
    {
        var change = FilterJobWorker.ChangeOf(new FilterActions { SkipInbox = true, MarkRead = true, Category = "SOCIAL", LabelIds = [Guid.Empty] });
        change.Should().HaveCount(2);
        change[0].RemoveSystem.Should().Contain([SystemLabels.Inbox, SystemLabels.CategoryPrimary]).And.NotContain(SystemLabels.CategorySocial);
        change[0].AddSystem.Should().Equal(SystemLabels.CategorySocial);
        change[0].Seen.Should().BeTrue();
        change[1].AddLabels.Should().ContainSingle();
        FilterJobWorker.ChangeOf(new FilterActions { Trash = true })[0].AddSystem.Should().Equal(SystemLabels.Trash);
    }
}
