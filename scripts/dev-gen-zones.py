#!/usr/bin/env python3
"""Generate CoreDNS zone files for the BlueMail development network.

The dev network (10.77.0.0/24) hosts three test domains:
  bluemail.test, acme.test  - tenants served by BlueMail
  doitac.test               - "external" mail server (Postfix + OpenDKIM), test-only

DKIM public keys are read from deploy/dev/dkim/<domain>.public.b64 (dev-only keys).
Re-run this script whenever the keys or IP plan change.
"""
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
DKIM_DIR = ROOT / "deploy" / "dev" / "dkim"
ZONE_DIR = ROOT / "deploy" / "coredns" / "zones"

# IP plan of the dev network (keep in sync with deploy/docker-compose.dev.yml)
IP = {
    "dns": "10.77.0.53",
    "mx": "10.77.0.25",          # BlueMail.Mx (inbound SMTP)
    "outbound": "10.77.0.26",    # BlueMail.Outbound (outbound SMTP source address)
    "submission": "10.77.0.27",  # BlueMail.Submission
    "imap": "10.77.0.28",        # BlueMail.Imap
    "web": "10.77.0.20",         # Caddy (web, API, mta-sts)
    "postfix": "10.77.0.30",     # doitac.test
}
SERIAL = "2026112301"
DKIM_SELECTOR = {"bluemail.test": "bm2026", "acme.test": "bm2026", "doitac.test": "dt2026"}


def txt(value: str) -> str:
    """Split a TXT value into <=255 byte character-strings (RFC 1035 3.3.14)."""
    chunks = [value[i:i + 250] for i in range(0, len(value), 250)]
    return " ".join(f'"{c}"' for c in chunks)


def soa(origin: str) -> str:
    return (f"$ORIGIN {origin}.\n$TTL 300\n"
            f"@ IN SOA ns1.{origin}. hostmaster.{origin}. {SERIAL} 3600 600 86400 60\n"
            f"@ IN NS ns1.{origin}.\nns1 IN A {IP['dns']}\n")


def dkim_record(domain: str) -> str:
    pub = (DKIM_DIR / f"{domain}.public.b64").read_text().strip()
    return f"{DKIM_SELECTOR[domain]}._domainkey IN TXT {txt('v=DKIM1; k=rsa; p=' + pub)}\n"


def bluemail_tenant_zone(domain: str) -> str:
    z = soa(domain)
    z += f"@ IN MX 10 mx.bluemail.test.\n"
    # Customer domains include the platform's SPF record, as the domain wizard tells them to (docs/06 P9).
    z += f"@ IN TXT {txt('v=spf1 include:_spf.bluemail.test -all')}\n"
    if domain == "bluemail.test":
        z += f"_spf IN TXT {txt('v=spf1 ip4:' + IP['outbound'] + ' ip4:' + IP['mx'] + ' -all')}\n"
    z += f"_dmarc IN TXT {txt('v=DMARC1; p=quarantine; rua=mailto:dmarc-reports@bluemail.test; adkim=r; aspf=r')}\n"
    z += dkim_record(domain)
    z += f"mta-sts IN A {IP['web']}\n"
    z += f"_mta-sts IN TXT {txt('v=STSv1; id=' + SERIAL)}\n"
    z += f"_smtp._tls IN TXT {txt('v=TLSRPTv1; rua=mailto:tlsrpt@bluemail.test')}\n"
    z += f"autoconfig IN A {IP['web']}\n"
    z += f"_imaps._tcp IN SRV 0 1 993 imap.bluemail.test.\n"
    z += f"_submissions._tcp IN SRV 0 1 465 smtp.bluemail.test.\n"
    if domain == "bluemail.test":
        z += f"mx IN A {IP['mx']}\n"
        z += f"out1 IN A {IP['outbound']}\n"
        z += f"smtp IN A {IP['submission']}\n"
        z += f"imap IN A {IP['imap']}\n"
        z += f"mail IN A {IP['web']}\n"
        z += f"id IN A {IP['web']}\n"
        # Intentionally CNAME chain for DNS client tests (CNAME -> CNAME -> A)
        z += "alias1 IN CNAME alias2.bluemail.test.\nalias2 IN CNAME mx.bluemail.test.\n"
        # Large TXT RRset to force UDP truncation (TC) -> TCP fallback in DNS client tests
        for i in range(40):
            z += f"bigtxt IN TXT {txt(f'filler-record-{i:02d}-' + 'x' * 60)}\n"
    return z


def doitac_zone() -> str:
    d = "doitac.test"
    z = soa(d)
    z += f"@ IN MX 10 mail.doitac.test.\n"
    z += f"mail IN A {IP['postfix']}\n"
    z += f"@ IN TXT {txt('v=spf1 ip4:' + IP['postfix'] + ' -all')}\n"
    z += f"_dmarc IN TXT {txt('v=DMARC1; p=reject; adkim=s; aspf=s')}\n"
    z += dkim_record(d)
    # A domain with null MX (RFC 7505) and one without MX (implicit A fallback)
    z += "nullmx IN MX 0 .\n"
    z += f"nomx IN A {IP['postfix']}\n"
    return z


def reverse_zone() -> str:
    z = ("$ORIGIN 0.77.10.in-addr.arpa.\n$TTL 300\n"
         f"@ IN SOA ns1.bluemail.test. hostmaster.bluemail.test. {SERIAL} 3600 600 86400 60\n"
         "@ IN NS ns1.bluemail.test.\n")
    ptr = {"25": "mx.bluemail.test.", "26": "out1.bluemail.test.", "27": "smtp.bluemail.test.",
           "28": "imap.bluemail.test.", "30": "mail.doitac.test.", "53": "ns1.bluemail.test."}
    for last, name in ptr.items():
        z += f"{last} IN PTR {name}\n"
    return z


def main() -> None:
    ZONE_DIR.mkdir(parents=True, exist_ok=True)
    files = {
        "bluemail.test.db": bluemail_tenant_zone("bluemail.test"),
        "acme.test.db": bluemail_tenant_zone("acme.test"),
        "doitac.test.db": doitac_zone(),
        "0.77.10.in-addr.arpa.db": reverse_zone(),
    }
    for name, content in files.items():
        (ZONE_DIR / name).write_text(content, newline="\n")
        print(f"wrote {name}")


if __name__ == "__main__":
    main()
