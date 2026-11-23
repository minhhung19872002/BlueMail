import { api } from "../../api/client";

// Confidential mode (Phase 12): the recipient's page (no account) and the sender's status/revoke calls.

export interface ConfidentialMeta {
  subject: string;
  from: string;
  expiresAt: string;
  requireCode: boolean;
  recipient: string;
}

export interface ConfidentialFile {
  name: string;
  contentType: string;
  size: number;
  url: string;
}

export interface ConfidentialContent {
  subject: string;
  from: string;
  date: string;
  expiresAt: string;
  htmlDocument: string | null;
  text: string | null;
  files: ConfidentialFile[];
}

export interface ConfidentialStatus {
  id: string;
  expiresAt: string;
  requireCode: boolean;
  revokedAt: string | null;
  recipients: { address: string; opens: number; firstOpenedAt: string | null }[];
}

const base = (token: string) => `/api/v1/confidential/${encodeURIComponent(token)}`;

export const confidentialApi = {
  meta: (token: string) => api.get<ConfidentialMeta>(base(token)),
  sendCode: (token: string) => api.post<boolean>(`${base(token)}/code`, {}),
  open: (token: string, code: string | null) => api.post<ConfidentialContent>(`${base(token)}/open`, { code }),
  status: (rfcMessageId: string) => api.get<ConfidentialStatus | null>(`/api/v1/mail/confidential?rfcMessageId=${encodeURIComponent(rfcMessageId)}`),
  setRevoked: (id: string, revoked: boolean) => api.put<boolean>(`/api/v1/mail/confidential/${id}/revoked`, { revoked }),
};

/** Link lifetimes offered in the compose window (days); must match DraftConfidential.AllowedDays on the server. */
export const confidentialDays = [1, 7, 30, 90, 365] as const;
