// Every value shown to the user is formatted with the user's locale and time zone (PROMPT 0.2, 14.15).

export interface UserFormat {
  locale: string;
  timeZone: string;
  hourCycle: "h12" | "h23" | null;
}

/** Gmail-style list time: today -> time, this year -> day + month, older -> full date. */
export function listTime(iso: string, f: UserFormat, now: Date = new Date()): string {
  const d = new Date(iso);
  const zoned = (x: Date) =>
    new Intl.DateTimeFormat("en-CA", { timeZone: f.timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(x);
  const opts: Intl.DateTimeFormatOptions = { timeZone: f.timeZone };
  if (zoned(d) === zoned(now)) {
    return new Intl.DateTimeFormat(f.locale, { ...opts, timeStyle: "short", hourCycle: f.hourCycle ?? undefined }).format(d);
  }
  const sameYear = zoned(d).slice(0, 4) === zoned(now).slice(0, 4);
  return new Intl.DateTimeFormat(f.locale, sameYear ? { ...opts, day: "numeric", month: "short" } : { ...opts, dateStyle: "short" }).format(d);
}

export function dateTime(iso: string, f: UserFormat): string {
  return new Intl.DateTimeFormat(f.locale, {
    timeZone: f.timeZone,
    dateStyle: "medium",
    timeStyle: "short",
    hourCycle: f.hourCycle ?? undefined,
  }).format(new Date(iso));
}

const units = ["byte", "kilobyte", "megabyte", "gigabyte", "terabyte"] as const;

/** 3.2 GB / 3,2 GB depending on locale. */
export function bytes(n: number, locale: string): string {
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return new Intl.NumberFormat(locale, {
    style: "unit",
    unit: units[i],
    unitDisplay: "short",
    maximumFractionDigits: v < 10 && i > 0 ? 1 : 0,
  }).format(v);
}
