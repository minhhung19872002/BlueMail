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
| 2 | Own libraries: BlueMail.Mime, BlueMail.Dns, BlueMail.Dkim (DKIM + ARC), BlueMail.Scanner (layers 1–6); verified against Python email, dnspython, dkimpy, OpenDKIM, Postfix and ClamAV; fuzzed | done |
| 3 | IMailStore + encrypted S3 store, threading, multilingual search; BlueMail.Mx (SMTP in, SPF/DKIM/DMARC/ARC, scanning, delivery) | done |
| 5 | Sending: compose (TipTap), drafts with autosave, resumable attachments (tus), signatures, templates, reply/forward, undo, schedule; BlueMail.Outbound (own SMTP client, DKIM, PostgreSQL queue with leases, MX/null MX, STARTTLS, MTA-STS, retries, DSN, suppression); internal delivery | done |
| 4 | Web reading: labels, conversation list (cursor paging), reading pane, sanitized HTML in a sandboxed iframe, image proxy, attachments (pdf.js, ZIP), single/bulk actions, SignalR live updates, incremental sync | done |

## Quick start (development)

```sh
docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev up -d --build dns postgres s3 valkey doitac tools
MSYS_NO_PATHCONV=1 sh scripts/s3-init.sh
MSYS_NO_PATHCONV=1 sh scripts/dev-phase0-check.sh
sh scripts/dev-run.sh identity     # once: migrates the database and seeds the sample tenants (Ctrl+C after start)
docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev up -d --build mx   # mx.bluemail.test, 10.77.0.25
sh scripts/dev-phase3-check.sh     # receive mail through Postfix, swaks/smtplib checks, database verification
MSYS_NO_PATHCONV=1 docker exec bluemail-dev-tools-1 python /work/scripts/dev-seed-mail.py   # sample mail for lan@
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
