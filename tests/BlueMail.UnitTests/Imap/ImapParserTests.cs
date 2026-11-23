using System.Text;
using BlueMail.Protocols.Imap;

namespace BlueMail.UnitTests.Imap;

public class ImapParserTests
{
    private static async Task<List<ImapResponse>> Parse(string wire, ImapReaderOptions? options = null)
    {
        var reader = new ImapReader(new MemoryStream(Encoding.UTF8.GetBytes(wire)), options);
        var list = new List<ImapResponse>();
        while (await reader.ReadLineAsync(null, default) is { } line)
        {
            list.Add(ImapResponseParser.Parse(line));
        }

        return list;
    }

    [Fact]
    public async Task Status_responses_with_codes_rfc3501_examples()
    {
        var r = await Parse(
            "* OK [UNSEEN 12] Message 12 is first unseen\r\n" +
            "* OK [PERMANENTFLAGS (\\Deleted \\Seen \\*)] Limited\r\n" +
            "* OK [UIDVALIDITY 3857529045] UIDs valid\r\n" +
            "A142 OK [READ-WRITE] SELECT completed\r\n" +
            "* BYE IMAP4rev1 Server logging out\r\n" +
            "a002 NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)\r\n" +
            "+ Ready for literal data\r\n");
        r[0].Should().Match<ImapResponse>(x => x.IsUntagged && x.Status == ImapStatus.Ok && x.Code == "UNSEEN" && x.Text == "Message 12 is first unseen");
        r[1].CodeArgs.Single().AsList().Select(v => v.AsText()).Should().Equal("\\Deleted", "\\Seen", "\\*");
        r[2].CodeArgs.Single().AsNumber().Should().Be(3857529045);
        r[3].Should().Match<ImapResponse>(x => x.Tag == "A142" && x.Code == "READ-WRITE");
        r[4].Status.Should().Be(ImapStatus.Bye);
        r[5].Code.Should().Be("AUTHENTICATIONFAILED");
        r[6].IsContinuation.Should().BeTrue();
    }

    [Fact]
    public async Task Data_responses_list_search_fetch_with_literal()
    {
        var body = "Subject: Xin chào\r\n\r\nNội dung\r\n";
        var bytes = Encoding.UTF8.GetByteCount(body);
        var r = await Parse(
            "* CAPABILITY IMAP4rev1 STARTTLS AUTH=PLAIN SASL-IR LITERAL+ SPECIAL-USE\r\n" +
            "* LIST (\\HasNoChildren \\Sent) \"/\" \"Sent Items\"\r\n" +
            "* LIST (\\Noselect) NIL \"\"\r\n" +
            "* LIST () \"/\" {9}\r\nM\"ail/x\\y\r\n" +
            "* SEARCH 2 84 882\r\n" +
            "* 23 EXISTS\r\n" +
            $"* 12 FETCH (UID 4827313 FLAGS (\\Seen $Forwarded) INTERNALDATE \"17-Jul-1996 02:44:25 -0700\" RFC822.SIZE 4286 BODY[] {{{bytes}}}\r\n{body})\r\n");
        r[0].Kind.Should().Be("CAPABILITY");
        r[1].Data[3].AsText().Should().Be("Sent Items");
        r[2].Data[2].Should().Be(ImapNil.Instance);
        r[3].Data[3].AsText().Should().Be("M\"ail/x\\y", "a literal is taken byte for byte");
        r[4].Data.Skip(1).Select(v => v.AsNumber()).Should().Equal(2, 84, 882);
        r[5].Kind.Should().Be("EXISTS");
        r[6].Kind.Should().Be("FETCH");
        var f = ImapClient.ParseFetch(r[6].Data[2].AsList())!;
        f.Uid.Should().Be(4827313u);
        f.Flags.Should().Equal("\\Seen", "$Forwarded");
        f.InternalDate.Should().Be(new DateTimeOffset(1996, 7, 17, 2, 44, 25, TimeSpan.FromHours(-7)));
        f.Size.Should().Be(4286);
        Encoding.UTF8.GetString(f.Body!.Bytes!).Should().Be(body);
    }

    [Fact]
    public async Task Gmail_labels_and_section_atoms()
    {
        var r = await Parse("* 1 FETCH (X-GM-LABELS (\\Inbox \\Important \"Dự án\" &AQMA4Q-n/Q4 Work) UID 7 BODY[HEADER.FIELDS (FROM TO)]<0> {2}\r\nab)\r\n");
        var attrs = r[0].Data[2].AsList();
        attrs.Select(a => a is ImapAtom at ? at.Value : null).Should().Contain("BODY[HEADER.FIELDS (FROM TO)]<0>");
        var f = ImapClient.ParseFetch(attrs)!;
        f.GmailLabels.Should().Equal("\\Inbox", "\\Important", "Dự án", "ăán/Q4", "Work");
    }

    [Fact]
    public async Task Large_literals_are_spooled_to_disk_and_streamed()
    {
        var big = new string('x', 5000) + "\r\n";
        var r = await Parse($"* 1 FETCH (UID 9 BODY[] {{{big.Length}}}\r\n{big})\r\n", new ImapReaderOptions { SpoolThreshold = 1024 });
        var f = ImapClient.ParseFetch(r[0].Data[2].AsList())!;
        f.Body!.Bytes.Should().BeNull("held in a temporary file");
        await using (var s = f.Body.OpenRead())
        {
            (await new StreamReader(s).ReadToEndAsync()).Should().Be(big);
        }

        r[0].Dispose();
    }

    [Theory]
    [InlineData("* 1 FETCH (UID 9\r\n")]
    [InlineData("* OK [UIDVALIDITY 5\r\n")]
    [InlineData("A1 HELLO\r\n")]
    [InlineData("* \"unterminated\r\n")]
    public async Task Broken_responses_fail_loudly(string wire)
    {
        var act = () => Parse(wire);
        await act.Should().ThrowAsync<ImapProtocolException>();
    }

    [Fact]
    public async Task Literal_cut_short_is_an_error_not_a_hang()
    {
        var act = () => Parse("* 1 FETCH (BODY[] {100}\r\nshort");
        await act.Should().ThrowAsync<ImapProtocolException>();
    }

    [Theory]
    [InlineData("~peter/mail/台北/日本語", "~peter/mail/&U,BTFw-/&ZeVnLIqe-")]
    [InlineData("Tom & Jerry", "Tom &- Jerry")]
    [InlineData("Thư đã gửi", "Th&AbA- &AREA4w- g&Hu0-i")] // computed independently with Python base64 over UTF-16BE
    [InlineData("INBOX", "INBOX")]
    public void Modified_utf7(string text, string wire)
    {
        var encoded = ModifiedUtf7.Encode(text);
        encoded.Should().Be(wire);
        ModifiedUtf7.Decode(wire).Should().Be(text);
    }

    [Fact]
    public void Arguments_uid_sets_and_dates()
    {
        ImapArgs.TryQuote("pa\"ss\\word").Should().Be("\"pa\\\"ss\\\\word\"");
        ImapArgs.TryQuote("mật khẩu").Should().BeNull("8-bit text goes as a literal");
        ImapArgs.UidSet([5, 1, 2, 3, 9, 10, 12]).Should().Be("1:3,5,9:10,12");
        ImapArgs.TryParseInternalDate(" 7-Feb-2026 09:05:00 +0700", out var d).Should().BeTrue();
        d.Should().Be(new DateTimeOffset(2026, 2, 7, 9, 5, 0, TimeSpan.FromHours(7)));
        ImapArgs.TryParseInternalDate("yesterday", out _).Should().BeFalse();
    }
}
