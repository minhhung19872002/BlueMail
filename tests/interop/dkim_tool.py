#!/usr/bin/env python3
"""dkimpy wrapper for interop tests (independent DKIM/ARC implementation).

  verify <file>                                     -> prints "pass" or "fail"
  sign <in> <out> <domain> <selector> <key> <canon> -> writes the signed message (canon e.g. relaxed/simple)
  arc-verify <file>                                 -> prints the ARC chain result (pass/fail/none)
  arc-sign <in> <out> <domain> <selector> <key> <authserv-id>
"""
import subprocess
import sys

import dkim


def load_key(path):
    """dkimpy reads PKCS#1 ("BEGIN RSA PRIVATE KEY") only; convert PKCS#8 keys with openssl."""
    pem = open(path, "rb").read()
    if b"BEGIN RSA PRIVATE KEY" in pem:
        return pem
    return subprocess.run(["openssl", "rsa", "-traditional", "-in", path], capture_output=True, check=True).stdout


def main():
    cmd = sys.argv[1]
    if cmd == "verify":
        print("pass" if dkim.verify(open(sys.argv[2], "rb").read()) else "fail")
    elif cmd == "sign":
        src, dst, domain, selector, key, canon = sys.argv[2:8]
        msg = open(src, "rb").read()
        h, b = canon.split("/")
        sig = dkim.sign(msg, selector.encode(), domain.encode(), load_key(key),
                        canonicalize=(h.encode(), b.encode()),
                        include_headers=[b"from", b"to", b"subject", b"date", b"message-id"])
        open(dst, "wb").write(sig + msg)
    elif cmd == "arc-verify":
        cv, results, comment = dkim.arc_verify(open(sys.argv[2], "rb").read())
        print(cv.decode())
    elif cmd == "arc-sign":
        src, dst, domain, selector, key, srv_id = sys.argv[2:8]
        msg = open(src, "rb").read()
        headers = dkim.arc_sign(msg, selector.encode(), domain.encode(), load_key(key), srv_id.encode(),
                                include_headers=[b"from", b"to", b"subject", b"date", b"message-id"])
        if not headers:
            raise SystemExit("dkimpy produced no ARC set (Authentication-Results for the authserv-id missing?)")
        open(dst, "wb").write(b"".join(headers) + msg)
    else:
        raise SystemExit("unknown command")


if __name__ == "__main__":
    main()
