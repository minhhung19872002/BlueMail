import { api } from "../../api/client";

// To-dos (docs/05-api.md "Tasks").

export type TaskView = "my" | "important" | "planned" | "completed";

export interface TaskItem {
  id: string;
  title: string;
  notes: string | null;
  /** YYYY-MM-DD */
  dueDate: string | null;
  important: boolean;
  completedAt: string | null;
  messageId: string | null;
  threadId: string | null;
  createdAt: string;
  /** Outlook "Categorize": a colour name of the calendar palette. */
  category: string | null;
  /** Assigned within the organisation: the assignee's name on the assigner's task, the assigner's on the copy. */
  assignedTo: string | null;
  assignedBy: string | null;
}

export interface TaskInput {
  title: string | null;
  notes: string | null;
  dueDate: string | null;
  important: boolean;
  completed: boolean;
  messageId?: string | null;
  category?: string | null;
}

export const taskKeys = { all: ["tasks"] as const, view: (v: TaskView) => ["tasks", v] as const };

export const tasksApi = {
  list: (view: TaskView) => api.get<TaskItem[]>(`/api/v1/tasks?view=${view}`),
  create: (t: TaskInput) => api.post<TaskItem>("/api/v1/tasks", t),
  update: (id: string, t: TaskInput) => api.put<TaskItem>(`/api/v1/tasks/${id}`, t),
  remove: (id: string) => api.delete<boolean>(`/api/v1/tasks/${id}`),
  assign: (id: string, email: string) => api.post<TaskItem>(`/api/v1/tasks/${id}/assign`, { email }),
  fromMessage: (messageId: string) =>
    api.post<TaskItem>("/api/v1/tasks", { title: null, notes: null, dueDate: null, important: false, completed: false, messageId }),
};

export const inputOf = (t: TaskItem, change: Partial<TaskInput> = {}): TaskInput => ({
  title: t.title, notes: t.notes, dueDate: t.dueDate, important: t.important, completed: t.completedAt !== null, category: t.category, ...change,
});

/** "overdue", "today", "soon" (within 7 days) or "later", by calendar date in the user's zone. */
export function dueState(due: string, today: string): "overdue" | "today" | "soon" | "later" {
  if (due < today) return "overdue";
  if (due === today) return "today";
  const days = (Date.parse(due + "T00:00:00Z") - Date.parse(today + "T00:00:00Z")) / 86_400_000;
  return days <= 7 ? "soon" : "later";
}

export type FollowUp = "today" | "tomorrow" | "thisWeek" | "nextWeek" | "noDate";

/**
 * Due date of an Outlook follow-up flag, from today's date (YYYY-MM-DD, user's zone): this week and next week end
 * on Friday (the last working day); a flag set after Friday for "this week" is due today.
 */
export function followUpDue(kind: FollowUp, today: string): string | null {
  const d = new Date(today + "T00:00:00Z");
  const add = (days: number) => new Date(d.getTime() + days * 86_400_000).toISOString().slice(0, 10);
  const toFriday = (5 - d.getUTCDay() + 7) % 7; // 0 on Friday
  switch (kind) {
    case "today": return today;
    case "tomorrow": return add(1);
    case "thisWeek": return d.getUTCDay() === 6 || d.getUTCDay() === 0 ? today : add(toFriday);
    case "nextWeek": return add((d.getUTCDay() === 6 ? 6 : d.getUTCDay() === 0 ? 5 : toFriday + 7));
    default: return null;
  }
}
