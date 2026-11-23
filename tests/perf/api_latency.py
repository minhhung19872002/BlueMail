#!/usr/bin/env python3
"""PROMPT 8.2 latency through the HTTP API (same calls as the web app), outside the browser.

Usage: api_latency.py BASE_URL TOKEN_FILE RUNS [inbox|search]
TOKEN_FILE holds the access token of a signed-in user (e.g. copied from the web app's sessionStorage).
Prints JSON: p50/p95/max per measurement, and the worst time of every search query.
"""
import json
import pathlib
import sys
import time
import urllib.parse
import urllib.request

BASE, TOKEN, RUNS = sys.argv[1].rstrip("/"), pathlib.Path(sys.argv[2]).read_text().strip().strip('"'), int(sys.argv[3])
WHAT = sys.argv[4] if len(sys.argv) > 4 else "both"
QUERIES = ["invoice", "hợp đồng", "from:sender17@partner3.example", "subject:budget review", "tokyo meeting -berlin", "has:attachment payment",
           "after:2025/01/01 before:2025/06/30 contract", "\"project schedule\"", "nonexistentword12345", "is:starred security"]


def get(path):
    t0 = time.perf_counter()
    req = urllib.request.Request(BASE + path, headers={"Authorization": "Bearer " + TOKEN, "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        body = json.loads(r.read())
    return (time.perf_counter() - t0) * 1000, body["data"]


def stats(xs):
    s = sorted(xs)
    at = lambda p: s[min(len(s) - 1, max(0, int(p * len(s) + 0.999) - 1))]  # noqa: E731
    return {"n": len(s), "p50": round(at(0.5)), "p95": round(at(0.95)), "max": round(s[-1])}


out = {}
labels = get("/api/v1/mail/labels")[1]["labels"]
inbox = next(l for l in labels if l["systemKey"] == "INBOX")
out["inboxMessages"] = inbox["total"]
if WHAT in ("both", "inbox"):
    out["inbox"] = stats([get(f"/api/v1/mail/conversations?limit=50&labelId={inbox['id']}")[0] for _ in range(RUNS)])
if WHAT in ("both", "search"):
    per, times = {}, []
    for i in range(RUNS):
        q = QUERIES[i % len(QUERIES)]
        ms = get("/api/v1/mail/conversations?limit=50&view=all&q=" + urllib.parse.quote(q))[0]
        times.append(ms)
        per[q] = max(per.get(q, 0), round(ms))
    out["search"] = stats(times)
    out["searchWorst"] = per
print(json.dumps(out, ensure_ascii=False))
