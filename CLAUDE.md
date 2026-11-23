# CLAUDE.md — working notes for BlueMail

Specification: `PROMPT_BlueMail_ClaudeCode.md` (authoritative; work phase by phase, section 13).
Decisions: `docs/00-quyet-dinh-ky-thuat.md`. Bugs (including our own): `docs/08-so-loi.md`.

## Ground rules (from the spec)
- BlueMail writes its own mail server. No MimeKit/MailKit/DnsClient/ClamAV/Stalwart/Postfix in production paths;
  third-party mail software only as **test references**.
- Only .NET BCL crypto (`System.Security.Cryptography`, `SslStream`). No Ed25519 signing.
- Every mailbox write goes through `IMailStore`. Every business table has `tenant_id`.
- No stubs, no TODOs, no hard-coded UI strings (en + vi resource files). Server processes run InvariantCulture.
- "Only one …" rules are DB unique constraints; concurrency tests must be truly parallel.
- Code/comments/commits in English. Commit per phase: `phase-N: …`, push to `origin main`
  (https://github.com/minhhung19872002/BlueMail).

## Dev environment
- Docker network `10.77.0.0/24`, IP plan in docs/00 Z1 and `scripts/dev-gen-zones.py`.
- Windows/Git Bash: prefix docker exec commands that contain absolute container paths with `MSYS_NO_PATHCONV=1`.
- Host ports (localhost only): PostgreSQL 55432, S3 53900, Valkey 56379, DNS 55353.
- Dev credentials: `deploy/.env.dev` (not secret). Dev DKIM keys: `deploy/dev/dkim/` (test domains only).
- Heredocs with many quotes can trip the shell safety check; write files with the Write tool instead.
