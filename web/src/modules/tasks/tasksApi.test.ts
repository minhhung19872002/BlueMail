import { describe, expect, it } from "vitest";
import { dueState, dueTaskReminders, followUpDue, inputOf, taskReminderKey, type TaskItem } from "./tasksApi";

describe("tasks", () => {
  it("classifies due dates by calendar day", () => {
    expect(dueState("2026-11-23", "2026-11-24")).toBe("overdue");
    expect(dueState("2026-11-24", "2026-11-24")).toBe("today");
    expect(dueState("2026-12-01", "2026-11-24")).toBe("soon");
    expect(dueState("2026-12-02", "2026-11-24")).toBe("later");
  });

  it("keeps every field when one changes", () => {
    const t: TaskItem = { id: "1", title: "A", notes: "n", dueDate: "2026-12-01", important: true, completedAt: null, messageId: null, threadId: null, createdAt: "", category: "green", assignedTo: null, assignedBy: null, reminderAt: null };
    expect(inputOf(t, { completed: true })).toEqual({ title: "A", notes: "n", dueDate: "2026-12-01", important: true, completed: true, category: "green", reminderAt: null });
  });
});

describe("follow-up flags", () => {
  it("dates them like Outlook", () => {
    // 2026-11-24 is a Tuesday.
    expect(followUpDue("today", "2026-11-24")).toBe("2026-11-24");
    expect(followUpDue("tomorrow", "2026-11-24")).toBe("2026-11-25");
    expect(followUpDue("thisWeek", "2026-11-24")).toBe("2026-11-27");
    expect(followUpDue("nextWeek", "2026-11-24")).toBe("2026-12-04");
    expect(followUpDue("thisWeek", "2026-11-27")).toBe("2026-11-27");
    expect(followUpDue("thisWeek", "2026-11-28")).toBe("2026-11-28");
    expect(followUpDue("nextWeek", "2026-11-28")).toBe("2026-12-04");
    expect(followUpDue("nextWeek", "2026-11-29")).toBe("2026-12-04");
    expect(followUpDue("noDate", "2026-11-24")).toBeNull();
  });
});

describe("task reminders", () => {
  const base = { id: "a", title: "A", notes: null, dueDate: null, important: false, completedAt: null, messageId: null, threadId: null, createdAt: "",
    category: null, assignedTo: null, assignedBy: null } as const;
  it("are due once their time has come, for a day, unless done or shown", () => {
    const now = Date.parse("2026-12-01T10:00:00Z");
    const due = { ...base, reminderAt: "2026-12-01T09:59:00Z" };
    const later = { ...base, id: "b", reminderAt: "2026-12-01T10:01:00Z" };
    const old = { ...base, id: "c", reminderAt: "2026-11-29T09:00:00Z" };
    const done = { ...base, id: "d", reminderAt: "2026-12-01T09:00:00Z", completedAt: "2026-12-01T09:30:00Z" };
    expect(dueTaskReminders([later, due, old, done], now, new Set()).map((t) => t.id)).toEqual(["a"]);
    expect(dueTaskReminders([due], now, new Set([taskReminderKey(due)]))).toEqual([]);
  });
});
