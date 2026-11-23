using BlueMail.MailStore;
using BlueMail.MailStore.Search;
using BlueMail.Mime;

namespace BlueMail.UnitTests.Search;

public class SearchParserTests
{
    private static readonly TimeZoneInfo Hcm = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private static SearchNode P(string q) => SearchParser.Parse(q, new SearchContext(Hcm, Now))!;

    private static string Fail(string q)
    {
        var act = () => SearchParser.Parse(q, new SearchContext(Hcm, Now));
        return act.Should().Throw<SearchSyntaxException>().Which.Code;
    }

    [Fact]
    public void Empty_query_is_no_search() => SearchParser.Parse("   ", SearchContext.Utc()).Should().BeNull();

    [Fact]
    public void Words_are_anded_or_binds_looser_and_minus_negates()
    {
        P("hop dong").Should().BeOfType<AndNode>().Which.Items.Should().Equal(new TextNode("hop", false), new TextNode("dong", false));
        var or = (OrNode)P("hop dong OR bao gia -nhap");
        or.Items.Should().HaveCount(2);
        ((AndNode)or.Items[1]).Items[^1].Should().BeOfType<NotNode>().Which.Item.Should().Be(new TextNode("nhap", false));
    }

    [Fact]
    public void Phrases_braces_and_parentheses()
    {
        P("\"hợp đồng\"").Should().Be(new TextNode("hợp đồng", true));
        P("{a b c}").Should().BeOfType<OrNode>().Which.Items.Should().HaveCount(3);
        P("(a OR b) c").Should().BeOfType<AndNode>().Which.Items[0].Should().BeOfType<OrNode>();
        P("e-mail").Should().Be(new TextNode("e-mail", false), "a dash inside a word is not a negation");
        P("a AND b").Should().BeOfType<AndNode>();
    }

    [Theory]
    [InlineData("from:an@acme.vn", AddressField.From)]
    [InlineData("to:lan", AddressField.To)]
    [InlineData("cc:minh", AddressField.Cc)]
    [InlineData("bcc:hoa", AddressField.Bcc)]
    [InlineData("deliveredto:sales@bluemail.test", AddressField.DeliveredTo)]
    public void Address_operators(string q, AddressField field) =>
        P(q).Should().BeOfType<AddressNode>().Which.Field.Should().Be(field);

    [Fact]
    public void Me_is_the_searchers_addresses()
    {
        P("from:me").Should().Be(new AddressNode(AddressField.From, "", true));
        P("FROM:Me").Should().Be(new AddressNode(AddressField.From, "", true), "operators and me are case-insensitive");
    }

    [Fact]
    public void Field_operators_take_quotes_and_groups()
    {
        P("subject:\"hợp đồng\"").Should().Be(new FieldNode(TextField.Subject, "hợp đồng"));
        P("subject:(bao gia)").Should().BeOfType<AndNode>().Which.Items.Should().AllBeOfType<FieldNode>();
        P("from:(an OR binh)").Should().BeOfType<OrNode>().Which.Items.Should().AllBeOfType<AddressNode>();
        P("filename:pdf").Should().Be(new FieldNode(TextField.FileName, "pdf"));
        P("list:news.acme.vn").Should().Be(new FieldNode(TextField.List, "news.acme.vn"));
        P("rfc822msgid:<abc@x>").Should().Be(new MessageIdNode("abc@x"));
    }

    [Fact]
    public void Where_state_and_has()
    {
        P("in:inbox").Should().Be(new InNode("INBOX"));
        P("in:anywhere").Should().Be(new InNode(InNode.Anywhere));
        P("in:du-an").Should().Be(new LabelNode("du-an"), "in:<label> works like label:");
        P("label:du-an").Should().Be(new LabelNode("du-an"));
        P("category:promotions").Should().Be(new InNode("CATEGORY_PROMOTIONS"));
        P("is:unread").Should().Be(new IsNode(MessageState.Unread));
        P("is:muted").Should().Be(new IsNode(MessageState.Muted));
        P("has:attachment").Should().Be(new HasNode(HasWhat.Attachment));
        P("has:nouserlabels").Should().Be(new HasNode(HasWhat.NoUserLabels));
    }

