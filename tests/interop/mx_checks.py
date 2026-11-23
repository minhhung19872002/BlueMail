"""Phase 3 acceptance checks against BlueMail.Mx (PROMPT 2.1, section 13 Phase 3).

Runs in the tools container (python smtplib + raw sockets as the reference SMTP client):
    docker exec bluemail-dev-tools-1 python /work/tests/interop/mx_checks.py [marker]
Prints one "PASS name" / "FAIL name: detail" line per check and a JSON summary; exit code 1 on any failure.
Messages are sent with a unique marker in the Subject so scripts/dev-phase3-check.sh can verify the database.
"""
import base64
import json
import smtplib
import socket
import ssl
import sys
import time
from email.message import EmailMessage

MX = "10.77.0.25"
results = []


def check(name, fn):
    try:
        detail = fn()
        ok = detail is True or detail is None
        results.append({"name": name, "ok": ok, "detail": None if ok else str(detail)})
    except Exception as e:  # report and continue: every check is independent
        results.append({"name": name, "ok": False, "detail": f"{type(e).__name__}: {e}"})
    r = results[-1]
    print(("PASS " if r["ok"] else "FAIL ") + name + ("" if r["ok"] else ": " + r["detail"]), flush=True)


def message(subject, to="lan@bluemail.test", sender="tester@example.test", body="test body"):
    m = EmailMessage()
    m["From"] = sender
    m["To"] = to
    m["Subject"] = subject
    m["Message-ID"] = f"<{time.time_ns()}@example.test>"
    m.set_content(body)
    return m


def rcpt_code(rcpt, sender="tester@example.test"):
    with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=30) as s:
        s.ehlo()
        s.mail(sender)
        code, _ = s.rcpt(rcpt)
        return code


class Raw:
    """Line-level SMTP client for what smtplib cannot do (BDAT, injection, pre-greet)."""

    def __init__(self, wait_banner=True):
        self.sock = socket.create_connection((MX, 25), timeout=30)
        self.buf = b""
        if wait_banner:
            self.banner = self.reply()

    def send(self, data):
        self.sock.sendall(data if isinstance(data, bytes) else data.encode())

    def reply(self):
        lines = []
        while True:
            while b"\r\n" not in self.buf:
                chunk = self.sock.recv(65536)
                if not chunk:
                    raise EOFError("connection closed; got " + repr(lines))
                self.buf += chunk
            line, self.buf = self.buf.split(b"\r\n", 1)
            lines.append(line.decode("utf-8", "replace"))
            if len(line) >= 4 and line[3:4] == b" ":
                return int(line[:3]), lines

    def cmd(self, line):
        self.send(line + "\r\n")
        return self.reply()

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


