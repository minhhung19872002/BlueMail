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
  domains: string[];
  /** Policy external_sender_banner (PROMPT 7.4). */
  externalSenderBanner: boolean;
  /** Set when a platform administrator is signed in as this user for support (PROMPT 7.6). */
  supportActor: string | null;
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

// ---- mail reading (Phase 4) ----

export interface Participant {
  name: string | null;
  address: string;
  unread: boolean;
}

export interface ConversationSummary {
  threadId: string;
  latestMessageId: string;
  lastMessageAt: string;
  subject: string | null;
  snippet: string | null;
  participants: Participant[];
  messageCount: number;
  unreadCount: number;
  starred: boolean;
  important: boolean;
  hasAttachments: boolean;
  attachmentNames: string[];
  labelIds: string[];
  muted: boolean;
}

export interface ConversationPage {
  items: ConversationSummary[];
  nextCursor: string | null;
  total: number;
  /** False for very large lists: total is then a lower bound ("1–50 of many"). */
  totalExact: boolean;
  /** The order applied: very large lists stay by date even when another order was asked for. */
  sort: ListSort;
}

export type ListSort = "date" | "from" | "subject" | "size";

export interface Address {
  name: string | null;
  address: string;
}

export type VirusStatus = "CLEAN" | "INFECTED" | "SUSPICIOUS" | "UNSCANNED";

export interface Attachment {
  id: string;
  partPath: string;
  fileName: string | null;
  contentType: string;
  size: number;
  inline: boolean;
  virusStatus: VirusStatus;
  downloadUrl: string | null;
  viewUrl: string | null;
}

export interface MessageView {
  id: string;
  threadId: string;
  rfcMessageId: string | null;
  subject: string | null;
  from: Address | null;
  to: Address[];
  cc: Address[];
  replyTo: Address[];
  sentAt: string | null;
  receivedAt: string;
  size: number;
  snippet: string | null;
  seen: boolean;
  starred: boolean;
  important: boolean;
  draft: boolean;
  labelIds: string[];
  authResults: string | null;
  spamReport: string | null;
  spamScore: number | null;
  listUnsubscribe: string | null;
  modseq: number;
}

export interface ThreadMessage {
  message: MessageView;
  attachments: Attachment[];
  rawUrl: string;
}

export interface Thread {
  threadId: string;
  subject: string | null;
  muted: boolean;
  messages: ThreadMessage[];
}

export interface MessageBody {
  htmlDocument: string | null;
  text: string | null;
  hasRemoteImages: boolean;
  remoteImagesShown: boolean;
  senderTrusted: boolean;
  calendar: string | null;
  zipUrl: string | null;
}

export interface Original {
  headers: string;
  authResults: string | null;
  size: number;
  rawUrl: string;
}

export interface MailChanges {
  highestModseq: number;
  changed: { id: string; threadId: string; modseq: number; seen: boolean; starred: boolean; labelIds: string[] }[];
  removed: string[];
  tooOld: boolean;
}

export interface AuthResultsSummary {
  spf?: string;
  dkim?: { result: string; domain: string | null; selector: string | null; reason: string }[];
  dmarc?: string;
  dmarcPolicy?: string;
  fromDomain?: string | null;
  arc?: string;
  spfDomain?: string | null;
  /** Received over TLS at our MX (absent on mail stored before this was recorded and on internal mail). */
  tls?: boolean;
}
