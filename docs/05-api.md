# 05 — API

Base path `/api/v1`. Bearer access token issued by BlueID (audience `bluemail-api`). Swagger (non-production):
`/swagger/v1/swagger.json`, title "BlueMail API".

## Envelopes

Success: `{ "success": true, "data": …, "message": "", "errors": [] }`

Error (`application/problem+json`, RFC 9457 plus a stable code):

```json
{ "type": "https://bluemail.dev/errors/permission_denied", "title": "Bạn không có quyền thực hiện thao tác này.",
  "status": 403, "detail": null, "code": "permission_denied",
  "errors": [ { "field": "email", "code": "invalid_email", "message": "…" } ], "traceId": "…", "success": false }
```

`title` and field `message` follow the caller's language (user setting → tenant default → `Accept-Language`);
clients should translate by `code`. Enum values are UPPER_SNAKE strings. Query strings containing U+0000 are
refused with `invalid_characters`; NUL characters in JSON string values are removed on input.

Admin lists: `{ items, total, page, pageSize }`, `pageSize` ≤ 200, ordered with a unique key last.

## Access rules

Every action declares one of: `[RequirePermission("mail.…")]`, `[AllowAnonymous]`, or `[SelfGuarded("reason")]`
(the handler only touches the caller's own resources). A scan test fails the build otherwise. Ids of other tenants
answer `404` (indistinguishable from "does not exist").

## Endpoints (Phase 1)

| Method | Path | Guard | Purpose |
|---|---|---|---|
| GET | `/health` | anonymous | Liveness |
| GET | `/health/ready` | anonymous | PostgreSQL, Valkey, S3 |
| GET | `/api/v1/me` | self | Profile, effective locale/time zone, permissions, tenant branding |
| GET | `/api/v1/mail/labels?mailboxId=` | self (via `IMailStore.OpenAsync`) | Labels with total/unread counts computed from rows; used/quota bytes |
| POST / PUT / DELETE | `/api/v1/mail/labels[/{id}]?mailboxId=` | self | Create (`{ name, parentId?, colorBg?, colorText? }`), rename/recolour, delete a user label (messages keep their other labels) |
| GET | `/api/v1/mail/conversations?mailboxId=&labelId=&view=all&threads=true&unreadOnly=&cursor=&limit=` | self | Cursor page of conversations (key: latest message time + id); `total` exact; `view=all` = everything except Spam/Trash |
| GET | `/api/v1/mail/threads/{id}` | self | Messages of a conversation with attachments (signed `downloadUrl`/`viewUrl`; none for infected files) and signed `rawUrl` |
| GET | `/api/v1/mail/messages/{id}/body?showImages=` | self | `htmlDocument` (HtmlSanitizer + CSP document for an iframe sandbox), `text`, remote-image state, `zipUrl` |
| GET | `/api/v1/mail/messages/{id}/original` | self | Raw headers (first 512 KB) + stored SPF/DKIM/DMARC/ARC results |
| POST | `/api/v1/mail/messages/{id}/trust-images` | self | "Always display images from this sender" |
| POST | `/api/v1/mail/actions?mailboxId=` | self | `{ action, threadIds?, messageIds?, labelId?, fromLabelId? }`; actions: markRead, markUnread, star, unstar, important, notImportant, archive, moveToInbox, trash, untrash, spam, notSpam, addLabel, removeLabel, moveTo, deleteForever (Trash/Spam only), mute, unmute; ≤ 500 items per call |
| GET | `/api/v1/mail/changes?mailboxId=&sinceModseq=` | self | Incremental sync: changed messages (flags, labels), removed ids, `highestModseq`; `tooOld` when > 2000 changes (reload lists) |
| GET | `/api/v1/mail/p/{token}` | signed URL (re-checks mailbox access) | Attachment / inline part / `.eml` / ZIP of attachments; `nosniff`, sandbox CSP, `Content-Disposition` |
| GET | `/api/v1/mail/img/{token}` | signed URL | Image proxy: only URLs BlueMail signed; http/https ports 80/443, public addresses only (checked per connection and redirect), image types without SVG, ≤ 10 MB, cached 6 h, no cookies/referrer |
| WS | `/hubs/mail` (SignalR, `access_token` query) | bearer, session re-validated on `Subscribe(mailboxId?)` | Event `mailboxChanged { mailboxId, modseq, kind }`; the client then calls `/mail/changes` |
| GET | `/api/v1/mail/compose/from-options` | self | Addresses the caller may send from (own, aliases, shared mailboxes with send-as) |
| GET | `/api/v1/mail/compose/from-message/{id}?mode=reply\|replyAll\|forward` | self | Prefilled draft state: recipients, Re:/Fwd:, In-Reply-To/References, quoted original, forwarded files |
| GET | `/api/v1/mail/compose/suggest?q=` | self | Recipient suggestions (users, groups, shared mailboxes of the tenant; recent correspondents) |
| POST | `/api/v1/mail/drafts` | self | `{ draftId?, version?, state }` autosave; stores a new RFC 5322 version in Drafts; 409 `draft_changed` on a stale version |
| GET / DELETE | `/api/v1/mail/drafts/{id}`, `GET /api/v1/mail/drafts/by-message/{messageId}` | self | Open / discard a draft |
| POST | `/api/v1/mail/drafts/{id}/send` | self | `{ scheduleAt?, archive?, undoSeconds (5/10/20/30) }`; 409 `already_sending`, 403 `from_not_allowed`, 400 `no_recipients`, `too_many_recipients`, `invalid_recipient`, `attachment_infected`, `upload_incomplete` |
| POST | `/api/v1/mail/outgoing/{id}/cancel` | self | Undo / cancel a scheduled send; returns the draft id; 409 `too_late_to_undo` |
| GET | `/api/v1/mail/outgoing` | self | Pending sends (undo window, scheduled, submitting) |
| GET / POST / DELETE | `/api/v1/mail/signatures[/{id}]`, `/api/v1/mail/templates[/{id}]` | self | Signatures (defaults for new/reply) and templates; HTML sanitized on save |
| OPTIONS / POST / HEAD / PATCH / DELETE | `/api/v1/uploads[/{id}]` | self | tus 1.0 resumable upload (`Upload-Length`, `Upload-Metadata`, `Upload-Offset`, `application/offset+octet-stream`, chunk ≤ 8 MiB); scanned on completion |
| GET | `/api/v1/uploads/{id}/content` | self | Uploaded image for the editor preview |
| GET | `/api/v1/files/{token}` | link token (hashed in DB, 30 days, counted) | Download of a file sent as a link (> 25 MB) |
| GET | `/api/v1/mail/conversations?…&q=` | self | `q`: Gmail operator query (PROMPT 6.7); 400 `search_<reason>` (unbalanced_parenthesis, invalid_date, date_range_reversed, …) with the position |
| GET / POST / PUT / DELETE | `/api/v1/mail/filters[/{id}]`, `PUT /api/v1/mail/filters/order` | self | Filters: `{ criteria (query), actions { skipInbox, markRead, star, labelIds, forwardTo (verified only), trash, neverSpam, important ALWAYS/NEVER, category }, isEnabled }` |
| GET | `/api/v1/mail/filters/matches?q=` | self | How many messages a query matches now (preview, max 5000) |
| POST / GET | `/api/v1/mail/filters/{id}/apply`, `/api/v1/mail/filter-jobs/{id}` | self | Apply to existing mail in the background; progress `{ status, total, done }` |
| GET / POST | `/api/v1/mail/filters/export`, `/api/v1/mail/filters/import` (multipart `file`) | self | Gmail mailFilters.xml; import reports entries it could not take |
| GET / POST / DELETE | `/api/v1/mail/blocked[/{id}]` | self | Blocked addresses or domains (`@shop.vn` or `shop.vn`) |
| GET / PUT | `/api/v1/mail/vacation` | self | Auto reply `{ enabled, startAt, endAt, subject, html, onlyContacts, onlyOrganization }` |
| GET / POST / DELETE | `/api/v1/mail/forwarding[/{id}]`, `POST …/{id}/verify { code }`, `PUT /api/v1/mail/forwarding/active { id?, keepCopy }` | self | Forwarding addresses: a 6-digit code is mailed to the address (24 h, 5 tries); one active automatic forward |
| POST | `/api/v1/mail/actions` `action: snooze \| unsnooze` (`snoozeUntil`), `mute` | self | Snooze (one active per conversation, DB rule; new mail wakes it); mute also archives |
| GET | `/api/v1/mail/snoozed` | self | Active snoozes with their time |
| POST | `/api/v1/mail/messages/{id}/unsubscribe` | self | `{ outcome: DONE \| OPEN_URL, url }`: RFC 8058 one-click POST (only with DKIM pass), else mailto:, else the page |
| GET / PUT | `/api/v1/me/settings`, `PUT /api/v1/me/profile` | self | Account settings (normalised server-side) and profile (name, locale, time zone, clock, week start) |
| GET / POST / PUT | `/api/v1/contacts[/{id}]`, `POST /api/v1/contacts/delete` | self | Contacts (`?q=&groupId=&other=`): emails/phones with labels, org, title, notes |
| GET / PUT / DELETE | `/api/v1/contacts/{id}/photo` | self | JPEG/PNG ≤ 1 MB (checked by magic bytes), encrypted in S3 |
| GET / POST / PUT / DELETE | `/api/v1/contacts/groups[/{id}]` | self | Contact groups |
| POST / GET | `/api/v1/contacts/import` (multipart vCard/CSV), `/api/v1/contacts/export?format=vcf\|csv` | self | Import merges cards whose address is known |
| POST | `/api/v1/contacts/merge-duplicates` | self | Merges contacts sharing an address |
| GET | `/api/v1/contacts/directory?q=` | self (tenant) | Users, groups, shared mailboxes (hidden people left out) |
| PUT | `/api/v1/admin/directory/{userId}` | `mail.admin.users.edit` | `{ hidden }` |
| GET / POST / DELETE | `/api/v1/admin/category-rules[/{id}]` | `mail.admin.policy.edit` | Inbox category rules (system defaults read-only) |
| GET | `/api/v1/account/sessions` | self | Signed-in devices |
| DELETE | `/api/v1/account/sessions/{id}` | self | Sign a device out (immediate) |
| GET | `/api/v1/admin/users?search=&page=&pageSize=` | `mail.admin.users.view` | Users of the caller's tenant |
| POST | `/api/v1/admin/users` | `mail.admin.users.edit` | `{ email, displayName, password, isTenantAdmin, quotaBytes }` — user must change password at first sign-in |
| PUT | `/api/v1/admin/users/{id}/status` | `mail.admin.users.edit` | `{ status: ACTIVE \| SUSPENDED }` — suspension revokes sessions and held tokens at once |
| GET | `/api/v1/admin/tenants` | `mail.platform.tenants.view` | All tenants (platform administrators) |
| POST | `/api/v1/admin/tenants` | `mail.platform.tenants.edit` | Create tenant + primary domain + first tenant admin |
| PUT | `/api/v1/admin/tenants/{id}/status` | `mail.platform.tenants.edit` | Suspend / reactivate a customer tenant |
| POST | `/api/v1/mail/actions` `action: spam \| notSpam \| reportPhishing` | self | Moves the messages and teaches the user's and the tenant's Bayes model (once per message; changing one's mind moves the lesson). `reportPhishing` also delivers a copy (original attached) to the tenant's phishing report mailbox |
| GET / PUT | `/api/v1/admin/spam/settings` | `mail.admin.spam.edit` | `{ spamThreshold (1–30), rejectThreshold (> spam), greylisting, suspiciousAttachments: SPAM \| QUARANTINE \| REJECT, phishingReportAddress (a mailbox of the tenant) }`; GET also lists block lists with licence notes |
| PUT | `/api/v1/admin/spam/lists/{id}` | `mail.admin.spam.edit` | `{ enabled }` — turn a DNS block list on/off for the tenant (all off by default, docs/00 Z4) |
| GET | `/api/v1/admin/quarantine?status=HELD\|RELEASED\|DELETED&page=&pageSize=` | `mail.admin.quarantine.view` | Held messages with reason, score and rule report |
| POST / DELETE | `/api/v1/admin/quarantine/{id}/release`, `/api/v1/admin/quarantine/{id}` | `mail.admin.quarantine.edit` | Release to the recipients' Inbox (exactly once; 409 `quarantine_already_decided`) or discard; audited |

### Administration console (Phase 9)

| Method | Path | Guard | Purpose |
|---|---|---|---|
| GET / POST | `/api/v1/admin/domains` | `mail.admin.domains.view` / `.edit` | List; add `{ domain, aliasOfDomainId? }` (IDN accepted, stored lower-case; 409 `domain_exists`) |
| GET | `/api/v1/admin/domains/{id}/records?check=` | `mail.admin.domains.view` | Records to publish (verification TXT, MX, SPF, DKIM per key, DMARC, MTA-STS + host, TLS-RPT, autoconfig/autodiscover, SRV) with state `OK \| MISSING \| WRONG \| UNKNOWN` and what DNS holds; `check=true` asks DNS now and verifies the domain the first time its TXT is seen |
| DELETE | `/api/v1/admin/domains/{id}` | `mail.admin.domains.edit` | Only when no address, group or alias domain uses it (409 `domain_in_use`); never the primary domain |
| PUT | `/api/v1/admin/domains/{id}/catch-all` | `mail.admin.domains.edit` | `{ target }` — a mailbox of the tenant, or null to reject unknown addresses |
| POST | `/api/v1/admin/domains/{id}/dkim/rotate` | `mail.admin.domains.edit` | New PENDING key; becomes ACTIVE when its record is in DNS, the old one RETIRING for 7 days (409 while a rotation is pending) |
| GET | `/.well-known/mta-sts.txt` (Host `mta-sts.<domain>`) | anonymous | RFC 8461 policy for verified domains only: `mode: enforce` once MX and MTA-STS records check out, `testing` before; `mx:` the platform MX |
| GET / PUT | `/api/v1/admin/users/{id}` | `mail.admin.users.view` / `.edit` | Details (aliases, quota use, last 20 sign-ins); update name, admin flag, quota, org unit, IMAP access |
| POST | `/api/v1/admin/users/{id}/reset-password` | `mail.admin.users.edit` | Returns a 16-character temporary password; the user must change it; sessions revoked |
| POST / DELETE | `/api/v1/admin/users/{id}/aliases[/{email}]` | `mail.admin.users.edit` | Alias on a verified tenant domain (409 `email_taken`) |
| PUT | `/api/v1/admin/users/{id}/address` | `mail.admin.users.edit` | `{ email }` — new primary address; the old one stays as alias |
| DELETE | `/api/v1/admin/users/{id}?transferTo=` | `mail.admin.users.edit` | Delete; with `transferTo` the mail is copied into that user's mailbox first (returns the number of messages) |
| POST | `/api/v1/admin/users/import/parse` | `mail.admin.users.edit` | multipart CSV (request ≤ 5 MB, en/vi headers, UTF-8 with or without BOM) → rows |
| POST | `/api/v1/admin/users/import` | `mail.admin.users.edit` | `{ rows, commit }` — ≤ 5000 rows; validates every row (`errors: [{ row, field, code }]`); creates nothing while any row is wrong; returns generated passwords once |
| GET | `/api/v1/admin/org-units` | `mail.admin.users.view` | Org units with user counts |
| GET / POST / PUT / DELETE | `/api/v1/admin/groups[/{id}]` | `mail.admin.groups.edit` | `{ email, name, description, whoCanPost: ANYONE \| DOMAIN \| MEMBERS \| MANAGERS, allowExternal, moderated }` |
| POST / DELETE | `/api/v1/admin/groups/{id}/members[/{memberId}]` | `mail.admin.groups.edit` | `{ email, role: MEMBER \| MANAGER \| OWNER }`; outside addresses only when `allowExternal` |
| GET / POST | `/api/v1/admin/shared-mailboxes` | `mail.admin.groups.edit` | Shared mailboxes with members; create `{ email, name }` |
| PUT | `/api/v1/admin/shared-mailboxes/{id}/members` | `mail.admin.groups.edit` | `{ userId, canRead, canSendAs, canSendOnBehalf }` (all false removes) |
| GET | `/api/v1/admin/policies?orgUnitId=` | `mail.admin.policy.edit` | Every policy key with type, default, tenant value and org-unit value (docs/00 D20) |
| PUT | `/api/v1/admin/policies/{key}` | `mail.admin.policy.edit` | `{ orgUnitId?, value }` — typed (`int`, `bool`, `list`, `text`); `null` removes the override; 404 unknown key |
| GET / POST / PUT / DELETE | `/api/v1/admin/routing-rules[/{id}]` | `mail.admin.policy.edit` | `{ name, direction: INBOUND \| OUTBOUND, condition { senderContains, recipientContains, subjectContains, hasAttachment }, action { type: BCC \| REDIRECT \| REJECT \| QUARANTINE, address }, isEnabled }`; outbound only BCC/REJECT |
| GET | `/api/v1/admin/trace?sender=&recipient=&messageId=&from=&to=&page=` | `mail.admin.trace.view` | Mail log rows |
| GET | `/api/v1/admin/trace/{id}` | `mail.admin.trace.view` | Row + where it was stored (labels, SPF/DKIM/DMARC, spam report) + outbound queue rows |
| GET | `/api/v1/admin/queue?status=PENDING\|QUEUED\|DEFERRED\|BOUNCED\|ALL&domain=&page=` | `mail.admin.trace.view` | Outbound recipients |
| POST | `/api/v1/admin/queue/retry`, `/api/v1/admin/queue/cancel` | `mail.admin.queue.edit` | `{ ids }` — retry now / cancel (status CANCELLED); audited |
| GET / DELETE | `/api/v1/admin/suppressions[/{id}]?search=` | `mail.admin.trace.view` / `mail.admin.queue.edit` | Suppressed recipients; remove to send again |
| GET | `/api/v1/admin/health?days=` | `mail.admin.trace.view` | Delivered/bounced/complaints, per-domain delivery rate, sending IPs (`BM_OUTBOUND_IPS`) on enabled block lists |
| GET | `/api/v1/admin/reports/{kind}?from=&to=` | `mail.admin.reports.view` | `MAIL_BY_DAY`, `SPAM_RATE`, `STORAGE_BY_USER`, `USERS_WITHOUT_2FA`: columns + rows (≤ 50 000, `truncated`) |
| GET | `/api/v1/admin/reports/{kind}/xlsx` | `mail.admin.reports.view` | Same report as XLSX (own writer); first lines state the filter and whether the export is complete |
| GET / PUT | `/api/v1/admin/tenants/{id}` | `mail.platform.tenants.view` / `.edit` | Details with usage (users, storage, mail in/out 30 days, domains); update plan, limits, locale, time zone, `suspendTemporary` (450 instead of 550 while suspended) |
| GET | `/api/v1/admin/tenants/{id}/users?search=` | `mail.platform.tenants.edit` | Users of a customer, for support sign-in |
| POST | `/api/v1/admin/tenants/{id}/support-signin` | `mail.platform.tenants.edit` | `{ userId, reason }` → one-time link (10 min) to `BlueID /account/support`; session lasts 1 h, carries the actor, audited, tenant admins mailed |


## BlueID (OIDC)

Issuer `BM_IDENTITY_URL/`. Endpoints `/connect/authorize`, `/connect/token`, `/connect/logout`, `/connect/userinfo`,
discovery `/.well-known/openid-configuration`. Client `bluemail-web`: public, authorization code + PKCE (S256)
required, scopes `openid email profile offline_access bluemail-api`. Access tokens 15 min, refresh tokens 14 days;
each refresh re-checks the session. CORS only for `BM_WEB_URL`. Pages: `/account/login`, `/account/login-2fa`,
`/account/change-password`, `/account/2fa`.

### Compliance and migration (Phase 10)

| Method | Path | Guard | Purpose |
|---|---|---|---|
| POST | `/api/v1/me/imports/test` | self | `{ host, port, tls: IMPLICIT \| STARTTLS, username, password }` → `{ ok, error, folders, messages, gmail }`; nothing stored |
| POST | `/api/v1/me/imports` | self | Start an IMAP import into the caller's mailbox (409 `import_running`; 400 `import_port_not_allowed` / `import_host_not_allowed`) |
| POST | `/api/v1/me/imports/file` | self | `{ uploadId }` of a finished tus upload: `.mbox`/`.mbx`, `.eml` or `.zip` of `.eml` |
| GET | `/api/v1/me/imports` | self | Jobs with counters (`imported`, `skipped`, `failed`), current folder, error code |
| GET | `/api/v1/me/imports/{id}/errors.csv` | owner, requester or `mail.admin.migration.edit` | Problems per message (CSV, formulas neutralised) |
| POST | `/api/v1/me/imports/{id}/cancel` | same | Cancel; the stored password is dropped |
| POST | `/api/v1/admin/imports` | `mail.admin.migration.edit` | `{ userId, source }` import for another user |
| POST | `/api/v1/admin/imports/batch` | `mail.admin.migration.edit` | multipart CSV (email, host, port, tls, username, password); all rows checked first, nothing starts while one is wrong |
| GET | `/api/v1/admin/imports?batchId=` | `mail.admin.migration.edit` | All imports of the tenant |
| GET / POST | `/api/v1/admin/compliance/holds` | `mail.admin.compliance.view` / `.edit` | Legal holds; create `{ name, scope: USER \| ORG_UNIT \| QUERY, userId?, orgUnitId?, query?, reason? }` (audited) |
| POST | `/api/v1/admin/compliance/holds/{id}/release` | `mail.admin.compliance.edit` | Release (409 `hold_released`) |
| GET / POST / PUT / DELETE | `/api/v1/admin/compliance/retention[/{id}]` | view / edit | `{ name, action: DELETE_AFTER \| KEEP_MIN, days, query?, orgUnitId?, isEnabled }` (KEEP_MIN without query) |
| POST | `/api/v1/admin/compliance/ediscovery/search` | `mail.admin.compliance.view` | `{ query, mailboxIds? }` over all mailboxes incl. Spam, Trash and deleted-but-kept mail: first 100 + count (≤ 10 000); audited |
| POST | `/api/v1/admin/compliance/ediscovery/export` | `mail.admin.compliance.edit` | `{ query, mailboxIds?, format: MBOX \| EML }` → background export with `manifest.csv` (SHA-256 per message); audited |
| POST | `/api/v1/admin/compliance/users/{id}/export` | `mail.admin.compliance.edit` | All data of one user (access request) |
| GET | `/api/v1/admin/compliance/exports` | `mail.admin.compliance.view` | Exports of the tenant made by administrators |
| POST / GET | `/api/v1/me/export`, `/api/v1/me/exports` | self | "Download my data": mbox per label + `contacts.vcf` + `settings.json` |
| POST | `/api/v1/exports/{id}/link` | requester only | Signed download link, 2 h; audited |
| GET | `/api/v1/exports/d/{token}` | signed token | The ZIP (requester still active, export not expired: 7 days) |
| POST | `/api/v1/admin/tenants/{id}/export` | `mail.platform.tenants.edit` | Whole tenant: every user and shared mailbox, `users.csv`, `domains.txt` |

### Mail programs (Phase 11)

| Method | Path | Guard | Purpose |
|---|---|---|---|
| GET / POST | `/api/v1/me/app-passwords` | self | List; create `{ name }` → `{ id, password, imapHost, imapPort, smtpHost, smtpPort }` (password shown once; ≤ 20 per user) |
| DELETE | `/api/v1/me/app-passwords/{id}` | self | Revoke; open IMAP connections using it are closed at once |
| GET | `/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress=`, `/mail/config-v1.1.xml` | anonymous | Thunderbird autoconfig (IMAP 993 SSL, SMTP 465 SSL) |
| POST | `/autodiscover/autodiscover.xml` | anonymous | Outlook autodiscover (POX 2006a) |

Protocols: IMAP `imap.<platform>` 993 (TLS) / 143 (STARTTLS required before LOGIN); SMTP submission `smtp.<platform>` 465 (TLS) /
587 (STARTTLS, AUTH only after TLS). AUTH: PLAIN, LOGIN, XOAUTH2, OAUTHBEARER (BlueID access token).

### Sending at scale and confidential mode (Phase 12)

| Method | Path | Guard | Purpose |
|---|---|---|---|
| GET | `/api/v1/admin/sending-locks` | `mail.admin.users.edit` | Users whose sending is paused (reason, last-24h sent / hard bounces / complaints) |
| DELETE | `/api/v1/admin/sending-locks/{userId}` | `mail.admin.users.edit` | Allow sending again (audited) |
| GET / POST | `/api/v1/admin/platform/ip-pools` | `mail.platform.tenants.view` / `.edit` | Pools with their IPs (PTR state, warm-up day and cap, sent today); create/update `{ id?, name, kind: TRIAL\|SHARED\|DEDICATED, isDefault }` |
| POST | `/api/v1/admin/platform/sending-ips` | `mail.platform.tenants.edit` | Add/update `{ id?, poolId, address, helo, enabled, startWarmup }` |
| GET | `/api/v1/admin/platform/tls-reports?days=` | `mail.platform.tenants.view` | TLS outcomes per destination domain and policy |
| PUT | `/api/v1/admin/tenants/{id}/ip-pool` | `mail.platform.tenants.edit` | `{ poolId }` (null = default pool) |
| GET | `/api/v1/confidential/{token}` | anonymous (token) | Subject, sender, expiry, whether a code is needed, masked recipient |
| POST | `/api/v1/confidential/{token}/code` | anonymous (token) | Mail a 6-digit code to the recipient (one a minute; 409 `confidential_code_wait`) |
| POST | `/api/v1/confidential/{token}/open` | anonymous (token + code) | `{ code }` → sanitized HTML document, text, file links (30 min) |
| GET | `/api/v1/confidential/{token}/files/{part}?e=&s=` | anonymous (signed link) | One attachment |
| GET | `/api/v1/mail/confidential?rfcMessageId=` | self | Sender's status: expiry, revoked, recipients with open counts |
| PUT | `/api/v1/mail/confidential/{id}/revoked` | self | `{ revoked }` withdraw / restore access |

Draft state gains `confidential: { expiresDays: 1|7|30|90|365, requireCode }`. Sending while paused fails with
`sending_locked` (web) or `550 5.7.1` (SMTP submission).
