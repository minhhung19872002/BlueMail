import { describe, expect, it, vi } from "vitest";

const calls: unknown[] = [];
vi.mock("./mailApi", () => ({ act: async (r: unknown) => { calls.push(r); return { messages: 1, modseq: 1 }; }, mailKeys: {} }));

const { leavesList, runQuickStep } = await import("./QuickSteps");

describe("quick steps", () => {
  it("run their actions in order on the conversations", async () => {
    await runQuickStep({ id: "1", name: "Done", actions: [{ action: "markRead", labelId: null }, { action: "addLabel", labelId: "L" }, { action: "archive", labelId: null }] }, ["t1"]);
    expect(calls).toEqual([
      { action: "markRead", threadIds: ["t1"], labelId: undefined },
      { action: "addLabel", threadIds: ["t1"], labelId: "L" },
      { action: "archive", threadIds: ["t1"], labelId: undefined },
    ]);
  });

  it("know when the conversation leaves the list", () => {
    expect(leavesList({ id: "1", name: "x", actions: [{ action: "star", labelId: null }] })).toBe(false);
    expect(leavesList({ id: "1", name: "x", actions: [{ action: "star", labelId: null }, { action: "moveTo", labelId: "L" }] })).toBe(true);
  });
});
