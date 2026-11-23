"""Phase 7 acceptance, SMTP side (PROMPT 6.8, section 13 Phase 7). Runs in the tools container.

    docker exec bluemail-dev-tools-1 python /work/tests/interop/rules_checks.py <marker> <srs-address>

1. sends through the doitac.test Postfix (a real MTA) to BlueMail.Mx, which applies the recipients' rules;
2. checks the copies forwarded by BlueMail.Outbound arrive at binh@doitac.test with ARC, an SRS return path and
   the original DKIM signature still valid;
3. checks a bounce to an SRS address is relayed back, and that SRS addresses refuse anything but bounces.
Prints PASS/FAIL lines and a JSON summary; exit code 1 on failure.
"""
import glob
import json
import os
import smtplib
import sys
import time
from email.message import EmailMessage
from email.utils import formatdate, make_msgid

POSTFIX = "10.77.0.30"
MX = "10.77.0.25"
marker = sys.argv[1]
results = []


def check(name, fn):
    try:
        detail = fn()
        ok = detail is True or detail is None
        results.append({"name": name, "ok": ok, "detail": None if ok else str(detail)})
    except Exception as e:  # every check is independent
        results.append({"name": name, "ok": False, "detail": f"{type(e).__name__}: {e}"})
    r = results[-1]
    print(("PASS " if r["ok"] else "FAIL ") + name + ("" if r["ok"] else ": " + r["detail"]), flush=True)


def via_postfix(sender, rcpt, subject, extra=None):
    m = EmailMessage()
    m["From"] = sender
    m["To"] = rcpt
    m["Subject"] = subject
    m["Date"] = formatdate(localtime=True)
    m["Message-ID"] = make_msgid(domain="doitac.test")
    for k, v in (extra or {}).items():
        m[k] = v
    m.set_content(f"Nội dung kiểm tra {marker}\n")
    with smtplib.SMTP(POSTFIX, 25, timeout=30) as s:
        s.send_message(m, from_addr=sender, to_addrs=[rcpt])


def wait_maildir(box, needle, seconds=90):
    end = time.time() + seconds
    while time.time() < end:
        for f in glob.glob(f"/doitacmail/doitac.test/{box}/new/*") + glob.glob(f"/doitacmail/doitac.test/{box}/cur/*"):
            data = open(f, "rb").read().decode("utf-8", "replace")
            if needle in data:
                return data
        time.sleep(2)
    return None


def send_all():
    via_postfix("an@doitac.test", "minh@bluemail.test", f"Hoa don thang 11 {marker}")
    via_postfix("binh@doitac.test", "hoa@bluemail.test", f"Blocked {marker}")
    via_postfix("an@doitac.test", "tuan@bluemail.test", f"Vacation {marker}")
    via_postfix("an@doitac.test", "ngoc@bluemail.test", f"Forward {marker}")
    via_postfix("chi@doitac.test", "minh@bluemail.test", f"Newsletter {marker}", {"List-Id": "<news.doitac.test>"})
    via_postfix("an@doitac.test", "yen@bluemail.test", f"Bounce {marker}")


def forward_bounce():
    data = wait_maildir("an", f"Bounce {marker}", seconds=120)
    if data is None:
        return "no DSN for the failed forward at the original sender an@doitac.test"
    return True if "report-type=delivery-status" in data else "not a DSN"


def forwarded():
    data = wait_maildir("binh", f"Forward {marker}")
    if data is None:
        return "forwarded copy did not arrive at binh@doitac.test"
    head = data.split("\n\n", 1)[0]
    problems = []
    if "Return-Path: <SRS0=" not in head:
        problems.append("no SRS return path")
    if "ARC-Seal: i=1; a=rsa-sha256" not in head or "d=bluemail.test" not in head:
        problems.append("no ARC set of bluemail.test")
    if "dkim=pass" not in head or "header.d=doitac.test" not in head:
        problems.append("original DKIM signature no longer valid")
    if "X-BlueMail-Loop:" not in head:
        problems.append("no loop marker")
    return True if not problems else "; ".join(problems)


def vacation():
    data = wait_maildir("an", f"Out of office {marker}")  # the subject the check script sets
    if data is None:
        return "no auto reply at an@doitac.test"
    head = data.split("\n\n", 1)[0]
    if "Auto-Submitted: auto-replied" not in head:
        return "reply lacks Auto-Submitted"
    if "Return-Path: <>" not in head:
        return "reply does not use the null return path"
    return True


def no_reply_to_lists():
    time.sleep(5)
    return None if wait_maildir("chi", f"Newsletter {marker}", seconds=5) is None else "auto reply sent to a mailing list"


def srs_bounce(srs):
    def run():
        with smtplib.SMTP(MX, 25, timeout=30) as s:
            s.ehlo("relay.tools.test")
            s.mail("someone@doitac.test")
            code, _ = s.rcpt(srs)
            if code != 550:
                return f"non-bounce to SRS address got {code}, expected 550"
            s.rset()
            s.mail("<>")
            code, _ = s.rcpt(srs)
            if code != 250:
                return f"bounce to SRS address got {code}"
            # From a domain without a DMARC policy (doitac.test has p=reject and this host is not its MTA)
            msg = f"From: MAILER-DAEMON@relay.tools.test\r\nTo: {srs}\r\nSubject: Undelivered {marker}\r\n\r\nbounce\r\n"
            code, text = s.data(msg.encode())  # smtplib does not raise on the final reply
            if code != 250:
                return f"bounce DATA got {code} {text}"
            bad = srs.replace("SRS0=", "SRS0=x", 1)
            s.rset()
            s.mail("<>")
            code, _ = s.rcpt(bad)
            if code != 550:
                return f"forged SRS address got {code}, expected 550"
        return None if wait_maildir("an", f"Undelivered {marker}") else "bounce was not relayed to an@doitac.test"
    return run


if len(sys.argv) > 2 and sys.argv[2]:
    # second pass: only the SRS checks, with the return path of a forward the first pass produced
    check("SRS: bounces relayed back, other senders and forged addresses refused", srs_bounce(sys.argv[2]))
else:
    check("mail sent through Postfix to minh, hoa, tuan, ngoc", send_all)
    check("forward: SRS return path, ARC set, original DKIM still valid, loop marker", forwarded)
    check("vacation: one auto reply with null return path and Auto-Submitted", vacation)
    check("vacation: no reply to mailing-list mail", no_reply_to_lists)
    check("failed forward: Outbound's DSN goes back to the original sender (SRS reversed)", forward_bounce)
print(json.dumps({"marker": marker, "passed": sum(r["ok"] for r in results), "total": len(results)}))
sys.exit(0 if all(r["ok"] for r in results) else 1)
