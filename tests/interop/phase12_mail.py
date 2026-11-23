#!/usr/bin/env python3
"""Phase 12 acceptance helpers, run in the tools container (independent SMTP client: Python's smtplib).

  phase12_mail.py send USER PASSWORD TO SUBJECT       submit through smtp.bluemail.test:587, print the Message-ID
  phase12_mail.py dsn VERP_ADDRESS RCPT ORIG_MSGID    a late bounce (RFC 3464) from the null sender to mx.bluemail.test
  phase12_mail.py arf FBL_ADDRESS RCPT ORIG_MSGID     a complaint (ARF, RFC 5965) to the feedback-loop address
"""
import email.utils
import smtplib
import ssl
import sys
import uuid

CTX = ssl.create_default_context(cafile="/work/deploy/dev/tls/cert.pem")


def send(user, password, to, subject):
    msg_id = email.utils.make_msgid(domain="bluemail.test")
    body = (f"From: {user}\r\nTo: {to}\r\nSubject: {subject}\r\nMessage-ID: {msg_id}\r\nDate: {email.utils.formatdate()}\r\n"
            "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nPhase 12 check.\r\n")
    with smtplib.SMTP("smtp.bluemail.test", 587, timeout=30) as s:
        s.starttls(context=CTX)
        s.login(user, password)
        s.sendmail(user, [to], body.encode())
    print(msg_id.strip("<>"))


def report(to, content):
    with smtplib.SMTP("mx.bluemail.test", 25, timeout=30, local_hostname="mail.doitac.test") as s:
        s.sendmail("", [to], content.encode())
    print("sent")


def dsn(verp, rcpt, orig):
    report(verp, (
        f"From: MAILER-DAEMON@mail.doitac.test\r\nTo: {verp}\r\nSubject: Undelivered Mail Returned to Sender\r\n"
        f"Message-ID: <{uuid.uuid4().hex}@mail.doitac.test>\r\nDate: {email.utils.formatdate()}\r\nMIME-Version: 1.0\r\n"
        "Content-Type: multipart/report; report-type=delivery-status; boundary=\"b\"\r\n\r\n"
        "--b\r\nContent-Type: text/plain\r\n\r\nThe mailbox was removed after your message arrived.\r\n"
        "--b\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; mail.doitac.test\r\n\r\n"
        f"Final-Recipient: rfc822; {rcpt}\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 user unknown\r\n"
        f"--b\r\nContent-Type: text/rfc822-headers\r\n\r\nMessage-ID: <{orig}>\r\nSubject: check\r\n--b--\r\n"))


def arf(fbl, rcpt, orig):
    report(fbl, (
        f"From: fbl@doitac.test\r\nTo: {fbl}\r\nSubject: Abuse report\r\nMessage-ID: <{uuid.uuid4().hex}@doitac.test>\r\n"
        f"Date: {email.utils.formatdate()}\r\nMIME-Version: 1.0\r\n"
        "Content-Type: multipart/report; report-type=feedback-report; boundary=\"f\"\r\n\r\n"
        "--f\r\nContent-Type: text/plain\r\n\r\nA user marked this message as spam.\r\n"
        "--f\r\nContent-Type: message/feedback-report\r\n\r\nFeedback-Type: abuse\r\nUser-Agent: doitac-fbl/1.0\r\nVersion: 1\r\n"
        f"Original-Rcpt-To: <{rcpt}>\r\nReporting-MTA: dns; mail.doitac.test\r\n"
        f"--f\r\nContent-Type: text/rfc822-headers\r\n\r\nMessage-ID: <{orig}>\r\nSubject: check\r\n--f--\r\n"))


if __name__ == "__main__":
    {"send": send, "dsn": dsn, "arf": arf}[sys.argv[1]](*sys.argv[2:])
