import { describe, expect, it } from "vitest";
import { eventWhen } from "./InviteCard";

const vn = { locale: "en-GB", timeZone: "Asia/Ho_Chi_Minh", hourCycle: "h23" as const };

describe("event time on the invitation card", () => {
  it("shows the day once when the meeting starts and ends the same day, in the reader's zone", () => {
    expect(eventWhen("2026-11-26T02:00:00Z", "2026-11-26T03:00:00Z", false, vn)).toBe("Thu, 26 Nov 2026, 09:00 – 10:00");
  });

  it("shows both days when it crosses midnight in the reader's zone", () => {
    expect(eventWhen("2026-11-26T16:00:00Z", "2026-11-26T18:00:00Z", false, vn)).toBe("Thu, 26 Nov 2026, 23:00 – Fri, 27 Nov 2026, 01:00");
  });

  it("treats all-day ends as exclusive and never shifts the date by zone", () => {
    expect(eventWhen("2026-12-24T00:00:00Z", "2026-12-25T00:00:00Z", true, { ...vn, timeZone: "America/Los_Angeles" })).toBe("Thu, 24 Dec 2026");
    expect(eventWhen("2026-12-24T00:00:00Z", "2026-12-27T00:00:00Z", true, vn)).toBe("Thu, 24 Dec 2026 – Sat, 26 Dec 2026");
  });
});
