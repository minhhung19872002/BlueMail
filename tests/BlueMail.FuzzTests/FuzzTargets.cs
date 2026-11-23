using System.Text;
using System.Net;
using BlueMail.Auth;
using BlueMail.Dkim;
using BlueMail.Dns;
using BlueMail.MailStore;
using BlueMail.Mime;
using BlueMail.Protocols.Smtp;
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
        new("smtp", Smtp, []),
        new("text", Text, []),
    ];

    private sealed class AcceptAll : ISmtpHandler
    {
        public ValueTask<SmtpReply?> OnConnectAsync(SmtpEnvelope s, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);
        public ValueTask<SmtpReply?> OnMailFromAsync(SmtpEnvelope s, SmtpPath f, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);
        public ValueTask<SmtpReply?> OnRecipientAsync(SmtpEnvelope s, SmtpPath r, CancellationToken ct) => ValueTask.FromResult<SmtpReply?>(null);

        public ValueTask<SmtpReply> OnMessageAsync(SmtpEnvelope s, Stream m, CancellationToken ct)
        {
            m.CopyTo(Stream.Null);
            return ValueTask.FromResult(SmtpReply.Ok);
        }
    }

    /// <summary>Client bytes in, replies discarded: line framing, DATA dot handling, BDAT, state machine.</summary>
    private sealed class ScriptStream(byte[] input) : Stream
    {
        private readonly MemoryStream _in = new(input);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private static readonly byte[] SmtpPrefix = "EHLO fuzz.test\r\nMAIL FROM:<a@b.test>\r\nRCPT TO:<c@d.test>\r\n"u8.ToArray();

    private static void Smtp(byte[] data)
    {
        // Half the inputs start inside a transaction so DATA/BDAT paths are reached often.
        var input = data.Length > 0 && (data[0] & 1) == 1 ? [.. SmtpPrefix, .. data] : data;
        var session = new SmtpServerSession(new SmtpServerOptions { Hostname = "mx.fuzz.test", MaxMessageBytes = 1024 * 1024, MaxCommandLineBytes = 1024 },
            new AcceptAll(), new SmtpEnvelope { RemoteAddress = IPAddress.Loopback });
        new SmtpTransport(new ScriptStream(input), session, new SmtpTransportOptions { CommandTimeout = TimeSpan.FromSeconds(5), DataTimeout = TimeSpan.FromSeconds(5) },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RunAsync(default).GetAwaiter().GetResult();
    }

    private static void Text(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        _ = SmtpParser.Parse(text);
        _ = SmtpParser.ParsePath(text, "FROM", allowNull: true, allowUtf8: true);
        _ = DmarcRecord.Parse(text);
        _ = SubjectNormalizer.Normalize(text, out _);
        _ = SearchText.Prepare(text);
        _ = SearchText.Snippet(text);
        _ = SearchText.Clean(text);
    }

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
