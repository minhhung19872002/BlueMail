using System.Text.RegularExpressions;
using BlueMail.Auth;

namespace BlueMail.UnitTests.Auth;

public partial class PublicSuffixListTests
{
    /// <summary>The official PSL test vectors (publicsuffix/list tests/test_psl.txt), loaded verbatim.</summary>
    public static TheoryData<string?, string?> Vectors()
    {
        var data = new TheoryData<string?, string?>();
        // Line by line: commented-out vectors ("//checkPublicSuffix(...)") are not part of the suite.
        var active = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Auth", "Data", "psl-tests.txt"))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        foreach (Match m in active.Select(l => Check().Match(l)).Where(m => m.Success))
        {
            static string? Arg(string s) => s == "null" ? null : s.Trim('\'');
            data.Add(Arg(m.Groups[1].Value), Arg(m.Groups[2].Value));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Official_vectors(string? domain, string? registrable) =>
        PublicSuffixList.Default.RegistrableDomain(domain).Should().Be(registrable is null ? null : new System.Globalization.IdnMapping().GetAscii(registrable));

    [GeneratedRegex(@"checkPublicSuffix\((null|'[^']*'),\s*(null|'[^']*')\);")]
    private static partial Regex Check();
}

public class DmarcRecordTests
{
    [Fact]
    public void Parses_policy_tags()
    {
        var r = DmarcRecord.Parse("v=DMARC1; p=reject; sp=quarantine; pct=50; adkim=s; aspf=r; rua=mailto:a@x.test,mailto:b@x.test")!;
        r.Policy.Should().Be(DmarcPolicy.Reject);
        r.SubdomainPolicy.Should().Be(DmarcPolicy.Quarantine);
        r.Percent.Should().Be(50);
        r.StrictDkim.Should().BeTrue();
        r.StrictSpf.Should().BeFalse();
        r.Rua.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("v=spf1 -all")]
    [InlineData("p=reject; v=DMARC1")]         // v= must come first
    [InlineData("v=DMARC1; p=bogus")]           // invalid p without rua
    public void Rejects_invalid(string txt) => DmarcRecord.Parse(txt).Should().BeNull();

    [Fact]
    public void Invalid_p_with_rua_is_none() => DmarcRecord.Parse("v=DMARC1; p=bogus; rua=mailto:r@x.test")!.Policy.Should().Be(DmarcPolicy.None);
}

public class OrganizationalDomainTests
{
    [Theory]
    [InlineData("mail.doitac.test", "doitac.test")]       // unlisted TLD: default rule "*" (RFC 7489 3.2)
    [InlineData("news.bbc.co.uk", "bbc.co.uk")]
    [InlineData("a.b.c.blogspot.com", "c.blogspot.com")]  // private-section rule
    [InlineData("vnexpress.net", "vnexpress.net")]
    public void Dmarc_organizational_domain(string domain, string org) => PublicSuffixList.Default.OrganizationalDomain(domain).Should().Be(org);
}
