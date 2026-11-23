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
| GET | `/api/v1/account/sessions` | self | Signed-in devices |
| DELETE | `/api/v1/account/sessions/{id}` | self | Sign a device out (immediate) |
| GET | `/api/v1/admin/users?search=&page=&pageSize=` | `mail.admin.users.view` | Users of the caller's tenant |
| POST | `/api/v1/admin/users` | `mail.admin.users.edit` | `{ email, displayName, password, isTenantAdmin, quotaBytes }` — user must change password at first sign-in |
| PUT | `/api/v1/admin/users/{id}/status` | `mail.admin.users.edit` | `{ status: ACTIVE \| SUSPENDED }` — suspension revokes sessions and held tokens at once |
| GET | `/api/v1/admin/tenants` | `mail.platform.tenants.view` | All tenants (platform administrators) |
| POST | `/api/v1/admin/tenants` | `mail.platform.tenants.edit` | Create tenant + primary domain + first tenant admin |
| PUT | `/api/v1/admin/tenants/{id}/status` | `mail.platform.tenants.edit` | Suspend / reactivate a customer tenant |

## BlueID (OIDC)

Issuer `BM_IDENTITY_URL/`. Endpoints `/connect/authorize`, `/connect/token`, `/connect/logout`, `/connect/userinfo`,
discovery `/.well-known/openid-configuration`. Client `bluemail-web`: public, authorization code + PKCE (S256)
required, scopes `openid email profile offline_access bluemail-api`. Access tokens 15 min, refresh tokens 14 days;
each refresh re-checks the session. CORS only for `BM_WEB_URL`. Pages: `/account/login`, `/account/login-2fa`,
`/account/change-password`, `/account/2fa`.
