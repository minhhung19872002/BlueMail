import type { AuthResultsSummary, Participant } from "../../api/types";

// View logic of the conversation list and reading pane, kept free of React so it is unit-tested.

export interface SenderPart {
  text: string;
  unread: boolean;
  me: boolean;
}

/** Gmail-style sender column: first names of participants, "me" for the reader, at most three shown. */
export function senderParts(participants: Participant[], myAddress: string): SenderPart[] {
  const mine = myAddress.toLowerCase();
  const parts = participants.map((p) => {
    const me = p.address.toLowerCase() === mine;
    const name = p.name?.trim();
    const short = name ? (participants.length > 1 ? firstName(name) : name) : p.address;
    return { text: short, unread: p.unread, me };
  });
  return parts.length > 3 ? [parts[0]!, ...parts.slice(-2)] : parts;
}

function firstName(name: string): string {
  // "Trần Thị Lan" -> "Lan" would be wrong for Vietnamese order, so keep the whole name when it has no Latin comma
  // convention; for "Olivia Bennett"-style names show the first word. CJK names stay whole.
  if (/[\p{Script=Han}\p{Script=Hangul}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Thai}]/u.test(name)) return name;
  if (/[ăâđêôơưàáạảãèéẹẻẽìíịỉĩòóọỏõùúụủũỳýỵỷỹ]/iu.test(name)) return name.split(/\s+/).slice(-1)[0] ?? name;
  return name.split(/\s+/)[0] ?? name;
}

export type AuthWarning = "spf" | "dkim" | "dmarc";

/** Failed checks recorded by BlueMail.Mx at delivery (not headers in the message, which a sender can forge). */
export function authWarnings(json: string | null): AuthWarning[] {
  if (!json) return [];
  let a: AuthResultsSummary;
  try {
    a = JSON.parse(json) as AuthResultsSummary;
  } catch {
    return [];
  }
  const out: AuthWarning[] = [];
  if (a.dmarc === "fail") out.push("dmarc");
  if (a.spf === "fail" || a.spf === "softfail") out.push("spf");
  if ((a.dkim ?? []).length > 0 && (a.dkim ?? []).every((d) => d.result !== "pass")) out.push("dkim");
  return out;
}

export function domainOf(address: string): string {
  const at = address.lastIndexOf("@");
  return at < 0 ? "" : address.slice(at + 1).toLowerCase();
}

// Common homoglyphs (Cyrillic, Greek, digits) mapped to the Latin letter they imitate (Unicode TR39 idea, small table).
const confusables: Record<string, string> = {
  а: "a", е: "e", о: "o", р: "p", с: "c", х: "x", у: "y", і: "i", ј: "j", ѕ: "s", ԁ: "d", ɡ: "g", һ: "h", ӏ: "l", ο: "o", α: "a",
  ν: "v", τ: "t", "0": "o", "1": "l", rn: "m", vv: "w",
};

export function skeleton(s: string): string {
  let out = s.normalize("NFKC").toLowerCase();
  for (const [k, v] of Object.entries(confusables)) out = out.split(k).join(v);
  return out;
}

function distance(a: string, b: string): number {
  const d = Array.from({ length: a.length + 1 }, (_, i) => [i, ...Array<number>(b.length).fill(0)]);
  for (let j = 1; j <= b.length; j++) d[0]![j] = j;
  for (let i = 1; i <= a.length; i++) {
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      d[i]![j] = Math.min(d[i - 1]![j]! + 1, d[i]![j - 1]! + 1, d[i - 1]![j - 1]! + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) d[i]![j] = Math.min(d[i]![j]!, d[i - 2]![j - 2]! + 1);
    }
  }
  return d[a.length]![b.length]!;
}

/** The sender domain imitates one of ours: same skeleton or one or two edits away, but not equal (PROMPT 6.3). */
export function looksLike(senderDomain: string, ours: string[]): string | null {
  const s = senderDomain.toLowerCase();
  for (const o of ours.map((x) => x.toLowerCase())) {
    if (s === o || s.endsWith("." + o)) return null;
  }
  for (const o of ours.map((x) => x.toLowerCase())) {
    if (skeleton(s) === skeleton(o) || (o.length >= 6 && distance(s, o) <= (o.length >= 10 ? 2 : 1))) return o;
  }
  return null;
}

export function isExternal(senderAddress: string, ours: string[]): boolean {
  const d = domainOf(senderAddress);
  return !ours.some((o) => d === o.toLowerCase() || d.endsWith("." + o.toLowerCase()));
}

export interface SpamHit {
  code: string;
  score: number;
  detail: string | null;
}

export interface SpamReport {
  score: number;
  threshold: number;
  hits: SpamHit[];
}

