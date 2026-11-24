import { describe, expect, it } from "vitest";
import dayjs from "dayjs";
import { calendarHtml, rangeDays } from "./calendarMail";
import type { CalendarEvent } from "./calendarApi";

const labels = { day: (iso: string) => iso.slice(0, 10), time: (iso: string) => iso.slice(11, 16), allDay: "All day", busy: "Busy", nothing: "Nothing" };
const ev = (summary: string, start: string, end: string, location: string | null = null) =>
  ({ summary, start, end, allDay: false, location } as unknown as CalendarEvent);

describe("e-mail calendar", () => {
  it("covers the chosen days", () => {
    const [from, to] = rangeDays("next7", dayjs("2026-11-24T15:00:00Z"));
    expect(to.diff(from, "day")).toBe(7);
    const [t1, t2] = rangeDays("tomorrow", dayjs("2026-11-24T15:00:00Z"));
    expect(t2.diff(t1, "day")).toBe(1);
  });

  it("shows busy blocks only, or escaped titles and places", () => {
    const list = [ev("<b>Secret</b>", "2026-11-25T09:00:00Z", "2026-11-25T10:00:00Z", "Room & 1")];
    const busy = calendarHtml(list, "BUSY", labels);
    expect(busy).toContain("Busy");
    expect(busy).not.toContain("Secret");
    const full = calendarHtml(list, "FULL", labels);
    expect(full).toContain("&lt;b&gt;Secret&lt;/b&gt;");
    expect(full).toContain("Room &amp; 1");
    expect(calendarHtml([], "FULL", labels)).toBe("<p>Nothing</p>");
  });
});
