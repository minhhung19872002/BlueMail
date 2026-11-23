import { api } from "../../api/client";
import type { Paged } from "../../api/types";

// Administration API (docs/05-api.md, PROMPT 7). The server checks every permission itself; the UI only hides.

export const AdminPermission = {
  UsersView: "mail.admin.users.view",
  UsersEdit: "mail.admin.users.edit",
  DomainsView: "mail.admin.domains.view",
  DomainsEdit: "mail.admin.domains.edit",
  GroupsEdit: "mail.admin.groups.edit",
  PolicyEdit: "mail.admin.policy.edit",
  SpamEdit: "mail.admin.spam.edit",
  QuarantineView: "mail.admin.quarantine.view",
  QuarantineEdit: "mail.admin.quarantine.edit",
  TraceView: "mail.admin.trace.view",
  QueueEdit: "mail.admin.queue.edit",
  ReportsView: "mail.admin.reports.view",
  ComplianceView: "mail.admin.compliance.view",
  ComplianceEdit: "mail.admin.compliance.edit",
  MigrationEdit: "mail.admin.migration.edit",
  TenantsView: "mail.platform.tenants.view",
  TenantsEdit: "mail.platform.tenants.edit",
} as const;

// ---- domains ----
export interface DkimKey { id: string; selector: string; status: string; activatedAt: string | null; retireAt: string | null }
export interface Domain {
  id: string; domain: string; isPrimary: boolean; verified: boolean; aliasOf: string | null; catchAllTarget: string | null;
  mxOk: boolean; spfOk: boolean; dkimOk: boolean; dmarcOk: boolean; mtaStsOk: boolean; lastCheckedAt: string | null; dkimKeys: DkimKey[];
}
export interface DnsRecordCheck { kind: string; type: string; name: string; expected: string; required: boolean; state: "OK" | "MISSING" | "WRONG" | "UNKNOWN"; found: string[] }

// ---- users ----
export interface LoginEntry { protocol: string; result: string; reason: string | null; ip: string | null; at: string }
export interface UserDetail {
  id: string; email: string; displayName: string; status: string; isTenantAdmin: boolean; twoFactorEnabled: boolean; quotaBytes: number; usedBytes: number;
  orgUnitId: string | null; imapEnabled: boolean; aliases: string[]; recentLogins: LoginEntry[]; lastLoginAt: string | null;
}
export interface ImportRow { email: string; displayName: string; password: string | null; isTenantAdmin: boolean; quotaGb: number | null; orgUnit: string | null }
export interface ImportResult { rows: ImportRow[]; errors: { row: number; field: string; code: string }[]; created: number; passwords: { email: string; password: string }[] }
export interface OrgUnit { id: string; name: string; parentId: string | null; users: number }

// ---- groups / shared ----
export interface GroupMember { id: string; userId: string | null; email: string; name: string | null; role: string }
export interface Group { id: string; email: string; name: string; description: string | null; whoCanPost: string; allowExternal: boolean; moderated: boolean; members: GroupMember[] }
export interface SharedMember { userId: string; email: string; canRead: boolean; canSendAs: boolean; canSendOnBehalf: boolean }
export interface SharedMailbox { id: string; email: string; name: string; members: SharedMember[] }

// ---- policies ----
export interface PolicyValue { key: string; type: "int" | "bool" | "list" | "text"; default: unknown; tenantValue: unknown; unitValue: unknown }
export interface RoutingRule {
  id: string; name: string; direction: "INBOUND" | "OUTBOUND";
  condition: { senderContains?: string | null; recipientContains?: string | null; subjectContains?: string | null; hasAttachment?: boolean | null };
  action: { type: "BCC" | "REDIRECT" | "REJECT" | "QUARANTINE"; address?: string | null }; sortOrder: number; isEnabled: boolean;
}
export interface CategoryRule { id: number; fromDomain: string | null; listId: string | null; category: string; isSystem: boolean; isEnabled: boolean }
export interface SpamSettings {
  spamThreshold: number; rejectThreshold: number; greylisting: boolean; suspiciousAttachments: string; phishingReportAddress: string | null;
  lists: { id: number; name: string; zone: string; kind: string; score: number; enabled: boolean; platform: boolean; licenseNote: string }[];
}
export interface QuarantineItem { id: string; createdAt: string; fromAddress: string | null; envelopeFrom: string | null; subject: string | null; reason: string; spamScore: number | null; recipients: number; status: string; report: string | null }

