# BlueMail

BlueMail Business Email — Gmail-class business email for custom domains, sold as multi-tenant cloud
service and as on-premises install. BlueMail implements its **own mail server** (SMTP in/out, MIME, DNS,
DKIM/ARC, SPF/DMARC, spam filtering, mail store, IMAP) on .NET 10. See `PROMPT_BlueMail_ClaudeCode.md`
for the full specification.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Test harness (CoreDNS, Postfix/OpenDKIM `doitac.test`, reference tools), decisions Z1–Z8, mail corpus | done |
| 1 | Foundation: solution, BlueID (OIDC, 2FA, sessions), tenants + isolation, hash-chained audit log, health, Serilog, mail store schema (modseq/UID, partitioned), React shell | done |
| 2 | Own libraries: BlueMail.Mime, BlueMail.Dns, BlueMail.Dkim, BlueMail.Scanner | next |

## Quick start (development)

```sh
docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev --profile phase0 up -d --build
MSYS_NO_PATHCONV=1 sh scripts/s3-init.sh
MSYS_NO_PATHCONV=1 sh scripts/dev-phase0-check.sh
```

Run the processes on the host against the dev containers:

```sh
sh scripts/dev-run.sh identity     # BlueID on http://localhost:5070
sh scripts/dev-run.sh api          # API on http://localhost:5080
cd web && npm install && npm run dev   # web on http://localhost:5173 (sample user lan@bluemail.test)
dotnet test BlueMail.slnx          # unit + integration (Docker required)
```

Production-like stack: `scripts/install.sh <web-host> <id-host> <mail-host> <internal|acme-email> <admin-email>`;
upgrades with `scripts/deploy.sh` (keeps the running and previous image sets).

Documentation lives in `docs/` (Vietnamese for internal technical notes; user/admin guides in English and
Vietnamese).
