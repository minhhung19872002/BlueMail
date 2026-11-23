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

## BlueID (OIDC)

Issuer `BM_IDENTITY_URL/`. Endpoints `/connect/authorize`, `/connect/token`, `/connect/logout`, `/connect/userinfo`,
discovery `/.well-known/openid-configuration`. Client `bluemail-web`: public, authorization code + PKCE (S256)
required, scopes `openid email profile offline_access bluemail-api`. Access tokens 15 min, refresh tokens 14 days;
each refresh re-checks the session. CORS only for `BM_WEB_URL`. Pages: `/account/login`, `/account/login-2fa`,
`/account/change-password`, `/account/2fa`.
