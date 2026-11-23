using System.Text;
using BlueMail.Mime;

namespace BlueMail.UnitTests.Mime;

public class EncodedWordTests
{
    [Theory]
    [InlineData("=?utf-8?B?VGnhur9uZyBWaeG7h3Q=?=", "Tiếng Việt")]
    [InlineData("=?ISO-8859-1?Q?R=E9union_=E0_Z=FCrich?=", "Réunion à Zürich")]
    [InlineData("=?utf-8?Q?a?= =?utf-8?Q?b?=", "ab")]                          // whitespace between words dropped (RFC 2047 6.2)
    [InlineData("Hello =?utf-8?Q?W=C3=B6rld?= !", "Hello Wörld !")]
    [InlineData("=?utf-8*vi?B?VGnhur9uZw==?=", "Tiếng")]                      // RFC 2231 language suffix
    [InlineData("=?utf-8?B?broken", "=?utf-8?B?broken")]                      // malformed stays as text
    [InlineData("=?x-unknown?Q?abc?=", "abc")]                                // unknown charset: ASCII still readable
    [InlineData("plain text", "plain text")]
    public void Decodes(string input, string expected) => EncodedWords.Decode(input).Should().Be(expected);

    [Fact]
    public void Joins_bytes_split_inside_a_multibyte_character()
    {
        var bytes = Encoding.UTF8.GetBytes("khẩn");
        var w1 = "=?utf-8?B?" + Convert.ToBase64String(bytes[..3]) + "?=";
        var w2 = "=?utf-8?B?" + Convert.ToBase64String(bytes[3..]) + "?=";
        EncodedWords.Decode(w1 + " " + w2).Should().Be("khẩn");
    }

    [Fact]
    public void Encoding_round_trips_and_keeps_words_short_and_graphemes_whole()
    {
        var text = "Trả lời: 👨‍👩‍👧‍👦 báo giá mới nhất 合作愉快 " + new string('x', 100);
        var encoded = EncodedWords.Encode(text);
        encoded.Split(' ').Should().OnlyContain(w => w.Length <= 75);
        EncodedWords.Decode(encoded).Should().Be(text);
        EncodedWords.Encode("plain ascii").Should().Be("plain ascii");
    }
}

public class ParameterTests
{
    [Fact]
    public void Rfc2231_continuations_with_charset_are_joined_and_decoded()
    {
        var v = HeaderValueWithParameters.Parse("attachment; filename*0*=utf-8''H%E1%BB%A3p%20%C4%91; filename*1*=%E1%BB%93ng.pdf; filename=old.pdf");
        v.Value.Should().Be("attachment");
        v["filename"].Should().Be("Hợp đồng.pdf", "extended parameter wins over the plain one");
    }

    [Fact]
    public void Quoted_values_comments_and_unterminated_quotes()
    {
        var v = HeaderValueWithParameters.Parse("multipart/mixed (comment); boundary=\"a;b \\\"c\\\"\"; charset=UTF-8");
        v.Value.Should().Be("multipart/mixed");
        v["boundary"].Should().Be("a;b \"c\"");
        v["charset"].Should().Be("UTF-8");
        HeaderValueWithParameters.Parse("text/plain; name=\"unterminated")["name"].Should().Be("unterminated");
    }

    [Fact]
    public void Encoded_word_inside_quoted_parameter_is_decoded()
    {
        HeaderValueWithParameters.Parse("application/pdf; name=\"=?utf-8?B?QsOhbyBnacOhLnBkZg==?=\"")["name"].Should().Be("Báo giá.pdf");
    }
}

public class AddressTests
{
    [Fact]
    public void Parses_names_groups_comments_and_encoded_words()
    {
        var list = AddressParser.Parse("\"Le, Minh (Sales)\" <minh@doitac.test> (team), Team: a@x.test, \"B. Tran\" <b@x.test>;, =?utf-8?B?TMOibg==?= <lan@y.test>, plain@z.test (Plain Name)");
        list.Should().Equal(
            new MailboxAddress("Le, Minh (Sales)", "minh@doitac.test"),
            new MailboxAddress(null, "a@x.test"),
            new MailboxAddress("B. Tran", "b@x.test"),
            new MailboxAddress("Lân", "lan@y.test"),
            new MailboxAddress("Plain Name", "plain@z.test"));
    }

