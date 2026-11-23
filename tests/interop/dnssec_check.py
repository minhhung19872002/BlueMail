#!/usr/bin/env python3
"""Reference DNSSEC verdicts with dnspython (Phase 12 acceptance: BlueMail.Dns must agree on real signed zones).

Usage: dnssec_check.py RESOLVER [--anchor "zone keytag alg digesttype hex"] NAME:TYPE ...
Prints JSON {"NAME:TYPE": "Secure" | "Insecure" | "Bogus"}.

The chain is walked from the root trust anchors (or the given anchor): at every label a DS RRset signed by the parent
leads to a DNSKEY RRset that must be self-signed by a key matching the DS; a label without DS but with its own SOA is an
unsigned delegation (Insecure). The final RRset (or, for an empty answer, the NSEC/NSEC3 records) must be signed by the
zone's keys. Signatures are checked with dns.dnssec.validate (dnspython + cryptography), independent of BlueMail.
"""
import json
import sys

import dns.dnssec
import dns.flags
import dns.message
import dns.name
import dns.query
import dns.rdatatype
import dns.rrset

ROOT_DS = [
    "20326 8 2 E06D44B80B8F1D39A95C0B0D7C65D08458E880409BBC683457104237C7F8EC8D",
    "38696 8 2 683D2D0ACB8C9B712A1948B27F741219298D0A450D612C483AF444A4C0FB2B16",
]


class Bogus(Exception):
    pass


def query(server, port, name, rdtype):
    q = dns.message.make_query(name, rdtype, want_dnssec=True)
    q.flags |= dns.flags.CD
    r = dns.query.udp(q, server, port=port, timeout=5)
    if r.flags & dns.flags.TC:
        r = dns.query.tcp(q, server, port=port, timeout=5)
    return r


def split(section, owner, rdtype):
    data = next((s for s in section if s.name == owner and s.rdtype == rdtype), None)
    sig = next((s for s in section if s.name == owner and s.rdtype == dns.rdatatype.RRSIG and s.covers == rdtype), None)
    return data, sig


def verify(rrset, sig, zone, keys):
    if rrset is None or sig is None:
        raise Bogus(f"unsigned {rrset.name if rrset else '?'}")
    try:
        dns.dnssec.validate(rrset, sig, {zone: keys})
    except dns.dnssec.ValidationFailure as e:
        raise Bogus(str(e)) from e


def keys_for(server, port, zone, ds_texts):
    r = query(server, port, zone, dns.rdatatype.DNSKEY)
    keys, sig = split(r.answer, zone, dns.rdatatype.DNSKEY)
    if keys is None:
        raise Bogus(f"no DNSKEY for {zone}")
    ds_set = [dns.rdata.from_text(1, dns.rdatatype.DS, t) for t in ds_texts]
    entry = [k for k in keys for d in ds_set
             if dns.dnssec.key_id(k) == d.key_tag and dns.dnssec.make_ds(zone, k, d.digest_type) == d]
    if not entry:
        raise Bogus(f"no DNSKEY of {zone} matches its DS")
    verify(keys, sig, zone, dns.rrset.from_rdata_list(zone, keys.ttl, entry))
    return keys


def resolve(server, port, qname, rdtype, anchors):
    labels = qname.labels
    start = None
    for i in range(len(labels)):
        z = dns.name.Name(labels[i:])
        if z.to_text() in anchors:
            start = (z, anchors[z.to_text()], i)
            break
    if start is None:
        zone, keys, i = dns.name.root, keys_for(server, port, dns.name.root, ROOT_DS), len(labels) - 1
    else:
        zone, keys, i = start[0], keys_for(server, port, start[0], start[1]), start[2]
    for j in range(i - 1, -1, -1):
        child = dns.name.Name(labels[j:])
        r = query(server, port, child, dns.rdatatype.DS)
        ds, sig = split(r.answer, child, dns.rdatatype.DS)
        if ds is not None:
            verify(ds, sig, zone, keys)
            zone, keys = child, keys_for(server, port, child, [d.to_text() for d in ds])
            continue
        soa = query(server, port, child, dns.rdatatype.SOA)
        if any(s.name == child and s.rdtype == dns.rdatatype.SOA for s in soa.answer):
            return "Insecure"  # a zone without DS in its parent
    r = query(server, port, qname, rdtype)
    data, sig = split(r.answer, qname, rdtype)
    if data is not None:
        verify(data, sig, zone, keys)
        return "Secure"
    proofs = [s for s in r.authority if s.rdtype in (dns.rdatatype.NSEC, dns.rdatatype.NSEC3)]
    if not proofs:
        raise Bogus("no denial records")
    for p in proofs:
        verify(p, next((s for s in r.authority if s.name == p.name and s.rdtype == dns.rdatatype.RRSIG and s.covers == p.rdtype), None), zone, keys)
    return "Secure"


def main():
    args = sys.argv[1:]
    server, port = args.pop(0), 53
    if ":" in server:
        server, port = server.split(":")[0], int(server.split(":")[1])
    anchors = {}
    while args and args[0] == "--anchor":
        f = args[1].split()
        anchors.setdefault(dns.name.from_text(f[0]).to_text(), []).append(" ".join(f[1:]))
        args = args[2:]
    out = {}
    for item in args:
        name, t = item.rsplit(":", 1)
        try:
            out[item] = resolve(server, port, dns.name.from_text(name), dns.rdatatype.from_text(t), anchors)
        except Bogus:
            out[item] = "Bogus"
    print(json.dumps(out))


if __name__ == "__main__":
    main()
