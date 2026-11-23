using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BlueMail.Scanner;

namespace BlueMail.UnitTests.Scanner;

public class PatternRuleTests
{
    // Built at run time from hex: the test source and binary never contain the EICAR string itself (BUG-009).
    private static byte[] Eicar() => Convert.FromHexString(
        "58354F2150254041505B345C505A58353428505E2937434329377D2445494341522D5354414E444152442D414E544956495255532D544553542D46494C452124482B482A");

    [Fact]
    public void Builtin_rules_parse_and_eicar_matches_only_at_the_start_of_a_small_file()
    {
        var rules = PatternRuleScanner.BuiltIn;
        rules.Rules.Select(r => r.Name).Should().Contain("eicar_test_file");
        var eicar = Eicar();
        rules.Match(eicar, eicar.Length).Select(r => r.Name).Should().Contain("eicar_test_file");
        var padded = new byte[] { 0x20 }.Concat(eicar).ToArray();
        rules.Match(padded, padded.Length).Should().NotContain(r => r.Name == "eicar_test_file", "the test string must be at offset 0");
        var big = eicar.Concat(new byte[200]).ToArray();
        rules.Match(big, big.Length).Should().NotContain(r => r.Name == "eicar_test_file", "an EICAR file is at most 128 bytes");
    }

    [Fact]
    public void Hex_wildcards_jumps_nocase_wide_and_conditions()
    {
        var set = PatternRuleSet.Parse("""
            rule r1 : suspicious
              hex $h = 41 ?? 43 [1-3] 46
              text $t = "hello" nocase
              text $w = "Xin chào" wide
              when $h and ($t or $w) and filesize < 1KB
            end
            rule r2 : malicious
              text $a = "aa"
              text $b = "bb"
              text $c = "cc"
              when 2 of them and not $c at 0
            end
            """);
        var d1 = Encoding.ASCII.GetBytes("xxA?CdeFyy HeLLo");
        set.Match(d1, d1.Length).Select(r => r.Name).Should().Equal("r1");
        var d2 = Encoding.ASCII.GetBytes("A?CdeeeeF hello"); // jump of 4 > 3
        set.Match(d2, d2.Length).Should().BeEmpty();
        var d3 = Encoding.ASCII.GetBytes("A0CxF ").Concat(Encoding.Unicode.GetBytes("Xin chào")).ToArray();
        set.Match(d3, d3.Length).Select(r => r.Name).Should().Equal("r1");
        set.Match(d1, 5000).Should().BeEmpty("filesize condition");
        var d4 = Encoding.ASCII.GetBytes("cc aa bb");
        set.Match(d4, d4.Length).Should().BeEmpty("$c at 0 excludes it");
        var d5 = Encoding.ASCII.GetBytes("xcc aa bb");
        set.Match(d5, d5.Length).Select(r => r.Name).Should().Equal("r2");
    }

    [Theory]
    [InlineData("rule x : bad\n text $a = \"a\"\n when $a\nend", "severity")]
    [InlineData("rule x : malicious\n text $a = \"a\"\n when $b\nend", "unknown string $b")]
    [InlineData("rule x : malicious\n hex $a = ?? 41\n when $a\nend", "start and end with a byte")]
    [InlineData("rule x : malicious\n text $a = \"a\"\n when $a and\nend", "ends too early")]
    [InlineData("rule x : malicious\n text $a = \"a\"\n when $a", "no 'end'")]
    [InlineData("text $a = \"a\"", "outside a rule")]
    [InlineData("rule x : malicious\n hex $a = 41 [0-99999] 42\n when $a\nend", "jump")]
    public void Syntax_errors_name_the_line(string text, string message)
    {
        var act = () => PatternRuleSet.Parse(text, "ops.rules");
        act.Should().Throw<FormatException>().Which.Message.Should().Contain("ops.rules").And.Contain(message);
    }

    [Fact]
    public async Task Operator_rules_override_builtin_and_scan_inside_zip()
    {
        var scanner = new PatternRuleScanner(PatternRuleSet.Parse("rule invoice_lure : malicious\n text $a = \"enable content to view the invoice\" nocase\n when $a\nend"));
        using var zip = new MemoryStream();
        using (var a = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var e = await a.CreateEntry("readme.txt").OpenAsync();
            await e.WriteAsync(Encoding.UTF8.GetBytes("Please ENABLE CONTENT to view the invoice."));
        }

        zip.Position = 0;
        var r = await scanner.ScanAsync(zip, "docs.zip");
        r.Status.Should().Be(ScanStatus.Malicious);
        r.Findings.Single().Path.Should().Be("docs.zip/readme.txt");
        r.Findings.Single().Detail.Should().Be("invoice_lure");
    }
}

public class KnownHashTests
{
    [Fact]
    public async Task Malwarebazaar_exports_text_and_zip_load_and_match_top_level_and_zip_entries()
    {
        var bad = Encoding.ASCII.GetBytes("pretend-malware-sample");
        var hex = Convert.ToHexStringLower(SHA256.HashData(bad));
        var text = $"################################\n# MalwareBazaar full data dump (SHA256)\n################################\n{hex}\nnot-a-hash\n{new string('a', 64)}\n";

        var (fromText, rejected) = MalwareHashList.Parse(new MemoryStream(Encoding.ASCII.GetBytes(text)));
        fromText.Should().HaveCount(2);
        rejected.Should().Be(1);

        using var zipped = new MemoryStream();
        using (var a = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var e = await a.CreateEntry("full_sha256.txt").OpenAsync();
            await e.WriteAsync(Encoding.ASCII.GetBytes(text));
        }

        zipped.Position = 0;
        var (fromZip, _) = MalwareHashList.Parse(zipped);
        fromZip.Should().BeEquivalentTo(fromText);

        var list = new MalwareHashList();
        list.Replace(fromZip);
        var scanner = new KnownHashScanner(list);
        (await scanner.ScanAsync(new MemoryStream(bad), "x.bin")).Status.Should().Be(ScanStatus.Malicious);
        (await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes("harmless")), "y.bin")).Status.Should().Be(ScanStatus.Clean);

        using var outer = new MemoryStream();
        using (var a = new ZipArchive(outer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var e = await a.CreateEntry("inner/tool.exe").OpenAsync();
            await e.WriteAsync(bad);
        }

        outer.Position = 0;
        var r = await scanner.ScanAsync(outer, "pack.zip");
        r.Findings.Should().ContainSingle(f => f.Code == ScanCodes.KnownMalwareHash && f.Path == "pack.zip/inner/tool.exe");
    }

    [Fact]
    public async Task Empty_list_finds_nothing_and_the_default_composite_includes_layers_7_and_8()
    {
        (await new KnownHashScanner(new MalwareHashList()).ScanAsync(new MemoryStream([1, 2, 3]), "a")).Status.Should().Be(ScanStatus.Clean);
        var eicar = Convert.FromHexString("58354F2150254041505B345C505A58353428505E2937434329377D2445494341522D5354414E444152442D414E544956495255532D544553542D46494C452124482B482A");
        var r = await BlueMailScanners.Create().ScanAsync(new MemoryStream(eicar), "note.txt");
        r.Status.Should().Be(ScanStatus.Malicious);
        r.Findings.Should().Contain(f => f.Code == ScanCodes.PatternRule && f.Detail == "eicar_test_file");
    }
}