    [Theory]
    [InlineData("undisclosed-recipients:;", 0)]
    [InlineData("", 0)]
    [InlineData("<@route.example:user@host.test>", 1)]
    [InlineData("Đặng Thị Hoa <hoa@đốitác.test>", 1)]
    [InlineData("a@b.test, , c@d.test", 2)]
    public void Edge_cases(string input, int count) => AddressParser.Parse(input).Should().HaveCount(count);

    [Fact]
    public void Obsolete_route_is_dropped() => AddressParser.ParseSingle("<@route.example:user@host.test>")!.Address.Should().Be("user@host.test");
}

public class DateTests
{
    [Theory]
    [InlineData("Mon, 2 Nov 2026 09:15:00 +0700", "2026-11-02T02:15:00Z")]
    [InlineData("2 Nov 2026 09:15 +0700", "2026-11-02T02:15:00Z")]                 // no day name, no seconds
    [InlineData("5 Nov 26 14:00 GMT", "2026-11-05T14:00:00Z")]                     // 2-digit year, zone name
    [InlineData("Sat, 7 Nov 99 07:07:07 EST", "1999-11-07T12:07:07Z")]             // 99 -> 1999
    [InlineData("Tue, 3 Nov 2026 10:00:00 +0000 (UTC)", "2026-11-03T10:00:00Z")]   // trailing comment
    [InlineData("Nov 5 2026 08:00:00 -0800", "2026-11-05T16:00:00Z")]              // month first (seen in the wild)
    public void Parses(string input, string utc) =>
        DateParser.Parse(input)!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture).Should().Be(utc);

    [Theory]
    [InlineData("31/12/2026 25:61")]
    [InlineData("Mon, 31 Feb 2026 10:00:00 +0000")]
    [InlineData("")]
    public void Rejects(string input) => DateParser.Parse(input).Should().BeNull();

    [Fact]
    public void Formats_rfc5322() =>
        DateParser.Format(new DateTimeOffset(2026, 11, 23, 13, 5, 0, TimeSpan.FromHours(7))).Should().Be("Mon, 23 Nov 2026 13:05:00 +0700");
}

public class TransferEncodingTests
{
    [Fact]
    public void Base64_skips_garbage_and_stops_at_padding()
    {
        var clean = Convert.ToBase64String("Nội dung có rác"u8.ToArray());
        var noisy = clean[..5] + "!*\t" + clean[5..12] + "\r\n" + clean[12..] + "===trailing";
        Encoding.UTF8.GetString(TransferDecoding.DecodeBase64Lenient(Encoding.ASCII.GetBytes(noisy))).Should().Be("Nội dung có rác");
    }

    [Theory]
    [InlineData("Soft=\r\nbreak", "Softbreak")]
    [InlineData("Soft=\nbreak", "Softbreak")]                  // soft break with bare LF
    [InlineData("a=3Db =ZZ end=", "a=b =ZZ end")]               // invalid escape kept, final '=' dropped
    [InlineData("trailing   \r\nnext", "trailing\r\nnext")]     // transport padding removed
    [InlineData("caf=C3=A9", "café")]
    public void Quoted_printable(string input, string expected) =>
        Encoding.UTF8.GetString(TransferDecoding.DecodeQuotedPrintable(Encoding.ASCII.GetBytes(input))).Should().Be(expected);

    [Theory]
    [InlineData("base64")]
    [InlineData("quoted-printable")]
    public void Streaming_decode_equals_whole_buffer_decode_at_any_chunk_size(string cte)
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Tiếng Việt = 100% \t \r\n", 3000)));
        var encoded = Encoding.ASCII.GetBytes(cte == "base64" ? TransferDecoding.EncodeBase64Lines(payload) : TransferDecoding.EncodeQuotedPrintable(payload));
        foreach (var chunk in new[] { 1, 2, 3, 7, 64, 16384 })
        {
            using var s = TransferDecoding.Decode(new ChunkedStream(encoded, chunk), cte);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.ToArray().Should().Equal(payload, $"chunk {chunk}");
        }
    }

    [Fact]
    public void Quoted_printable_encoder_keeps_lines_under_77()
    {
        var qp = TransferDecoding.EncodeQuotedPrintable(Encoding.UTF8.GetBytes(new string('é', 500) + " \r\nend  "));
        qp.Split("\r\n").Should().OnlyContain(l => l.Length <= 76);
        Encoding.UTF8.GetString(TransferDecoding.DecodeQuotedPrintable(Encoding.ASCII.GetBytes(qp))).Should().Be(new string('é', 500) + " \r\nend  ");
    }

    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
    }
}