// ---- operations ----
export interface TraceRow { id: number; at: string; direction: string; status: string; envelopeFrom: string | null; rcpt: string; messageId: string | null; size: number; remoteIp: string | null; remoteHost: string | null; smtpResponse: string | null; spamScore: number | null }
export interface QueueItem { id: number; rcpt: string; domain: string; status: string; attempts: number; nextAttemptAt: string; lastResponse: string | null; lastMx: string | null; envelopeFrom: string; messageId: string | null; createdAt: string }
export interface TraceDetail { row: TraceRow; details: unknown; storedIn: { mailbox: string; labels: string[]; authResults: unknown; spamScore: number | null; spamReport: unknown }[]; queue: QueueItem[] }
export interface Suppression { id: number; address: string; reason: string; detail: string | null; createdAt: string; expiresAt: string | null }
export interface SendingHealth {
  days: number; delivered: number; bounced: number; complaints: number; bounceRate: number;
  domains: { domain: string; delivered: number; deferred: number; bounced: number; deliveryRate: number }[];
  ipListings: { ip: string; list: string; listed: boolean | null }[];
}
export interface Report { kind: string; from: string; to: string; columns: string[]; rows: (string | number | boolean | null)[][]; truncated: boolean; limit: number }

// ---- tenants ----
export interface TenantDetail {
  id: string; code: string; name: string; status: string; plan: string; userLimit: number; storageQuotaBytes: number; outboundDailyLimit: number;
  defaultLocale: string; defaultTimeZone: string; suspendTemporary: boolean; branding: string;
  usage: { users: number; storageBytes: number; received30d: number; sent30d: number; domains: string[] };
}

const q = (o: Record<string, string | number | boolean | null | undefined>) =>
  new URLSearchParams(Object.entries(o).filter(([, v]) => v !== null && v !== undefined && v !== "").map(([k, v]) => [k, String(v)])).toString();

