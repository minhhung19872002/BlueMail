using System.Text;
using BlueMail.Application.Features.Migration;
using BlueMail.Domain.Mail;
using BlueMail.Mime;
using BlueMail.Protocols.Imap;

namespace BlueMail.UnitTests.Imports;

public class ImportMappingTests
{
    private static ImapFolder F(string name, params string[] attrs) => new(name, name, "/", attrs);

    [Fact]
    public void Folders_become_system_or_user_labels()
    {
        ImportMapping.Folder(F("INBOX")).Should().Be((SystemLabels.Inbox, (IReadOnlyList<string>?)null));
        ImportMapping.Folder(F("Elementos enviados", "\\Sent")).System.Should().Be(SystemLabels.Sent, "special-use wins over the name");
        ImportMapping.Folder(F("Sent Items")).System.Should().Be(SystemLabels.Sent, "Outlook's name without special-use");
        ImportMapping.Folder(F("Thùng rác")).System.Should().Be(SystemLabels.Trash);
        ImportMapping.Folder(F("[Gmail]/All Mail", "\\All")).Should().Be(((string?)null, (IReadOnlyList<string>?)null));
        ImportMapping.Folder(F("INBOX/Dự án/Q4")).UserPath.Should().Equal("Dự án", "Q4");
        ImportMapping.Folder(F("Khách hàng")).UserPath.Should().Equal("Khách hàng");
    }

    [Fact]
    public void Gmail_labels_and_takeout_states()
    {
        var (system, user) = ImportMapping.GmailLabels(["\\Inbox", "\\Important", "\\Starred", "Dự án/Q4", "Opened", "Category Updates", "\\Muted"]);
        system.Should().Equal(SystemLabels.Inbox, SystemLabels.Important, SystemLabels.Starred);
        user.Should().ContainSingle().Which.Should().Equal("Dự án", "Q4");
        ImportMapping.SplitLabels("Inbox,\"Khách hàng, VIP\",Unread").Should().Equal("Inbox", "Khách hàng, VIP", "Unread");
    }

    [Fact]
    public void Imap_flags()
    {
        var f = ImportMapping.Flags(["\\Seen", "\\Answered", "$Forwarded", "\\Recent", "$Label1", "\\Deleted"]);
        (f.Seen, f.Flagged, f.Answered, f.Forwarded, f.Deleted).Should().Be((true, false, true, true, true));
        f.Keywords.Should().Equal("$Label1");
    }

    [Theory]
    [InlineData("X-Mozilla-Status: 0005\r\n", true, true, false)]   // read + flagged (Thunderbird)
    [InlineData("X-Mozilla-Status: 0000\r\n", false, false, false)]
    [InlineData("Status: RO\r\nX-Status: AF\r\n", true, true, true)] // mutt: read, answered, flagged
    [InlineData("Status: O\r\n", false, false, false)]
    [InlineData("", true, false, false)]                              // no state: treated as read
    public void Mbox_state_headers(string headers, bool seen, bool flagged, bool answered)
    {
        var m = MimeMessage.Parse(Encoding.ASCII.GetBytes(headers + "Subject: x\r\n\r\nbody\r\n"));
        var s = ImportMapping.MboxState(m);
        (s.Seen, s.Flagged, s.Answered).Should().Be((seen, flagged, answered));
    }

    [Fact]
    public async Task Mbox_separator_dates_and_too_large_entries()
    {
        Mbox.Separator("a@b.test", new DateTimeOffset(2026, 2, 7, 9, 5, 3, TimeSpan.FromHours(7))).Should().Be("From a@b.test Sat Feb 7 02:05:03 2026");
        Mbox.Separator("has space@x", DateTimeOffset.UnixEpoch).Should().StartWith("From MAILER-DAEMON ");
        var text = "preamble ignored\n\nFrom a@b Sat Feb  7 02:05:03 2026\nSubject: one\n\nbody\n\nFrom b@c Sun Feb  8 10:00:00 2026\nSubject: two\n\n" + new string('x', 5000) + "\n";
        var list = new List<MboxEntry>();
        await foreach (var e in Mbox.ReadAsync(new MemoryStream(Encoding.ASCII.GetBytes(text)), 1000))
        {
            list.Add(e);
        }

        list.Should().HaveCount(2);
        Encoding.ASCII.GetString(list[0].Content).Should().Be("Subject: one\r\n\r\nbody\r\n");
        list[0].SeparatorDate.Should().Be(new DateTimeOffset(2026, 2, 7, 2, 5, 3, TimeSpan.Zero));
        list[1].TooLarge.Should().BeTrue();
    }
}