public class ParserTests
{
    private static byte[] Msg(string s) => Encoding.UTF8.GetBytes(s.Replace("\n", "\r\n", StringComparison.Ordinal));

    [Fact]
    public void Offsets_let_a_part_be_read_alone_and_exclude_the_boundary_line_break()
    {
        var data = Msg("Subject: t\nContent-Type: multipart/mixed; boundary=XX\n\npreamble\n--XX\nContent-Type: text/plain\n\nfirst\n--XX\nContent-Type: application/octet-stream\nContent-Transfer-Encoding: base64\n\nAAEC\n--XX--\nepilogue\n");
        using var s = new MemoryStream(data);
        var m = MimeMessage.Parse(s);
        m.Defects.Should().BeEmpty();
        var parts = m.Root.Children;
        parts.Should().HaveCount(2);
        parts[0].PartPath.Should().Be("1");
        parts[0].ReadText(s).Should().Be("first");
        parts[1].ReadDecodedBody(s).Should().Equal(0, 1, 2);
        Encoding.ASCII.GetString(data, (int)parts[1].BodyOffset, (int)parts[1].BodyLength).Should().Be("AAEC");
    }

    [Fact]
    public void Nested_boundary_that_extends_the_outer_one_is_not_confused()
    {
        var data = Msg("Content-Type: multipart/mixed; boundary=A\n\n--A\nContent-Type: multipart/alternative; boundary=A2\n\n--A2\n\ninner\n--A2--\n--A\n\nsecond\n--A--\n");
        var m = MimeMessage.Parse(data);
        m.Root.Children.Should().HaveCount(2);
        m.Root.Children[0].Children.Should().HaveCount(1);
        m.Root.Children[0].Children[0].PartPath.Should().Be("1.1");
    }

    [Fact]
    public void Header_without_colon_starts_the_body_and_is_recorded()
    {
        var m = MimeMessage.Parse(Msg("Subject: x\nNot A Header\nContent-Type: text/html\n\nbody"));
        m.Defects.Should().Contain(d => d.Kind == MimeDefectKind.HeaderWithoutColon);
        m.Root.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public void Depth_limit_turns_deep_nesting_into_a_leaf()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            sb.Append($"Content-Type: multipart/mixed; boundary=B{i}\n\n--B{i}\n");
        }

        sb.Append("\nleaf\n");
        var m = MimeMessage.Parse(Msg(sb.ToString()), MimeLimits.Default with { MaxDepth = 3 });
        m.Defects.Should().Contain(d => d.Kind == MimeDefectKind.DepthLimitReached);
        m.Root.Walk().Max(e => e.Depth).Should().Be(3);
    }

    [Fact]
    public void Header_block_and_line_limits_are_enforced_without_throwing()
    {
        var huge = "Subject: x\nX-Big: " + new string('a', 300_000) + "\n\nbody";
        var m = MimeMessage.Parse(Encoding.ASCII.GetBytes(huge), MimeLimits.Default with { MaxHeaderBlockBytes = 1000, MaxLineBytes = 5000 });
        m.Defects.Select(d => d.Kind).Should().Contain([MimeDefectKind.LineTooLong, MimeDefectKind.HeaderBlockTooLarge]);
    }

    [Fact]
    public void Message_rfc822_children_and_digest_default_type()
    {
        var data = Msg("Content-Type: multipart/digest; boundary=D\n\n--D\n\nSubject: inner\n\nhello\n--D--\n");
        var m = MimeMessage.Parse(data);
        var part = m.Root.Children[0];
        part.MediaType.Should().Be("message/rfc822");
        part.Children.Should().ContainSingle();
        part.Children[0].Headers.Text("Subject").Should().Be("inner");
    }

    [Fact]
    public void Undeclared_8bit_header_uses_declared_charset()
    {
        var head = Encoding.ASCII.GetBytes("Content-Type: text/plain; charset=windows-1251\r\nSubject: ");
        var subject = Charsets.GetEncoding("windows-1251")!.GetBytes("Договор");
        var data = head.Concat(subject).Concat("\r\n\r\nx"u8.ToArray()).ToArray();
        MimeMessage.Parse(data).Subject.Should().Be("Договор");
    }

    [Fact]
    public void File_names_lose_paths_and_control_characters()
    {
        var m = MimeMessage.Parse(Msg("Content-Type: application/pdf\nContent-Disposition: attachment; filename=\"..\\\\..\\\\evil\u0007.pdf\"\n\nx"));
        m.Root.FileName.Should().Be("evil.pdf");
    }
}

