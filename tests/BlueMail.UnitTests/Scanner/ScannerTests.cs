using BlueMail.Scanner;
using BlueMail.TestSamples;

namespace BlueMail.UnitTests.Scanner;

public class ScannerSampleTests
{
    private static readonly BlueMailScanner Scanner = new();

    public static TheoryData<string> DangerousNames => new(ScannerSamples.Dangerous().Select(s => s.Name));

    public static TheoryData<string> CleanNames => new(ScannerSamples.Clean().Select(s => s.Name));

    [Theory]
    [MemberData(nameof(DangerousNames))]
    public async Task Dangerous_samples_are_stopped_with_the_right_reason(string name)
    {
        var sample = ScannerSamples.Dangerous().Single(s => s.Name == name);
        var r = await Scanner.ScanAsync(new MemoryStream(sample.Data), sample.Name);
        r.Status.Should().Be(sample.Expected, string.Join("; ", r.Findings));
        r.Findings.Should().Contain(f => f.Code == sample.ExpectedCode, string.Join("; ", r.Findings));
    }

    [Theory]
    [MemberData(nameof(CleanNames))]
    public async Task Clean_samples_are_not_flagged(string name)
    {
        var sample = ScannerSamples.Clean().Single(s => s.Name == name);
        var r = await Scanner.ScanAsync(new MemoryStream(sample.Data), sample.Name);
        r.Status.Should().Be(ScanStatus.Clean, string.Join("; ", r.Findings));
    }

    [Fact]
    public async Task Non_seekable_input_is_spooled_and_scanned()
    {
        var sample = ScannerSamples.Dangerous().Single(s => s.Name == "payroll.zip");
        var r = await Scanner.ScanAsync(new NonSeekable(sample.Data), sample.Name);
        r.Status.Should().Be(ScanStatus.Malicious);
    }

    [Fact]
    public async Task Policy_can_block_macro_documents_outright()
    {
        var strict = new BlueMailScanner(ScannerPolicy.Default with { MacroDocuments = ScanStatus.Malicious });
        var sample = ScannerSamples.Dangerous().Single(s => s.Name == "bao-gia.docx");
        (await strict.ScanAsync(new MemoryStream(sample.Data), sample.Name)).Status.Should().Be(ScanStatus.Malicious);
    }

    [Fact]
    public async Task Composite_takes_the_most_severe_verdict()
    {
        var composite = new CompositeScanner([new BlueMailScanner(), new BlueMailScanner(ScannerPolicy.Default with { MacroDocuments = ScanStatus.Malicious })]);
        var sample = ScannerSamples.Dangerous().Single(s => s.Name == "bao-gia.docx");
        (await composite.ScanAsync(new MemoryStream(sample.Data), sample.Name)).Status.Should().Be(ScanStatus.Malicious);
    }

    [Fact]
    public void Pdf_name_escapes_are_decoded() => PdfAnalyzer.NormalizeNames("/J#61vaScript /#4As").Should().Be("/JavaScript /Js");

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

public class ArchiveListingRegressionTests
{
    // docs/08 BUG-014: found by fuzzing (scanner-76605). A huge RAR5 name length overflowed to a negative int.
    [Theory]
    // type 2 (file), flags 0, file flags 0, unpacked 0, attributes 0x20, compression 0, host 1, name length 0xFFFFFFFF, name
    [InlineData(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x20, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, (byte)'a' })]
    // file flags 0x06 (mtime + CRC present) on a header too short for them: the read position passes the end
    [InlineData(new byte[] { 0x02, 0x00, 0x06, 0x00 })]
    public async Task Rar5_name_length_overflow_is_rejected(byte[] header)
    {
        byte[] rar = [.. "Rar!\x1A\x07\x01\x00"u8, 0, 0, 0, 0, (byte)header.Length, .. header];
        var r = await new BlueMailScanner().ScanAsync(new MemoryStream(rar), "x.rar");
        r.Status.Should().Be(ScanStatus.Clean, "an unreadable header lists nothing; it must not throw");
    }
}
