#!/usr/bin/env python3
"""Appends a message from an imaptest .mbox file to BlueMail.Imap and prints FETCH BODY[section] results (debug helper)."""
import imaplib
import mailbox
import ssl
import sys

mbox_path, index, sections = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
msg = mailbox.mbox(mbox_path)[index].as_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
ctx = ssl.create_default_context(cafile="/work/deploy/dev/tls/cert.pem")
c = imaplib.IMAP4_SSL("imap.bluemail.test", 993, ssl_context=ctx)
c.login("imaptest@bluemail.test", "Imaptest-Dev-Password-1")
try:
    c.create("probe")
except imaplib.IMAP4.error:
    pass
c.select("probe")
c.store("1:*", "+FLAGS", "(\\Deleted)")
c.expunge()
c.append("probe", None, None, msg)
c.select("probe")
for s in sections:
    typ, data = c.fetch("1", "(BODY.PEEK[" + s + "])")
    body = data[0][1] if isinstance(data[0], tuple) else b""
    print("==", s, len(body))
    print(body.decode("utf-8", "replace"))
c.logout()
