using System.Net;
using System.Security.Cryptography;
using BlueMail.Application.Features.Mail;
using BlueMail.Infrastructure.Net;
using BlueMail.Infrastructure.Security;

namespace BlueMail.UnitTests.Mail;

public class MailHtmlTests
{
    private static SanitizedHtml Clean(string html, bool show = false) =>
        MailHtml.Sanitize(html, cid => cid == "logo@x" ? "https://mail.test/api/v1/mail/p/T" : null, url => "https://mail.test/api/v1/mail/img/" + url.Length, show);

    [Theory]
    [InlineData("<script>alert(1)</script><p>x</p>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "onerror")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>", "alert")]
    [InlineData("<form action=\"https://evil.test\"><input name=p></form>", "<form")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.test\">", "refresh")]
    [InlineData("<div style=\"width: expression(alert(1))\">x</div>", "expression")]
    [InlineData("<div style=\"background:url(https://track.test/p.gif)\">x</div>", "track.test")]
    [InlineData("<table background=\"https://track.test/bg.gif\"><tr><td>x</td></tr></table>", "track.test")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe>", "<iframe")]
    [InlineData("<svg><script>alert(1)</script></svg>", "<svg")]
    [InlineData("<object data=\"x.swf\"></object>", "<object")]
    [InlineData("<base href=\"https://evil.test/\">", "<base")]
    [InlineData("<style>@import url(https://track.test/a.css)</style>", "track.test")]
    [InlineData("<img src=\"data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=\">", "svg+xml")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "data:")]
    public void Dangerous_content_is_removed(string html, string mustNotContain) =>
        Clean(html).Html.Should().NotContainEquivalentOf(mustNotContain);

    [Fact]
    public void Remote_images_are_blocked_by_default_and_proxied_when_shown()
    {
        var blocked = Clean("<p>Hi</p><img src=\"https://track.test/open.gif?id=42\">");
        blocked.HasRemoteImages.Should().BeTrue();
        blocked.RemoteImagesShown.Should().BeFalse();
        blocked.Html.Should().NotContain("track.test").And.Contain("bm-blocked-image");

        var shown = Clean("<img src=\"https://track.test/open.gif?id=42\">", show: true);
        shown.RemoteImagesShown.Should().BeTrue();
        shown.Html.Should().Contain("https://mail.test/api/v1/mail/img/").And.NotContain("track.test");
    }

    [Fact]
    public void Cid_images_use_signed_internal_urls_and_unknown_cids_are_dropped()
    {
        var r = Clean("<img src=\"cid:logo@x\"><img src=\"cid:missing@x\">");
        r.Html.Should().Contain("https://mail.test/api/v1/mail/p/T");
        r.Html.Should().NotContain("cid:");
        r.HasRemoteImages.Should().BeFalse();
    }

    [Fact]
    public void Links_open_in_new_tab_without_referrer_and_deceptive_links_are_marked()
    {
        var r = Clean("<a href=\"https://paypal.com.evil.test/login\">https://www.paypal.com</a> <a href=\"https://www.bluemail.test/x\">bluemail.test</a>");
        r.Html.Should().Contain("target=\"_blank\"").And.Contain("rel=\"noopener noreferrer\"");
        r.Html.Should().Contain("bm-link-mismatch");
        MailHtml.LooksDeceptive("bluemail.test", "https://www.bluemail.test/x").Should().BeFalse();
        MailHtml.LooksDeceptive("Click here", "https://evil.test").Should().BeFalse("ordinary link text names no host");
        MailHtml.LooksDeceptive("ngân-hàng.vn", "https://xn--ngn-hng-dwao.vn/").Should().BeFalse("IDN shown as U-label");
    }

    [Fact]
    public void Document_has_strict_csp()
    {
        var doc = MailHtml.Document("<p>x</p>", "https://mail.test", rtl: false);
        doc.Should().Contain("default-src 'none'").And.Contain("img-src https://mail.test data:").And.NotContain("script-src");
    }
}

public class SafeHttpFetcherTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.77.0.10")]
    [InlineData("172.16.5.4")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of 169.254.169.254
    [InlineData("2002:0a00:0001::1")]   // 6to4 of 10.0.0.1
    public void Internal_addresses_are_not_public(string ip) => SafeHttpFetcher.IsPublic(IPAddress.Parse(ip)).Should().BeFalse();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string ip) => SafeHttpFetcher.IsPublic(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://x.test/a.png")]
    [InlineData("http://x.test:8080/a.png")]
    [InlineData("http://user:pw@x.test/a.png")]
    [InlineData("gopher://x.test/")]
    public void Only_http_and_https_on_standard_ports(string url) => SafeHttpFetcher.IsAllowedUrl(new Uri(url)).Should().BeFalse();

    [Fact]
    public async Task Host_resolving_to_an_internal_address_is_refused_dns_rebinding()
    {
        using var f = new SafeHttpFetcher((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var act = () => f.FetchAsync("http://looks-public.test/pixel.png", default);
        (await act.Should().ThrowAsync<FetchException>()).Which.Reason.Should().Be(FetchFailure.Forbidden);
    }

    [Fact]
    public async Task Literal_metadata_address_is_refused()
    {
        using var f = new SafeHttpFetcher();
        var act = () => f.FetchAsync("http://169.254.169.254/latest/meta-data/", default);
        (await act.Should().ThrowAsync<FetchException>()).Which.Reason.Should().Be(FetchFailure.Forbidden);
    }
}

public class SignedUrlTests
{
    private static readonly SignedUrls Signer = new(new KeyService(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void Round_trip_tamper_and_expiry()
    {
        var g = SignedUrls.Grant(SignedKind.Part, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.2");
        var token = Signer.Sign(g);
        Signer.Verify(token).Should().Be(g);

        var tampered = token[..5] + (token[5] == 'A' ? 'B' : 'A') + token[6..];
        Signer.Verify(tampered).Should().BeNull();
        Signer.Verify(token, DateTimeOffset.UtcNow.Add(SignedUrls.Lifetime).AddMinutes(1)).Should().BeNull();
        Signer.Verify("garbage").Should().BeNull();
        Signer.Verify("a.b").Should().BeNull();

        var other = new SignedUrls(new KeyService(RandomNumberGenerator.GetBytes(32)));
        other.Verify(token).Should().BeNull("another deployment's key");
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\evil.exe", "evil.exe")]
    [InlineData("a<b>c:d.pdf", "a_b_c_d.pdf")]
    [InlineData("...", "attachment")]
    [InlineData("hoá đơn.pdf", "hoá đơn.pdf")]
    public void File_names_are_made_safe(string name, string expected) =>
        SignedContent.SafeFileName(name).Should().Be(expected);
}
