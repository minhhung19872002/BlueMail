"""Postfix of doitac.test verifies DKIM of inbound mail (dkimpy-milter, BUG-027).

    docker exec bluemail-dev-tools-1 python /work/tests/interop/postfix_dkim_check.py <marker>

Signs a message for bluemail.test with the dev key (selector bm2026, published in the dev DNS), submits it to
Postfix (10.77.0.30), and waits for the Maildir copy to carry "dkim=pass". Exit code 0 on success.
"""
import glob
import os
import smtplib
import sys
import time
from email.message import EmailMessage
from email.utils import formatdate, make_msgid

import dkim

sys.path.insert(0, os.path.dirname(__file__))
from dkim_tool import load_key  # noqa: E402  (PKCS#8 -> PKCS#1 for dkimpy)


def main():
    marker = sys.argv[1] if len(sys.argv) > 1 else str(int(time.time()))
    m = EmailMessage()
    m["From"] = "lan@bluemail.test"
    m["To"] = "binh@doitac.test"
    m["Subject"] = "postfix-dkim " + marker
    m["Date"] = formatdate()
    m["Message-ID"] = make_msgid(domain="bluemail.test")
    m.set_content("DKIM check " + marker)
    raw = m.as_bytes(policy=m.policy.clone(linesep="\r\n"))
    key = load_key("/work/deploy/dev/dkim/bluemail.test.private.pem")
    sig = dkim.sign(raw, b"bm2026", b"bluemail.test", key, include_headers=[b"from", b"to", b"subject", b"date", b"message-id"])
    with smtplib.SMTP("10.77.0.30", 25, local_hostname="tools.example.test", timeout=30) as s:
        s.sendmail("lan@bluemail.test", ["binh@doitac.test"], sig + raw)
    for _ in range(30):
        for f in sorted(glob.glob("/doitacmail/doitac.test/binh/new/*"), key=os.path.getmtime, reverse=True)[:20]:
            data = open(f, "rb").read()
            if marker.encode() in data:
                head = data.split(b"\r\n\r\n", 1)[0].split(b"\n\n", 1)[0]
                ok = b"dkim=pass" in head
                print(("PASS" if ok else "FAIL") + " Postfix Authentication-Results: " + head.split(b"Authentication-Results:", 1)[-1][:120].decode(errors="replace").replace("\n", " "))
                sys.exit(0 if ok else 1)
        time.sleep(1)
    print("FAIL message not delivered")
    sys.exit(1)


if __name__ == "__main__":
    main()
