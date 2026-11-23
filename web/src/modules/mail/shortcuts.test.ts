import { describe, expect, it } from "vitest";
import { ShortcutReader, helpRows, inverse } from "./shortcuts";

const none = { ctrl: false, alt: false, meta: false };

describe("keyboard shortcuts", () => {
  it("reads single keys and two-key sequences within 1.5 s", () => {
    let t = 0;
    const r = new ShortcutReader(() => t);
    expect(r.read("c", none)).toBe("compose");
    expect(r.read("g", none)).toBeNull();
    expect(r.read("i", none)).toBe("goInbox");
    expect(r.read("*", none)).toBeNull();
    expect(r.read("a", none)).toBe("selectAll");
    r.read("g", none);
    t = 2000;
    expect(r.read("i", none)).toBeNull();
    expect(r.read("I", none)).toBe("markRead");
    expect(r.read("c", { ctrl: true, alt: false, meta: false })).toBeNull();
  });

  it("covers every key of PROMPT 6.12 and undo has an inverse for list actions", () => {
    const keys = helpRows.map((h) => h.keys);
    for (const k of ["c", "/", "j", "k", "n", "p", "o", "Enter", "u", "e", "#", "!", "s", "+", "-", "x", "r", "a", "f", "Shift+i", "Shift+u", "l", "v", "b", "m", "z",
      "g i", "g s", "g t", "g d", "g a", "* a", "* n", "Esc", "?"]) {
      expect(keys).toContain(k);
    }
    expect(inverse.archive).toBe("moveToInbox");
    expect(inverse.snooze).toBe("unsnooze");
  });
});
