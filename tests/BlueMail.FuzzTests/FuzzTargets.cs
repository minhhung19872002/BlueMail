using System.Text;
using BlueMail.Dkim;
using BlueMail.Dns;
using BlueMail.Mime;
using BlueMail.Scanner;

namespace BlueMail.FuzzTests;

/// <summary>
/// Fuzz targets for every parser that reads data from the Internet (PROMPT 8.1). A target may throw only the
/// exception types listed in <see cref="Expected"/>; anything else, a hang or runaway memory is a bug.
/// </summary>
public static class FuzzTargets
{
    public sealed record Target(string Name, Action<byte[]> Run, Type[] Expected);

    private static readonly BlueMailScanner Scanner = new(ScannerPolicy.Default with { MaxUncompressedBytes = 64 * 1024 * 1024, MaxInspectBytes = 4 * 1024 * 1024 });

    public static readonly IReadOnlyList<Target> All =
    [
        new("mime", Mime, []),
        new("mime-headers", MimeHeaders, []),
        new("dns", d => DnsWire.Decode(d), [typeof(DnsFormatException)]),
        new("dkim", Dkim, [typeof(InvalidDataException)]),
        new("scanner", d => Scanner.ScanAsync(new MemoryStream(d), "fuzz.bin").GetAwaiter().GetResult(), []),
    ];

    private static void Mime(byte[] data)
    {
        using var s = new MemoryStream(data);
        var m = MimeMessage.Parse(s, MimeLimits.Default with { MaxDepth = 20, MaxParts = 500 });
        _ = m.Subject;
        _ = m.From;
        _ = m.To;
        _ = m.Date;
        _ = m.References;
        foreach (var e in m.Root.Walk())
        {
            _ = e.FileName;
            _ = e.ContentId;
            if (e.IsLeaf && e.BodyLength < 1_000_000)
            {
                var text = e.ReadText(s);
                if (e.MediaType == "text/html")
                {
                    _ = HtmlToText.Convert(text);
                }
                else if (e.MediaType == "text/calendar")
                {
                    _ = CalendarInvite.Parse(text);
                }
                else if (e.MediaType == "message/delivery-status")
                {
                    _ = DeliveryStatus.Parse(text);
                }
            }
        }
    }

    private static void MimeHeaders(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        _ = AddressParser.Parse(text);
        _ = DateParser.Parse(text);
        _ = EncodedWords.Decode(text);
        _ = HeaderValueWithParameters.Parse(text);
        _ = MessageIds.ParseList(text);
        _ = FormatFlowed.Decode(text, true);
    }

    private sealed class NoKeys : IDkimKeyResolver
    {
        public Task<(DnsStatus Status, IReadOnlyList<string> Records)> LookupAsync(string selector, string domain, CancellationToken ct) =>
            Task.FromResult((DnsStatus.Ok, (IReadOnlyList<string>)["v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDBX5"]));
    }

    private static void Dkim(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        _ = TagList.Parse(text);
        _ = DkimKey.Parse(text);
        _ = new DkimVerifier(new NoKeys()).VerifyAsync(new MemoryStream(data)).GetAwaiter().GetResult();
        _ = new ArcVerifier(new NoKeys()).VerifyAsync(new MemoryStream(data)).GetAwaiter().GetResult();
    }
}
