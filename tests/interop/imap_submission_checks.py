#!/usr/bin/env python3
"""Phase 11 checks of BlueMail.Imap and BlueMail.Submission with Python's imaplib/smtplib (independent clients).

Usage: imap_submission_checks.py USER PASSWORD APP_PASSWORD
Prints PASS/FAIL lines; exit code 1 when something failed.
"""
import email.utils
import imaplib
import smtplib
import socket
import ssl
import sys
import threading
import time
import uuid

USER, PASSWORD, APP = sys.argv[1], sys.argv[2], sys.argv[3]
IMAP, SMTP = "imap.bluemail.test", "smtp.bluemail.test"
CTX = ssl.create_default_context(cafile="/work/deploy/dev/tls/cert.pem")
failures = []


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else " :: " + str(detail)))
    if not ok:
        failures.append(name)


def imap():
    c = imaplib.IMAP4_SSL(IMAP, 993, ssl_context=CTX)
    c.login(USER, PASSWORD)
    return c


# 1. Port 143: no login before STARTTLS, then STARTTLS works.
c = imaplib.IMAP4(IMAP, 143)
check("143 advertises STARTTLS and LOGINDISABLED", "STARTTLS" in c.capabilities and "LOGINDISABLED" in c.capabilities, c.capabilities)
try:
    c.login(USER, PASSWORD)
    check("plaintext LOGIN refused", False, "accepted")
except imaplib.IMAP4.error as e:
    check("plaintext LOGIN refused", "PRIVACYREQUIRED" in str(e) or "LOGINDISABLED" in str(e), e)
c = imaplib.IMAP4(IMAP, 143)
c.starttls(ssl_context=CTX)
check("STARTTLS then LOGIN", c.login(USER, PASSWORD)[0] == "OK")
c.logout()

# 2. App password signs in; a wrong one is AUTHENTICATIONFAILED.
c = imaplib.IMAP4_SSL(IMAP, 993, ssl_context=CTX)
check("app password login", c.login(USER, APP)[0] == "OK")
c.logout()
c = imaplib.IMAP4_SSL(IMAP, 993, ssl_context=CTX)
try:
    c.login(USER, "wrong-password")
    check("wrong password refused", False, "accepted")
except imaplib.IMAP4.error as e:
    check("wrong password refused", "AUTHENTICATIONFAILED" in str(e), e)

# 3. IDLE: another connection appends to INBOX; the idling one hears EXISTS.
box = "chk-" + uuid.uuid4().hex[:6]
a = imap()
a.create(box)
a.select(box)
b = imap()
events = []


def idle():
    tag = a._new_tag().decode()
    a.send(f"{tag} IDLE\r\n".encode())
    a.readline()  # + idling
    a.sock.settimeout(15)
    try:
        line = a.readline()
        events.append(line)
    except socket.timeout:
        pass
    a.send(b"DONE\r\n")
    while True:
        l = a.readline()
        if l.startswith(tag.encode()):
            break


t = threading.Thread(target=idle)
t.start()
time.sleep(1.5)
b.append(box, None, None, b"From: a@bluemail.test\r\nSubject: idle test\r\n\r\nx\r\n")
t.join()
check("IDLE reports new mail", any(b"EXISTS" in e for e in events), events)
# A second IDLE on the same connection must hear the next change too (the first IDLE's wait must not linger).
events.clear()
t = threading.Thread(target=idle)
t.start()
time.sleep(1.5)
b.append(box, None, None, b"From: a@bluemail.test\r\nSubject: idle test 2\r\n\r\nx\r\n")
t.join()
check("second IDLE reports new mail", any(b"EXISTS" in e for e in events), events)
a.sock.settimeout(None)

# 4. QRESYNC: expunge in one session, the other resyncs with VANISHED (EARLIER).
b.select(box)
b.append(box, None, None, b"From: a@bluemail.test\r\nSubject: second\r\n\r\ny\r\n")
typ, data = a.status(box, "(UIDVALIDITY HIGHESTMODSEQ)")
status = data[0].decode()
validity = status.split("UIDVALIDITY ")[1].split()[0].strip(")")
modseq = status.split("HIGHESTMODSEQ ")[1].split()[0].strip(")")
b.select(box)
b.store("1", "+FLAGS", "(\\Deleted)")
b.expunge()
q = imap()
q._simple_command("ENABLE", "QRESYNC")
typ, data = q._simple_command("SELECT", box, f"(QRESYNC ({validity} {modseq}))")
untagged = q.untagged_responses
check("QRESYNC SELECT returns VANISHED (EARLIER)", "VANISHED" in untagged, untagged.keys())
q.logout()

# 5. Submission 587 STARTTLS + AUTH, and 465 implicit TLS; AUTH not offered before TLS.
mid = f"<{uuid.uuid4().hex}@bluemail.test>"
msg = (f"From: {USER}\r\nTo: {USER}, an@doitac.test\r\nSubject: submission test {mid}\r\nMessage-ID: {mid}\r\n"
       f"Date: {email.utils.formatdate()}\r\n\r\nSent with smtplib\r\n").encode()
s = smtplib.SMTP(SMTP, 587, timeout=30)
s.ehlo()
check("587 hides AUTH before STARTTLS", not s.has_extn("auth"), s.esmtp_features)
s.starttls(context=CTX)
s.ehlo()
check("587 offers AUTH after STARTTLS", s.has_extn("auth"), s.esmtp_features)
s.login(USER, APP)
refused = s.sendmail(USER, [USER, "an@doitac.test"], msg)
check("587 accepts the message", refused == {}, refused)
s.quit()
s = smtplib.SMTP_SSL(SMTP, 465, context=CTX, timeout=30)
s.login(USER, PASSWORD)
try:
    s.sendmail(USER, ["an@doitac.test"], b"From: someone.else@bluemail.test\r\nTo: an@doitac.test\r\nSubject: spoof\r\n\r\nx\r\n")
    check("465 refuses a From the user may not use", False, "accepted")
except smtplib.SMTPDataError as e:
    check("465 refuses a From the user may not use", e.smtp_code == 550, e)
try:
    s.sendmail(USER, ["nobody-here@bluemail.test"], msg)
    check("unknown local recipient refused at RCPT", False, "accepted")
except smtplib.SMTPRecipientsRefused as e:
    check("unknown local recipient refused at RCPT", True)
s.quit()

# 6. Sent copy exists once, also after the program APPENDs it to Sent (as Thunderbird does).
time.sleep(1)
c = imap()
c.append("Sent", "(\\Seen)", None, msg)
c.select("Sent")
typ, data = c.search(None, "HEADER", "Message-ID", mid)
check("one Sent copy after SMTP + APPEND", len(data[0].split()) == 1, data)
c.delete(box)
c.logout()

# 7. The external recipient (Postfix of doitac.test) gets it through the outbound queue, DKIM-signed and verified there.
got = None
d = imaplib.IMAP4_SSL("mail.doitac.test", 993, ssl_context=ssl.create_default_context(cafile="/work/deploy/dev/postfix/tls/cert.pem"))
d.login("an@doitac.test", "Doitac-Imap-Dev-1")
for _ in range(60):
    d.select("INBOX")
    typ, data = d.search(None, "HEADER", "Message-ID", mid)
    if data[0].split():
        typ, raw = d.fetch(data[0].split()[-1], "(BODY.PEEK[HEADER])")
        got = raw[0][1].decode("utf-8", "replace")
        break
    time.sleep(2)
d.logout()
check("delivered outside through the queue", got is not None)
check("outside receiver verified DKIM", got is not None and "dkim=pass" in got, got[:800] if got else None)

sys.exit(1 if failures else 0)
