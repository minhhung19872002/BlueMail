import { api } from "../../api/client";

// Filters, blocked senders, vacation reply and forwarding (docs/05-api.md, PROMPT 6.8).

export type Important = "ALWAYS" | "NEVER" | null;
export type Category = "PRIMARY" | "SOCIAL" | "PROMOTIONS" | "UPDATES" | "FORUMS" | null;

export interface FilterActions {
  skipInbox: boolean;
  markRead: boolean;
  star: boolean;
  labelIds: string[];
  forwardTo: string | null;
  trash: boolean;
  neverSpam: boolean;
  important: Important;
  category: Category;
}

export const noActions: FilterActions = {
  skipInbox: false, markRead: false, star: false, labelIds: [], forwardTo: null, trash: false, neverSpam: false, important: null, category: null,
};

export function hasAction(a: FilterActions): boolean {
  return a.skipInbox || a.markRead || a.star || a.labelIds.length > 0 || !!a.forwardTo || a.trash || a.neverSpam || a.important !== null || a.category !== null;
}

export interface Filter {
  id: string;
  criteria: string;
  actions: FilterActions;
  sortOrder: number;
  isEnabled: boolean;
}

export interface FilterJob {
  id: string;
  status: "QUEUED" | "RUNNING" | "DONE" | "FAILED";
  total: number;
  done: number;
}

export interface ImportResult {
  imported: number;
  problems: { entry: number; code: string; detail: string | null }[];
}

export interface BlockedSender {
  id: string;
  addressOrDomain: string;
  createdAt: string;
}

export interface Vacation {
  enabled: boolean;
  startAt: string | null;
  endAt: string | null;
  subject: string;
  html: string;
  onlyContacts: boolean;
  onlyOrganization: boolean;
}

export interface ForwardingAddress {
  id: string;
  targetEmail: string;
  verified: boolean;
  isActive: boolean;
  keepCopy: boolean;
}

export interface ForwardingList {
  addresses: ForwardingAddress[];
  externalAllowed: boolean;
}

export const rulesKeys = {
  filters: ["filters"] as const,
  blocked: ["blocked"] as const,
  vacation: ["vacation"] as const,
  forwarding: ["forwarding"] as const,
};

export const rulesApi = {
  filters: () => api.get<Filter[]>("/api/v1/mail/filters"),
  saveFilter: (f: { id: string | null; criteria: string; actions: FilterActions; isEnabled: boolean }) =>
    f.id ? api.put<string>(`/api/v1/mail/filters/${f.id}`, f) : api.post<string>("/api/v1/mail/filters", f),
  deleteFilter: (id: string) => api.delete<boolean>(`/api/v1/mail/filters/${id}`),
  reorder: (ids: string[]) => api.put<boolean>("/api/v1/mail/filters/order", { ids }),
  matches: (q: string) => api.get<number>(`/api/v1/mail/filters/matches?q=${encodeURIComponent(q)}`),
  apply: (id: string) => api.post<FilterJob>(`/api/v1/mail/filters/${id}/apply`, {}),
  job: (id: string) => api.get<FilterJob>(`/api/v1/mail/filter-jobs/${id}`),
  exportXml: () => api.blob("/api/v1/mail/filters/export"),
  importXml: (file: File) => {
    const form = new FormData();
    form.append("file", file);
    return api.form<ImportResult>("/api/v1/mail/filters/import", form);
  },

  blocked: () => api.get<BlockedSender[]>("/api/v1/mail/blocked"),
  block: (addressOrDomain: string) => api.post<string>("/api/v1/mail/blocked", { addressOrDomain }),
  unblock: (id: string) => api.delete<boolean>(`/api/v1/mail/blocked/${id}`),

  vacation: () => api.get<Vacation>("/api/v1/mail/vacation"),
  saveVacation: (v: Vacation) => api.put<boolean>("/api/v1/mail/vacation", v),

  forwarding: () => api.get<ForwardingList>("/api/v1/mail/forwarding"),
  addForwarding: (targetEmail: string) => api.post<string>("/api/v1/mail/forwarding", { targetEmail }),
  verifyForwarding: (id: string, code: string) => api.post<boolean>(`/api/v1/mail/forwarding/${id}/verify`, { code }),
  setActive: (id: string | null, keepCopy: boolean) => api.put<boolean>("/api/v1/mail/forwarding/active", { id, keepCopy }),
  deleteForwarding: (id: string) => api.delete<boolean>(`/api/v1/mail/forwarding/${id}`),
};

/** The vacation reply is on right now (enabled and inside its dates). */
export function vacationActive(v: Vacation | undefined, now = new Date()): boolean {
  if (!v?.enabled) return false;
  if (v.startAt && now < new Date(v.startAt)) return false;
  if (v.endAt && now >= new Date(v.endAt)) return false;
  return true;
}
