using BlueMail.Domain.Common;
using BlueMail.Infrastructure.Audit;
using BlueMail.Infrastructure.Persistence;

namespace BlueMail.UnitTests.Domain;

public class EmailAddressTests
{
    [Theory]
    [InlineData("Lan@BlueMail.Test", "lan@bluemail.test")]
    [InlineData("  an@acme.test ", "an@acme.test")]
    [InlineData("hoa@đốitác.vn", "hoa@xn--itc-fla1v254n.vn")]
    [InlineData("用户@例子.广告", "用户@xn--fsqu00a.xn--4rr70v")]
    [InlineData("o'brien+news@example.ie", "o'brien+news@example.ie")]
    public void Normalises_to_lower_case_and_a_label(string input, string expected)
    {
        EmailAddress.TryParse(input, out var a).Should().BeTrue();
        a!.Value.Value.Should().Be(expected);
    }

    [Fact]
    public void Local_part_is_nfc_normalised()
    {
        // "é" as e + combining acute (NFD) must equal precomposed é (NFC)
        EmailAddress.TryParse("rémi@example.fr", out var a).Should().BeTrue();
        a!.Value.LocalPart.Should().Be("rémi");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@localhost")]
    [InlineData(".user@example.com")]
    [InlineData("us..er@example.com")]
    [InlineData("us er@example.com")]
    [InlineData("\"quoted\"@example.com")]
    [InlineData("user@exa mple.com")]
    public void Rejects_invalid(string? input)
    {
        EmailAddress.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Display_domain_is_unicode()
    {
        EmailAddress.Parse("hoa@đốitác.vn").DisplayDomain.Should().Be("đốitác.vn");
    }
}

public class EnumNamingTests
{
    [Theory]
    [InlineData("Active", "ACTIVE")]
    [InlineData("IfUnread", "IF_UNREAD")]
    [InlineData("Rsa2048", "RSA2048")]
    [InlineData("HardBounce", "HARD_BOUNCE")]
    public void Upper_snake(string name, string expected)
    {
        EnumNames.ToUpperSnake(name).Should().Be(expected);
        EnumNames.FromUpperSnake(expected).Should().BeEquivalentTo(name.ToUpperInvariant().Replace("_", "", StringComparison.Ordinal));
    }
}

public class AuditHashTests
{
    [Fact]
    public void Canonical_json_ignores_key_order_and_whitespace()
    {
        AuditWriter.CanonicalJson("{ \"b\": 1, \"a\": [ {\"y\":2,\"x\":\"é\"} ] }")
            .Should().Be(AuditWriter.CanonicalJson("{\"a\":[{\"x\":\"é\",\"y\":2}],\"b\":1}"));
    }

    [Fact]
    public void Hash_changes_when_any_field_or_previous_hash_changes()
    {
        var row = new BlueMail.Domain.Audit.AuditLog { Action = "user.create", OccurredAt = DateTimeOffset.UnixEpoch, PrevHash = [1, 2, 3] };
        var h1 = AuditWriter.ComputeHash(row);
        row.Action = "user.delete";
        AuditWriter.ComputeHash(row).Should().NotEqual(h1);
        row.Action = "user.create";
        row.PrevHash = [1, 2, 4];
        AuditWriter.ComputeHash(row).Should().NotEqual(h1);
    }

    [Fact]
    public void Field_separator_inside_values_cannot_forge_another_split()
    {
        var a = new BlueMail.Domain.Audit.AuditLog { Action = "a\u001fb", Entity = "c" };
        var b = new BlueMail.Domain.Audit.AuditLog { Action = "a", Entity = "b\u001fc" };
        AuditWriter.ComputeHash(a).Should().NotEqual(AuditWriter.ComputeHash(b));
    }
}
