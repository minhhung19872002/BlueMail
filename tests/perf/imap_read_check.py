#!/usr/bin/env python3
"""Backup/restore drill check (docs/03): signs in over IMAP and prints the number of INBOX messages and a SHA-256 over
every message's full content (BODY.PEEK[]), so the same mailbox can be compared before and after a restore.
Usage: imap_read_check.py HOST PORT USER PASSWORD   (certificate not verified: drill installations use internal TLS)
"""
import hashlib
import imaplib
import ssl
import sys

host, port, user, password = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE
c = imaplib.IMAP4_SSL(host, port, ssl_context=ctx)
c.login(user, password)
c.select("INBOX", readonly=True)
_, data = c.uid("SEARCH", None, "ALL")
uids = data[0].split()
digest = hashlib.sha256()
for uid in uids:
    _, parts = c.uid("FETCH", uid, "(BODY.PEEK[])")
    digest.update(parts[0][1])
c.logout()
print(f"messages={len(uids)} sha256={digest.hexdigest()}")
