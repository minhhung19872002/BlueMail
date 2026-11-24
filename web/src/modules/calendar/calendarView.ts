import dayjs, { type Dayjs } from "dayjs";
import utc from "dayjs/plugin/utc";
import timezone from "dayjs/plugin/timezone";

dayjs.extend(utc);
dayjs.extend(timezone);

export type CalendarView = "day" | "week" | "month";

/** The days a view shows around `anchor` (in the user's zone); weeks start on `weekStart` (0 = Sunday, 1 = Monday). */
export function visibleDays(view: CalendarView, anchor: Dayjs, weekStart: number): Dayjs[] {
  const day = anchor.startOf("day");
  if (view === "day") return [day];
  if (view === "week") {
    const first = day.subtract((day.day() - weekStart + 7) % 7, "day");
    return Array.from({ length: 7 }, (_, i) => first.add(i, "day"));
  }
  const month = day.startOf("month");
  const first = month.subtract((month.day() - weekStart + 7) % 7, "day");
  return Array.from({ length: 42 }, (_, i) => first.add(i, "day"));
}

/** Moves the anchor one view forward (+1) or back (-1). */
export function step(view: CalendarView, anchor: Dayjs, direction: 1 | -1): Dayjs {
  return anchor.add(direction, view);
}

export interface Span {
  start: string;
  end: string;
  allDay: boolean;
}

/**
 * The calendar dates an event covers, as YYYY-MM-DD in the user's zone. All-day events are dates (UTC midnight,
 * end exclusive); timed ones count every day they touch, an end exactly at midnight not counting the next day.
 */
export function eventDates(e: Span, tz: string): string[] {
  const s = e.allDay ? dayjs.utc(e.start) : dayjs(e.start).tz(tz);
  const endRaw = e.allDay ? dayjs.utc(e.end) : dayjs(e.end).tz(tz);
  const last = endRaw.subtract(1, "millisecond").isBefore(s) ? s : endRaw.subtract(1, "millisecond");
  const out: string[] = [];
  for (let d = s.startOf("day"); !d.isAfter(last, "day") && out.length < 400; d = d.add(1, "day")) out.push(d.format("YYYY-MM-DD"));
  return out;
}

export interface Placed<T> {
  item: T;
  /** Minutes from midnight of the day, clipped to the day. */
  top: number;
  height: number;
  column: number;
  columns: number;
}

/**
 * Timed events of one day placed in a time grid: overlapping events share the width (each cluster of overlapping
 * events gets as many columns as it needs, every event takes the first free one).
 */
export function layoutDay<T extends Span>(events: T[], day: Dayjs, tz: string): Placed<T>[] {
  const dayStart = day.tz(tz, true).startOf("day");
  const dayEnd = dayStart.add(1, "day");
  const timed = events
    .filter((e) => !e.allDay)
    .map((e) => ({ e, s: dayjs(e.start), f: dayjs(e.end) }))
    .filter((x) => x.s.isBefore(dayEnd) && x.f.isAfter(dayStart))
    .sort((a, b) => a.s.valueOf() - b.s.valueOf() || b.f.valueOf() - a.f.valueOf());

  const placed: Placed<T>[] = [];
  let cluster: { p: Placed<T>; end: number }[] = [];
  let clusterEnd = -Infinity;
  const flush = () => {
    const n = Math.max(1, ...cluster.map((c) => c.p.column + 1));
    for (const c of cluster) c.p.columns = n;
    cluster = [];
  };
  for (const x of timed) {
    const s = Math.max(x.s.valueOf(), dayStart.valueOf());
    const f = Math.min(Math.max(x.f.valueOf(), x.s.valueOf() + 15 * 60_000), dayEnd.valueOf());
    if (s >= clusterEnd) {
      flush();
      clusterEnd = -Infinity;
    }
    const used = new Set(cluster.filter((c) => c.end > s).map((c) => c.p.column));
    let column = 0;
    while (used.has(column)) column++;
    const p: Placed<T> = { item: x.e, top: (s - dayStart.valueOf()) / 60_000, height: Math.max(15, (f - s) / 60_000), column, columns: 1 };
    placed.push(p);
    cluster.push({ p, end: f });
    clusterEnd = Math.max(clusterEnd, f);
  }
  flush();
  return placed;
}

export type RepeatKind = "none" | "daily" | "weekdays" | "weekly" | "monthly" | "yearly" | "custom";

const rules: Record<Exclude<RepeatKind, "none" | "custom">, string> = {
  daily: "FREQ=DAILY",
  weekdays: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
  weekly: "FREQ=WEEKLY",
  monthly: "FREQ=MONTHLY",
  yearly: "FREQ=YEARLY",
};

/** RRULE for a repeat choice of the editor. */
export function ruleFor(kind: RepeatKind, keep: string | null): string | null {
  if (kind === "none") return null;
  if (kind === "custom") return keep;
  return rules[kind];
}

/** The editor choice for a stored rule ("custom" when it is something else, e.g. from another calendar). */
export function repeatKind(rule: string | null): RepeatKind {
  if (!rule) return "none";
  const found = (Object.keys(rules) as (keyof typeof rules)[]).find((k) => rules[k] === rule.toUpperCase());
  return found ?? "custom";
}

export const eventColors: Record<string, string> = {
  blue: "#1a5fb4", green: "#26734d", red: "#a51d2d", purple: "#6b2fa0", orange: "#9c4b00", teal: "#0f6b6b", gray: "#5e5c64",
};
