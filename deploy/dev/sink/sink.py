"""Phase-0 SMTP sink standing in for mx.bluemail.test until BlueMail.Mx exists.

Test-only. Saves every accepted message as an .eml file (envelope in X-Sink-* headers)
so the harness can be verified end to end: Postfix -> CoreDNS MX lookup -> this sink ->
dkimpy verification.
"""
import asyncio
import pathlib
import time
import uuid

from aiosmtpd.controller import Controller

OUT = pathlib.Path("/sink")


class Handler:
    async def handle_DATA(self, server, session, envelope):
        OUT.mkdir(parents=True, exist_ok=True)
        name = OUT / f"{int(time.time())}-{uuid.uuid4().hex[:8]}.eml"
        head = (f"X-Sink-Mail-From: {envelope.mail_from}\r\n"
                f"X-Sink-Rcpt-To: {', '.join(envelope.rcpt_tos)}\r\n"
                f"X-Sink-Peer: {session.peer[0]}\r\n").encode()
        name.write_bytes(head + envelope.original_content)
        return "250 2.0.0 Saved by test sink"


if __name__ == "__main__":
    c = Controller(Handler(), hostname="0.0.0.0", port=25, decode_data=False)
    c.start()
    print("sink listening on :25", flush=True)
    asyncio.get_event_loop().run_forever()
