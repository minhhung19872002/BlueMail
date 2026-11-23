import { api } from "../../api/client";

// Contacts, groups and the organisation directory (PROMPT 6.10, docs/05-api.md).

export interface LabeledValue {
  value: string;
  label: string | null;
}

export interface Contact {
  id: string;
  displayName: string;
  emails: LabeledValue[];
  phones: LabeledValue[];
  org: string | null;
  title: string | null;
  notes: string | null;
  hasPhoto: boolean;
  isAutoSaved: boolean;
  lastContactedAt: string | null;
  groupIds: string[];
}

export interface ContactPage {
  items: Contact[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ContactGroup {
  id: string;
  name: string;
  members: number;
}

export interface DirectoryEntry {
  kind: "USER" | "GROUP" | "SHARED";
  id: string;
  name: string;
  email: string;
  orgUnit: string | null;
}

export interface ContactInput {
  displayName: string;
  emails: LabeledValue[];
  phones: LabeledValue[];
  org: string | null;
  title: string | null;
  notes: string | null;
  groupIds: string[];
}

export const contactKeys = {
  all: ["contacts"] as const,
  groups: ["contact-groups"] as const,
};

export const contactsApi = {
  list: (q: string, groupId: string | null, other: boolean, page: number) => {
    const p = new URLSearchParams({ page: String(page), pageSize: "50", other: String(other) });
    if (q) p.set("q", q);
    if (groupId) p.set("groupId", groupId);
    return api.get<ContactPage>(`/api/v1/contacts?${p.toString()}`);
  },
  save: (id: string | null, c: ContactInput) => (id ? api.put<string>(`/api/v1/contacts/${id}`, c) : api.post<string>("/api/v1/contacts", c)),
  remove: (ids: string[]) => api.post<number>("/api/v1/contacts/delete", { ids }),
  photo: (id: string) => api.blob(`/api/v1/contacts/${id}/photo`),
  setPhoto: (id: string, file: File) => {
    const form = new FormData();
    form.append("file", file);
    return api.form<boolean>(`/api/v1/contacts/${id}/photo`, form, "PUT");
  },
  groups: () => api.get<ContactGroup[]>("/api/v1/contacts/groups"),
  saveGroup: (id: string | null, name: string) => (id ? api.put<string>(`/api/v1/contacts/groups/${id}`, { name }) : api.post<string>("/api/v1/contacts/groups", { name })),
  deleteGroup: (id: string) => api.delete<boolean>(`/api/v1/contacts/groups/${id}`),
  importFile: (file: File, groupId: string | null) => {
    const form = new FormData();
    form.append("file", file);
    return api.form<{ created: number; merged: number; skipped: number }>(`/api/v1/contacts/import${groupId ? `?groupId=${groupId}` : ""}`, form);
  },
  exportFile: (format: "vcf" | "csv", groupId: string | null) => api.blob(`/api/v1/contacts/export?format=${format}${groupId ? `&groupId=${groupId}` : ""}`),
  mergeDuplicates: () => api.post<number>("/api/v1/contacts/merge-duplicates", {}),
  directory: (q: string) => api.get<DirectoryEntry[]>(`/api/v1/contacts/directory?q=${encodeURIComponent(q)}`),
};
