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
- Stop host-run dev processes before `dotnet build/test` (they lock bin/). Integration tests need Docker. Stopping the
  `dev-run.sh` shell leaves the `BlueMail.Api`/`BlueMail.Identity` processes running: kill them by command line
  (PowerShell `Get-CimInstance Win32_Process | ? CommandLine -match 'BlueMail\.(Api|Identity)'`).
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

## Mail flow (Phase 3)
- `BlueMail.Protocols.Smtp`: `SmtpServerSession` (state machine, no I/O, `ISmtpHandler` hooks) + `SmtpTransport`
  (stream framing, DATA/BDAT to temp files, STARTTLS). Reused by submission (Phase 4).
- `BlueMail.Mx` runs in the dev compose (`mx`, 10.77.0.25, image `bluemail/server:dev`, `command: [mx]`); rebuild it
  after code changes: `docker compose … up -d --build mx`. It needs a migrated DB (run identity once).
- `PgMailStore.DeliverAsync`: S3 first (chunked AES-GCM per tenant, `ChunkedAead`), then one PG transaction, then
  the Valkey event `bm:mbx:<mailbox>`. Only `MessageFacts.Extract` decides what is indexed.
- Integration tests start Garage too (StackFixture); the Garage image has no shell: wait on log lines, not port probes.
- Acceptance: `sh scripts/dev-phase3-check.sh` (uses `tests/interop/mx_checks.py`).

## Web reading (Phase 4)
- Store reads/actions in `PgMailStore` (MailReading.cs, MailActions.cs): one modseq per action, new UIDs for added
  labels, label_changes rows for every (label, UID) - keep this when adding actions (IMAP depends on it).
- Content the browser loads without the bearer token goes through `SignedUrls` (`/api/v1/mail/p|img/{token}`).
- Hub calls have no ICurrentUser: validate the session from `Context.User` (BUG-022).
- Sample mail: `scripts/dev-seed-mail.py`; doitac.test senders must go via its Postfix (DMARC p=reject).
- Check UI at 375 px with screenshots, not only measurements (BUG-023, BUG-024).

## Sending (Phase 5)
- Send path: draft (mail.drafts + a real message in DRAFTS) -> mail.outgoing (UNDO_WINDOW/SCHEDULED, unique per draft)
  -> `OutgoingDispatcher` (in BlueMail.Outbound) -> Sent copy + internal delivery + mta.outbound_* -> `DeliveryWorker`.
  Every dispatcher step is idempotent by the fixed Message-ID; keep it that way.
- Raw SQL via `SqlQuery<T>`: column aliases must be snake_case (naming convention applies, BUG-030).
- Outbound runs in the dev compose (`outbound`, 10.77.0.26); rebuild with mx. Dev DKIM keys are imported by
  `dev-run.sh identity` (BM_DEV_DKIM_DIR). In the doitac container OpenDKIM signs and dkimpy-milter verifies (BUG-027).
- Start `dev-run.sh identity` and `api` one after the other: two `dotnet run` builds at once lock shared obj/ files.
- Docker's embedded DNS answers PTR of container IPs with container names; ask CoreDNS (@10.77.0.53) in checks.
- Dev keys in git are intentional (docs/00 D12).

## Antispam (Phase 6)
- `BlueMail.Antispam` (BCL only): Tokenizer, Bayes (Robinson-Fisher), `SpamRules.Score`. New rule code -> add en/vi text in
  `web/src/i18n/locales` `spam.rule.<CODE>` (a unit test checks). DB side in `Application/Features/Antispam`; MX side `MxSpamFilter`.
- After changing rules run `SpamQualityTests` (2-fold CV on `tests/BlueMail.SpamCorpus/corpus.jsonl`); false positives must stay 0.
- Acceptance: `sh scripts/dev-phase6-check.sh` (rebuild mx first). Keep the phishing sample below the reject threshold (15).

## Search and rules (Phase 7)
- Query language: `BlueMail.MailStore/Search` (parser → `SearchSql` for search, `SearchMatcher` for filters). A new operator needs
  both evaluators and a case in `RulesTests.Search_in_sql_and_the_filter_matcher_agree…`; new parser error codes go into
  `SearchParser.ErrorCodes` + `error.search_<code>` translations.
- Mailbox rules run in `MailboxRules` from both MX and the dispatcher's internal delivery; forwards/vacation go out via `SystemMail`.
- Acceptance: `sh scripts/dev-phase7-check.sh` (rebuild mx + outbound first).
- A vite dev server may already be running on 5173 (BM_WEB_URL); reuse it instead of starting a second one on another port.
  If a page goes blank after a script rewrote files ("does not provide an export", lazy element resolves to undefined),
  vite cached a half-written file: `touch` the files and reload.

