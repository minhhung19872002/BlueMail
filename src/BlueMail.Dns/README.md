# BlueMail.Dns

Stub resolver client that asks configured recursive resolvers. BCL only.

| Standard | Done | Not done |
|---|---|---|
| RFC 1035 | message format; name compression with pointer loops/forward pointers refused and 255/63 limits; A, NS, CNAME, SOA, PTR, MX, TXT | other legacy types are returned raw |
| RFC 3596 | AAAA, ip6.arpa | — |
| RFC 6891 | EDNS0 OPT, 1232-byte UDP payload | EDNS options |
| RFC 7766 | TCP fallback on TC with 2-byte framing | connection reuse / pipelining |
| RFC 2308 | negative caching from the SOA minimum; NXDOMAIN distinguished from NODATA | — |
| RFC 2782 / 6698 / 8659 | SRV, TLSA, CAA parsing | — |
| RFC 7505 | null MX visible to callers ("0 .") | — |
| RFC 4033–4035 (DNSSEC) | — | Phase 12; until then nothing relies on validated answers |

Anti-spoofing: random query id, OS-chosen random source port per query, answers accepted only from the queried server
with matching id and question. Verification: same answers as `dnspython` on CoreDNS (`DnsInteropTests`), hostile
packets in unit tests, fuzzing.
