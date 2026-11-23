#!/usr/bin/env python3
"""PROMPT 8.2 ingestion check: sustained SMTP delivery to BlueMail.Mx, time from end of DATA to the 250 reply.

Usage (tools container): smtp_load.py MX_HOST RATE_PER_S SECONDS CONCURRENCY RCPT[,RCPT...] [SIZE_KB]
Sends at RATE_PER_S (token bucket) over CONCURRENCY reused connections; prints JSON with the achieved rate, p50/p95/max
latency of the final reply, and failures. Every message is distinct (unique Message-ID and body).
"""
import json
import smtplib
import sys
import threading
import time
import uuid
from email.utils import formatdate

HOST, RATE, SECONDS, CONC = sys.argv[1], float(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
RCPTS = sys.argv[5].split(",")
SIZE = int(sys.argv[6]) if len(sys.argv) > 6 else 20

lock = threading.Lock()
latencies, failures = [], []
issued = 0
start = time.monotonic()
words = "invoice contract meeting report budget project schedule review proposal delivery shipment order payment".split()


def take():
    """Token bucket: returns False when the run is over."""
    global issued
    while True:
        with lock:
            now = time.monotonic()
            if now - start >= SECONDS:
                return False
            if issued < (now - start) * RATE:
                issued += 1
                return True
        time.sleep(0.002)


def message(n):
    body_line = " ".join(words[(n + i) % len(words)] for i in range(12))
    body = "\r\n".join(f"{body_line} {n}-{i}" for i in range(SIZE * 1024 // 100))
    return (f"From: Load Test <load@perf.example>\r\nTo: {RCPTS[n % len(RCPTS)]}\r\nSubject: Load {n} {words[n % len(words)]}\r\n"
            f"Date: {formatdate()}\r\nMessage-ID: <{uuid.uuid4().hex}@perf.example>\r\nMIME-Version: 1.0\r\n"
            f"Content-Type: text/plain; charset=utf-8\r\n\r\n{body}\r\n").encode()


def worker(w):
    n = w
    s = None
    while take():
        try:
            if s is None:
                s = smtplib.SMTP(HOST, 25, local_hostname="load.perf.example", timeout=60)
                s.ehlo()
            msg = message(n)
            s.mail("load@perf.example")
            code, _ = s.rcpt(RCPTS[n % len(RCPTS)])
            if code != 250:
                raise smtplib.SMTPException(f"rcpt {code}")
            t0 = time.monotonic()
            code, reply = s.data(msg)
            dt = time.monotonic() - t0
            with lock:
                if code == 250:
                    latencies.append(dt)
                else:
                    failures.append(f"{code} {reply[:80]!r}")
        except (smtplib.SMTPException, OSError) as e:
            with lock:
                failures.append(str(e)[:120])
            s = None
        n += CONC
    if s is not None:
        try:
            s.quit()
        except (smtplib.SMTPException, OSError):
            pass


threads = [threading.Thread(target=worker, args=(i,)) for i in range(CONC)]
for t in threads:
    t.start()
for t in threads:
    t.join()
elapsed = time.monotonic() - start
lat = sorted(latencies)


def pct(p):
    return round(lat[min(len(lat) - 1, max(0, int(p * len(lat) + 0.999) - 1))] * 1000) if lat else None


print(json.dumps({"target_rate": RATE, "seconds": round(elapsed, 1), "delivered": len(lat), "rate": round(len(lat) / elapsed, 1),
                  "p50_ms": pct(0.5), "p95_ms": pct(0.95), "max_ms": round(lat[-1] * 1000) if lat else None,
                  "failures": len(failures), "failure_samples": failures[:5]}))
