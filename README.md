# BlueMail

BlueMail Business Email — Gmail-class business email for custom domains, sold as multi-tenant cloud
service and as on-premises install. BlueMail implements its **own mail server** (SMTP in/out, MIME, DNS,
DKIM/ARC, SPF/DMARC, spam filtering, mail store, IMAP) on .NET 10. See `PROMPT_BlueMail_ClaudeCode.md`
for the full specification.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Test harness (CoreDNS, Postfix/OpenDKIM `doitac.test`, reference tools), decisions Z1–Z8, mail corpus | done |
| 1 | Foundation: solution, BlueID, tenants, audit log, mail store schema, React shell | in progress |

## Quick start (development)

```sh
docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev --profile phase0 up -d --build
MSYS_NO_PATHCONV=1 sh scripts/dev-s3-init.sh
MSYS_NO_PATHCONV=1 sh scripts/dev-phase0-check.sh
```

Documentation lives in `docs/` (Vietnamese for internal technical notes; user/admin guides in English and
Vietnamese).
