using System.Globalization;
using System.Text;
using BlueMail.Application.Features.Mail;
using BlueMail.Application.Features.Me;
using BlueMail.Application.Features.Sending;
using BlueMail.MailStore;
using BlueMail.Mime;

namespace BlueMail.UnitTests.Mail;

public class MessagePriorityTests
{
    private static HeaderList Headers(params (string Name, string Value)[] fields)
    {
        var list = new HeaderList();
        list.AddRange(fields.Select(f => new HeaderField(f.Name, Encoding.ASCII.GetBytes(f.Value))));
        return list;
    }

    [Theory]
    [InlineData("Importance", "High", MessagePriority.High)]
    [InlineData("Importance", "low", MessagePriority.Low)]
    [InlineData("Importance", "normal", MessagePriority.Normal)]
    [InlineData("X-Priority", "1 (Highest)", MessagePriority.High)]
    [InlineData("X-Priority", "2", MessagePriority.High)]
    [InlineData("X-Priority", "3", MessagePriority.Normal)]
    [InlineData("X-Priority", "5 (Lowest)", MessagePriority.Low)]
    [InlineData("X-Priority", "9", MessagePriority.Normal)]
    [InlineData("Priority", "urgent", MessagePriority.High)]
    [InlineData("Priority", "non-urgent", MessagePriority.Low)]
    [InlineData("Subject", "x", MessagePriority.Normal)]
    public void Reads_the_senders_importance(string header, string value, short expected) =>
        MessagePriority.Of(Headers((header, value))).Should().Be(expected);

    [Fact]
    public void Importance_wins_over_X_Priority() =>
        MessagePriority.Of(Headers(("X-Priority", "5"), ("Importance", "high"))).Should().Be(MessagePriority.High);

    [Fact]
    public void Writes_Outlook_headers_for_high_and_low_only()
    {
        MessagePriority.Headers(MessagePriority.High).Should().Equal(("Importance", "high"), ("X-Priority", "1 (Highest)"));
        MessagePriority.Headers(MessagePriority.Low).Should().Equal(("Importance", "low"), ("X-Priority", "5 (Lowest)"));
        MessagePriority.Headers(MessagePriority.Normal).Should().BeEmpty();
    }

    [Theory]
    [InlineData("HIGH", DraftImportance.High)]
    [InlineData("low", DraftImportance.Low)]
    [InlineData("urgent", DraftImportance.Normal)]
    [InlineData(null, DraftImportance.Normal)]
    public void Draft_importance_is_normalised(string? input, string expected) => DraftImportance.Normalize(input).Should().Be(expected);

    [Fact]
    public void Read_receipt_setting_falls_back_to_ask()
    {
        MailSettings.Normalize(new MailSettings { ReadReceipts = "ALWAYS" }).ReadReceipts.Should().Be("ALWAYS");
        MailSettings.Normalize(new MailSettings { ReadReceipts = "sometimes" }).ReadReceipts.Should().Be("ASK");
    }
}

public class ReadReceiptTests
{
    private static MessageFacts Facts(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        var stream = new MemoryStream(bytes);
        return MessageFacts.Extract(MimeMessage.Parse(stream), stream);
    }

    [Fact]
    public void Facts_keep_the_read_receipt_address_and_importance()
    {
        var f = Facts("From: An <an@doitac.test>\r\nTo: minh@bluemail.test\r\nSubject: Báo giá\r\nDisposition-Notification-To: \"An\" <an@doitac.test>\r\n"
                      + "Importance: high\r\n\r\nNội dung\r\n");
        f.DispositionNotificationTo.Should().Be("an@doitac.test");
        f.Priority.Should().Be(MessagePriority.High);
    }

    [Fact]
    public void Facts_without_a_request_have_no_address() =>
        Facts("From: an@doitac.test\r\nSubject: x\r\n\r\nbody\r\n").DispositionNotificationTo.Should().BeNull();

    private static MdnInput Input(bool automatic = false, string from = "minh@bluemail.test") => new(
        new MailboxAddress("Minh Nguyễn", from), "an@doitac.test", "Báo giá tháng 11", "orig-1@doitac.test",
        new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.Zero), ["minh@bluemail.test"], new DateTimeOffset(2026, 11, 28, 3, 30, 0, TimeSpan.Zero),
        automatic, "mail.bluemail.test", CultureInfo.GetCultureInfo("vi"), TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));

    [Fact]
    public void Mdn_is_a_disposition_notification_report()
    {
        var bytes = MdnBuilder.Build(Input());
        var msg = MimeMessage.Parse(bytes);

        msg.Root.MediaType.Should().Be("multipart/report");
        msg.Root.ContentType["report-type"].Should().Be("disposition-notification");
        msg.Subject.Should().Be("Đã đọc: Báo giá tháng 11");
        msg.InReplyTo.Should().Be("orig-1@doitac.test");
        msg.To.Single().Address.Should().Be("an@doitac.test");
        msg.Headers.All("MIME-Version").Should().ContainSingle();
        msg.Headers["Auto-Submitted"].Should().BeNull(); // sent by the user's click

        msg.Root.Children.Should().HaveCount(2);
        var human = msg.Root.Children[0].ReadText(new MemoryStream(bytes));
        human.Should().Contain("Báo giá tháng 11").And.Contain("10:30"); // read time in the reader's zone (UTC+7)

        var machine = msg.Root.Children[1];
        machine.MediaType.Should().Be("message/disposition-notification");
        machine.Charset.Should().BeNull();
        var fields = Encoding.ASCII.GetString(machine.ReadDecodedBody(new MemoryStream(bytes)));
        fields.Should().Contain("Reporting-UA: mail.bluemail.test; BlueMail\r\n")
            .And.Contain("Final-Recipient: rfc822;minh@bluemail.test\r\n")
            .And.Contain("Original-Message-ID: <orig-1@doitac.test>\r\n")
            .And.Contain("Disposition: manual-action/MDN-sent-manually; displayed\r\n");
    }

    [Fact]
    public void Automatic_mdn_says_so_and_is_auto_submitted()
    {
        var bytes = MdnBuilder.Build(Input(automatic: true));
        var msg = MimeMessage.Parse(bytes);
        msg.Headers.Text("Auto-Submitted").Should().Be("auto-replied");
        Encoding.ASCII.GetString(msg.Root.Children[1].ReadDecodedBody(new MemoryStream(bytes)))
            .Should().Contain("Disposition: automatic-action/MDN-sent-automatically; displayed");
    }

    [Fact]
    public void An_mdn_never_asks_for_another_mdn()
    {
        var mdn = Encoding.UTF8.GetString(MdnBuilder.Build(Input()));
        // Even with the header added by a careless sender, a disposition report does not ask for a receipt (RFC 8098 2.1).
        Facts("Disposition-Notification-To: minh@bluemail.test\r\n" + mdn).DispositionNotificationTo.Should().BeNull();
    }

    [Fact]
    public void Internationalised_address_uses_the_global_report_type()
    {
        var msg = MimeMessage.Parse(MdnBuilder.Build(Input(from: "minh@bưuđiện.test")));
        msg.Root.Children[1].MediaType.Should().Be("message/global-disposition-notification");
    }
}