## Organising (Phase 8)
- Tabs/importance: `InboxOrganizer` (rules only, docs/00 D17), called from `MailboxRules.PlanAsync`; filters run after and win.
- Snooze: `SnoozeService` + `MailMaintenanceWorker` (API process; also empties Trash/Spam after 30 days via `PgMailStore.PurgeTrashAndSpamAsync`).
- User settings: `mail.user_settings` JSON normalised by `MailSettings.Normalize`; web reads them via `useSettings()` (settingsApi.ts).
- Keyboard shortcuts: `shortcuts.ts` (keys) + `ShortcutHost`/`useShortcuts` (components register the commands they own).

## Administration (Phase 9)
- Admin API in `Application/Features/Admin/{Domains,Users,Directory,Policies,Operations,Tenants}`; web console `/admin/:section`
  (`modules/admin/AdminConsole.tsx`, one tab per permission; `adminApi.ts`).
- Policies: add a key to `PolicyKeys` and read it where it acts (`PolicyService`); `Every_policy_key_has_a_reader` enforces it.
- Domain records come from `DomainRecordPlanner` (customer domains include `_spf.<platform>`); keep `scripts/dev-gen-zones.py`
  matching it, then `docker restart bluemail-dev-dns-1`. Integration tests use `stack.Dns` (`FakeDomainDns`) for DNS answers.
- Seed platform admin must change password at first sign-in; tests clear `MustChangePassword`/`TwoFactorEnabled` before using it.

## Compliance and migration (Phase 10)
- `BlueMail.Protocols.Imap`: shared reader/parser + import client (Phase 11 adds the server on the same parser). Interop:
  `tests/interop/imap_source.py` fills nguon@doitac.test on the dev Dovecot (127.0.0.1:55993, pinned cert deploy/dev/postfix/tls).
  Integration tests that import need the dev compose running (doitac + tools).
- Permanent delete = `PgMailStore.ExpungeSql` only: it hides always and expunges only when not held and past KEEP_MIN (docs/00 D22).
  Never add another `expunged_at = now()` path; a change of hold scope calls `RecomputeHoldCountsAsync`.
- Import/export jobs use the lease pattern like FilterJobWorker; workers are singletons (tests call `RunOnceAsync`/`HousekeepingAsync`).
- Python's mailbox appends to existing files: delete outputs before writing in interop tests.

## Mail programs (Phase 11)
- `BlueMail.Submission` (compose `submission`, 10.77.0.27, host ports 55587/55465) and `BlueMail.Imap` (`imap`, 10.77.0.28,
  55143/55994) use the dev certificate `deploy/dev/tls` (SANs smtp/imap/mx.bluemail.test). Rebuild them with mx.
- Sign-in for both: `MailLoginService` (passwords, app passwords, BlueID tokens, brute-force counters in Valkey).
- IMAP session code: `src/BlueMail.Imap/ImapSession*.cs`; store queries `MailStore/ImapStore.cs`; writes only through
  `ApplyAsync`/`DeliverAsync`. After IMAP changes run `sh scripts/dev-phase11-check.sh` (imaptest must stay 0/460).
- SMTP AUTH lives in `SmtpServerSession` (pending SASL line state); MX passes no Authenticate and offers no AUTH.

## Sending at scale (Phase 12)
- DNSSEC: `BlueMail.Dns/Dnssec.cs` (validator), `Dane.cs`; outbound uses them unless `BM_DANE=off`. Dev zone doitac.test is
  signed by CoreDNS (`deploy/coredns/keys`, anchor in compose `BM_DNSSEC_ANCHORS`); keep `tests/interop/dnssec_check.py` agreeing.
- VERP/feedback: `Application/Features/Sending/Feedback.cs` (Verp, ReportParser, FeedbackProcessor, SenderReputation); MX accepts
  VERP addresses and `BM_FEEDBACK_ADDRESS` (default fbl@<platform>). IP pools/warm-up: `Outbound/SendingIps.cs`, `WarmupSchedule`.
- Confidential mode: `ConfidentialService` + `web/src/modules/mail/ConfidentialPage.tsx` (public route `/c/:token`).
- Acceptance: `sh scripts/dev-phase12-check.sh` (rebuild mx/outbound/submission first). Build the whole slnx before `docker compose build`.

## Finishing (Phase 13)
- Production stack: `deploy/docker-compose.yml` (worker = api image with `BM_ROLE=worker`; mail certs from the Caddy volume).
  Local trial: separate project name and ports (see docs/06 P13-09); stop it when done (Docker Desktop hangs with too many stacks).
- Perf: `tests/perf/fill.sql` / `cleanup.sql` (synthetic rows, dev only), `api_latency.py`, `smtp_load.py`, `imap_idle_load.py`.
  The conversation list has two paths (D30); keep `ConversationPagingTests` green when touching it.
- Backups: `scripts/backup.sh` / `restore.sh` (pg_dump + rclone); drill numbers in docs/03.
- Long fuzz: `BM_FUZZ_SECONDS=240 BM_FUZZ_ITERATIONS=100000000 dotnet test tests/BlueMail.FuzzTests`; replay saved inputs, then a red test.
