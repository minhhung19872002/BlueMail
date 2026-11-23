import dayjs, { type Dayjs } from "dayjs";
import utc from "dayjs/plugin/utc";
import timezone from "dayjs/plugin/timezone";

dayjs.extend(utc);
dayjs.extend(timezone);

export type SnoozePreset = "laterToday" | "tomorrow" | "weekend" | "nextWeek";

/**
 * Snooze presets as Gmail offers them, in the user's time zone: later today (18:00, or 3 hours from now once that is
 * past), tomorrow 08:00, this weekend Saturday 08:00, next week Monday 08:00. `weekStart` decides nothing here: the
 * weekend is Saturday everywhere BlueMail is used today.
 */
export function snoozePresets(now: Dayjs, tz: string): { key: SnoozePreset; at: Dayjs }[] {
  const local = now.tz(tz);
  const at8 = (d: Dayjs) => d.hour(8).minute(0).second(0).millisecond(0);
  const evening = local.hour(18).minute(0).second(0).millisecond(0);
  const laterToday = local.isBefore(evening.subtract(1, "hour")) ? evening : local.add(3, "hour").minute(0).second(0).millisecond(0);
  const tomorrow = at8(local.add(1, "day"));
  const daysToSat = (6 - local.day() + 7) % 7 || 7;
  const weekend = at8(local.add(daysToSat, "day"));
  const daysToMon = (1 - local.day() + 7) % 7 || 7;
  const nextWeek = at8(local.add(daysToMon, "day"));
  const list: { key: SnoozePreset; at: Dayjs }[] = [
    { key: "laterToday", at: laterToday },
    { key: "tomorrow", at: tomorrow },
    { key: "weekend", at: weekend },
    { key: "nextWeek", at: nextWeek },
  ];
  // On Friday "this weekend" and on Sunday "next week" equal "tomorrow": keep only distinct times.
  return list.filter((p, i) => list.findIndex((q) => q.at.valueOf() === p.at.valueOf()) === i);
}
