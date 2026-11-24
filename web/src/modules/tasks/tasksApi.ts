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
}

export interface TaskInput {
  title: string | null;
  notes: string | null;
  dueDate: string | null;
  important: boolean;
  completed: boolean;
  messageId?: string | null;
}

export const taskKeys = { all: ["tasks"] as const, view: (v: TaskView) => ["tasks", v] as const };

export const tasksApi = {
  list: (view: TaskView) => api.get<TaskItem[]>(`/api/v1/tasks?view=${view}`),
  create: (t: TaskInput) => api.post<TaskItem>("/api/v1/tasks", t),
  update: (id: string, t: TaskInput) => api.put<TaskItem>(`/api/v1/tasks/${id}`, t),
  remove: (id: string) => api.delete<boolean>(`/api/v1/tasks/${id}`),
  fromMessage: (messageId: string) =>
    api.post<TaskItem>("/api/v1/tasks", { title: null, notes: null, dueDate: null, important: false, completed: false, messageId }),
};

export const inputOf = (t: TaskItem, change: Partial<TaskInput> = {}): TaskInput => ({
  title: t.title, notes: t.notes, dueDate: t.dueDate, important: t.important, completed: t.completedAt !== null, ...change,
});

/** "overdue", "today", "soon" (within 7 days) or "later", by calendar date in the user's zone. */
export function dueState(due: string, today: string): "overdue" | "today" | "soon" | "later" {
  if (due < today) return "overdue";
  if (due === today) return "today";
  const days = (Date.parse(due + "T00:00:00Z") - Date.parse(today + "T00:00:00Z")) / 86_400_000;
  return days <= 7 ? "soon" : "later";
}
