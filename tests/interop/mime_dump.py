#!/usr/bin/env python3
"""Reference dump of the MIME corpus with Python's email package (independent implementation).

Run in the tools container:  python /work/tests/interop/mime_dump.py /work/tests/BlueMail.Corpus/messages
Prints one JSON document {file: summary}. BlueMail.InteropTests produces the same shape with BlueMail.Mime
and compares field by field.
"""
import email
import email.policy
import hashlib
import json
import pathlib
import sys


def addresses(msg, name):
    out = []
    for header in msg.get_all(name) or []:
        try:
            for a in header.addresses:
                out.append([a.display_name or None, a.addr_spec])
        except Exception:  # noqa: BLE001 - reference tool: record failure as None
            return None
    return out


def safe(fn):
    try:
        return fn()
    except Exception:  # noqa: BLE001
        return None


def summarize(path):
    raw = path.read_bytes()
    msg = email.message_from_bytes(raw, policy=email.policy.default)
    parts = []
    for part in msg.walk():
        entry = {"type": part.get_content_type(), "filename": safe(part.get_filename)}
        if not part.is_multipart():
            payload = part.get_payload(decode=True) or b""
            entry["sha256"] = hashlib.sha256(payload).hexdigest()
            if part.get_content_maintype() == "text":
                text = safe(part.get_content)
                entry["text"] = text.replace("\r\n", "\n") if isinstance(text, str) else None
        parts.append(entry)
    date = safe(lambda: msg["date"].datetime)
    return {
        "subject": safe(lambda: str(msg["subject"])) if msg["subject"] is not None else None,
        "from": addresses(msg, "from"),
        "to": addresses(msg, "to"),
        "cc": addresses(msg, "cc"),
        "date": date.astimezone(__import__("datetime").timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ") if date and date.tzinfo else None,
        "message_id": safe(lambda: str(msg["message-id"]).strip().strip("<>")) if msg["message-id"] else None,
        "parts": parts,
    }


def main():
    root = pathlib.Path(sys.argv[1])
    result = {}
    for p in sorted(root.rglob("*.eml")):
        result[f"{p.parent.name}/{p.name}"] = summarize(p)
    json.dump(result, sys.stdout, ensure_ascii=False, indent=1, sort_keys=True)


if __name__ == "__main__":
    main()
