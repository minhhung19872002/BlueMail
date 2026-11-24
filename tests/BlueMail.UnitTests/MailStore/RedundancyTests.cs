using BlueMail.Application.Features.Mail;

namespace BlueMail.UnitTests.MailStore;

/// <summary>Clean Up Conversation: which messages a later reply quotes in full.</summary>
public sealed class RedundancyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private static CleanUpCandidate M(string id, int minutes, string text, bool files = false, bool draft = false) =>
        new(Guid.Parse(id.PadLeft(32, '0')), T0.AddMinutes(minutes), text, files, draft);

    [Fact]
    public void A_message_quoted_by_a_later_reply_is_redundant()
    {
        var first = M("1", 0, "Chào anh,\nBên em gửi báo giá thiết bị mạng quý IV.\nTrân trọng");
        var reply = M("2", 10, "Cảm ơn em, anh đồng ý.\n\nOn Mon, An wrote:\n> Chào anh,\n> Bên em gửi báo giá thiết bị mạng   quý IV.\n> Trân trọng");
        var other = M("3", 20, "Một thư khác hoàn toàn, không trích dẫn gì cả.");
        Redundancy.Find([first, reply, other]).Should().Equal(first.Id);
    }

    [Fact]
    public void Files_drafts_short_texts_and_later_messages_are_kept()
    {
        var withFile = M("1", 0, "Gửi anh hoá đơn tháng 11 kèm theo đây.", files: true);
        var shortOne = M("2", 1, "OK");
        var draft = M("3", 2, "Bản nháp chưa gửi nhưng có nội dung dài.", draft: true);
        var quotesAll = M("4", 5, "> Gửi anh hoá đơn tháng 11 kèm theo đây.\n> OK\n> Bản nháp chưa gửi nhưng có nội dung dài.\nĐã nhận.");
        Redundancy.Find([withFile, shortOne, draft, quotesAll]).Should().BeEmpty();

        // A message is only made redundant by a later one, never by an earlier one.
        var later = M("5", 30, "Nội dung lặp lại y hệt nhau ở đây.");
        var earlier = M("6", 1, "Trích: nội dung lặp lại y hệt nhau ở đây. Thêm câu.");
        Redundancy.Find([later, earlier]).Should().BeEmpty();
    }

    [Fact]
    public void A_reply_in_the_same_second_counts_as_later_by_its_place_in_the_conversation()
    {
        var first = M("1", 0, "Please review the network equipment quote before Friday.");
        var reply = M("2", 0, "Looks good.\n> Please review the network equipment quote before Friday.");
        Redundancy.Find([first, reply]).Should().Equal(first.Id);
    }

    [Fact]
    public void Quote_markers_spacing_and_case_do_not_matter() =>
        Redundancy.Normalize(">  Hello   World\n>> Second  LINE\r\nthird").Should().Be("hello world second line third");
}
