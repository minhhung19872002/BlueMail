import { api } from "../../api/client";
import type { ConversationPage, MailChanges, MessageBody, Original, Thread } from "../../api/types";

// Calls of the mail reading API (docs/05-api.md). Query keys live here so invalidation stays consistent.

export const mailKeys = {
  labels: ["labels"] as const,
  lists: ["conversations"] as const,
  list: (scope: string, cursor: string | null) => ["conversations", scope, cursor] as const,
  thread: (id: string) => ["thread", id] as const,
  body: (id: string, images: boolean) => ["body", id, images] as const,
};

export interface ListScope {
  labelId: string | null; // null = All Mail
  q?: string; // Gmail-style search query (PROMPT 6.7)
}

export function scopeKey(s: ListScope): string {
  return (s.labelId ?? "all") + (s.q ? "?" + s.q : "");
}

export function listConversations(scope: ListScope, cursor: string | null, limit = 50, threads = true): Promise<ConversationPage> {
  const q = new URLSearchParams({ limit: String(limit) });
  if (!threads) q.set("threads", "false");
  if (scope.labelId) q.set("labelId", scope.labelId);
  else q.set("view", "all");
  if (scope.q) q.set("q", scope.q);
  if (cursor) q.set("cursor", cursor);
  return api.get<ConversationPage>(`/api/v1/mail/conversations?${q.toString()}`);
}

export interface Snoozed {
  threadId: string;
  untilAt: string;
}

export const getSnoozed = () => api.get<Snoozed[]>("/api/v1/mail/snoozed");

export interface UnsubscribeResult {
  outcome: "DONE" | "OPEN_URL";
  url: string | null;
}

export const unsubscribe = (messageId: string) => api.post<UnsubscribeResult>(`/api/v1/mail/messages/${messageId}/unsubscribe`, {});

export const getThread = (id: string) => api.get<Thread>(`/api/v1/mail/threads/${id}`);

export const getBody = (id: string, showImages: boolean) =>
  api.get<MessageBody>(`/api/v1/mail/messages/${id}/body?showImages=${showImages ? "true" : "false"}`);

export const getOriginal = (id: string) => api.get<Original>(`/api/v1/mail/messages/${id}/original`);

export const trustImages = (id: string) => api.post<boolean>(`/api/v1/mail/messages/${id}/trust-images`, {});

export const getChanges = (since: number) => api.get<MailChanges>(`/api/v1/mail/changes?sinceModseq=${since}`);

export type MailAction =
  | "markRead" | "markUnread" | "star" | "unstar" | "important" | "notImportant" | "archive" | "moveToInbox"
  | "trash" | "untrash" | "spam" | "notSpam" | "addLabel" | "removeLabel" | "moveTo" | "deleteForever" | "mute" | "unmute" | "reportPhishing" | "snooze" | "unsnooze";

export interface ActionRequest {
  action: MailAction;
  threadIds?: string[];
  messageIds?: string[];
  labelId?: string;
  fromLabelId?: string;
  snoozeUntil?: string;
}

/** Server limit per request; "select all" beyond it runs as a background bulk job (PROMPT 8.3). */
export const maxSelection = 500;

/** Most conversations one bulk job changes (BulkMailJobWorker.MaxThreads). */
export const maxBulk = 50_000;

export interface BulkJob {
  id: string;
  status: "QUEUED" | "RUNNING" | "DONE" | "FAILED";
  total: number;
  done: number;
  error: string | null;
}

export const startBulkJob = (r: { action: MailAction; labelId?: string; fromLabelId?: string; snoozeUntil?: string; scopeLabelId: string | null; query?: string }) =>
  api.post<BulkJob>("/api/v1/mail/bulk-jobs", r);

export const getBulkJob = (id: string) => api.get<BulkJob>(`/api/v1/mail/bulk-jobs/${id}`);

export const act = (r: ActionRequest) => api.post<{ messages: number; modseq: number }>("/api/v1/mail/actions", r);

export const createLabel = (name: string, colorBg: string | null, parentId: string | null) =>
  api.post<string>("/api/v1/mail/labels", { name, colorBg, colorText: colorBg ? "#ffffff" : null, parentId });

export const deleteLabel = (id: string) => api.delete<boolean>(`/api/v1/mail/labels/${id}`);
