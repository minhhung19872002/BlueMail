#!/usr/bin/env python3
"""Converts the verbatim openspf RFC 7208 test suite (YAML) to JSON for the .NET unit tests.
Usage (tools container): python /work/scripts/spf-suite-to-json.py
The JSON carries the SHA-256 of the YAML so a test can prove it was generated from the committed file."""
import hashlib
import json

import yaml

SRC = "/work/tests/BlueMail.UnitTests/Auth/Data/rfc7208-tests.yml"
DST = "/work/tests/BlueMail.UnitTests/Auth/Data/rfc7208-tests.json"


def main():
    raw = open(SRC, "rb").read()
    docs = list(yaml.safe_load_all(raw))
    out = {"source_sha256": hashlib.sha256(raw).hexdigest(), "scenarios": docs}
    with open(DST, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, indent=1, sort_keys=True, default=str)


if __name__ == "__main__":
    main()
