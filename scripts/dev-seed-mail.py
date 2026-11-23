"""Sample mailbox content for the dev stack (PROMPT 10): delivered through BlueMail.Mx over SMTP, never inserted.

    docker exec bluemail-dev-tools-1 python /work/scripts/dev-seed-mail.py [recipient]

Sends a multilingual set to the recipient (default lan@bluemail.test): a reply thread, an HTML newsletter with a
remote tracking pixel and an inline cid: image, attachments (PDF, text, PNG), a message from a look-alike domain,
and messages in Vietnamese, Japanese, Chinese, Thai, Arabic and Russian. Each run adds new messages (unique ids).
"""
import base64
import smtplib
import struct
import sys
import time
import zlib
from email.message import EmailMessage
from email.utils import format_datetime, make_msgid
from datetime import datetime, timedelta, timezone

MX = "10.77.0.25"
# doitac.test publishes DMARC p=reject: its mail must come from its own Postfix (DKIM-signed, SPF-listed),
# or BlueMail.Mx rightly refuses it as a spoof. Other sample domains have no DMARC and go straight to mx.
DOITAC_RELAY = "10.77.0.30"


def png(w=48, h=48, rgb=(26, 115, 232)):
    raw = b"".join(b"\x00" + bytes(rgb) * w for _ in range(h))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


def pdf(text):
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        None,
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    stream = b"BT /F1 24 Tf 72 760 Td (" + text.encode("latin-1") + b") Tj ET"
    objs[3] = b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream"
    out = b"%PDF-1.4\n"
    offsets = []
    for i, o in enumerate(objs, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + o + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1) + b"".join(b"%010d 00000 n \n" % x for x in offsets)
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return out


def msg(sender, to, subject, body, when, html=None, msgid=None, reply_to=None):
    m = EmailMessage()
    m["From"] = sender
    m["To"] = to
    m["Subject"] = subject
    m["Date"] = format_datetime(when)
    m["Message-ID"] = msgid or make_msgid(domain=sender.split("@")[-1].rstrip(">"))
    if reply_to:
        m["In-Reply-To"] = reply_to
        m["References"] = reply_to
    m.set_content(body)
    if html:
        m.add_alternative(html, subtype="html")
    return m


def main():
    to = sys.argv[1] if len(sys.argv) > 1 else "lan@bluemail.test"
    now = datetime.now(timezone.utc)
    tag = str(int(time.time()))[-5:]
    out = []

    first = make_msgid(domain="doitac.test")
    out.append(msg('"Nguyễn Văn An" <an@doitac.test>', to, f"Báo giá thiết bị mạng quý IV ({tag})",
                   "Chào chị Lan,\n\nGửi chị báo giá thiết bị mạng quý IV như đã trao đổi.\n\nTrân trọng,\nAn", now - timedelta(days=2), msgid=first))
    reply = make_msgid(domain="doitac.test")
    out.append(msg('"Nguyễn Văn An" <an@doitac.test>', to, f"Re: Báo giá thiết bị mạng quý IV ({tag})",
                   "Bổ sung: giá đã gồm VAT và phí vận chuyển nội thành.", now - timedelta(days=1), msgid=reply, reply_to=first))

    att = msg('"Phòng Kế toán Đối tác" <ketoan@doitac.test>', to, f"Hoá đơn tháng 11 ({tag})",
              "Kính gửi Quý khách,\n\nHoá đơn và bảng kê đính kèm.", now - timedelta(hours=5))
    att.add_attachment(pdf("Invoice 2026-11"), maintype="application", subtype="pdf", filename="hoá-đơn-11-2026.pdf")
    att.add_attachment("Mã hàng;Số lượng;Đơn giá\nSW-24;3;12.500.000\n".encode("utf-8"), maintype="text", subtype="plain", filename="bảng-kê.txt")
    att.add_attachment(png(), maintype="image", subtype="png", filename="sơ-đồ.png")
    out.append(att)

    cid = make_msgid(domain="shop.test")[1:-1]
    news = msg("BlueShop <news@shop.test>", to, f"Ưu đãi cuối năm — giảm đến 50% ({tag})",
               "Xem bản HTML để thấy ưu đãi.", now - timedelta(hours=3),
               html=f"""<html><body style="font-family:sans-serif">
<h2 style="color:#0b57d0">Ưu đãi cuối năm</h2>
<p><img src="cid:{cid}" alt="logo" width="48"> Giảm đến <b>50%</b> cho đơn hàng trong tuần này.</p>
<p><img src="https://track.shop.test/open.gif?u=lan" width="1" height="1" alt=""></p>
<p><a href="https://shop.test/sale">Mua ngay</a> · <a href="https://evil.test/login">https://www.shop.test/account</a></p>
<script>alert('x')</script><p onclick="steal()">Hủy đăng ký</p>
</body></html>""")
    news["List-Unsubscribe"] = "<https://shop.test/unsub?u=lan>, <mailto:unsub@shop.test>"
    news["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
    news.get_payload()[1].add_related(png(rgb=(24, 128, 56)), maintype="image", subtype="png", cid=f"<{cid}>")
    out.append(news)

    out.append(msg('"BlueMail IT" <it-support@bluemaiI.test>', to, f"Xác minh mật khẩu hộp thư ({tag})",
                   "Mật khẩu của bạn sắp hết hạn. Bấm vào liên kết để giữ mật khẩu.", now - timedelta(hours=2)))
    out.append(msg('"田中 健二" <kenji.tanaka@doitac.test>', to, f"会議の議事録 ({tag})", "お疲れ様です。本日の会議の議事録を共有します。", now - timedelta(hours=1)))
    out.append(msg('"王美玲" <wang@doitac.test>', to, f"合同已经签署 ({tag})", "您好，合同已经签署，请查收附件。", now - timedelta(minutes=40)))
    out.append(msg('"สมชาย ใจดี" <somchai@doitac.test>', to, f"ยืนยันการประชุม ({tag})", "สวัสดีครับ ยืนยันการประชุมวันพรุ่งนี้", now - timedelta(minutes=30)))
    out.append(msg('"فاطمة" <fatima@doitac.test>', to, f"تأكيد الطلب ({tag})", "مرحباً، تم تأكيد طلبكم.", now - timedelta(minutes=20)))
    out.append(msg('"Иван Петров" <ivan@doitac.test>', to, f"Отчёт за ноябрь ({tag})", "Добрый день! Отчёт во вложении.", now - timedelta(minutes=10)))

    sent = 0
    for m in out:
        sender = m["From"].addresses[0].addr_spec
        via = DOITAC_RELAY if sender.endswith("@doitac.test") else MX
        with smtplib.SMTP(via, 25, local_hostname="seed.example.test", timeout=60) as s:
            s.ehlo()
            s.send_message(m, from_addr=sender, to_addrs=[to])
            sent += 1
    print(f"submitted {sent} messages for {to} (doitac.test via its Postfix, others direct to {MX})")


if __name__ == "__main__":
    main()
