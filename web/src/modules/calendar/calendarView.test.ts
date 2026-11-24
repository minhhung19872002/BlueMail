import { describe, expect, it } from "vitest";
import dayjs from "dayjs";
import { eventDates, layoutDay, repeatKind, ruleFor, step, visibleDays } from "./calendarView";

const tz = "Asia/Ho_Chi_Minh";

describe("calendar views", () => {
  it("shows a week from the user's first day and a 6-week month grid", () => {
    const anchor = dayjs.tz("2026-11-26 10:00", tz); // Thursday
    expect(visibleDays("week", anchor, 1).map((d) => d.format("YYYY-MM-DD"))).toEqual([
      "2026-11-23", "2026-11-24", "2026-11-25", "2026-11-26", "2026-11-27", "2026-11-28", "2026-11-29",
    ]);
    expect(visibleDays("week", anchor, 0)[0]!.format("YYYY-MM-DD")).toBe("2026-11-22");
    const month = visibleDays("month", anchor, 1);
    expect(month).toHaveLength(42);
    expect(month[0]!.format("YYYY-MM-DD")).toBe("2026-10-26");
    expect(visibleDays("day", anchor, 1).map((d) => d.format("YYYY-MM-DD"))).toEqual(["2026-11-26"]);
    expect(step("month", anchor, -1).format("YYYY-MM")).toBe("2026-10");
  });

  it("counts the dates an event covers in the reader's zone", () => {
    expect(eventDates({ start: "2026-11-26T02:00:00Z", end: "2026-11-26T03:00:00Z", allDay: false }, tz)).toEqual(["2026-11-26"]);
    // 23:00–01:00 local crosses midnight; an end exactly at midnight does not.
    expect(eventDates({ start: "2026-11-26T16:00:00Z", end: "2026-11-26T18:00:00Z", allDay: false }, tz)).toEqual(["2026-11-26", "2026-11-27"]);
    expect(eventDates({ start: "2026-11-26T15:00:00Z", end: "2026-11-26T17:00:00Z", allDay: false }, tz)).toEqual(["2026-11-26"]);
    // All-day: dates, end exclusive, no zone shift even far west.
    expect(eventDates({ start: "2026-12-24T00:00:00Z", end: "2026-12-26T00:00:00Z", allDay: true }, "America/Los_Angeles")).toEqual(["2026-12-24", "2026-12-25"]);
  });

  it("puts overlapping events side by side and lets separate ones use the full width", () => {
    const day = dayjs.tz("2026-11-26", tz);
    const ev = (id: string, s: string, e: string) => ({ id, start: dayjs.tz(`2026-11-26 ${s}`, tz).toISOString(), end: dayjs.tz(`2026-11-26 ${e}`, tz).toISOString(), allDay: false });
    const placed = layoutDay([ev("a", "09:00", "10:00"), ev("b", "09:30", "11:00"), ev("c", "10:00", "10:30"), ev("d", "13:00", "14:00")], day, tz);
    const by = Object.fromEntries(placed.map((p) => [p.item.id, p]));
    expect(by.a).toMatchObject({ top: 540, height: 60, column: 0, columns: 2 });
    expect(by.b).toMatchObject({ column: 1, columns: 2 });
    expect(by.c).toMatchObject({ column: 0, columns: 2 }); // a ended: its column is free again
    expect(by.d).toMatchObject({ column: 0, columns: 1 });
  });

  it("clips events that run past midnight and gives short ones a minimum height", () => {
    const day = dayjs.tz("2026-11-26", tz);
    const late = { start: dayjs.tz("2026-11-26 23:00", tz).toISOString(), end: dayjs.tz("2026-11-27 01:00", tz).toISOString(), allDay: false };
    const short = { start: dayjs.tz("2026-11-26 08:00", tz).toISOString(), end: dayjs.tz("2026-11-26 08:05", tz).toISOString(), allDay: false };
    const [a, b] = layoutDay([late, short], day, tz).sort((x, y) => x.top - y.top);
    expect(a).toMatchObject({ top: 480, height: 15 });
    expect(b).toMatchObject({ top: 1380, height: 60 });
  });

  it("maps repeat choices to rules and back", () => {
    expect(ruleFor("weekdays", null)).toBe("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
    expect(ruleFor("none", "FREQ=DAILY")).toBeNull();
    expect(ruleFor("custom", "FREQ=MONTHLY;BYDAY=1FR")).toBe("FREQ=MONTHLY;BYDAY=1FR");
    expect(repeatKind("freq=weekly")).toBe("weekly");
    expect(repeatKind("FREQ=MONTHLY;BYDAY=1FR")).toBe("custom");
    expect(repeatKind(null)).toBe("none");
  });
});