export const adminApi = {
  domains: () => api.get<Domain[]>("/api/v1/admin/domains"),
  records: (id: string, check: boolean) => api.get<{ domain: Domain; records: DnsRecordCheck[] }>(`/api/v1/admin/domains/${id}/records?check=${check}`),
  addDomain: (domain: string, aliasOfDomainId: string | null) => api.post<string>("/api/v1/admin/domains", { domain, aliasOfDomainId }),
  deleteDomain: (id: string) => api.delete<boolean>(`/api/v1/admin/domains/${id}`),
  catchAll: (id: string, target: string | null) => api.put<boolean>(`/api/v1/admin/domains/${id}/catch-all`, { target }),
  rotateDkim: (id: string) => api.post<DkimKey>(`/api/v1/admin/domains/${id}/dkim/rotate`, {}),

  user: (id: string) => api.get<UserDetail>(`/api/v1/admin/users/${id}`),
  updateUser: (id: string, b: { displayName: string; isTenantAdmin: boolean; quotaBytes: number; orgUnitId: string | null; imapEnabled: boolean }) =>
    api.put<boolean>(`/api/v1/admin/users/${id}`, { id, ...b }),
  resetPassword: (id: string) => api.post<string>(`/api/v1/admin/users/${id}/reset-password`, {}),
  addAlias: (id: string, email: string) => api.post<boolean>(`/api/v1/admin/users/${id}/aliases`, { email }),
  removeAlias: (id: string, email: string) => api.delete<boolean>(`/api/v1/admin/users/${id}/aliases/${encodeURIComponent(email)}`),
  changeAddress: (id: string, email: string) => api.put<boolean>(`/api/v1/admin/users/${id}/address`, { email }),
  deleteUser: (id: string, transferTo: string | null) => api.delete<number>(`/api/v1/admin/users/${id}${transferTo ? `?transferTo=${transferTo}` : ""}`),
  setStatus: (id: string, status: "ACTIVE" | "SUSPENDED") => api.put<boolean>(`/api/v1/admin/users/${id}/status`, { status }),
  parseUsers: (file: File) => {
    const f = new FormData();
    f.append("file", file);
    return api.form<ImportRow[]>("/api/v1/admin/users/import/parse", f);
  },
  importUsers: (rows: ImportRow[], commit: boolean) => api.post<ImportResult>("/api/v1/admin/users/import", { rows, commit }),
  orgUnits: () => api.get<OrgUnit[]>("/api/v1/admin/org-units"),
  setDirectoryHidden: (userId: string, hidden: boolean) => api.put<boolean>(`/api/v1/admin/directory/${userId}`, { hidden }),

  groups: () => api.get<Group[]>("/api/v1/admin/groups"),
  saveGroup: (id: string | null, g: Omit<Group, "id" | "members">) => (id ? api.put<string>(`/api/v1/admin/groups/${id}`, { id, ...g }) : api.post<string>("/api/v1/admin/groups", g)),
  deleteGroup: (id: string) => api.delete<boolean>(`/api/v1/admin/groups/${id}`),
  addMember: (id: string, email: string, role: string) => api.post<string>(`/api/v1/admin/groups/${id}/members`, { email, role }),
  removeMember: (id: string, memberId: string) => api.delete<boolean>(`/api/v1/admin/groups/${id}/members/${memberId}`),
  shared: () => api.get<SharedMailbox[]>("/api/v1/admin/shared-mailboxes"),
  createShared: (email: string, name: string) => api.post<string>("/api/v1/admin/shared-mailboxes", { email, name }),
  setSharedMember: (id: string, m: { userId: string; canRead: boolean; canSendAs: boolean; canSendOnBehalf: boolean }) =>
    api.put<boolean>(`/api/v1/admin/shared-mailboxes/${id}/members`, { sharedMailboxId: id, ...m }),

  policies: (orgUnitId: string | null) => api.get<PolicyValue[]>(`/api/v1/admin/policies${orgUnitId ? `?orgUnitId=${orgUnitId}` : ""}`),
  setPolicy: (key: string, orgUnitId: string | null, value: unknown) => api.put<boolean>(`/api/v1/admin/policies/${key}`, { orgUnitId, value }),
  routingRules: () => api.get<RoutingRule[]>("/api/v1/admin/routing-rules"),
  saveRoutingRule: (r: Omit<RoutingRule, "id" | "sortOrder"> & { id: string | null }) =>
    r.id ? api.put<string>(`/api/v1/admin/routing-rules/${r.id}`, r) : api.post<string>("/api/v1/admin/routing-rules", r),
  deleteRoutingRule: (id: string) => api.delete<boolean>(`/api/v1/admin/routing-rules/${id}`),
  categoryRules: () => api.get<CategoryRule[]>("/api/v1/admin/category-rules"),
  addCategoryRule: (r: { fromDomain: string | null; listId: string | null; category: string }) => api.post<number>("/api/v1/admin/category-rules", r),
  deleteCategoryRule: (id: number) => api.delete<boolean>(`/api/v1/admin/category-rules/${id}`),
  spamSettings: () => api.get<SpamSettings>("/api/v1/admin/spam/settings"),
  saveSpamSettings: (s: Omit<SpamSettings, "lists">) => api.put<boolean>("/api/v1/admin/spam/settings", s),
  toggleList: (id: number, enabled: boolean) => api.put<boolean>(`/api/v1/admin/spam/lists/${id}`, { enabled }),
  quarantine: (status: string, page: number) => api.get<Paged<QuarantineItem>>(`/api/v1/admin/quarantine?${q({ status, page, pageSize: 50 })}`),
  release: (id: string) => api.post<boolean>(`/api/v1/admin/quarantine/${id}/release`, {}),
  discard: (id: string) => api.delete<boolean>(`/api/v1/admin/quarantine/${id}`),

  trace: (f: { sender?: string; recipient?: string; messageId?: string; from?: string; to?: string; page: number }) =>
    api.get<Paged<TraceRow>>(`/api/v1/admin/trace?${q({ ...f, pageSize: 50 })}`),
  traceDetail: (id: number) => api.get<TraceDetail>(`/api/v1/admin/trace/${id}`),
  queue: (status: string, domain: string, page: number) => api.get<Paged<QueueItem>>(`/api/v1/admin/queue?${q({ status, domain, page, pageSize: 50 })}`),
  retry: (ids: number[]) => api.post<number>("/api/v1/admin/queue/retry", { ids }),
  cancel: (ids: number[]) => api.post<number>("/api/v1/admin/queue/cancel", { ids }),
  suppressions: (search: string, page: number) => api.get<Paged<Suppression>>(`/api/v1/admin/suppressions?${q({ search, page, pageSize: 50 })}`),
  removeSuppression: (id: number) => api.delete<boolean>(`/api/v1/admin/suppressions/${id}`),
  health: (days: number) => api.get<SendingHealth>(`/api/v1/admin/health?days=${days}`),
  report: (kind: string, from: string | null, to: string | null) => api.get<Report>(`/api/v1/admin/reports/${kind}?${q({ from, to })}`),
  reportXlsx: (kind: string, from: string | null, to: string | null) => api.blob(`/api/v1/admin/reports/${kind}/xlsx?${q({ from, to })}`),

  tenant: (id: string) => api.get<TenantDetail>(`/api/v1/admin/tenants/${id}`),
  updateTenant: (id: string, t: Omit<TenantDetail, "id" | "code" | "status" | "usage">) => api.put<boolean>(`/api/v1/admin/tenants/${id}`, { id, ...t }),
  setTenantStatus: (id: string, status: "ACTIVE" | "SUSPENDED") => api.put<boolean>(`/api/v1/admin/tenants/${id}/status`, { status }),
  exportTenant: (id: string) => api.post<string>(`/api/v1/admin/tenants/${id}/export`, {}),
  supportSignIn: (id: string, userId: string, reason: string) => api.post<string>(`/api/v1/admin/tenants/${id}/support-signin`, { userId, reason }),
};

export function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}
