using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlueMail.Mime;

namespace BlueMail.InteropTests;

/// <summary>Messages built by BlueMail.Mime are read correctly by Python's email and by Postfix (Phase 2 acceptance).</summary>
public sealed class MimeBuilderInteropTests
{
    private static readonly string OutDir = Path.Combine(ToolsContainer.RepoRoot, "tests", "interop", "out", "built");

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Big = RandomNumberGenerator.GetBytes(300_000);

    private static readonly string[] Names = ["multilingual-alt", "attachments-unicode-names", "inline-image", "plain-long-lines", "rtl-and-emoji"];

    public static TheoryData<string> Cases => new(Names);

    private static MessageBuilder Build(string name)
    {
        var from = new MailboxAddress("Nguyễn Văn An", "an@bluemail.test");
        var to = new[] { new MailboxAddress("Trần Thị Lan", "lan@doitac.test"), new MailboxAddress(null, "binh@doitac.test") };
        var date = new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.FromHours(7));
        return name switch
        {
            "multilingual-alt" => new MessageBuilder
            {
                From = from, To = to, Date = date, MessageId = "multi@bluemail.test",
                Subject = "Báo giá – 報價單 – Счёт – عرض سعر 🎉 với một tiêu đề rất dài để buộc phải gấp dòng và chia nhiều từ mã hoá",
                TextBody = "Kính gửi anh Bình,\nĐây là báo giá.\n中文内容。Русский текст.\n",
                HtmlBody = "<p>Kính gửi anh Bình,</p><p>Đây là <b>báo giá</b>.</p>",
            },
            "attachments-unicode-names" => new MessageBuilder
            {
                From = from, To = to, Date = date, MessageId = "attach@bluemail.test", Subject = "Tệp đính kèm",
                TextBody = "Xem tệp đính kèm.",
                Attachments =
                [
                    new BuilderAttachment { FileName = "Hợp đồng nguyên tắc hợp tác chiến lược giai đoạn 2026–2030 (bản ký cuối cùng).pdf", ContentType = "application/pdf", Open = () => new MemoryStream(Pdf) },
                    new BuilderAttachment { FileName = "合同草案.bin", ContentType = "application/octet-stream", Open = () => new MemoryStream(Big) },
                    new BuilderAttachment { FileName = "report \"Q4\".txt", ContentType = "text/plain", Open = () => new MemoryStream("plain"u8.ToArray()) },
                ],
            },
            "inline-image" => new MessageBuilder
            {
                From = from, To = to, Date = date, MessageId = "inline@bluemail.test", Subject = "Ảnh trong thân",
                TextBody = "Ảnh bên dưới.", HtmlBody = "<p>Ảnh: <img src=\"cid:logo@bm\"></p>",
                Attachments = [new BuilderAttachment { FileName = "logo.png", ContentType = "image/png", ContentId = "logo@bm", Open = () => new MemoryStream(Png) }],
            },
            "plain-long-lines" => new MessageBuilder
            {
                From = from, To = to, Date = date, MessageId = "long@bluemail.test", Subject = "ascii only",
                TextBody = string.Concat(Enumerable.Repeat("0123456789", 300)) + "\nshort line\n",
            },
            "rtl-and-emoji" => new MessageBuilder
            {
                From = new MailboxAddress("فاطمة الزهراء", "fatima@acme.test"), To = to, Date = date, MessageId = "rtl@acme.test",
                Subject = "تقرير المبيعات 👨‍👩‍👧‍👦", TextBody = "مرحبا 👨‍👩‍👧‍👦 שלום",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Built_message_is_well_formed(string name)
    {
        var bytes = Build(name).ToBytes();
        var lines = SplitLines(bytes);
        lines.Should().OnlyContain(l => l.Length <= 998, "RFC 5322 2.1.1");
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '\n')
            {
                (i > 0 && bytes[i - 1] == '\r').Should().BeTrue("every line ends with CRLF");
            }
        }

        // Round trip through our own parser as a sanity check (the real proof is the Python comparison below).
        var m = MimeMessage.Parse(bytes);
        m.Defects.Should().BeEmpty();
        m.Subject.Should().Be(Build(name).Subject);
    }

