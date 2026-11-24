import { describe, expect, it } from "vitest";
import { dueState, inputOf, type TaskItem } from "./tasksApi";

describe("tasks", () => {
  it("classifies due dates by calendar day", () => {
    expect(dueState("2026-11-23", "2026-11-24")).toBe("overdue");
    expect(dueState("2026-11-24", "2026-11-24")).toBe("today");
    expect(dueState("2026-12-01", "2026-11-24")).toBe("soon");
    expect(dueState("2026-12-02", "2026-11-24")).toBe("later");
  });

  it("keeps every field when one changes", () => {
    const t: TaskItem = { id: "1", title: "A", notes: "n", dueDate: "2026-12-01", important: true, completedAt: null, messageId: null, threadId: null, createdAt: "" };
    expect(inputOf(t, { completed: true })).toEqual({ title: "A", notes: "n", dueDate: "2026-12-01", important: true, completed: true });
  });
});
