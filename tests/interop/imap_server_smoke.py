#!/usr/bin/env python3
"""Quick conversation with BlueMail.Imap using Python's imaplib (Phase 11 smoke check, not the compliance suite)."""
import imaplib
import ssl
import sys

USER = sys.argv[1] if len(sys.argv) > 1 else "minh@bluemail.test"
PASSWORD = sys.argv[2] if len(sys.argv) > 2 else "Sample-User-Dev-1"

ctx = ssl.create_default_context(cafile="/work/deploy/dev/tls/cert.pem")
c = imaplib.IMAP4_SSL("imap.bluemail.test", 993, ssl_context=ctx)
print("CAPS", c.capabilities)
print(c.login(USER, PASSWORD))
print(c.capability())
typ, folders = c.list()
for f in folders:
    print("LIST", f.decode())
typ, data = c.select("INBOX")
print("SELECT", typ, data)
typ, data = c.uid("SEARCH", None, "ALL")
uids = data[0].split()
print("UIDS", len(uids))
if uids:
    last = uids[-1]
    typ, data = c.uid("FETCH", last, "(UID FLAGS INTERNALDATE RFC822.SIZE ENVELOPE BODYSTRUCTURE BODY.PEEK[HEADER.FIELDS (SUBJECT FROM)])")
    for part in data:
        print("FETCH", part if isinstance(part, bytes) else part[0][:300], (part[1][:200] if isinstance(part, tuple) else b""))
    print(c.uid("STORE", last, "+FLAGS", "(\\Flagged)"))
    print(c.uid("STORE", last, "-FLAGS", "(\\Flagged)"))
print(c.status("INBOX", "(MESSAGES UNSEEN UIDNEXT UIDVALIDITY HIGHESTMODSEQ)"))
print(c.append("Drafts", "(\\Seen)", None, b"From: x@bluemail.test\r\nSubject: imap draft\r\nMessage-ID: <imapdraft1@t>\r\n\r\nhello\r\n"))
print(c.logout())
