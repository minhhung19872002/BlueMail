using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BlueMail.Application.Features.Admin.Domains;
using BlueMail.Application.Features.Admin.Operations;
using BlueMail.Application.Features.Admin.Policies;
using BlueMail.Application.Features.Admin.Users;
using BlueMail.Dns;
using BlueMail.Infrastructure.Configuration;

namespace BlueMail.UnitTests.Admin;

public class DomainRecordTests
{
    private static readonly BmSettings Settings = new()
    {
        DbConnection = "x", MasterKeyBase64 = "x", Valkey = "x", S3Endpoint = "x", S3AccessKey = "x", S3SecretKey = "x", IdentityUrl = "x", WebUrl = "x",
        MailHostname = "mx.bluemail.test",
    };

    private sealed class FakeDns(Dictionary<(string, string), string[]> records) : IDomainDns
    {
        private Task<(DnsStatus, IReadOnlyList<string>)> Get(string type, string name) =>
            Task.FromResult(records.TryGetValue((type, name), out var v) ? (DnsStatus.Ok, (IReadOnlyList<string>)v) : (DnsStatus.NxDomain, []));

        public Task<(DnsStatus Status, IReadOnlyList<string> Values)> TxtAsync(string name, CancellationToken ct) => Get("TXT", name);
        public Task<(DnsStatus Status, IReadOnlyList<string> Values)> MxAsync(string name, CancellationToken ct) => Get("MX", name);
        public Task<(DnsStatus Status, IReadOnlyList<string> Values)> CnameAsync(string name, CancellationToken ct) => Get("CNAME", name);
        public Task<(DnsStatus Status, IReadOnlyList<string> Values)> SrvAsync(string name, CancellationToken ct) => Get("SRV", name);
    }

    [Fact]
    public async Task Plan_lists_every_record_and_check_reads_ok_missing_and_wrong()
    {
        var planner = new DomainRecordPlanner(Settings);
        planner.PlatformMailDomain.Should().Be("bluemail.test");
        var dkim = new[] { new DomainRecordPlanner.DkimPlan("bm202611", "MIIBIjAN", "ACTIVE") };
        var plan = planner.Plan("acme.vn", "bluemail-verify=abc", dkim, DateTimeOffset.UnixEpoch);
        plan.Select(p => p.Kind).Should().Contain([RecordKind.Verification, RecordKind.Mx, RecordKind.Spf, RecordKind.Dkim, RecordKind.Dmarc, RecordKind.MtaSts,
            RecordKind.MtaStsHost, RecordKind.TlsRpt, RecordKind.Autoconfig, RecordKind.Autodiscover, RecordKind.SrvSubmission, RecordKind.SrvImaps]);

        var dns = new FakeDns(new()
        {
            [("TXT", "acme.vn")] = ["bluemail-verify=abc", "v=spf1 include:_spf.bluemail.test include:other.example ~all"],
            [("MX", "acme.vn")] = ["mx.bluemail.test"],
            [("TXT", "bm202611._domainkey.acme.vn")] = ["v=DKIM1; k=rsa; p=MIIB IjAN"],
            [("TXT", "_dmarc.acme.vn")] = ["v=DMARC1; p=quarantine"],
            [("CNAME", "mta-sts.acme.vn")] = ["somewhere.else"],
            [("SRV", "_submissions._tcp.acme.vn")] = ["0 1 465 smtp.bluemail.test"],
        });
        var checks = await planner.CheckAsync(dns, "acme.vn", "bluemail-verify=abc", dkim, DateTimeOffset.UnixEpoch, default);
        RecordState S(RecordKind k) => checks.Single(c => c.Kind == k).State;
        S(RecordKind.Verification).Should().Be(RecordState.Ok);
        S(RecordKind.Mx).Should().Be(RecordState.Ok);
        S(RecordKind.Spf).Should().Be(RecordState.Ok, "other includes are the admin's business");
        S(RecordKind.Dkim).Should().Be(RecordState.Ok, "spaces inside p= are allowed (RFC 6376 3.6.1)");
        S(RecordKind.Dmarc).Should().Be(RecordState.Ok, "any single DMARC policy is accepted");
        S(RecordKind.MtaStsHost).Should().Be(RecordState.Wrong);
        S(RecordKind.TlsRpt).Should().Be(RecordState.Missing);
        S(RecordKind.SrvSubmission).Should().Be(RecordState.Ok);
        checks.Single(c => c.Kind == RecordKind.MtaStsHost).Found.Should().Equal("somewhere.else");
    }

