# BlueMail.Dkim

DKIM signing and verification, and ARC. Cryptography only from `System.Security.Cryptography`.

| Standard | Done | Not done |
|---|---|---|
| RFC 6376 (DKIM) | simple/relaxed canonicalisation (streaming body hash), tag lists, key records (v, k, p, t=y/s, h, s), signing with over-signing of From/Subject/To/Date/Reply-To/Cc, verification incl. x= expiry and i=/d= alignment | z= (diagnostic copies) |
| RFC 8301 | rsa-sha1 refused, keys under 1024 bits refused, signing keys at least 2048 bits | — |
| RFC 8463 (ed25519-sha256) | recognised and reported as neutral | Ed25519 is not in the .NET BCL (PROMPT principle 8) |
| RFC 8617 (ARC) | chain structure (contiguous instances, one AS/AMS/AAR each, cv rules), verification of the newest AMS and every AS, sealing a new hop with cv none/pass/fail | — |

`l=` is ignored when verifying: the whole body is hashed, so text appended after a signed prefix fails.
Verification: `DkimInteropTests` (dkimpy verifies our signatures in three canonicalisations and detects tampering and
an added From; we verify dkimpy and OpenDKIM signatures) and `ArcInteropTests` (both directions, two hops).
