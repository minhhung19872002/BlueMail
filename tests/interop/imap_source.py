#!/usr/bin/env python3
"""Reference side of the IMAP client check (Phase 10).

populate: fills nguon@doitac.test on the dev Dovecot (mail.doitac.test) with folders whose names need modified UTF-7,
          messages with flags and dates, an 8-bit message and a large one.
dump:     reads every folder back with Python's imaplib and prints JSON (UIDVALIDITY, UID, flags, INTERNALDATE, size,
          SHA-256 of the full message) - what BlueMail's own IMAP client must also see.
"""
import base64
import email.utils
import hashlib
import imaplib
import json
import re
import ssl
import sys
import time

HOST, PORT = "mail.doitac.test", 993
USER, PASSWORD = "nguon@doitac.test", "Doitac-Imap-Dev-1"
FOLDERS = ["Dự án", "Dự án/Q4", "Hóa đơn & chứng từ", "Archive"]


def mutf7_encode(s):
    out, i = "", 0
    while i < len(s):
        c = s[i]
        if 0x20 <= ord(c) <= 0x7E:
            out += "&-" if c == "&" else c
            i += 1
            continue
        j = i
        while j < len(s) and not 0x20 <= ord(s[j]) <= 0x7E:
            j += 1
        out += "&" + base64.b64encode(s[i:j].encode("utf-16-be")).decode().rstrip("=").replace("/", ",") + "-"
        i = j
    return out


def mutf7_decode(s):
    def repl(m):
        body = m.group(1)
        if body == "":
            return "&"
        b = body.replace(",", "/")
        b += "=" * (-len(b) % 4)
        return base64.b64decode(b).decode("utf-16-be")
    return re.sub(r"&([A-Za-z0-9+,]*)-", repl, s)


def connect():
    ctx = ssl.create_default_context(cafile="/work/deploy/dev/postfix/tls/cert.pem")
    c = imaplib.IMAP4_SSL(HOST, PORT, ssl_context=ctx)
    c.login(USER, PASSWORD)
    return c


def message(i, subject, body, extra_headers=""):
    date = email.utils.format_datetime(email.utils.localtime().replace(year=2025, month=1 + i % 12, day=1 + i % 27))
    return (f"From: Người gửi {i} <sender{i}@khachhang.test>\r\nTo: nguon@doitac.test\r\nSubject: {subject}\r\n"
            f"Date: {date}\r\nMessage-ID: <src{i}@khachhang.test>\r\nMIME-Version: 1.0\r\n{extra_headers}"
            f"Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n{body}\r\n").encode("utf-8")


def populate():
    c = connect()
    # Start from a known state: remove our folders and empty INBOX.
    typ, data = c.list()
    for line in data:
        name = line.decode().rsplit(' "/" ', 1)[-1].strip('"')
        if name not in ("INBOX", "Sent", "Drafts", "Junk", "Trash"):
            c.delete('"' + name + '"')
    for box in ["INBOX", "Sent", "Trash"]:
        typ, count = c.select(box)
        if int(count[0]) == 0:
            continue
        c.store("1:*", "+FLAGS.SILENT", "(\\Deleted)")
        c.expunge()
    for f in FOLDERS:
        c.create('"' + mutf7_encode(f) + '"')

    plan = [
        ("INBOX", "Báo giá tháng 11", "Kính gửi anh chị,\r\nGửi báo giá.", "(\\Seen)"),
        ("INBOX", "Hợp đồng số 12", "Nội dung hợp đồng", "(\\Seen \\Flagged)"),
        ("INBOX", "Chưa đọc", "Thư chưa đọc", "()"),
        ("Sent", "Re: Báo giá", "Cảm ơn", "(\\Seen \\Answered)"),
        ("Dự án", "Kế hoạch dự án", "Lịch làm việc", "(\\Seen)"),
        ("Dự án/Q4", "Báo cáo Q4", "Doanh thu quý 4", "(\\Seen $Forwarded)"),
        ("Hóa đơn & chứng từ", "Hóa đơn GTGT", "Số hoá đơn 0001", "(\\Seen)"),
        ("Trash", "Thư rác cũ", "Xoá rồi", "(\\Seen \\Deleted)"),
    ]
    for i, (box, subject, body, flags) in enumerate(plan):
        stamp = imaplib.Time2Internaldate(time.mktime((2025, 1 + i, 10 + i, 8, 30, 0, 0, 0, -1)))
        c.append('"' + mutf7_encode(box) + '"', flags, stamp, message(i, subject, body))
    # A 3 MB message: the client must stream it.
    big = "Phụ lục " + ("dữ liệu " * 400_000)
    c.append('"Archive"', "(\\Seen)", imaplib.Time2Internaldate(time.time()), message(99, "Tệp lớn", big))
    c.logout()
    print("ok")


def dump():
    c = connect()
    out = {}
    typ, data = c.list()
    for line in data:
        m = re.match(rb'\((.*?)\) "(.*?)" (.*)', line)
        raw = m.group(3).decode().strip('"')
        attrs = m.group(1).decode()
        if "\\Noselect" in attrs:
            continue
        c.select('"' + raw + '"', readonly=True)
        validity = int(c.response("UIDVALIDITY")[1][0])
        typ, ids = c.uid("SEARCH", None, "ALL")
        msgs = []
        for uid in ids[0].split():
            typ, parts = c.uid("FETCH", uid, "(UID FLAGS INTERNALDATE RFC822.SIZE BODY.PEEK[])")
            meta, body = parts[0]
            flags = sorted(f.decode() for f in imaplib.ParseFlags(meta))
            idate = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.mktime(imaplib.Internaldate2tuple(meta))))
            size = int(re.search(rb"RFC822\.SIZE (\d+)", meta).group(1))
            msgs.append({"uid": int(uid), "flags": flags, "internaldate": idate, "size": size,
                         "sha256": hashlib.sha256(body).hexdigest()})
        out[mutf7_decode(raw)] = {"uidvalidity": validity, "messages": msgs}
    c.logout()
    print(json.dumps(out, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    {"populate": populate, "dump": dump}[sys.argv[1]]()
