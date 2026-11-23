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

## Layout (Phase 1)
- `src/BlueMail.Domain` entities · `Infrastructure` (EF context, migrations, security, audit, localisation JSON)
  · `MailStore` (IMailStore, the only writer of mail.*) · `Application` (MediatR features, provisioning, seeder,
  web plumbing) · `Identity` (BlueID) · `Api`.
- DI: every process `AddBlueMailApplicationCore()`; only the API `AddBlueMailApiFeatures()` (needs ICurrentUser).
- Migrations: generated ones + hand-written `MailStorePartitionedTables` (partitioned tables, triggers). Never edit a
  committed migration. `dotnet ef migrations add <Name> -p src/BlueMail.Infrastructure -s src/BlueMail.Infrastructure -o Persistence/Migrations`.
- Stop host-run dev processes before `dotnet build/test` (they lock bin/). Integration tests need Docker.
- Server strings: `src/BlueMail.Infrastructure/Localization/Resources/{en,vi}.json`; web: `web/src/i18n/locales`.
- New endpoint: add `[RequirePermission]` or `[SelfGuarded("why")]`; new package: list it in docs/09.
- Python heredocs: never put `\r\n` / `\n` escapes for C# strings inside them (they become real newlines); use the Edit tool.

## Own libraries (Phase 2)
- `BlueMail.Mime`, `BlueMail.Dns`, `BlueMail.Dkim`, `BlueMail.Scanner`: standalone (BCL only), each with a README of
  RFC coverage. Reference checks live in `tests/BlueMail.InteropTests` and run tools in the `tools` container
  (`tests/interop/*.py`); outputs go to `tests/interop/out/` (git-ignored).
- New corpus message: add it to `tests/BlueMail.Corpus/generate.py`, regenerate, run `MimeCorpusTests`; record any
  intended difference with a reason in `python-differences.json`.
- Fuzz finds a crash: keep the saved input (`tests/interop/out/fuzz`), write a red unit test, fix, log in docs/08.
- Never write EICAR or other AV-test files to the host disk (Windows Defender deletes them); stream them into containers.