    [Fact]
    public void Dates_are_local_midnight_and_periods_relative_to_now()
    {
        P("after:2026/01/01").Should().Be(new DateNode(true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(7))));
        P("before:2026-02-03").Should().Be(new DateNode(false, new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.FromHours(7))));
        P("older:2026/1/5").Should().BeOfType<DateNode>().Which.After.Should().BeFalse();
        P("after:1767225600").Should().Be(new DateNode(true, DateTimeOffset.FromUnixTimeSeconds(1767225600)));
        P("newer_than:7d").Should().Be(new DateNode(true, Now.AddDays(-7)));
        P("older_than:1y").Should().Be(new DateNode(false, Now.AddYears(-1)));
        P("newer_than:2m").Should().Be(new DateNode(true, Now.AddMonths(-2)));
    }

    [Fact]
    public void Sizes()
    {
        P("larger:10M").Should().Be(new SizeNode(true, 10L << 20));
        P("size:2048").Should().Be(new SizeNode(true, 2048));
        P("smaller:500K").Should().Be(new SizeNode(false, 500L << 10));
        P("larger:1GB").Should().Be(new SizeNode(true, 1L << 30));
    }

    [Fact]
    public void Unknown_prefix_is_just_a_word() => P("10:30").Should().Be(new TextNode("10:30", false));

    [Theory]
    [InlineData("\"hợp đồng", "unterminated_quote")]
    [InlineData("(a b", "unbalanced_parenthesis")]
    [InlineData("a b)", "unbalanced_parenthesis")]
    [InlineData("{a b", "unbalanced_brace")]
    [InlineData("a }", "unbalanced_brace")]
    [InlineData("from:", "missing_value")]
    [InlineData("from: an", "missing_value")]
    [InlineData("subject:\"\"", "missing_value")]
    [InlineData("is:green", "invalid_value")]
    [InlineData("has:drive", "invalid_value")]
    [InlineData("category:news", "invalid_value")]
    [InlineData("after:2026/13/01", "invalid_date")]
    [InlineData("after:2026/02/30", "invalid_date")]
    [InlineData("before:yesterday", "invalid_date")]
    [InlineData("newer_than:7w", "invalid_period")]
    [InlineData("older_than:d", "invalid_period")]
    [InlineData("larger:ten", "invalid_size")]
    [InlineData("a OR", "dangling_or")]
    [InlineData("OR a", "dangling_or")]
    [InlineData("a OR OR b", "dangling_or")]
    [InlineData("AND a", "dangling_and")]
    [InlineData("()", "empty_group")]
    [InlineData("{}", "empty_group")]
    [InlineData("\"  \"", "empty_phrase")]
    [InlineData("after:2026/03/01 before:2026/02/01", "date_range_reversed")]
    [InlineData("newer_than:1d older_than:7d", "date_range_reversed")]
    [InlineData("subject:(from:a)", "operator_inside_operator")]
    public void Broken_queries_say_why(string q, string code) => Fail(q).Should().Be(code);

    [Fact]
    public void Deep_nesting_and_long_queries_are_refused()
    {
        Fail(new string('(', 30) + "a" + new string(')', 30)).Should().Be("too_deep");
        Fail(new string('a', SearchParser.MaxLength + 1)).Should().Be("query_too_long");
    }

    [Fact]
    public void Spam_and_trash_are_hidden_unless_named()
    {
        SearchParser.LooksInSpamOrTrash(P("hop dong")).Should().BeFalse();
        SearchParser.LooksInSpamOrTrash(P("in:spam lottery")).Should().BeTrue();
        SearchParser.LooksInSpamOrTrash(P("in:anywhere")).Should().BeTrue();
        SearchParser.LooksInSpamOrTrash(P("-in:trash")).Should().BeFalse();
    }
}

public class SearchMatcherTests
{
    private static readonly SearchMatcher M = new(LatinTextFolder.Instance, ["minh@bluemail.test"]);

