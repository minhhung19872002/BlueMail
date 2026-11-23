#!/usr/bin/env python3
"""Generate the BlueMail MIME test corpus (PROMPT Z8).

All messages are synthetic and written byte-by-byte here, so the corpus carries no personal
data and no third-party licence. They imitate the structure produced by common clients
(Outlook, Gmail, Thunderbird, Apple Mail, Yahoo, notification systems), use many languages
and legacy charsets, and include deliberately malformed mail seen in the wild.

Output: messages/wellformed/*.eml and messages/malformed/*.eml (CRLF unless the case is
about bare LF). Re-running overwrites the files deterministically.
"""
import base64
import pathlib
import quopri
import unicodedata

ROOT = pathlib.Path(__file__).resolve().parent / "messages"
OUT = {"wellformed": ROOT / "wellformed", "malformed": ROOT / "malformed"}
CRLF = b"\r\n"


def b64(data: bytes, width: int = 76) -> bytes:
    enc = base64.b64encode(data)
    return CRLF.join(enc[i:i + width] for i in range(0, len(enc), width))


def qp(text: str, charset: str = "utf-8") -> bytes:
    return quopri.encodestring(text.encode(charset)).replace(b"\n", CRLF)


def ew_b(text: str, charset: str) -> str:
    """RFC 2047 B encoded-word."""
    return f"=?{charset}?B?{base64.b64encode(text.encode(charset)).decode()}?="


def ew_q(text: str, charset: str) -> str:
    """RFC 2047 Q encoded-word (minimal: spaces as _, non-safe bytes as =XX)."""
    out = []
    for byte in text.encode(charset):
        ch = chr(byte)
        if ch == " ":
            out.append("_")
        elif ch.isalnum() and byte < 128:
            out.append(ch)
        else:
            out.append(f"={byte:02X}")
    return f"=?{charset}?Q?{''.join(out)}?="


def lines(*parts) -> bytes:
    """Join header/body lines with CRLF. Accepts str (encoded as UTF-8) or bytes."""
    return CRLF.join(p if isinstance(p, bytes) else p.encode("utf-8") for p in parts) + CRLF


def hdr(subject_encoded: str, frm: str = "An Nguyen <an@doitac.test>", to: str = "lan@bluemail.test",
        msgid: str = "corpus", date: str = "Mon, 2 Nov 2026 09:15:00 +0700") -> list:
    return [f"From: {frm}", f"To: {to}", f"Subject: {subject_encoded}", f"Date: {date}",
            f"Message-ID: <{msgid}@corpus.bluemail.test>", "MIME-Version: 1.0"]


PNG_1PX = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==")
PDF_MIN = b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"


def to_cp1258_form(text: str) -> str:
    """Rewrite Vietnamese text into the form windows-1258 can encode: precomposed base letters
    (a with breve, o with horn...) followed by a combining tone mark."""
    out = []
    for ch in unicodedata.normalize("NFD", text):
        if unicodedata.combining(ch) and out:
            merged = unicodedata.normalize("NFC", out[-1] + ch)
            if len(merged) == 1:
                try:
                    merged.encode("windows-1258")
                    out[-1] = merged
                    continue
                except UnicodeEncodeError:
                    pass
        out.append(ch)
    return "".join(out)


def single_charset(name, charset, subject, body, lang_note, cte="8bit"):
    """A text/plain message in a legacy charset with an RFC 2047 subject in the same charset."""
    if charset == "windows-1258":
        subject, body = to_cp1258_form(subject), to_cp1258_form(body)
    raw_body = body.encode(charset)
    if cte == "base64":
        enc_body = b64(raw_body)
    elif cte == "quoted-printable":
        enc_body = quopri.encodestring(raw_body).replace(b"\n", CRLF)
    else:
        enc_body = raw_body.replace(b"\n", CRLF)
    msg = lines(*hdr(ew_b(subject, charset), msgid=name),
                f"Content-Type: text/plain; charset={charset}",
                f"Content-Transfer-Encoding: {cte}",
                f"X-Corpus-Note: {lang_note}", "") + enc_body + CRLF
    return name, "wellformed", msg


