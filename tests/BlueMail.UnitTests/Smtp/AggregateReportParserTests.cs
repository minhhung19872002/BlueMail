using System.IO.Compression;
using System.Text;
using BlueMail.Application.Features.Sending;
using BlueMail.Domain.Mail;

namespace BlueMail.UnitTests.Smtp;

/// <summary>DMARC (RFC 7489 appendix C) and SMTP TLS (RFC 8460) aggregate reports as mailbox providers send them.</summary>
public sealed class AggregateReportParserTests
{
    internal const string DmarcXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>9391651994964116463</report_id>
            <date_range><begin>1795392000</begin><end>1795478399</end></date_range>
          </report_metadata>
          <policy_published><domain>bluemail.test</domain><adkim>r</adkim><aspf>r</aspf><p>none</p><sp>none</sp><pct>100</pct></policy_published>
          <record>
            <row><source_ip>14.225.83.93</source_ip><count>12</count>
              <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated></row>
            <identifiers><header_from>BlueMail.test</header_from></identifiers>
            <auth_results><dkim><domain>bluemail.test</domain><result>pass</result></dkim><spf><domain>bluemail.test</domain><result>pass</result></spf></auth_results>
          </record>
          <record>
            <row><source_ip>203.0.113.9</source_ip><count>3</count>
              <policy_evaluated><disposition>none</disposition><dkim>fail</dkim><spf>fail</spf></policy_evaluated></row>
            <identifiers><header_from>bluemail.test</header_from></identifiers>
            <auth_results><spf><domain>spoof.example</domain><result>fail</result></spf></auth_results>
          </record>
        </feedback>
        """;

    // RFC 8460 4.4 (shortened to the fields we read, values as in its example).
    internal const string TlsJson = """
        {
          "organization-name": "Company-X",
          "date-range": { "start-datetime": "2026-11-22T00:00:00Z", "end-datetime": "2026-11-22T23:59:59Z" },
          "contact-info": "sts-reporting@company-x.example",
          "report-id": "5065427c-23d3-47ca-b6e0-946ea0e8c4be",
          "policies": [{
            "policy": { "policy-type": "sts", "policy-domain": "bluemail.test", "mx-host": ["*.mail.bluemail.test"] },
            "summary": { "total-successful-session-count": 5326, "total-failure-session-count": 303 },
            "failure-details": [
              { "result-type": "certificate-expired", "sending-mta-ip": "2001:db8:abcd:0012::1", "receiving-mx-hostname": "mx1.mail.bluemail.test", "failed-session-count": 100 },
              { "result-type": "starttls-not-supported", "sending-mta-ip": "2001:db8:abcd:0013::1", "receiving-mx-hostname": "mx2.mail.bluemail.test", "failed-session-count": 200 }
            ]
          }]
        }
        """;

    internal static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(Encoding.UTF8.GetBytes(text));
        }

        return ms.ToArray();
    }

    internal static byte[] Zip(string name, string text)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }

        return ms.ToArray();
    }

    /// <summary>A report mail: a cover note and the report as a base64 attachment, like Google, Microsoft and Yahoo send.</summary>
    internal static byte[] Mail(string contentType, string fileName, byte[] payload) => Encoding.ASCII.GetBytes(
        "From: noreply-dmarc-support@google.com\r\nTo: dmarc-reports@bluemail.test\r\nSubject: Report domain: bluemail.test\r\n" +
        "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n" +
        "--b1\r\nContent-Type: text/plain; charset=us-ascii\r\n\r\nThis is an aggregate report.\r\n" +
        $"--b1\r\nContent-Type: {contentType}; name=\"{fileName}\"\r\nContent-Disposition: attachment; filename=\"{fileName}\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(payload, Base64FormattingOptions.InsertLineBreaks) + "\r\n--b1--\r\n");

    [Fact]
    public void Dmarc_report_in_gzip_reads_every_record()
    {
        var reports = AggregateReportParser.Parse(Mail("application/gzip", "google.com!bluemail.test!1795392000!1795478399.xml.gz", Gzip(DmarcXml)));
        var r = reports.Should().ContainSingle().Subject;
        r.Kind.Should().Be(AggregateReportKind.Dmarc);
        r.Domain.Should().Be("bluemail.test");
        r.OrgName.Should().Be("google.com");
        r.ReportId.Should().Be("9391651994964116463");
        r.BeginAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1795392000));
        r.Rows.Should().HaveCount(2);
        r.Rows[0].Should().Be(new ParsedAggregateRow("14.225.83.93", 12, 0, "none", true, true, "bluemail.test", null));
        r.Rows[1].DkimPass.Should().BeFalse();
        r.Rows[1].SpfPass.Should().BeFalse();
    }

    [Fact]
    public void Dmarc_report_in_zip_and_with_a_namespace_reads_too()
    {
        var namespaced = DmarcXml.Replace("<feedback>", "<feedback xmlns=\"urn:ietf:params:xml:ns:dmarc-2.0\">", StringComparison.Ordinal);
        AggregateReportParser.Parse(Mail("application/zip", "report.zip", Zip("report.xml", namespaced))).Should().ContainSingle()
            .Which.Rows.Sum(x => x.Count).Should().Be(15);
    }

    [Fact]
    public void Tls_report_reads_counts_and_failure_types()
    {
        var r = AggregateReportParser.Parse(Mail("application/tlsrpt+gzip", "company-x.example!bluemail.test!1795392000!1795478399!001.json.gz", Gzip(TlsJson)))
            .Should().ContainSingle().Subject;
        r.Kind.Should().Be(AggregateReportKind.TlsRpt);
        r.Domain.Should().Be("bluemail.test");
        r.ReportId.Should().Be("5065427c-23d3-47ca-b6e0-946ea0e8c4be");
        r.Rows.Should().ContainSingle().Which.Should().Be(new ParsedAggregateRow("sts", 5326, 303, null, null, null, null,
            "certificate-expired:100, starttls-not-supported:200"));
    }

    [Fact]
    public void A_dtd_is_refused_so_no_external_entity_is_read()
    {
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE feedback [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + DmarcXml[DmarcXml.IndexOf("<feedback>", StringComparison.Ordinal)..]
            .Replace("<org_name>google.com</org_name>", "<org_name>&x;</org_name>", StringComparison.Ordinal);
        AggregateReportParser.Parse(Mail("application/xml", "r.xml", Encoding.UTF8.GetBytes(evil))).Should().BeEmpty();
    }

    [Fact]
    public void A_report_that_inflates_past_the_limit_is_dropped()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i <= AggregateReportParser.MaxUncompressed / zeros.Length; i++)
            {
                gz.Write(zeros);
            }
        }

        ms.Length.Should().BeLessThan(1024 * 1024, "a tiny attachment");
        AggregateReportParser.Parse(Mail("application/gzip", "bomb.xml.gz", ms.ToArray())).Should().BeEmpty();
    }

    [Fact]
    public void Ordinary_mail_and_broken_attachments_give_no_report()
    {
        AggregateReportParser.Parse(Encoding.ASCII.GetBytes("From: a@b.test\r\nSubject: hi\r\n\r\nhello\r\n")).Should().BeEmpty();
        AggregateReportParser.Parse(Mail("application/gzip", "r.xml.gz", [0x1f, 0x8b, 1, 2, 3])).Should().BeEmpty();
        AggregateReportParser.Parse(Mail("application/xml", "r.xml", Encoding.UTF8.GetBytes("<feedback><report_metadata/></feedback>"))).Should().BeEmpty();
    }
}
