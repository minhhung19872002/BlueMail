"""Dev only: a DNSSEC key for the doitac.test zone, so CoreDNS signs it (dnssec plugin) and BlueMail.Outbound can
validate DNSSEC and use DANE against it with a private trust anchor (BM_DNSSEC_ANCHORS).

Run once in the tools container (needs dnspython + cryptography):
    docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev exec -T tools python /work/scripts/dev-gen-dnssec.py
Writes deploy/coredns/keys/Kdoitac.test.+013+<tag>.{key,private} (a dev key, committed like the other dev keys, docs/00 D12)
and prints the anchor line for BM_DNSSEC_ANCHORS. The key is reused when it already exists.
"""
import base64
import glob
import os

import dns.dnssec
import dns.name
import dns.rdata
import dns.rdataclass
import dns.rdatatype
from cryptography.hazmat.primitives.asymmetric import ec

ZONE = "doitac.test."
KEYS = os.path.join(os.path.dirname(__file__), "..", "deploy", "coredns", "keys")


def main() -> None:
    os.makedirs(KEYS, exist_ok=True)
    existing = glob.glob(os.path.join(KEYS, "Kdoitac.test.+013+*.key"))
    if existing:
        line = open(existing[0], encoding="ascii").read().split()
        dnskey = dns.rdata.from_text(dns.rdataclass.IN, dns.rdatatype.DNSKEY, " ".join(line[3:]))
    else:
        key = ec.generate_private_key(ec.SECP256R1())
        dnskey = dns.dnssec.make_dnskey(key.public_key(), dns.dnssec.Algorithm.ECDSAP256SHA256, flags=257)
        tag = dns.dnssec.key_id(dnskey)
        base = os.path.join(KEYS, f"Kdoitac.test.+013+{tag:05d}")
        with open(base + ".key", "w", encoding="ascii", newline="\n") as f:
            f.write(f"{ZONE} IN DNSKEY {dnskey.to_text()}\n")
        d = key.private_numbers().private_value.to_bytes(32, "big")
        with open(base + ".private", "w", encoding="ascii", newline="\n") as f:
            f.write("Private-key-format: v1.3\nAlgorithm: 13 (ECDSAP256SHA256)\n")
            f.write("PrivateKey: " + base64.b64encode(d).decode() + "\n")
    ds = dns.dnssec.make_ds(dns.name.from_text(ZONE), dnskey, "SHA256")
    print(f"doitac.test {ds.key_tag} {ds.algorithm} {ds.digest_type} {ds.digest.hex().upper()}")


if __name__ == "__main__":
    main()