/** The scorer's report stored with a message (mail.messages.spam_report, written by BlueMail.Mx). */
export function parseSpamReport(json: string | null): SpamReport | null {
  if (!json) return null;
  try {
    const r = JSON.parse(json) as { spam?: Partial<SpamReport> };
    if (!r.spam || typeof r.spam.score !== "number" || !Array.isArray(r.spam.hits)) return null;
    return { score: r.spam.score, threshold: typeof r.spam.threshold === "number" ? r.spam.threshold : 5, hits: r.spam.hits };
  } catch {
    return null;
  }
}

/** Date headings of the message list, as Outlook groups "By date". */
export type DateGroup = "today" | "yesterday" | "thisWeek" | "lastWeek" | "thisMonth" | "lastMonth" | "older";

/** Day number (days since 1970-01-01) of an instant in a time zone. */
function dayIn(at: Date, timeZone: string): number {
  const ymd = new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(at);
  return Math.floor(Date.parse(ymd + "T00:00:00Z") / 86_400_000);
}

/**
 * The heading a message received at `at` goes under, seen at `now` in `timeZone`. Weeks start on `weekStart`
 * (0 = Sunday, 1 = Monday, as the user's profile); months are calendar months.
 */
export function dateGroup(at: Date, now: Date, timeZone: string, weekStart = 1): DateGroup {
  const d = dayIn(at, timeZone);
  const today = dayIn(now, timeZone);
  if (d >= today) return "today";
  if (d === today - 1) return "yesterday";
  // 1970-01-01 was a Thursday (weekday 4).
  const weekday = (today + 4) % 7;
  const thisWeek = today - ((weekday - weekStart + 7) % 7);
  if (d >= thisWeek) return "thisWeek";
  if (d >= thisWeek - 7) return "lastWeek";
  const ym = (day: number) => new Date(day * 86_400_000).toISOString().slice(0, 7);
  const month = ym(today);
  if (ym(d) === month) return "thisMonth";
  const prev = new Date(Date.UTC(Number(month.slice(0, 4)), Number(month.slice(5, 7)) - 2, 1)).toISOString().slice(0, 7);
  if (ym(d) === prev) return "lastMonth";
  return "older";
}

/** Initials for the round avatar of a sender (Outlook on the web): first and last word of the name, else the address. */
export function initials(name: string | null, address: string): string {
  const words = (name ?? "").replace(/[<>"()[\]]/g, " ").trim().split(/\s+/).filter((w) => /\p{L}/u.test(w));
  if (words.length === 0) return (address.split("@")[0] ?? "?").replace(/[^\p{L}\p{N}]/gu, "").slice(0, 2).toUpperCase() || "?";
  const first = [...words[0]!][0] ?? "";
  // CJK names: one character is the usual avatar.
  if (/[\p{Script=Han}\p{Script=Hangul}\p{Script=Hiragana}\p{Script=Katakana}]/u.test(first)) return first;
  const last = words.length > 1 ? [...words[words.length - 1]!][0] ?? "" : "";
  return (first + last).toUpperCase();
}

/** A stable avatar colour per address (same sender, same colour), chosen from a palette with readable white text. */
export const avatarPalette = ["#1a5fb4", "#26734d", "#a51d2d", "#6b2fa0", "#9c4b00", "#0f6b6b", "#5e5c64", "#8f3b76"];
export function avatarColor(address: string): string {
  let h = 0;
  for (const ch of address.toLowerCase()) h = (h * 31 + ch.codePointAt(0)!) >>> 0;
  return avatarPalette[h % avatarPalette.length]!;
}

export interface SenderDetails {
  /** Domain that passed SPF ("mailed-by"). */
  mailedBy: string | null;
  /** Domains with a passing DKIM signature ("signed-by"). */
  signedBy: string[];
  /** Whether the message reached our MX over TLS; null when unknown. */
  tls: boolean | null;
}

/** The "mailed-by / signed-by / security" lines of the sender details, from the stored authentication summary. */
export function senderDetails(json: string | null): SenderDetails {
  const none: SenderDetails = { mailedBy: null, signedBy: [], tls: null };
  if (!json) return none;
  let a: AuthResultsSummary;
  try {
    a = JSON.parse(json) as AuthResultsSummary;
  } catch {
    return none;
  }
  const signedBy = [...new Set((a.dkim ?? []).filter((d) => d.result === "pass" && d.domain).map((d) => d.domain!.toLowerCase()))];
  return {
    mailedBy: a.spf === "pass" && a.spfDomain ? a.spfDomain.toLowerCase() : null,
    signedBy,
    tls: typeof a.tls === "boolean" ? a.tls : null,
  };
}
