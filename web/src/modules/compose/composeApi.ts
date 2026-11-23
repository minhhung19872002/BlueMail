import { api } from "../../api/client";
import type { Recipient } from "./compose";

export interface DraftAttachment {
  kind: "upload" | "part";
  uploadId: string | null;
  messageId: string | null;
  partPath: string | null;
  fileName: string;
  contentType: string;
  size: number;
  inline: boolean;
}

export interface DraftState {
  from: string | null;
  to: Recipient[];
  cc: Recipient[];
  bcc: Recipient[];
  subject: string;
  html: string;
  inReplyTo: string | null;
  references: string[];
  replyToMessageId: string | null;
  mode: "new" | "reply" | "replyAll" | "forward";
  attachments: DraftAttachment[];
  /** Confidential mode (Phase 12): outside recipients get a link that expires; optional mailed code. */
  confidential?: { expiresDays: number; requireCode: boolean } | null;
}

export interface DraftDto {
  id: string;
  version: number;
  state: DraftState;
  messageId: string | null;
  updatedAt: string;
}

export interface FromOption {
  address: string;
  name: string;
  kind: "primary" | "alias" | "shared";
}

export interface Suggestion {
  name: string | null;
  address: string;
  kind: "user" | "group" | "shared" | "recent";
}

export interface SendResult {
  outgoingId: string;
  draftId: string;
  sendAt: string;
  scheduled: boolean;
}

export interface Signature {
  id: string;
  name: string;
  html: string;
  isDefaultNew: boolean;
  isDefaultReply: boolean;
  fromAddress: string | null;
}

export interface Template {
  id: string;
  name: string;
  subject: string;
  html: string;
}

export interface PendingSend {
  id: string;
  draftId: string;
  status: "UNDO_WINDOW" | "SCHEDULED" | "SUBMITTING";
  sendAt: string;
}

export const emptyDraft = (): DraftState => ({
  from: null, to: [], cc: [], bcc: [], subject: "", html: "", inReplyTo: null, references: [], replyToMessageId: null, mode: "new", attachments: [],
});

export const composeApi = {
  fromOptions: () => api.get<FromOption[]>("/api/v1/mail/compose/from-options"),
  fromMessage: (messageId: string, mode: "reply" | "replyAll" | "forward") =>
    api.get<DraftState>(`/api/v1/mail/compose/from-message/${messageId}?mode=${mode}`),
  suggest: (q: string) => api.get<Suggestion[]>(`/api/v1/mail/compose/suggest?q=${encodeURIComponent(q)}`),
  save: (draftId: string | null, version: number | null, state: DraftState) => api.post<DraftDto>("/api/v1/mail/drafts", { draftId, version, state }),
  get: (draftId: string) => api.get<DraftDto>(`/api/v1/mail/drafts/${draftId}`),
  byMessage: (messageId: string) => api.get<DraftDto>(`/api/v1/mail/drafts/by-message/${messageId}`),
  discard: (draftId: string) => api.delete<boolean>(`/api/v1/mail/drafts/${draftId}`),
  send: (draftId: string, body: { scheduleAt?: string; archive?: boolean; undoSeconds: number }) =>
    api.post<SendResult>(`/api/v1/mail/drafts/${draftId}/send`, body),
  cancel: (outgoingId: string) => api.post<string>(`/api/v1/mail/outgoing/${outgoingId}/cancel`, {}),
  pending: () => api.get<PendingSend[]>("/api/v1/mail/outgoing"),
  signatures: () => api.get<Signature[]>("/api/v1/mail/signatures"),
  saveSignature: (s: Omit<Signature, "id"> & { id: string | null }) => api.post<string>("/api/v1/mail/signatures", s),
  deleteSignature: (id: string) => api.delete<boolean>(`/api/v1/mail/signatures/${id}`),
  templates: () => api.get<Template[]>("/api/v1/mail/templates"),
  saveTemplate: (t: Omit<Template, "id"> & { id: string | null }) => api.post<string>("/api/v1/mail/templates", t),
  deleteTemplate: (id: string) => api.delete<boolean>(`/api/v1/mail/templates/${id}`),
  uploadPreview: (uploadId: string) => api.blob(`/api/v1/uploads/${uploadId}/content`),
};