    [Fact]
    public void Two_spf_records_or_a_foreign_mx_are_wrong()
    {
        var planner = new DomainRecordPlanner(Settings);
        planner.Satisfies(RecordKind.Spf, "", ["v=spf1 include:_spf.bluemail.test -all", "v=spf1 -all"]).Should().BeFalse("RFC 7208 4.5: one record");
        planner.Satisfies(RecordKind.Mx, "", ["mx.bluemail.test", "mail.oldhost.example"]).Should().BeFalse("mail would go elsewhere");
    }

    [Theory]
    [InlineData("acme.vn", true)]
    [InlineData("công-ty.vn", true)]
    [InlineData("localhost", false)]
    [InlineData("-bad.vn", false)]
    [InlineData("a..b", false)]
    public void Domain_names(string name, bool ok) => AddDomainHandler.IsDomain(name).Should().Be(ok);
}

public class XlsxTests
{
    [Fact]
    public void Writes_a_valid_package_with_preamble_header_and_typed_cells()
    {
        var bytes = Xlsx.Write("Mail by day", ["Thư theo ngày", "Khoảng thời gian: …"], ["Ngày", "Nhận"],
            [["2026-11-23", 12], ["<b>&\"x\"\u0001", 3.5]]);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        zip.Entries.Select(e => e.FullName).Should().Contain(["[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/styles.xml"]);
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = sheet.Descendants(ns + "c").ToList();
        cells.First().Value.Should().Be("Thư theo ngày");
        cells.Should().Contain(c => (string?)c.Attribute("r") == "B5" && c.Value == "12" && c.Attribute("t") == null, "numbers stay numbers");
        cells.Should().Contain(c => c.Value == "<b>&\"x\"", "text is escaped and control characters dropped");
        Xlsx.Column(0).Should().Be("A");
        Xlsx.Column(27).Should().Be("AB");
    }
}

public class PolicyAndRoutingTests
{
    [Fact]
    public void Address_or_domain_lists()
    {
        IReadOnlyList<string> list = ["spam@shop.test", "@bad.test", "evil.test", "*.ads.test"];
        PolicyService.ListMatches(list, "Spam@Shop.test").Should().BeTrue();
        PolicyService.ListMatches(list, "x@bad.test").Should().BeTrue();
        PolicyService.ListMatches(list, "x@evil.test").Should().BeTrue();
        PolicyService.ListMatches(list, "x@mail.ads.test").Should().BeTrue();
        PolicyService.ListMatches(list, "ok@shop.test").Should().BeFalse();
        PolicyService.ListMatches(list, null).Should().BeFalse();
        PolicyKeys.All.Select(k => k.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Routing_conditions()
    {
        var m = new RoutedMessage("ceo@acme.vn", ["finance@acme.vn", "x@partner.test"], "Báo cáo Q4", HasAttachment: true);
        RoutingEngine.Matches(new RoutingCondition(RecipientContains: "finance@"), m).Should().BeTrue();
        RoutingEngine.Matches(new RoutingCondition(SenderContains: "CEO@", SubjectContains: "báo cáo"), m).Should().BeTrue();
        RoutingEngine.Matches(new RoutingCondition(HasAttachment: false), m).Should().BeFalse();
        RoutingEngine.Matches(new RoutingCondition(SenderContains: "cfo"), m).Should().BeFalse();
    }

    [Fact]
    public async Task Users_csv_with_free_headers()
    {
        var rows = await new ParseUsersCsvHandler().Handle(new ParseUsersCsvCommand(
            "﻿Họ tên,Email,Quản trị,Dung lượng,Đơn vị\r\nNguyễn An,an@acme.vn,có,10,Kế toán\r\nBình,binh@acme.vn,,,\r\n"), default);
        rows.Should().HaveCount(2);
        rows[0].Should().Be(new ImportUserRow("an@acme.vn", "Nguyễn An", null, true, 10, "Kế toán"));
        rows[1].IsTenantAdmin.Should().BeFalse();
        ResetPasswordHandler.NewPassword().Should().HaveLength(16).And.MatchRegex("^[A-Za-z0-9]+$");
    }
}
