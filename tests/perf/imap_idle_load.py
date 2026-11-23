#!/usr/bin/env python3
"""PROMPT 8.2: many simultaneous IMAP IDLE connections to BlueMail.Imap, and how fast they hear about new mail.

Usage (tools container): imap_idle_load.py IMAP_HOST USER PASSWORD CONNECTIONS MX_HOST
Opens CONNECTIONS TLS connections (port 993, dev certificate), logs in, SELECTs INBOX and IDLEs; then delivers one
message through MX_HOST:25 and measures, per connection, the delay until its "* n EXISTS". Prints JSON.
"""
import asyncio
import json
import smtplib
import ssl
import sys
import time
import uuid

HOST, USER, PASSWORD, N, MX = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5]
CTX = ssl.create_default_context(cafile="/work/deploy/dev/tls/cert.pem")


async def expect(reader, tag):
    while True:
        line = await reader.readline()
        if not line:
            raise ConnectionError("closed")
        if line.startswith(tag.encode()):
            if b" OK" not in line:
                raise ConnectionError(line.decode(errors="replace").strip())
            return


async def session(i, ready, go, results, sem):
    try:
        async with sem:  # connect and log in a bounded number at a time
            reader, writer = await asyncio.wait_for(asyncio.open_connection(HOST, 993, ssl=CTX, server_hostname=HOST, limit=1 << 20), 120)
            await reader.readline()
            writer.write(f'a1 LOGIN "{USER}" "{PASSWORD}"\r\n'.encode())
            await expect(reader, "a1")
            writer.write(b"a2 SELECT INBOX\r\n")
            await expect(reader, "a2")
            writer.write(b"a3 IDLE\r\n")
            await reader.readline()  # "+ idling"
        ready.append(i)
        await go.wait()
        while True:
            line = await asyncio.wait_for(reader.readline(), 60)
            if b"EXISTS" in line:
                results.append(time.monotonic() - go.started)
                break
        writer.write(b"DONE\r\n")
        writer.close()
    except Exception as e:  # noqa: BLE001 - every failure is counted and reported
        results.append(("error", type(e).__name__, str(e)[:80]))


async def main():
    ready, results = [], []
    go = asyncio.Event()
    sem = asyncio.Semaphore(100)  # connect/login 100 at a time

    t_open = time.monotonic()
    tasks = [asyncio.create_task(session(i, ready, go, results, sem)) for i in range(N)]
    while len(ready) + sum(1 for r in results if isinstance(r, tuple)) < N and time.monotonic() - t_open < 600:
        await asyncio.sleep(1)
    opened = time.monotonic() - t_open
    idle = len(ready)
    with smtplib.SMTP(MX, 25, local_hostname="idle.perf.example", timeout=60) as s:
        go.started = time.monotonic()
        go.set()
        s.sendmail("idle@perf.example", [USER], f"From: idle@perf.example\r\nTo: {USER}\r\nSubject: idle {uuid.uuid4().hex}\r\n\r\nhi\r\n".encode())
    await asyncio.wait(tasks, timeout=120)
    lat = sorted(r for r in results if isinstance(r, float))
    errors = [r for r in results if isinstance(r, tuple)]

    def pct(p):
        return round(lat[min(len(lat) - 1, int(p * len(lat) + 0.999) - 1)] * 1000) if lat else None

    print(json.dumps({"connections": N, "idling": idle, "seconds_to_open": round(opened, 1), "notified": len(lat),
                      "p50_ms": pct(0.5), "p95_ms": pct(0.95), "max_ms": round(lat[-1] * 1000) if lat else None,
                      "errors": len(errors), "error_samples": errors[:5]}))


asyncio.run(main())
