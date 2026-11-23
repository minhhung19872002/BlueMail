// DTOs of the BlueMail API (/api/v1). Enum values are UPPER_SNAKE strings.

export interface Me {
  id: string;
  email: string;
  displayName: string;
  tenantId: string;
  tenantName: string;
  locale: string;
  timeZone: string;
  hourCycle: "h12" | "h23" | null;
  weekStart: number | null;
  isPlatformAdmin: boolean;
  isTenantAdmin: boolean;
  twoFactorEnabled: boolean;
  mustChangePassword: boolean;
  permissions: string[];
  branding: string;
}

export type LabelVisibility = "SHOW" | "HIDE" | "IF_UNREAD";

export interface LabelSummary {
  id: string;
  systemKey: string | null;
  name: string;
  parentId: string | null;
  colorBg: string | null;
  colorText: string | null;
  showInList: LabelVisibility;
  showInMessageList: boolean;
  total: number;
  unread: number;
}

export interface Labels {
  mailboxId: string;
  usedBytes: number;
  quotaBytes: number;
  labels: LabelSummary[];
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export type UserStatus = "ACTIVE" | "SUSPENDED" | "DELETED";

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  status: UserStatus;
  isTenantAdmin: boolean;
  twoFactorEnabled: boolean;
  quotaBytes: number;
  lastLoginAt: string | null;
  createdAt: string;
}

export interface Tenant {
  id: string;
  code: string;
  name: string;
  kind: "CUSTOMER" | "PLATFORM";
  status: "ACTIVE" | "SUSPENDED" | "TRIAL";
  plan: string;
  userLimit: number;
  storageQuotaBytes: number;
  defaultLocale: string;
  defaultTimeZone: string;
  users: number;
  domains: string[];
}

export interface SessionInfo {
  id: string;
  device: string | null;
  ip: string | null;
  userAgent: string | null;
  createdAt: string;
  lastSeenAt: string;
  isCurrent: boolean;
}

export const Permission = {
  PlatformTenantsView: "mail.platform.tenants.view",
  PlatformTenantsEdit: "mail.platform.tenants.edit",
  AdminUsersView: "mail.admin.users.view",
  AdminUsersEdit: "mail.admin.users.edit",
} as const;
