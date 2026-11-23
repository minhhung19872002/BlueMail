// Keyboard shortcuts (PROMPT 6.12). Keys map to command names; two-key sequences ("g i", "* a") wait up to
// 1.5 s for the second key. Typing in a field never triggers anything. Components listen for the command they
// handle (list, reading pane, shell) through a DOM event, so there is no global mutable state.

export type Command =
  | "compose" | "search" | "help" | "next" | "prev" | "nextMessage" | "prevMessage" | "open" | "back" | "archive" | "trash" | "spam" | "star"
  | "important" | "notImportant" | "select" | "reply" | "replyAll" | "forward" | "markRead" | "markUnread" | "label" | "move" | "snooze" | "mute"
  | "undo" | "goInbox" | "goStarred" | "goSent" | "goDrafts" | "goAll" | "selectAll" | "selectNone" | "close";

export const singleKeys: Record<string, Command> = {
  c: "compose", "/": "search", "?": "help", j: "next", k: "prev", n: "nextMessage", p: "prevMessage", o: "open", Enter: "open", u: "back",
  e: "archive", "#": "trash", "!": "spam", s: "star", "+": "important", "-": "notImportant", x: "select", r: "reply", a: "replyAll", f: "forward",
  I: "markRead", U: "markUnread", l: "label", v: "move", b: "snooze", m: "mute", z: "undo", Escape: "close",
};

export const sequences: Record<string, Command> = {
  "g i": "goInbox", "g s": "goStarred", "g t": "goSent", "g d": "goDrafts", "g a": "goAll", "* a": "selectAll", "* n": "selectNone",
};

/** The keys of the help table, in the order of PROMPT 6.12. */
export const helpRows: { keys: string; command: Command }[] = [
  ...Object.entries(singleKeys).map(([keys, command]) => ({ keys: keys === "I" ? "Shift+i" : keys === "U" ? "Shift+u" : keys === "Escape" ? "Esc" : keys, command })),
  ...Object.entries(sequences).map(([keys, command]) => ({ keys, command })),
];

export const shortcutEvent = "bm-shortcut";

/** A stateful reader of keydown events: returns the command, or null (also while waiting for a sequence). */
export class ShortcutReader {
  private pending: string | null = null;
  private pendingAt = 0;

  constructor(private readonly now: () => number = () => Date.now()) {}

  read(key: string, mods: { ctrl: boolean; alt: boolean; meta: boolean }): Command | null {
    if (mods.ctrl || mods.alt || mods.meta) return null;
    if (this.pending && this.now() - this.pendingAt <= 1500) {
      const seq = sequences[`${this.pending} ${key}`];
      this.pending = null;
      if (seq) return seq;
    }
    this.pending = null;
    if (key === "g" || key === "*") {
      this.pending = key;
      this.pendingAt = this.now();
      return null;
    }
    return singleKeys[key] ?? null;
  }
}

/** True when the key goes to a text field, an editor or a menu that owns the keyboard. */
export function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName;
  if (tag === "TEXTAREA" || tag === "SELECT") return true;
  if (tag === "INPUT") {
    const type = (target as HTMLInputElement).type;
    return !["checkbox", "radio", "button", "submit"].includes(type);
  }
  return target.closest("[role=menu],[role=listbox],[role=dialog] input") !== null;
}

/** The inverse of an action, for "z" (undo the last action in the list). */
export const inverse: Partial<Record<string, string>> = {
  archive: "moveToInbox", trash: "untrash", spam: "notSpam", markRead: "markUnread", markUnread: "markRead", star: "unstar", unstar: "star",
  important: "notImportant", notImportant: "important", mute: "unmute", snooze: "unsnooze",
};