    private static SearchSubject Msg() => new()
    {
        Subject = "Hợp đồng số 12 - Báo giá",
        From = new MailboxAddress("Nguyễn Văn An", "an@acme.vn"),
        To = [new MailboxAddress("Minh", "minh@bluemail.test")],
        Cc = [new MailboxAddress(null, "hoa@bluemail.test")],
        DeliveredTo = ["minh@bluemail.test"],
        FileNames = ["BaoCao_Q4.xlsx"],
        HasAttachments = true,
        ListId = "news.acme.vn",
        RfcMessageId = "abc@acme.vn",
        ReceivedAt = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero),
        Size = 3 << 20,
        SystemLabels = ["INBOX", "CATEGORY_PRIMARY"],
        UserLabels = ["Dự án/Q4"],
        IndexSubject = SearchText.Prepare("Hợp đồng số 12 - Báo giá"),
        IndexPeople = SearchText.Prepare("Nguyễn Văn An an@acme.vn an acme vn Minh minh@bluemail.test"),
        IndexFiles = SearchText.Prepare("BaoCao_Q4.xlsx"),
        IndexBody = SearchText.Prepare("Kính gửi anh Minh, bên em gửi báo giá và hợp đồng. 合同已经签署"),
    };

    private static bool Is(string q) => M.Matches(SearchParser.Parse(q, new SearchContext(TimeZoneInfo.Utc, new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero)))!, Msg());

    [Theory]
    [InlineData("hop dong", true)]
    [InlineData("HỢP ĐỒNG", true)]
    [InlineData("\"bao gia va hop dong\"", true)]
    [InlineData("\"hop dong bao gia\"", false)]
    [InlineData("签署", true)]
    [InlineData("hop -dong", false)]
    [InlineData("hop OR khong", true)]
    [InlineData("{khong co gi}", false)]
    [InlineData("from:an@acme", true)]
    [InlineData("from:nguyen", true)]
    [InlineData("from:me", false)]
    [InlineData("to:me", true)]
    [InlineData("cc:hoa", true)]
    [InlineData("bcc:hoa", false)]
    [InlineData("deliveredto:me", true)]
    [InlineData("subject:\"bao gia\"", true)]
    [InlineData("subject:(hop gia)", true)]
    [InlineData("filename:xlsx", true)]
    [InlineData("filename:pdf", false)]
    [InlineData("list:acme", true)]
    [InlineData("rfc822msgid:abc@acme.vn", true)]
    [InlineData("label:du-an-q4", true)]
    [InlineData("label:Dự-án/Q4", true)]
    [InlineData("in:inbox", true)]
    [InlineData("in:spam", false)]
    [InlineData("category:primary", true)]
    [InlineData("is:unread", true)]
    [InlineData("is:starred", false)]
    [InlineData("has:attachment", true)]
    [InlineData("has:userlabels", true)]
    [InlineData("has:nouserlabels", false)]
    [InlineData("after:2026/11/20 before:2026/11/21", true)]
    [InlineData("after:2026/11/21", false)]
    [InlineData("newer_than:7d", true)]
    [InlineData("older_than:1d", true)]
    [InlineData("larger:2M", true)]
    [InlineData("smaller:2M", false)]
    public void Operators_match_like_search(string q, bool expected) => Is(q).Should().Be(expected, q);

    [Fact]
    public void Latin_folder_removes_vietnamese_marks_but_not_kana_voicing()
    {
        LatinTextFolder.Instance.Fold("Đường Hợp Đồng").Should().Be("duong hop dong");
        LatinTextFolder.Instance.Fold("ガギ").Should().Be("ガギ");
        LatinTextFolder.Instance.Fold("Straße Łódź").Should().Be("strasse lodz");
    }
}

public class SearchErrorCodeListTests
{
    [Fact]
    public void The_published_code_list_covers_every_code_the_parser_raises()
    {
        var source = File.ReadAllText(Path.Combine(BlueMail.UnitTests.Conventions.Repo.Root, "src", "BlueMail.MailStore", "Search", "SearchQuery.cs"));
        var raised = System.Text.RegularExpressions.Regex.Matches(source, @"SearchSyntaxException\(""([a-z_]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        raised.Should().BeSubsetOf(SearchParser.ErrorCodes);
    }
}
