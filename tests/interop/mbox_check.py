#!/usr/bin/env python3
"""Reference side of the mbox checks (Phase 10, PROMPT 11: "mbox xuất ra mở được bằng mailbox của Python").

read FILE            prints JSON: for every message Python's mailbox.mbox finds, the SHA-256 of its bytes with CRLF line
                     ends and mboxrd quoting undone (">From " -> "From ", ">>From " -> ">From "), plus its Message-ID.
write OUT FILES...   writes the given .eml files into a new mbox with Python's mailbox module.
"""
import hashlib
import json
import mailbox
import re
import sys

QUOTED = re.compile(rb"^>(>*From )", re.M)


def read(path):
    out = []
    box = mailbox.mbox(path, create=False)
    for key in box.iterkeys():
        raw = box.get_bytes(key)
        raw = QUOTED.sub(rb"\1", raw)
        raw = raw.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        # The blank line before the next "From " belongs to the file format; trailing blank lines are not message content.
        raw = raw.rstrip(b"\r\n") + b"\r\n"
        msg = box.get_message(key)
        out.append({"sha256": hashlib.sha256(raw).hexdigest(), "message_id": msg.get("Message-ID", "")})
    print(json.dumps(out))


def write(out, files):
    box = mailbox.mbox(out, create=True)
    box.lock()
    for f in files:
        with open(f, "rb") as fh:
            box.add(fh.read())
    box.flush()
    box.unlock()
    print(len(files))


if __name__ == "__main__":
    if sys.argv[1] == "read":
        read(sys.argv[2])
    else:
        write(sys.argv[2], sys.argv[3:])
