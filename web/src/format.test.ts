import { describe, expect, it } from "vitest";
import { bytes, listTime } from "./format";

describe("listTime uses the user's time zone, never UTC", () => {
  const now = new Date("2026-11-23T03:00:00Z"); // 10:00 in Ho Chi Minh City

  it("same local day shows the time", () => {
    // 2026-11-22T20:30Z is already 23 Nov 03:30 in Asia/Ho_Chi_Minh, but still 22 Nov in UTC.
    const s = listTime("2026-11-22T20:30:00Z", { locale: "vi", timeZone: "Asia/Ho_Chi_Minh", hourCycle: "h23" }, now);
    expect(s).toBe("03:30");
  });

  it("other day of this year shows day and month", () => {
    const s = listTime("2026-03-05T10:00:00Z", { locale: "en-GB", timeZone: "Europe/London", hourCycle: null }, now);
    expect(s).toBe("5 Mar");
  });

  it("other year shows the full date in the user's locale", () => {
    expect(listTime("2024-12-31T23:30:00Z", { locale: "en-US", timeZone: "America/New_York", hourCycle: null }, now)).toBe("12/31/24");
    expect(listTime("2024-12-31T23:30:00Z", { locale: "de-DE", timeZone: "Europe/Berlin", hourCycle: null }, now)).toBe("01.01.25");
  });

  it("handles a DST change: 2026-03-29 01:30 UTC is 03:30 CEST in Berlin", () => {
    const s = listTime("2026-03-29T01:30:00Z", { locale: "de-DE", timeZone: "Europe/Berlin", hourCycle: "h23" }, new Date("2026-03-29T12:00:00Z"));
    expect(s).toBe("03:30");
  });
});

describe("bytes", () => {
  it("formats per locale", () => {
    expect(bytes(3.2 * 1024 ** 3, "en")).toBe("3.2 GB");
    expect(bytes(3.2 * 1024 ** 3, "vi")).toMatch(/^3,2\s?GB$/);
  });
});
