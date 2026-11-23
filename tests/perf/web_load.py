#!/usr/bin/env python3
"""PROMPT 8.2 "500 concurrent web users": virtual users doing what the web app does, against the HTTP API.

Usage: web_load.py BASE_URL TOKEN_FILE USERS SECONDS
Each user keeps one HTTP connection and loops: open the Inbox (list), open a conversation, read a message body,
sometimes search or reload the labels, with 1-3 s of think time between steps (a person reading mail).
Prints JSON: requests/s, p50/p95/max per kind of request, errors. The API must allow the rate from one address
(BM_API_RATE_PER_MINUTE), since every virtual user comes from this machine.
"""
import http.client
import json
import pathlib
import random
import sys
import threading
import time
import urllib.parse

BASE, TOKEN, USERS, SECONDS = sys.argv[1], pathlib.Path(sys.argv[2]).read_text().strip().strip('"'), int(sys.argv[3]), int(sys.argv[4])
url = urllib.parse.urlparse(BASE)
QUERIES = ["invoice", "hợp đồng", "from:an@doitac.test", "has:attachment", "is:unread", "báo giá", "meeting"]
lock = threading.Lock()
times: dict[str, list[float]] = {}
errors: list[str] = []
stop_at = time.monotonic() + SECONDS


def run(user: int) -> None:
    rnd = random.Random(user)
    conn = http.client.HTTPConnection(url.hostname, url.port, timeout=60)
    headers = {"Authorization": "Bearer " + TOKEN, "Accept": "application/json", "Accept-Encoding": "identity"}

    def get(kind: str, path: str):
        nonlocal conn
        t0 = time.perf_counter()
        try:
            conn.request("GET", path, headers=headers)
            r = conn.getresponse()
            body = r.read()
            ms = (time.perf_counter() - t0) * 1000
            with lock:
                times.setdefault(kind, []).append(ms)
                if r.status != 200:
                    errors.append(f"{kind} {r.status}")
            return json.loads(body)["data"] if r.status == 200 else None
        except (OSError, http.client.HTTPException, ValueError) as e:
            with lock:
                errors.append(f"{kind} {type(e).__name__}")
            conn.close()
            conn = http.client.HTTPConnection(url.hostname, url.port, timeout=60)
            return None

    time.sleep(rnd.random() * 3)  # users do not all start in the same second
    labels = get("labels", "/api/v1/mail/labels")
    inbox = next((l["id"] for l in (labels or {}).get("labels", []) if l["systemKey"] == "INBOX"), None)
    while time.monotonic() < stop_at:
        page = get("inbox", f"/api/v1/mail/conversations?limit=50&labelId={inbox}")
        time.sleep(rnd.uniform(1, 3))
        items = (page or {}).get("items") or []
        if items:
            conv = rnd.choice(items)
            thread = get("thread", f"/api/v1/mail/threads/{conv['threadId']}")
            msgs = (thread or {}).get("messages") or []
            if msgs:
                get("body", f"/api/v1/mail/messages/{msgs[-1]['message']['id']}/body")
            time.sleep(rnd.uniform(1, 3))
        r = rnd.random()
        if r < 0.25:
            get("search", "/api/v1/mail/conversations?limit=50&view=all&q=" + urllib.parse.quote(rnd.choice(QUERIES)))
            time.sleep(rnd.uniform(1, 3))
        elif r < 0.35:
            get("labels", "/api/v1/mail/labels")


start = time.monotonic()
threads = [threading.Thread(target=run, args=(i,), daemon=True) for i in range(USERS)]
for t in threads:
    t.start()
for t in threads:
    t.join()
elapsed = time.monotonic() - start


def stats(xs):
    s = sorted(xs)
    at = lambda p: s[min(len(s) - 1, max(0, int(p * len(s) + 0.999) - 1))]  # noqa: E731
    return {"n": len(s), "p50": round(at(0.5)), "p95": round(at(0.95)), "max": round(s[-1])}


total = sum(len(v) for v in times.values())
print(json.dumps({"users": USERS, "seconds": round(elapsed), "requests": total, "per_second": round(total / elapsed, 1),
                  "all": stats([x for v in times.values() for x in v]), "by_kind": {k: stats(v) for k, v in sorted(times.items())},
                  "errors": len(errors), "error_samples": sorted(set(errors))[:8]}))
