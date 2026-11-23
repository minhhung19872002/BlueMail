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
