using System.Text;
using BlueMail.Imap;
using BlueMail.Protocols.Imap;

namespace BlueMail.UnitTests.Imap;

public class ImapServerUnitTests
{
    [Theory]
    [InlineData("1:4,7", 3, 10, true)]
    [InlineData("1:4,7", 7, 10, true)]
    [InlineData("1:4,7", 5, 10, false)]
    [InlineData("5:*", 9, 9, true)]
    [InlineData("*:5", 7, 9, true)]
    [InlineData("*", 9, 9, true)]
    [InlineData("*", 8, 9, false)]
    public void Sequence_sets(string text, long n, long max, bool expected)
    {
        ImapSequenceSet.TryParse(text, out var set).Should().BeTrue();
        set.Contains(n, max).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1:")]
    [InlineData("a")]
    [InlineData("1,,2")]
    public void Invalid_sequence_sets(string text) => ImapSequenceSet.TryParse(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("*", "Dự án/Q4", true)]
    [InlineData("%", "Dự án/Q4", false)]
    [InlineData("Dự án/%", "Dự án/Q4", true)]
    [InlineData("inbox", "INBOX", true)]
    [InlineData("*Q4", "Dự án/Q4", true)]
    [InlineData("a.b", "aXb", false)]
    public void List_patterns(string pattern, string name, bool expected) => ImapSession.Matches(pattern, name).Should().Be(expected);

    private static readonly string Nested = string.Join("\r\n",
        "From: Lan <lan@bluemail.test>",
        "To: team: a@x.test, b@x.test;, c@y.test",
        "Subject: =?utf-8?B?VGjGsCBt4bubaQ==?=",
        "Message-ID: <n1@t>",
        "MIME-Version: 1.0",
        "Content-Type: multipart/mixed; boundary=\"o\"",
        "",
        "preamble",
        "--o",
        "Content-Type: text/plain; charset=utf-8",
        "",
        "body one",
        "--o",
        "Content-Type: message/rfc822",
        "",
        "From: inner@x.test",
        "Subject: inner",
        "Content-Type: multipart/alternative; boundary=\"i\"",
        "",
        "--i",
        "Content-Type: text/plain",
        "",
        "inner text",
        "--i--",
        "inner epilogue",
        "--o--",
        "outer epilogue",
        "");

    [Fact]
    public void Sections_include_epilogues_and_nested_messages()
    {
        var f = new ImapMessageFormat(Encoding.UTF8.GetBytes(Nested));
        Encoding.UTF8.GetString(f.Section("1")!).Should().Be("body one");
        Encoding.UTF8.GetString(f.Section("TEXT")!).Should().StartWith("preamble").And.EndWith("outer epilogue\r\n");
        Encoding.UTF8.GetString(f.Section("2.TEXT")!).Should().EndWith("inner epilogue", "a part runs to the next boundary of its parent");
        Encoding.UTF8.GetString(f.Section("2.1")!).Should().Be("inner text");
        Encoding.UTF8.GetString(f.Section("2.HEADER")!).Should().StartWith("From: inner@x.test").And.EndWith("\r\n\r\n");
        Encoding.UTF8.GetString(f.Section("1.MIME")!).Should().StartWith("Content-Type: text/plain");
        Encoding.UTF8.GetString(f.Section("HEADER.FIELDS (SUBJECT message-id)")!).Should().Be("Subject: =?utf-8?B?VGjGsCBt4bubaQ==?=\r\nMessage-ID: <n1@t>\r\n\r\n");
        f.Section("9").Should().BeNull();
    }

    [Fact]
    public async Task Envelope_keeps_groups_and_encoded_words()
    {
        var f = new ImapMessageFormat(Encoding.UTF8.GetBytes(Nested));
        using var ms = new MemoryStream();
        using var w = new ImapResponseWriter(ms);
        f.Envelope(w);
        await w.FlushAsync(default);
        var text = Encoding.UTF8.GetString(ms.ToArray());
        text.Should().Contain("\"=?utf-8?B?VGjGsCBt4bubaQ==?=\"", "the subject stays as written");
        text.Should().Contain("((NIL NIL \"team\" NIL)(NIL NIL \"a\" \"x.test\")(NIL NIL \"b\" \"x.test\")(NIL NIL NIL NIL)(NIL NIL \"c\" \"y.test\"))");
        text.Should().Contain("((\"Lan\" NIL \"lan\" \"bluemail.test\"))");
    }
}
