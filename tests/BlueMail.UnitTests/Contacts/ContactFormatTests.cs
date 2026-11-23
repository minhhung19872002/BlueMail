using System.Text;
using BlueMail.Application.Features.Contacts;
using BlueMail.Application.Features.Mail;
using BlueMail.Application.Features.Me;

namespace BlueMail.UnitTests.Contacts;

public class VCardTests
{
    [Fact]
    public void Reads_vcard_4_3_and_2_1_with_folding_escapes_types_and_quoted_printable()
    {
        var text = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Nguyễn Văn An\r\nEMAIL;TYPE=work:an@acme.vn\r\nEMAIL;TYPE=home,pref:an.nguyen@\r\n gmail.com\r\n" +
                   "TEL;VALUE=uri;TYPE=cell:tel:+84-912-345-678\r\nORG:Công ty ACME\\, Hà Nội;Kinh doanh\r\nTITLE:Trưởng phòng\r\nNOTE:Dòng 1\\nDòng 2\r\nEND:VCARD\r\n" +
                   "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Kowalska;Anna;;;\r\nitem1.EMAIL;type=INTERNET:anna@firma.pl\r\nEND:VCARD\r\n" +
                   "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=C4=90=E1=BB=97;H=C3=B9ng;;;\r\nEMAIL;INTERNET:hung@x.vn\r\nEND:VCARD\r\n";
        var cards = VCard.Read(text);
        cards.Should().HaveCount(3);
        cards[0].DisplayName.Should().Be("Nguyễn Văn An");
        cards[0].Emails.Should().Equal(new LabeledValue("an@acme.vn", "work"), new LabeledValue("an.nguyen@gmail.com", "home"));
        cards[0].Phones.Should().Equal(new LabeledValue("+84-912-345-678", "cell"));
        cards[0].Org.Should().Be("Công ty ACME, Hà Nội");
        cards[0].Notes.Should().Be("Dòng 1\nDòng 2");
        cards[1].DisplayName.Should().Be("Anna Kowalska");
        cards[1].Emails.Single().Value.Should().Be("anna@firma.pl");
        cards[2].DisplayName.Should().Be("Hùng Đỗ");
    }

    [Fact]
    public void Write_then_read_round_trips_and_folds_at_75_octets_without_splitting_characters()
    {
        var card = new ContactCard
        {
            DisplayName = "田中 健二, Kenji; \\ " + new string('ễ', 60),
            Emails = [new LabeledValue("kenji@example.jp", "work")],
            Phones = [new LabeledValue("+81 90 1234 5678", "cell")],
            Org = "株式会社",
            Notes = "a\nb",
            Photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3],
            PhotoType = "image/jpeg",
        };
        var text = VCard.Write([card]);
        foreach (var line in text.Split("\r\n"))
        {
            Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(75);
        }

        var back = VCard.Read(text).Single();
        back.DisplayName.Should().Be(card.DisplayName);
        back.Emails.Should().Equal(card.Emails);
        back.Phones.Single().Value.Should().Be("+81901234 5678".Replace(" ", "", StringComparison.Ordinal));
        back.Org.Should().Be("株式会社");
        back.Notes.Should().Be("a\nb");
        back.Photo.Should().Equal(card.Photo);
    }

    [Fact]
    public void Photos_that_are_not_jpeg_or_png_or_too_big_are_dropped()
    {
        var big = Convert.ToBase64String(new byte[VCard.MaxPhotoBytes + 1]);
        VCard.Read($"BEGIN:VCARD\r\nFN:x\r\nPHOTO:data:image/jpeg;base64,{big}\r\nEND:VCARD\r\n").Single().Photo.Should().BeNull();
        VCard.Read("BEGIN:VCARD\r\nFN:x\r\nPHOTO:data:image/svg+xml;base64,PHN2Zy8+\r\nEND:VCARD\r\n").Single().Photo.Should().BeNull();
    }
}

