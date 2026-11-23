"""Phase 6 acceptance: BlueMail.Mx scores mail (PROMPT 2.3, section 13 Phase 6).

Runs in the tools container (python smtplib as an outside sender without SPF/DKIM):
    docker exec bluemail-dev-tools-1 python /work/tests/interop/spam_checks.py <marker>
Sends three messages with the marker in the Subject; scripts/dev-phase6-check.sh checks where they landed.
Prints "PASS name" / "FAIL name: detail" and a JSON summary; exit code 1 on any failure.
"""
import json
import smtplib
import sys
from email.message import EmailMessage
from email.utils import formatdate, make_msgid

MX = "10.77.0.25"
marker = sys.argv[1] if len(sys.argv) > 1 else "p6"
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


def send(helo, mail_from, msg):
    with smtplib.SMTP(MX, 25, local_hostname=helo, timeout=30) as s:
        s.ehlo(helo)
        try:
            s.send_message(msg, from_addr=mail_from, to_addrs=["minh@bluemail.test"])
            return 250, ""
        except smtplib.SMTPDataError as e:
            return e.smtp_code, e.smtp_error.decode(errors="replace")


def ordinary():
    m = EmailMessage()
    m["From"] = "Chị Hạnh <hanh@nhacungcap.test>"
    m["To"] = "minh@bluemail.test"
    m["Subject"] = f"Lịch giao hàng tuần sau {marker}"
    m["Date"] = formatdate(localtime=True)
    m["Message-ID"] = make_msgid(domain="nhacungcap.test")
    m.set_content("Chào anh Minh,\n\nBên em dự kiến giao 40 thùng vào thứ Ba. Anh xác nhận giúp em nhé.\n\nCảm ơn anh.\n")
    code, text = send("mail.nhacungcap.test", "hanh@nhacungcap.test", m)
    return True if code == 250 else f"{code} {text}"


def phishing():
    m = EmailMessage()
    m["From"] = "\"bluemail.test IT Helpdesk\" <helpdesk@bluemaiI-support.test>"
    m["To"] = "minh@bluemail.test"
    m["Reply-To"] = "bluemail.helpdesk@gmail.com"
    m["Subject"] = f"Your password expires today - verify your account {marker}"
    m["Date"] = formatdate(localtime=True)
    m["Message-ID"] = make_msgid(domain="bluemaiI-support.test")
    m.set_content("Your mailbox will be suspended. Verify your account now.")
    m.add_alternative(
        "<p>Your mailbox will be <b>suspended</b>. "
        "<a href=\"http://bluemaii-support.test/owa/login.php\">https://mail.bluemail.test/owa</a> to verify your account now.</p>",
        subtype="html")
    code, text = send("mail.bluemaiI-support.test", "helpdesk@bluemaiI-support.test", m)
    return True if code == 250 else f"{code} {text}"


def extreme():
    m = EmailMessage()
    m["From"] = "\"paypal.com Security\" <security@bluemaiI.test>"
    m["To"] = "minh@bluemail.test"
    m["Reply-To"] = "claims.desk@gmail.com"
    m["Subject"] = f"URGENT!!! YOUR ACCOUNT IS SUSPENDED - VERIFY YOUR PASSWORD NOW {marker}"
    m.set_content("Verify your account. Your password expires today. Confirm your bank details to claim your prize.")
    m.add_alternative(
        "<a href=\"http://192.0.2.9/login\">https://www.paypal.com/signin</a> "
        "<a href=\"https://bit.ly/x9\">https://bluemail.test/verify</a> "
        "<a href=\"http://xn--pypal-4ve.test/\">https://www.paypal.com/</a>"
        "<img src=\"http://192.0.2.9/a.png\"><img src=\"http://192.0.2.9/b.png\">",
        subtype="html")
    code, text = send("localhost", "security@bluemaiI.test", m)
    return True if code == 550 and "spam" in text.lower() else f"expected 550 spam, got {code} {text}"


check("ordinary mail from an unknown sender is accepted", ordinary)
check("phishing look-alike is accepted (to be filed in Spam)", phishing)
check("score above the reject threshold is refused in session (550)", extreme)
print(json.dumps({"marker": marker, "passed": sum(r["ok"] for r in results), "total": len(results)}))
sys.exit(0 if all(r["ok"] for r in results) else 1)
