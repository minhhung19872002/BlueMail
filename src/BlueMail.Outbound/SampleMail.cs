using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BlueMail.Infrastructure.Persistence;
using BlueMail.Mime;
using BlueMail.Protocols.Smtp;
using Microsoft.EntityFrameworkCore;

namespace BlueMail.Outbound;

/// <summary>
/// Sample mailbox content (PROMPT 10) for a fresh installation started with BM_SEED_SAMPLE_DATA=true: real messages
/// delivered to BlueMail.Mx over SMTP (never inserted into the database), so they go through the same checks, spam
/// scoring, threading and indexing as any mail. Once per recipient: the guard asks whether that recipient already has
/// the first sample message. Senders use the reserved ".example" domains (no SPF/DMARC, so nothing is a forgery).
/// </summary>
public sealed class SampleMail(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<SampleMail> log) : BackgroundService
{
    private static readonly string[] Recipients = ["lan", "minh", "olivia", "lucas"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(cfg["BM_SEED_SAMPLE_DATA"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var mx = cfg["BM_SEED_MX"] is { Length: > 0 } h ? h : "mx";
        try
        {
            var address = await WaitForMxAsync(mx, stoppingToken);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BlueMailDbContext>();
            var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.DeletedAt == null)
                .Select(u => u.PrimaryEmail).ToListAsync(stoppingToken);
            foreach (var to in users.Where(e => Recipients.Contains(e[..e.IndexOf('@', StringComparison.Ordinal)])))
            {
                var guard = MessageId(to, 1);
                if (await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.RfcMessageId == guard, stoppingToken))
                {
                    continue;
                }

                var sent = 0;
                foreach (var (from, raw) in Build(to, DateTimeOffset.UtcNow))
                {
                    await using var client = new SmtpClient(new SmtpClientOptions { Helo = "seed.example" });
                    await client.ConnectAsync(mx, address, 25, stoppingToken);
                    var (rcpts, final) = await client.SendAsync(from, [to], raw, smtpUtf8: false, eightBit: true, stoppingToken);
                    await client.QuitAsync(stoppingToken);
                    sent += rcpts[0].Response.IsPositive && final is { IsPositive: true } ? 1 : 0;
                }

                log.LogInformation("Sample mail: {Sent} messages delivered to {To}", sent, to);
            }
        }
        catch (Exception e) when (e is SmtpClientException or SocketException or TimeoutException)
        {
            log.LogWarning(e, "Sample mail could not be delivered");
        }
    }

    private static async Task<IPAddress> WaitForMxAsync(string host, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
                using var probe = new TcpClient();
                await probe.ConnectAsync(addresses[0], 25, ct);
                return addresses[0];
            }
            catch (SocketException) when (i < 60)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static string MessageId(string to, int n) => $"sample-{n}-{to.Replace('@', '.')}@seed.example";