    [Fact]
    public void Python_email_reads_built_messages_as_intended()
    {
        Directory.CreateDirectory(OutDir);
        foreach (var name in Names)
        {
            File.WriteAllBytes(Path.Combine(OutDir, name + ".eml"), Build(name).ToBytes());
        }

        var (code, stdout, stderr) = ToolsContainer.Exec("python", "/work/tests/interop/mime_dump.py", "/work/tests/interop/out/built");
        code.Should().Be(0, stderr);
        var py = JsonNode.Parse(stdout)!.AsObject();

        foreach (var name in Names)
        {
            var b = Build(name);
            var r = py["built/" + name + ".eml"]!;
            r["subject"]!.GetValue<string>().Should().Be(b.Subject, name);
            r["from"]![0]![0]!.GetValue<string>().Should().Be(b.From.Name, name);
            r["from"]![0]![1]!.GetValue<string>().Should().Be(b.From.Address);
            r["to"]!.AsArray().Select(a => a![1]!.GetValue<string>()).Should().Equal(b.To.Select(t => t.Address));
            r["message_id"]!.GetValue<string>().Should().Be(b.MessageId);
            r["date"]!.GetValue<string>().Should().Be("2026-11-23T02:30:00Z");

            var parts = r["parts"]!.AsArray();
            var texts = parts.Where(p => p!["type"]!.GetValue<string>() == "text/plain" && p["filename"] is null).Select(p => p!["text"]?.GetValue<string>()).ToList();
            if (b.TextBody is not null)
            {
                texts.Should().Contain(b.TextBody.Replace("\r\n", "\n", StringComparison.Ordinal), name);
            }

            if (b.HtmlBody is not null)
            {
                parts.Select(p => p!["text"]?.GetValue<string>()).Should().Contain(b.HtmlBody, name);
            }

            foreach (var a in b.Attachments)
            {
                using var s = a.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var hash = Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
                parts.Should().Contain(p => p!["filename"] != null && p["filename"]!.GetValue<string>() == a.FileName && p["sha256"]!.GetValue<string>() == hash,
                    $"{name}: attachment '{a.FileName}' must keep name and content");
            }
        }
    }

    [Fact]
    public void Postfix_accepts_and_delivers_built_messages_unchanged()
    {
        var b = Build("attachments-unicode-names");
        var marker = "pf-" + Guid.NewGuid().ToString("N")[..8];
        var msg = new MessageBuilder
        {
            From = b.From, To = [new MailboxAddress(null, "chi@doitac.test")], Date = b.Date, MessageId = marker + "@bluemail.test",
            Subject = b.Subject + " " + marker, TextBody = b.TextBody, Attachments = b.Attachments,
        }.ToBytes();

        var (code, stdout, stderr) = ToolsContainer.ExecWithInput(msg, "swaks", "--server", "10.77.0.30", "--from", "an@bluemail.test",
            "--to", "chi@doitac.test", "--data", "-");
        code.Should().Be(0, stdout + stderr);
        stdout.Should().Contain("250 2.0.0");

        // Find the delivered file in the maildir and read it with Python.
        string? delivered = null;
        for (var i = 0; i < 20 && delivered is null; i++)
        {
            Thread.Sleep(500);
            var (_, list, _) = ToolsContainer.Exec("sh", "-c", $"grep -l '{marker}' /doitacmail/doitac.test/chi/new/* 2>/dev/null | head -1");
            delivered = string.IsNullOrWhiteSpace(list) ? null : list.Trim();
        }

        delivered.Should().NotBeNull("Postfix must deliver the message");
        var script = "import email,email.policy,hashlib,sys,json;m=email.message_from_binary_file(open(sys.argv[1],'rb'),policy=email.policy.default);" +
                     "print(json.dumps({'subject':str(m['subject']),'files':{p.get_filename():hashlib.sha256(p.get_payload(decode=True)).hexdigest() for p in m.walk() if p.get_filename()}},ensure_ascii=False))";
        var (c2, out2, err2) = ToolsContainer.Exec("python", "-c", script, delivered!);
        c2.Should().Be(0, err2);
        var r = JsonNode.Parse(out2)!;
        r["subject"]!.GetValue<string>().Should().EndWith(marker);
        r["files"]!.AsObject().Should().HaveCount(3);
        using var big = b.Attachments[1].Open();
        using var ms = new MemoryStream();
        big.CopyTo(ms);
        r["files"]![b.Attachments[1].FileName]!.GetValue<string>().Should().Be(Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())));
    }

    private static List<byte[]> SplitLines(byte[] data)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == '\n')
            {
                lines.Add(data[start..i]);
                start = i + 1;
            }
        }

        return lines;
    }
}