def build() -> list:
    cases = []

    # 1. Outlook style: multipart/related(alternative(plain, html), inline cid image) + attachment
    b1, b2, b3 = "_000_OUTLOOK_MIXED_", "_001_OUTLOOK_REL_", "_002_OUTLOOK_ALT_"
    html = '<html><body><p>Kính gửi anh Bình,</p><p>Báo giá đính kèm.<img src="cid:image001.png@01DB"></p></body></html>'
    msg = lines(*hdr(ew_b("Báo giá tháng 11 – Công ty Đối Tác", "utf-8"),
                     frm='"Nguyễn Văn An" <an@doitac.test>', msgid="outlook-related"),
                "Thread-Topic: Báo giá tháng 11", "X-MS-Has-Attach: yes", "Content-Language: vi-VN",
                f'Content-Type: multipart/mixed; boundary="{b1}"', "",
                f"--{b1}", f'Content-Type: multipart/related; boundary="{b2}"; type="multipart/alternative"', "",
                f"--{b2}", f'Content-Type: multipart/alternative; boundary="{b3}"', "",
                f"--{b3}", 'Content-Type: text/plain; charset="utf-8"', "Content-Transfer-Encoding: quoted-printable", "",
                qp("Kính gửi anh Bình,\nBáo giá đính kèm.\n"),
                f"--{b3}", 'Content-Type: text/html; charset="utf-8"', "Content-Transfer-Encoding: quoted-printable", "",
                qp(html), f"--{b3}--", "",
                f"--{b2}", 'Content-Type: image/png; name="image001.png"', "Content-Description: image001.png",
                'Content-Disposition: inline; filename="image001.png"; size=68', "Content-ID: <image001.png@01DB>",
                "Content-Transfer-Encoding: base64", "", b64(PNG_1PX), f"--{b2}--", "",
                f"--{b1}", 'Content-Type: application/pdf; name="=?utf-8?B?QsOhbyBnacOhLnBkZg==?="',
                'Content-Disposition: attachment; filename="=?utf-8?B?QsOhbyBnacOhLnBkZg==?="',
                "Content-Transfer-Encoding: base64", "", b64(PDF_MIN), f"--{b1}--")
    cases.append(("outlook-related-cid", "wellformed", msg))

    # 2. Gmail style: multipart/alternative with 0000 boundary, emoji subject, RFC 2231 filename
    bm, ba = "0000000000001a2b3c05e9f0mixed", "0000000000001a2b3c05e9f0alt"
    long_name = "Hợp đồng nguyên tắc hợp tác chiến lược giai đoạn 2026–2030 (bản ký) 📄.xlsx"
    enc = "".join(c if (c.isascii() and c.isalnum()) or c in ".-_" else "".join(f"%{x:02X}" for x in c.encode("utf-8")) for c in long_name)
    pieces = [enc[i:i + 60] for i in range(0, len(enc), 60)]
    cd = ["Content-Disposition: attachment;"]
    for i, p in enumerate(pieces):
        cd.append(f" filename*{i}*={'utf-8' + chr(39) + 'vi' + chr(39) if i == 0 else ''}{p}{';' if i < len(pieces) - 1 else ''}")
    msg = lines(*hdr(ew_b("🎉 Chúc mừng – 合作愉快 – Поздравляем", "utf-8"), frm="Lan Tran <lan.tran@gmail.example>",
                     msgid="gmail-emoji"),
                f'Content-Type: multipart/mixed; boundary="{bm}"', "",
                f"--{bm}", f'Content-Type: multipart/alternative; boundary="{ba}"', "",
                f"--{ba}", 'Content-Type: text/plain; charset="UTF-8"', "Content-Transfer-Encoding: base64", "",
                b64("Chúc mừng! 🎉 合作愉快 Поздравляем\n".encode()),
                f"--{ba}", 'Content-Type: text/html; charset="UTF-8"', "Content-Transfer-Encoding: base64", "",
                b64('<div dir="ltr">Chúc mừng! 🎉 合作愉快 Поздравляем</div>'.encode()), f"--{ba}--", "",
                f"--{bm}", 'Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', *cd,
                "Content-Transfer-Encoding: base64", "", b64(b"PK\x03\x04" + b"\x00" * 26), f"--{bm}--")
    cases.append(("gmail-emoji-rfc2231", "wellformed", msg))

    # 3. Thunderbird format=flowed reply with References chain
    msg = lines("From: Binh Le <binh@doitac.test>", "To: An Nguyen <an@doitac.test>",
                "Subject: Re: Re: Lịch họp", "Date: Tue, 3 Nov 2026 10:00:00 +0700",
                "Message-ID: <tb-flowed@doitac.test>", "In-Reply-To: <reply-1@bluemail.test>",
                "References: <root-0@bluemail.test>", " <reply-1@bluemail.test>",
                "User-Agent: Mozilla Thunderbird", "MIME-Version: 1.0",
                "Content-Type: text/plain; charset=UTF-8; format=flowed; delsp=no", "Content-Transfer-Encoding: 8bit", "",
                "Đồng ý, hẹn gặp anh lúc 9 giờ sáng thứ Năm tại phòng họp ",
                "tầng 3.", "", "On 02/11/2026 09:15, An Nguyen wrote:", "> Anh xem lịch giúp em ", "> nhé.", "",
                "-- ", "Binh")
    cases.append(("thunderbird-flowed", "wellformed", msg))

    # 4. Apple Mail: quoted-printable HTML, Apple-Mail boundary, attachment of a nested message
    ba = "Apple-Mail=_5F3A2B1C-0000-4000-8000-000000000001"
    inner = lines(*hdr("Original", frm="chi@doitac.test", msgid="inner-forwarded"),
                  "Content-Type: text/plain; charset=us-ascii", "", "Forwarded original body.")
    msg = lines(*hdr(ew_q("Fwd: Tài liệu", "utf-8"), frm="Chi Pham <chi@doitac.test>", msgid="apple-rfc822"),
                f'Content-Type: multipart/mixed; boundary="{ba}"', "",
                f"--{ba}", "Content-Transfer-Encoding: quoted-printable", "Content-Type: text/html; charset=utf-8", "",
                qp('<html><body style="word-wrap: break-word;">Gửi anh tài liệu.</body></html>'),
                f"--{ba}", "Content-Disposition: attachment; filename=original.eml",
                "Content-Type: message/rfc822", "", inner, f"--{ba}--")
    cases.append(("apple-message-rfc822", "wellformed", msg))

    # 5. Yahoo style with ISO-8859-1 and Q-encoded From display name
    msg = lines(f"From: {ew_q('José Müller', 'iso-8859-1')} <jose@yahoo.example>", "To: lan@bluemail.test",
                f"Subject: {ew_q('Réunion à Zürich', 'iso-8859-1')}", "Date: Wed, 4 Nov 2026 08:00:00 +0100",
                "Message-ID: <yahoo-latin1@yahoo.example>", "MIME-Version: 1.0",
                "Content-Type: text/plain; charset=ISO-8859-1", "Content-Transfer-Encoding: quoted-printable", "",
                quopri.encodestring("Grüße aus Zürich, à bientôt!\n".encode("iso-8859-1")).replace(b"\n", CRLF))
    cases.append(("yahoo-latin1", "wellformed", msg))

    # 6. Automated notification: List-Unsubscribe, List-Unsubscribe-Post, Precedence bulk, HTML only
    msg = lines(*hdr("Your weekly report", frm="Reports <noreply@notify.example>", msgid="bulk-newsletter"),
                "List-Id: Weekly Reports <weekly.notify.example>",
                "List-Unsubscribe: <https://notify.example/u/abc123>, <mailto:unsub@notify.example?subject=unsub>",
                "List-Unsubscribe-Post: List-Unsubscribe=One-Click", "Precedence: bulk",
                "Auto-Submitted: auto-generated", "Content-Type: text/html; charset=utf-8",
                "Content-Transfer-Encoding: 7bit", "",
                '<html><body><img src="https://tracker.notify.example/p.gif"><a href="https://notify.example/r">Report</a></body></html>')
    cases.append(("notification-list-unsubscribe", "wellformed", msg))

    # 7. Legacy charsets, one per script
    cases.append(single_charset("charset-iso-2022-jp", "iso-2022-jp", "会議のお知らせ", "明日の会議は10時からです。\n", "Japanese JIS", "7bit"))
    cases.append(single_charset("charset-shift-jis", "shift_jis", "見積書の送付", "見積書を添付いたします。\n", "Japanese SJIS", "base64"))
    cases.append(single_charset("charset-gb2312", "gb2312", "合同草案", "请查收合同草案。\n", "Chinese simplified"))
    cases.append(single_charset("charset-big5", "big5", "會議記錄", "附上會議記錄，請查閱。\n", "Chinese traditional", "base64"))
    cases.append(single_charset("charset-euc-kr", "euc-kr", "회의 안내", "내일 회의가 있습니다.\n", "Korean"))
    cases.append(single_charset("charset-koi8-r", "koi8-r", "Счёт на оплату", "Добрый день! Счёт во вложении.\n", "Russian KOI8-R"))
    cases.append(single_charset("charset-windows-1251", "windows-1251", "Договор", "Высылаем договор.\n", "Russian cp1251", "quoted-printable"))
    cases.append(single_charset("charset-windows-1258", "windows-1258", "Thông báo nghỉ lễ", "Công ty thông báo lịch nghỉ lễ.\n", "Vietnamese cp1258 (combining marks)", "quoted-printable"))
    cases.append(single_charset("charset-windows-1256", "windows-1256", "اجتماع الغد", "مرحبا، الاجتماع غدا الساعة العاشرة.\n", "Arabic RTL", "base64"))
    cases.append(single_charset("charset-iso-8859-8", "iso-8859-8", "פגישה", "שלום, הפגישה מחר.\n", "Hebrew RTL"))
    cases.append(single_charset("charset-tis-620", "tis-620", "ประชุม", "สวัสดีครับ ประชุมพรุ่งนี้\n", "Thai (no spaces)"))
    cases.append(single_charset("charset-iso-8859-7", "iso-8859-7", "Συνάντηση", "Καλημέρα, η συνάντηση είναι αύριο.\n", "Greek"))
    cases.append(single_charset("charset-iso-8859-9", "iso-8859-9", "Toplantı", "Günaydın, toplantı yarın.\n", "Turkish"))
    cases.append(single_charset("charset-windows-1250", "windows-1250", "Schůzka", "Dobrý den, schůzka je zítra. Žluťoučký kůň.\n", "Czech"))

    # 8. RFC 6532 internationalised addresses (SMTPUTF8) with raw UTF-8 headers
    msg = lines("From: Đặng Thị Hoa <hoa@đốitác.test>", "To: 用户@例子.test", "Subject: Địa chỉ quốc tế hoá",
                "Date: Thu, 5 Nov 2026 14:00:00 +0700", "Message-ID: <eai-utf8@xn--itc-0na7g.test>",
                "MIME-Version: 1.0", "Content-Type: text/plain; charset=utf-8", "Content-Transfer-Encoding: 8bit", "",
                "Thư gửi tới địa chỉ có dấu.")
    cases.append(("eai-utf8-headers", "wellformed", msg))

    # 9. DSN (RFC 3464) multipart/report
    br = "DSN_BOUNDARY_1"
    msg = lines("From: Mail Delivery System <MAILER-DAEMON@mail.doitac.test>", "To: an@bluemail.test",
                "Subject: Undelivered Mail Returned to Sender", "Date: Fri, 6 Nov 2026 01:02:03 +0000",
                "Message-ID: <dsn-1@mail.doitac.test>", "Auto-Submitted: auto-replied", "MIME-Version: 1.0",
                f'Content-Type: multipart/report; report-type=delivery-status; boundary="{br}"', "",
                f"--{br}", "Content-Type: text/plain; charset=us-ascii", "",
                "I'm sorry to have to inform you that your message could not be delivered.",
                f"--{br}", "Content-Type: message/delivery-status", "",
                "Reporting-MTA: dns; mail.doitac.test", "Arrival-Date: Fri, 6 Nov 2026 01:02:00 +0000", "",
                "Final-Recipient: rfc822; reject@doitac.test", "Original-Recipient: rfc822;reject@doitac.test",
                "Action: failed", "Status: 5.1.1", "Remote-MTA: dns; mail.doitac.test",
                "Diagnostic-Code: smtp; 550 5.1.1 Mailbox does not exist", "",
                f"--{br}", "Content-Type: text/rfc822-headers", "",
                "From: an@bluemail.test", "To: reject@doitac.test", "Subject: test", "", f"--{br}--")
    cases.append(("dsn-delivery-status", "wellformed", msg))

    # 10. Calendar invitation (iMIP, RFC 6047)
    bc = "CAL_BOUNDARY"
    ics = CRLF.join([b"BEGIN:VCALENDAR", b"PRODID:-//Corpus//EN", b"VERSION:2.0", b"METHOD:REQUEST", b"BEGIN:VEVENT",
                     b"UID:evt-123@doitac.test", b"DTSTAMP:20261101T000000Z", b"DTSTART:20261120T020000Z",
                     b"DTEND:20261120T030000Z", "SUMMARY:Họp tổng kết quý".encode(),
                     b"ORGANIZER;CN=An:mailto:an@doitac.test",
                     b"ATTENDEE;RSVP=TRUE;PARTSTAT=NEEDS-ACTION:mailto:lan@bluemail.test",
                     b"END:VEVENT", b"END:VCALENDAR"])
    msg = lines(*hdr(ew_b("Lời mời: Họp tổng kết quý", "utf-8"), msgid="calendar-invite"),
                f'Content-Type: multipart/alternative; boundary="{bc}"', "",
                f"--{bc}", "Content-Type: text/plain; charset=utf-8", "Content-Transfer-Encoding: 8bit", "",
                "Mời anh tham dự họp tổng kết quý.",
                f"--{bc}", 'Content-Type: text/calendar; charset=utf-8; method=REQUEST', "Content-Transfer-Encoding: 8bit", "",
                ics, f"--{bc}--")
    cases.append(("calendar-invite", "wellformed", msg))

    # 11. RTL HTML (Arabic) in UTF-8 with mixed numbers
    msg = lines(*hdr(ew_b("تقرير المبيعات ٢٠٢٦", "utf-8"), frm="Omar <omar@example.test>", msgid="rtl-arabic-html"),
                "Content-Type: text/html; charset=utf-8", "Content-Transfer-Encoding: base64", "",
                b64('<html dir="rtl"><body><p>مرحبا، المبيعات ارتفعت بنسبة 15%.</p></body></html>'.encode()))
    cases.append(("rtl-arabic-html", "wellformed", msg))

    # 12. RFC 2047 encoded-word split in the middle of a multi-byte character across two words
    s = "Thông báo khẩn cấp".encode("utf-8")
    cut = s.index("ẩ".encode("utf-8")) + 1  # cut inside the 3-byte sequence of "ẩ"
    w1 = "=?utf-8?B?" + base64.b64encode(s[:cut]).decode() + "?="
    w2 = "=?utf-8?B?" + base64.b64encode(s[cut:]).decode() + "?="
    msg = lines(*hdr(w1, msgid="ew-split-multibyte"), f" {w2}", "Content-Type: text/plain; charset=utf-8", "", "body")
    cases.append(("ew-split-multibyte", "malformed", msg))

    # 13. Plain 7bit, no MIME headers at all (RFC 822 era), old date format
    msg = lines("From: old@legacy.test", "To: lan@bluemail.test", "Subject: legacy", "Date: 5 Nov 26 14:00 GMT",
                "Message-Id: <legacy-no-mime@legacy.test>", "", "No MIME headers here.")
    cases.append(("legacy-no-mime-2digit-year", "wellformed", msg))

    # 14. Group address syntax, quoted display names with comments
    msg = lines('From: "Le, Minh (Sales)" <minh@doitac.test> (sales team)',
                "To: undisclosed-recipients:;", "Cc: Team: a@doitac.test, \"B. Tran\" <b@doitac.test>;",
                "Subject: groups", "Date: Sat, 7 Nov 2026 07:07:07 -0000", "Message-ID: <groups@doitac.test>",
                "MIME-Version: 1.0", "Content-Type: text/plain", "", "x")
    cases.append(("address-groups-comments", "wellformed", msg))

    # 15. Deeply nested multipart (8 levels) - within default limits
    depth, body = 8, lines("Content-Type: text/plain; charset=utf-8", "", "leaf")
    for d in range(depth):
        bd = f"NEST{d}"
        body = lines(f'Content-Type: multipart/mixed; boundary="{bd}"', "", f"--{bd}") + body + lines(f"--{bd}--")
    cases.append(("nested-8-levels", "wellformed", lines(*hdr("nested", msgid="nested-8")) + body))

    # ---------------- malformed mail seen in the wild ----------------
    bmiss = "NO_CLOSE"
    msg = lines(*hdr("missing close boundary", msgid="missing-close"),
                f'Content-Type: multipart/mixed; boundary="{bmiss}"', "",
                f"--{bmiss}", "Content-Type: text/plain", "", "part one",
                f"--{bmiss}", "Content-Type: text/plain", "", "part two, then EOF")
    cases.append(("missing-close-boundary", "malformed", msg))

    msg = lines(*hdr("declared boundary differs", msgid="wrong-boundary"),
                'Content-Type: multipart/alternative; boundary="DECLARED"', "",
                "--ACTUAL", "Content-Type: text/plain", "", "text", "--ACTUAL--")
    cases.append(("boundary-mismatch", "malformed", msg))

    bare = ("From: lf@bare.test\nTo: lan@bluemail.test\nSubject: bare LF line endings\n"
            "Date: Sun, 8 Nov 2026 08:00:00 +0000\nMessage-ID: <bare-lf@bare.test>\nMIME-Version: 1.0\n"
            "Content-Type: text/plain; charset=utf-8\n\nLine one\nLine two\n").encode()
    cases.append(("bare-lf", "malformed", bare))

    msg = lines(*hdr("placeholder", msgid="raw-8bit-header")[:2],
                "Subject: ".encode() + "Đơn hàng số 123 – không khai báo".encode("utf-8"),
                *hdr("x", msgid="raw-8bit-header")[3:], "Content-Type: text/plain", "", "8-bit subject undeclared")
    cases.append(("raw-8bit-header-undeclared", "malformed", msg))

    msg = lines(*hdr("raw cp1258 header", msgid="raw-cp1258-header")[:2],
                b"Subject: " + to_cp1258_form("Báo cáo").encode("windows-1258"),
                *hdr("x", msgid="raw-cp1258-header")[3:], "Content-Type: text/plain", "", "non-UTF-8 raw header")
    cases.append(("raw-cp1258-header", "malformed", msg))

    good = base64.b64encode("Nội dung base64 có rác".encode()).decode()
    noisy = good[:10] + "!!**" + good[10:20] + "\t~" + good[20:]
    msg = lines(*hdr("base64 with garbage", msgid="b64-garbage"), "Content-Type: text/plain; charset=utf-8",
                "Content-Transfer-Encoding: base64", "", noisy)
    cases.append(("base64-garbage-chars", "malformed", msg))

    msg = lines(*hdr("first subject", msgid="dup-headers"), "Subject: second subject",
                "Content-Type: text/plain", "Content-Type: text/html", "", "duplicated headers")
    cases.append(("duplicate-headers", "malformed", msg))

    msg = lines(*hdr("wrong charset label", msgid="wrong-charset"),
                "Content-Type: text/plain; charset=us-ascii", "Content-Transfer-Encoding: 8bit", "",
                "Tiêu đề khai us-ascii nhưng thân là UTF-8".encode("utf-8"))
    cases.append(("charset-label-wrong-utf8", "malformed", msg))

    msg = lines(*hdr("unknown charset", msgid="unknown-charset"),
                "Content-Type: text/plain; charset=x-unknown-9999", "Content-Transfer-Encoding: 8bit", "",
                "Grüße".encode("windows-1252"))
    cases.append(("charset-unknown-cp1252-bytes", "malformed", msg))

    msg = lines(*hdr("long line", msgid="long-line"), "Content-Type: text/plain", "", "A" * 5000)
    cases.append(("line-over-998", "malformed", msg))

    msg = lines(*hdr("header without colon", msgid="no-colon"), "This-Is-Not-A-Header", "Content-Type: text/plain", "", "x")
    cases.append(("header-without-colon", "malformed", msg))

    msg = lines(*hdr("no body", msgid="no-body"), "Content-Type: text/plain")
    cases.append(("headers-only-no-body", "malformed", msg))

    msg = lines(*hdr("bad qp", msgid="bad-qp"), "Content-Type: text/plain; charset=utf-8",
                "Content-Transfer-Encoding: quoted-printable", "", "Soft=", "break and =ZZ invalid escape and trailing =")
    cases.append(("quoted-printable-invalid", "malformed", msg))

    msg = lines(*hdr("unterminated quoted param", msgid="unterminated-quote"),
                'Content-Type: multipart/mixed; boundary="UNTERMINATED', "", "--UNTERMINATED",
                "Content-Type: text/plain", "", "x", "--UNTERMINATED--")
    cases.append(("param-unterminated-quote", "malformed", msg))

    depth, body = 120, lines("Content-Type: text/plain", "", "deep leaf")
    for d in range(depth):
        bd = f"D{d}"
        body = lines(f'Content-Type: multipart/mixed; boundary="{bd}"', "", f"--{bd}") + body + lines(f"--{bd}--")
    cases.append(("nested-120-levels-over-limit", "malformed", lines(*hdr("too deep", msgid="deep-120")) + body))

    msg = lines("From: nul@nul.test", "To: lan@bluemail.test", b"Subject: has \x00 NUL byte",
                "Date: Mon, 9 Nov 2026 09:00:00 +0000", "Message-ID: <nul-byte@nul.test>", "", b"body with \x00 nul")
    cases.append(("nul-bytes", "malformed", msg))

    msg = lines(*hdr("ew in quoted string", msgid="ew-in-quotes")[:1],
                'To: "=?utf-8?B?TMOibg==?=" <lan@bluemail.test>', *hdr("x", msgid="ew-in-quotes")[2:],
                "Content-Type: text/plain", "", "Encoded word inside quoted display name (common, non-standard)")
    cases.append(("ew-inside-quoted-string", "malformed", msg))

    msg = lines(*hdr("bad date", msgid="bad-date")[:3], "Date: 31/12/2026 25:61", *hdr("x", msgid="bad-date")[4:],
                "Content-Type: text/plain", "", "x")
    cases.append(("date-unparseable", "malformed", msg))

    return cases


def main() -> None:
    for d in OUT.values():
        d.mkdir(parents=True, exist_ok=True)
    cases = build()
    names = set()
    for name, kind, data in cases:
        assert name not in names, name
        names.add(name)
        (OUT[kind] / f"{name}.eml").write_bytes(data)
    print(f"generated {len(cases)} messages")


if __name__ == "__main__":
    main()
