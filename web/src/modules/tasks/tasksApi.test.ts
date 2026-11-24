import { describe, expect, it } from "vitest";
import { dueState, followUpDue, inputOf, type TaskItem } from "./tasksApi";

describe("tasks", () => {
  it("classifies due dates by calendar day", () => {
    expect(dueState("2026-11-23", "2026-11-24")).toBe("overdue");
    expect(dueState("2026-11-24", "2026-11-24")).toBe("today");
    expect(dueState("2026-12-01", "2026-11-24")).toBe("soon");
    expect(dueState("2026-12-02", "2026-11-24")).toBe("later");
  });

  it("keeps every field when one changes", () => {
    const t: TaskItem = { id: "1", title: "A", notes: "n", dueDate: "2026-12-01", important: true, completedAt: null, messageId: null, threadId: null, createdAt: "", category: "green" };
    expect(inputOf(t, { completed: true })).toEqual({ title: "A", notes: "n", dueDate: "2026-12-01", important: true, completed: true, category: "green" });
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