public class TextFormatTests
{
    [Fact]
    public void Format_flowed_joins_soft_breaks_and_keeps_quotes_and_signature()
    {
        var text = "Đồng ý, hẹn gặp anh \nlúc 9 giờ.\n> Anh xem \n> giúp em.\n-- \nBinh";
        FormatFlowed.Decode(text, delSp: false).Should().Be("Đồng ý, hẹn gặp anh lúc 9 giờ.\n> Anh xem giúp em.\n-- \nBinh");
    }

    [Fact]
    public void Html_to_text_drops_scripts_and_decodes_entities()
    {
        HtmlToText.Convert("<html><head><style>p{}</style><script>alert(1)</script></head><body><p>Xin&nbsp;chào &amp; <b>tạm biệt</b></p><p>Dòng 2</p><!-- secret --></body></html>")
            .Should().Be("Xin chào & tạm biệt\n\nDòng 2", "paragraphs are separated by one blank line");
    }

    [Fact]
    public void Calendar_invite_is_read()
    {
        var ics = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:e1\r\nSUMMARY:Họp tổng kết\\, quý 4\r\nDTSTART:20261120T020000Z\r\nDTEND;TZID=Asia/Ho_Chi_Minh:20261120T100000\r\nORGANIZER;CN=An:mailto:an@doitac.test\r\nATTENDEE;RSVP=TRUE:mailto:lan@bluemail.test\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var inv = CalendarInvite.Parse(ics)!;
        inv.Method.Should().Be("REQUEST");
        inv.Summary.Should().Be("Họp tổng kết, quý 4");
        inv.Start.Should().Be(new DateTimeOffset(2026, 11, 20, 2, 0, 0, TimeSpan.Zero));
        inv.End!.Value.UtcDateTime.Should().Be(new DateTime(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc));
        inv.Attendees.Should().Equal("lan@bluemail.test");
    }

    [Fact]
    public void Delivery_status_is_read()
    {
        var r = DeliveryStatus.Parse("Reporting-MTA: dns; mx.test\n\nFinal-Recipient: rfc822; reject@doitac.test\nAction: failed\nStatus: 5.1.1\nDiagnostic-Code: smtp; 550 5.1.1 No such\n user\n");
        r.Should().ContainSingle();
        r[0].FinalRecipient.Should().Be("reject@doitac.test");
        r[0].Status.Should().Be("5.1.1");
        r[0].DiagnosticCode.Should().Be("550 5.1.1 No such user");
    }
}

public class BuilderTests
{
    [Fact]
    public void Rfc2231_segments_never_split_an_escape()
    {
        var p = MessageBuilder.Rfc2231FileName(new string('đ', 80) + ".pdf");
        var values = p.Split("; ").Select(s => s[(s.IndexOf('=') + 1)..]).ToList();
        values.Should().HaveCountGreaterThan(1);
        foreach (var v in values)
        {
            var body = v.StartsWith("utf-8''", StringComparison.Ordinal) ? v[7..] : v;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '%')
                {
                    (i + 2 < body.Length).Should().BeTrue($"escape at end of segment '{body}'");
                }
            }
        }

        HeaderValueWithParameters.Parse("attachment; " + p)["filename"].Should().Be(new string('đ', 80) + ".pdf");
    }

    [Theory]
    [InlineData("hello\r\nworld", "7bit")]
    [InlineData("Bonjour à tous, voici le rapport de la réunion de lundi dernier.", "quoted-printable")]
    [InlineData("Xin chào các bạn, đây là một đoạn văn bản dài bằng tiếng Việt có dấu.", "base64")] // > 20 % non-ASCII bytes
    [InlineData("中文内容中文内容中文内容", "base64")]
    public void Chooses_transfer_encoding(string text, string expected) =>
        MessageBuilder.ChooseTextEncoding(Encoding.UTF8.GetBytes(text)).Should().Be(expected);

    [Fact]
    public void Smtputf8_needed_only_for_international_addresses()
    {
        new MessageBuilder { From = new MailboxAddress("Ân", "an@x.test"), MessageId = "a@x" }.RequiresSmtpUtf8.Should().BeFalse();
        new MessageBuilder { From = new MailboxAddress(null, "ân@x.test"), MessageId = "a@x" }.RequiresSmtpUtf8.Should().BeTrue();
    }
}
