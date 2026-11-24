import type { Dayjs } from "dayjs";
import type { CalendarEvent } from "./calendarApi";

// "E-mail Calendar" message parts (a message body, so plain inline styles, not theme tokens).

export type Range = "today" | "tomorrow" | "next7" | "next30";
export type Detail = "BUSY" | "FULL";

export const escapeHtml = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

/** First day and the day after the last one of a range, from today in the user's zone. */
export function rangeDays(range: Range, today: Dayjs): [Dayjs, Dayjs] {
  const start = today.startOf("day");
  switch (range) {
    case "today": return [start, start.add(1, "day")];
    case "tomorrow": return [start.add(1, "day"), start.add(2, "day")];
    case "next7": return [start, start.add(7, "day")];
    default: return [start, start.add(30, "day")];
  }
}

/**
 * The message body of "E-mail Calendar": one table row per event (busy blocks only, or with title and place),
 * grouped by day; every value is escaped text.
 */
export function calendarHtml(events: CalendarEvent[], detail: Detail, labels: { day: (iso: string) => string; time: (iso: string) => string; allDay: string; busy: string; nothing: string }): string {
  if (events.length === 0) return `<p>${escapeHtml(labels.nothing)}</p>`;
  const rows: string[] = [];
  let lastDay = "";
  for (const e of events) {
    const day = labels.day(e.start);
    if (day !== lastDay) {
      rows.push(`<tr><th colspan="2" style="text-align:start;padding:8px 4px 4px;border-bottom:1px solid #ccc">${escapeHtml(day)}</th></tr>`);
      lastDay = day;
    }
    const when = e.allDay ? labels.allDay : `${labels.time(e.start)} – ${labels.time(e.end)}`;
    const what = detail === "FULL" ? escapeHtml(e.summary) + (e.location ? ` <span style="color:#666">(${escapeHtml(e.location)})</span>` : "") : escapeHtml(labels.busy);
    rows.push(`<tr><td style="padding:2px 12px 2px 4px;white-space:nowrap">${escapeHtml(when)}</td><td style="padding:2px 4px">${what}</td></tr>`);
  }
  return `<table style="border-collapse:collapse">${rows.join("")}</table>`;
}