def main():
    marker = sys.argv[1] if len(sys.argv) > 1 else str(int(time.time()))

    check("no open relay: RCPT to a foreign domain is 550 5.7.1", lambda: rcpt_code("someone@example.org") == 550 or "not 550")
    check("unknown user is refused at RCPT with 550", lambda: rcpt_code("no.such.user@bluemail.test") == 550 or "not 550")
    check("alias and group addresses are accepted at RCPT",
          lambda: (rcpt_code("sales@bluemail.test"), rcpt_code("kinhdoanh@bluemail.test")) == (250, 250) or "not 250")
    check("sub-address lan+news@ is accepted", lambda: rcpt_code("lan+news@bluemail.test") == 250 or "not 250")
    check("internal-only group refuses an outside sender (550 5.7.1)", lambda: rcpt_code("kythuat@bluemail.test") == 550 or "not 550")

    def declared_size():
        with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=30) as s:
            s.ehlo()
            limit = int(s.esmtp_features["size"])
            code, _ = s.mail("tester@example.test", ["SIZE=%d" % (limit + 1)])
            return code == 552 or f"MAIL with SIZE over {limit} gave {code}"
    check("SIZE over the limit is refused at MAIL (552)", declared_size)

    def actual_oversize():
        with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=120) as s:
            s.ehlo()
            limit = int(s.esmtp_features["size"])
            s.mail("tester@example.test")
            s.rcpt("lan@bluemail.test")
            line = b"x" * 998 + b"\r\n"
            body = b"Subject: big " + marker.encode() + b"\r\n\r\n" + line * (limit // len(line) + 10)
            try:
                code, _ = s.data(body)
            except smtplib.SMTPDataError as e:
                code = e.smtp_code
            return code == 552 or f"oversized DATA gave {code}"
    check("message over the limit is refused after DATA (552), nothing stored", actual_oversize)

    def deliver_utf8():
        with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=30) as s:
            s.ehlo()
            if not s.has_extn("smtputf8"):
                return "SMTPUTF8 not advertised"
            m = message(f"Tiếng Việt có dấu {marker}-utf8", to="lan@bluemail.test", sender="người.gửi@ví-dụ.test")
            s.send_message(m, from_addr="người.gửi@ví-dụ.test", to_addrs=["lan@bluemail.test"], mail_options=["SMTPUTF8"])
    check("SMTPUTF8: UTF-8 envelope and headers are accepted", deliver_utf8)

    def utf8_without_extension():
        r = Raw()
        r.cmd("EHLO tools.example.test")
        code, _ = r.cmd("MAIL FROM:<người.gửi@ví-dụ.test>")
        r.close()
        return code == 553 or f"gave {code}"
    check("UTF-8 address without SMTPUTF8 parameter is 553", utf8_without_extension)

    def bdat():
        r = Raw()
        r.cmd("EHLO tools.example.test")
        r.cmd("MAIL FROM:<tester@example.test>")
        r.cmd("RCPT TO:<lan@bluemail.test>")
        msg = (f"From: tester@example.test\r\nTo: lan@bluemail.test\r\nSubject: bdat {marker}\r\n"
               f"Message-ID: <bdat-{marker}@example.test>\r\n\r\n.line starting with a dot\r\n").encode()
        half = len(msg) // 2
        r.send(b"BDAT %d\r\n" % half + msg[:half])
        c1, _ = r.reply()
        r.send(b"BDAT %d LAST\r\n" % (len(msg) - half) + msg[half:])
        c2, _ = r.reply()
        r.cmd("QUIT")
        r.close()
        return (c1, c2) == (250, 250) or f"chunk replies {c1}, {c2}"
    check("BDAT (CHUNKING) in two chunks is delivered", bdat)

    def pipelining():
        r = Raw()
        r.cmd("EHLO tools.example.test")
        r.send("MAIL FROM:<tester@example.test>\r\nRCPT TO:<lan@bluemail.test>\r\nRCPT TO:<nobody@bluemail.test>\r\nDATA\r\n")
        codes = [r.reply()[0] for _ in range(4)]
        body = f"From: tester@example.test\r\nSubject: pipelined {marker}\r\nMessage-ID: <pipe-{marker}@example.test>\r\n\r\nhello\r\n.\r\n"
        r.send(body)
        codes.append(r.reply()[0])
        r.close()
        return codes == [250, 250, 550, 354, 250] or f"codes {codes}"
    check("PIPELINING: grouped commands get ordered replies", pipelining)

    def starttls_injection():
        r = Raw()
        r.cmd("EHLO tools.example.test")
        # Plaintext injected after STARTTLS in the same packet must be discarded, never answered inside TLS.
        r.send("STARTTLS\r\nMAIL FROM:<evil@example.test>\r\n")
        code, _ = r.reply()
        if code != 220:
            return f"STARTTLS gave {code}"
        if r.buf:
            return "server sent data after 220"
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE  # opportunistic TLS between MTAs (RFC 7435)
        r.sock = ctx.wrap_socket(r.sock, server_hostname="mx.bluemail.test")
        version = r.sock.version()
        c, _ = r.cmd("NOOP")
        if c != 250:
            return f"first reply inside TLS was {c}: the injected command was executed"
        c, lines = r.cmd("EHLO tools.example.test")
        if any("STARTTLS" in l for l in lines):
            return "STARTTLS still offered inside TLS"
        c, _ = r.cmd("MAIL FROM:<tester@example.test>")
        r.close()
        return (c == 250 and version in ("TLSv1.2", "TLSv1.3")) or f"MAIL inside TLS gave {c} over {version}"
    check("STARTTLS: TLS 1.2+, injected plaintext discarded", starttls_injection)

    def pregreet():
        r = Raw(wait_banner=False)
        r.send("EHLO tools.example.test\r\n")
        code, _ = r.reply()
        r.close()
        return code == 554 or f"early talker got {code}"
    check("client talking before the greeting is refused (554)", pregreet)

    def dmarc_reject():
        # doitac.test publishes p=reject; this host is not in its SPF and the message is unsigned.
        with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=30) as s:
            s.ehlo()
            try:
                s.send_message(message(f"spoof {marker}", sender="an@doitac.test"), from_addr="an@doitac.test", to_addrs=["lan@bluemail.test"])
                return "spoofed message was accepted"
            except smtplib.SMTPDataError as e:
                return (e.smtp_code == 550 and b"DMARC" in e.smtp_error) or f"{e.smtp_code} {e.smtp_error!r}"
    check("DMARC p=reject spoof is refused in session (550 5.7.1)", dmarc_reject)

    def malware():
        m = message(f"malware {marker}")
        # A Windows executable disguised as a PDF (PE header): blocked by scanner layer 1/2.
        pe = b"MZ" + b"\x90" * 58 + b"\x80\x00\x00\x00" + b"\x00" * 64 + b"PE\x00\x00" + b"\x4c\x01" + b"\x00" * 200
        m.add_attachment(pe, maintype="application", subtype="pdf", filename="hoa-don.pdf.exe")
        with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=30) as s:
            s.ehlo()
            try:
                s.send_message(m, to_addrs=["lan@bluemail.test"])
                return "dangerous attachment was accepted"
            except smtplib.SMTPDataError as e:
                return e.smtp_code == 554 or f"{e.smtp_code} {e.smtp_error!r}"
    check("dangerous attachment is refused in session (554 5.7.1)", malware)

    def group_and_alias():
        with smtplib.SMTP(MX, 25, local_hostname="tools.example.test", timeout=30) as s:
            s.ehlo()
            s.send_message(message(f"group {marker}", to="kinhdoanh@bluemail.test"), to_addrs=["kinhdoanh@bluemail.test", "minh@bluemail.test", "sales@bluemail.test"])
    check("group + member + alias in one transaction is accepted", group_and_alias)

    def too_many_errors():
        r = Raw()
        codes = []
        try:
            for _ in range(12):
                codes.append(r.cmd("BOGUS")[0])
                while b"\r\n" in r.buf:  # the final error and the 421 arrive together
                    codes.append(r.reply()[0])
                if codes[-1] == 421:
                    break
            r.reply()
            return "connection still open after 421"
        except (EOFError, BrokenPipeError, ConnectionResetError):
            return (421 in codes) or f"closed without 421 after {codes}"
        finally:
            r.close()
    check("repeated bad commands close the connection (421)", too_many_errors)

    ok = all(r["ok"] for r in results)
    print(json.dumps({"marker": marker, "passed": sum(r["ok"] for r in results), "total": len(results)}))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