public class ContactCsvTests
{
    [Fact]
    public void Google_and_outlook_exports_are_read()
    {
        var google = "﻿Name,Given Name,Family Name,E-mail 1 - Type,E-mail 1 - Value,Phone 1 - Type,Phone 1 - Value,Organization 1 - Name,Notes\r\n" +
                     "Trần Thị Lan,Lan,Trần,* Work,lan@acme.vn ::: lan.tran@gmail.com,Mobile,0912 345 678,ACME,\"Ghi chú, có dấu phẩy\r\nvà xuống dòng\"\r\n";
        var g = ContactCsv.Read(google).Single();
        g.DisplayName.Should().Be("Trần Thị Lan");
        g.Emails.Select(e => e.Value).Should().Equal("lan@acme.vn", "lan.tran@gmail.com");
        g.Emails[0].Label.Should().Be("work");
        g.Phones.Single().Value.Should().Be("0912 345 678");
        g.Org.Should().Be("ACME");
        g.Notes.Should().Be("Ghi chú, có dấu phẩy\r\nvà xuống dòng");

        var outlook = "First Name,Middle Name,Last Name,Company,Job Title,E-mail Address,Mobile Phone,Business Phone\r\nJohn,,Smith,Contoso,CEO,john@contoso.com,+1 555 0100,\r\n";
        var o = ContactCsv.Read(outlook).Single();
        o.DisplayName.Should().Be("John Smith");
        o.Title.Should().Be("CEO");
        o.Phones.Single().Label.Should().Be("mobile");
    }

    [Fact]
    public void Export_round_trips_and_neutralises_spreadsheet_formulas_but_not_phone_numbers()
    {
        var csv = ContactCsv.Write([new ContactCard
        {
            DisplayName = "=HYPERLINK(\"http://evil\")", Emails = [new LabeledValue("a@b.vn", "work")], Phones = [new LabeledValue("+84 912", "mobile")], Notes = "x, \"y\"",
        }]);
        csv.Should().Contain("'=HYPERLINK").And.Contain(",+84 912,");
        var back = ContactCsv.Read(csv).Single();
        back.Emails.Single().Value.Should().Be("a@b.vn");
        back.Notes.Should().Be("x, \"y\"");
    }

    [Fact]
    public void Merge_unions_addresses_and_keeps_existing_fields()
    {
        var a = new ContactCard { DisplayName = "an@acme.vn", Emails = [new LabeledValue("an@acme.vn", null)], Phones = [new LabeledValue("0912 345 678", null)] };
        var b = new ContactCard { DisplayName = "Nguyễn An", Emails = [new LabeledValue("AN@acme.vn", "work"), new LabeledValue("an@gmail.com", null)],
            Phones = [new LabeledValue("0912-345-678", "mobile")], Org = "ACME" };
        var m = ContactMerge.Combine(a, b);
        m.DisplayName.Should().Be("Nguyễn An", "an address used as name is replaced by a real name");
        m.Emails.Should().HaveCount(2);
        m.Phones.Should().ContainSingle("same digits");
        m.Org.Should().Be("ACME");
    }
}

public class UnsubscribeAndSettingsTests
{
    [Fact]
    public void List_unsubscribe_header_is_understood()
    {
        var o = UnsubscribeOptions.Parse("<mailto:leave@news.vn?subject=unsubscribe>, <https://news.vn/u/123>", "List-Unsubscribe=One-Click");
        o.Https.Should().Be("https://news.vn/u/123");
        o.Mailto.Should().StartWith("mailto:leave@news.vn");
        o.OneClick.Should().BeTrue();
        UnsubscribeOptions.Parse("<http://news.vn/u>", null).Should().Match<UnsubscribeOptions>(x => !x.Any, "plain http is not offered");
        UnsubscribeOptions.Parse("<https://news.vn/u>", null).OneClick.Should().BeFalse("no List-Unsubscribe-Post");
    }

    [Fact]
    public void Settings_are_normalised_never_stored_invalid()
    {
        var s = MailSettings.Normalize(new MailSettings { PageSize = 7, UndoSendSeconds = 99, InboxCategories = ["SOCIAL", "NOPE"], Theme = "PINK" });
        s.PageSize.Should().Be(50);
        s.UndoSendSeconds.Should().Be(10);
        s.InboxCategories.Should().Equal("PRIMARY", "SOCIAL");
        s.Theme.Should().Be("SYSTEM");
        MailSettings.Parse("{not json").Should().BeEquivalentTo(new MailSettings());
    }
}