    /// <summary>The sample set for one recipient: (envelope sender, RFC 5322 bytes).</summary>
    internal static IEnumerable<(string From, byte[] Raw)> Build(string to, DateTimeOffset now)
    {
        var rcpt = new MailboxAddress(null, to);
        byte[] Mail(int n, MailboxAddress from, string subject, string text, DateTimeOffset when, string? html = null, int? replyTo = null,
            IReadOnlyList<BuilderAttachment>? files = null, IReadOnlyList<(string, string)>? headers = null) =>
            new MessageBuilder
            {
                From = from, To = [rcpt], Subject = subject, Date = when, MessageId = MessageId(to, n), TextBody = text, HtmlBody = html,
                InReplyTo = replyTo is { } r ? MessageId(to, r) : null, References = replyTo is { } r2 ? [MessageId(to, r2)] : [],
                Attachments = files ?? [], ExtraHeaders = headers ?? [],
            }.ToBytes();

        var an = new MailboxAddress("Nguyễn Văn An", "an@partner.example");
        yield return (an.Address, Mail(1, an, "Báo giá thiết bị mạng quý IV", "Chào anh/chị,\n\nGửi báo giá thiết bị mạng quý IV như đã trao đổi.\n\nTrân trọng,\nAn",
            now.AddDays(-3)));
        yield return (an.Address, Mail(2, an, "Re: Báo giá thiết bị mạng quý IV", "Bổ sung: giá đã gồm VAT và phí vận chuyển nội thành.", now.AddDays(-2), replyTo: 1));

        var accounting = new MailboxAddress("Phòng Kế toán Đối tác", "ketoan@partner.example");
        yield return (accounting.Address, Mail(3, accounting, "Hoá đơn tháng 11", "Kính gửi Quý khách,\n\nHoá đơn và bảng kê đính kèm.", now.AddHours(-30),
            files:
            [
                new BuilderAttachment { FileName = "hoá-đơn-11-2026.pdf", ContentType = "application/pdf", Open = () => new MemoryStream(Pdf("Invoice 2026-11")) },
                new BuilderAttachment { FileName = "bảng-kê.csv", ContentType = "text/csv", Open = () => new MemoryStream(Encoding.UTF8.GetBytes("Mã hàng;Số lượng;Đơn giá\nSW-24;3;12.500.000\n")) },
                new BuilderAttachment { FileName = "sơ-đồ.png", ContentType = "image/png", Open = () => new MemoryStream(Png(26, 115, 232)) },
            ]));

        var shop = new MailboxAddress("BlueShop", "news@shop.example");
        yield return (shop.Address, Mail(4, shop, "Ưu đãi cuối năm — giảm đến 50%", "Xem bản HTML để thấy ưu đãi.", now.AddHours(-20),
            html: "<h2 style=\"color:#0b57d0\">Ưu đãi cuối năm</h2><p><img src=\"cid:logo@shop.example\" alt=\"BlueShop\" width=\"48\"> Giảm đến <b>50%</b>.</p>" +
                  "<p><img src=\"https://track.shop.example/open.gif\" width=\"1\" height=\"1\" alt=\"\"></p><p><a href=\"https://shop.example/sale\">Mua ngay</a></p>",
            files: [new BuilderAttachment { FileName = "logo.png", ContentType = "image/png", ContentId = "logo@shop.example", Open = () => new MemoryStream(Png(24, 128, 56)) }],
            headers: [("List-Unsubscribe", "<https://shop.example/unsub>, <mailto:unsub@shop.example>"), ("List-Unsubscribe-Post", "List-Unsubscribe=One-Click")]));

        var calendar = new MailboxAddress("Lịch họp", "calendar@partner.example");
        var start = now.AddDays(2).UtcDateTime.Date.AddHours(3);
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//partner.example//Sample//EN\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n" +
                  $"UID:sample-meeting-{to}\r\nDTSTAMP:{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\nDTSTART:{start:yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{start.AddHours(1):yyyyMMdd'T'HHmmss'Z'}\r\n" +
                  $"SUMMARY:Họp tiến độ dự án\r\nORGANIZER:mailto:calendar@partner.example\r\nATTENDEE;RSVP=TRUE:mailto:{to}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        yield return (calendar.Address, Mail(5, calendar, "Lời mời: Họp tiến độ dự án", "Mời anh/chị tham dự cuộc họp tiến độ.", now.AddHours(-10),
            files: [new BuilderAttachment { FileName = "invite.ics", ContentType = "text/calendar; method=REQUEST", Open = () => new MemoryStream(Encoding.UTF8.GetBytes(ics)) }]));

        var lookalike = new MailboxAddress("BlueMail IT", "it-support@bluemaiI.example");
        yield return (lookalike.Address, Mail(6, lookalike, "Xác minh mật khẩu hộp thư", "Mật khẩu của bạn sắp hết hạn. Bấm vào liên kết để giữ mật khẩu: http://bluemaiI.example/verify",
            now.AddHours(-8)));

        var spam = new MailboxAddress("Lucky Winner Dept", "winner@prize.example");
        yield return (spam.Address, Mail(7, spam, "CONGRATULATIONS!!! YOU WON $1,000,000 — CLAIM NOW",
            "Dear winner, you have WON $1,000,000 in our international lottery!!! Send your bank account details and a processing fee of $99 via Western Union " +
            "TODAY to claim your prize. 100% FREE MONEY, act now, limited time, urgent!!! Click http://prize.example/claim", now.AddHours(-6)));

        var langs = new (MailboxAddress From, string Subject, string Text)[]
        {
            (new("田中 健二", "kenji.tanaka@partner.example"), "会議の議事録", "お疲れ様です。本日の会議の議事録を共有します。"),
            (new("王美玲", "wang@partner.example"), "合同已经签署", "您好，合同已经签署，请查收附件。"),
            (new("สมชาย ใจดี", "somchai@partner.example"), "ยืนยันการประชุม", "สวัสดีครับ ยืนยันการประชุมวันพรุ่งนี้"),
            (new("فاطمة الزهراء", "fatima@partner.example"), "تأكيد الطلب", "مرحباً، تم تأكيد طلبكم. سنرسل لكم تفاصيل الشحن قريباً."),
            (new("Иван Петров", "ivan@partner.example"), "Отчёт за ноябрь", "Добрый день! Отчёт за ноябрь во вложении."),
        };
        for (var i = 0; i < langs.Length; i++)
        {
            var (from, subject, text) = langs[i];
            yield return (from.Address, Mail(8 + i, from, subject, text, now.AddMinutes(-50 + (i * 10))));
        }
    }

    private static byte[] Png(byte r, byte g, byte b)
    {
        const int w = 48, h = 48;
        var raw = new byte[h * (1 + (w * 3))];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * (1 + (w * 3))) + 1 + (x * 3);
                (raw[o], raw[o + 1], raw[o + 2]) = (r, g, b);
            }
        }

        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
        {
            zl.Write(raw);
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [0, 0, 0, w, 0, 0, 0, h, 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(t);
        s.Write(data);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. t, .. data]));
        s.Write(crc);
    }

    /// <summary>CRC-32 (ISO-HDLC) as PNG chunks need it.</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] Pdf(string text)
    {
        var content = $"BT /F1 24 Tf 72 760 Td ({text}) Tj ET";
        string[] objs =
        [
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
