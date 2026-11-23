#!/usr/bin/env python3
"""Reference DNS answers with dnspython (independent implementation) from the dev CoreDNS.

Usage: python dns_dump.py <server-ip> <name>:<TYPE> ...   -> JSON {"name:TYPE": {"status", "records", "canonical"}}
"""
import json
import sys

import dns.flags
import dns.resolver
import dns.rdatatype


def text(rr):
    t = rr.rdtype
    if t == dns.rdatatype.TXT:
        return b"".join(rr.strings).decode("utf-8", "replace")
    if t == dns.rdatatype.MX:
        return f"{rr.preference} {rr.exchange.to_text().lower()}"
    if t == dns.rdatatype.SRV:
        return f"{rr.priority} {rr.weight} {rr.port} {rr.target.to_text().lower()}"
    if t == dns.rdatatype.SOA:
        return f"{rr.mname.to_text().lower()} {rr.rname.to_text().lower()} {rr.serial} {rr.refresh} {rr.retry} {rr.expire} {rr.minimum}"
    if t in (dns.rdatatype.CNAME, dns.rdatatype.NS, dns.rdatatype.PTR):
        return rr.target.to_text().lower()
    return rr.to_text().lower()


def main():
    server = sys.argv[1]
    res = dns.resolver.Resolver(configure=False)
    res.nameservers = [server]
    res.use_edns(0, 0, 1232)
    res.lifetime = 5
    out = {}
    for q in sys.argv[2:]:
        name, rdtype = q.rsplit(":", 1)
        try:
            ans = res.resolve(name, rdtype, raise_on_no_answer=False, search=False)
            if ans.rrset is None:
                out[q] = {"status": "NoData", "records": [], "canonical": ans.canonical_name.to_text().lower()}
            else:
                out[q] = {"status": "Ok", "records": sorted(text(r) for r in ans.rrset),
                          "canonical": ans.canonical_name.to_text().lower()}
        except dns.resolver.NXDOMAIN:
            out[q] = {"status": "NxDomain", "records": [], "canonical": None}
        except Exception as e:  # noqa: BLE001
            out[q] = {"status": "TempFail", "records": [], "canonical": None, "error": type(e).__name__}
    json.dump(out, sys.stdout, ensure_ascii=False, indent=1, sort_keys=True)


if __name__ == "__main__":
    main()
