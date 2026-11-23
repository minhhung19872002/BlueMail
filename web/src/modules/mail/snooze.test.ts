import { describe, expect, it } from "vitest";
import dayjs from "dayjs";
import { snoozePresets } from "./snooze";

const fmt = (d: dayjs.Dayjs) => d.format("ddd YYYY-MM-DD HH:mm");

describe("snooze presets", () => {
  it("are local times in the user's zone", () => {
    // Monday 2026-11-23 10:00 in Ho Chi Minh City (03:00 UTC)
    const p = snoozePresets(dayjs.utc("2026-11-23T03:00:00Z"), "Asia/Ho_Chi_Minh");
    expect(p.map((x) => `${x.key} ${fmt(x.at)}`)).toEqual([
      "laterToday Mon 2026-11-23 18:00",
      "tomorrow Tue 2026-11-24 08:00",
      "weekend Sat 2026-11-28 08:00",
      "nextWeek Mon 2026-11-30 08:00",
    ]);
  });

  it("late in the day 'later today' is 3 hours away; duplicates are dropped", () => {
    // Friday 2026-11-27 17:30 local
    const p = snoozePresets(dayjs.utc("2026-11-27T10:30:00Z"), "Asia/Ho_Chi_Minh");
    expect(fmt(p[0]!.at)).toBe("Fri 2026-11-27 20:00");
    expect(p.map((x) => x.key)).toEqual(["laterToday", "tomorrow", "nextWeek"]);
  });
});
