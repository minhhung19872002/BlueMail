// Advanced search form <-> Gmail operator query (PROMPT 6.7). The server parses and validates the query; this
// only builds it, quoting values so that spaces and operators inside a field stay inside that field.

export type SizeUnit = "K" | "M";
export type Within = "" | "1d" | "3d" | "7d" | "14d" | "1m" | "2m" | "6m" | "1y";

export interface AdvancedSearch {
  from: string;
  to: string;
  subject: string;
  hasWords: string;
  notWords: string;
  sizeOp: "larger" | "smaller";
  size: string;
  sizeUnit: SizeUnit;
  within: Within;
  /** yyyy/mm/dd: with `within`, the window ends on this day; alone, messages before it. */
  date: string;
  where: string; // "" (all mail, not spam/trash) | inbox | sent | starred | anywhere | ...
  hasAttachment: boolean;
  unread: boolean;
}

export const emptySearch: AdvancedSearch = {
  from: "", to: "", subject: "", hasWords: "", notWords: "", sizeOp: "larger", size: "", sizeUnit: "M", within: "", date: "", where: "",
  hasAttachment: false, unread: false,
};

/** A value as one operand: bare when it is a single plain word, otherwise quoted (inner quotes dropped). */
export function operand(v: string): string {
  const s = v.trim().replace(/"/g, "");
  return /^[^\s(){}:-][^\s(){}:]*$/.test(s) && s !== "OR" && s !== "AND" ? s : `"${s}"`;
}

function words(v: string): string[] {
  return v.trim().split(/\s+/).filter(Boolean).map((w) => w.replace(/"/g, "")).filter(Boolean);
}

function shiftDate(date: string, within: Within, sign: 1 | -1): string {
  const [y, m, d] = date.split("/").map(Number) as [number, number, number];
  const n = parseInt(within, 10);
  const dt = new Date(Date.UTC(y, m - 1, d));
  if (within.endsWith("d")) dt.setUTCDate(dt.getUTCDate() + sign * n);
  else if (within.endsWith("m")) dt.setUTCMonth(dt.getUTCMonth() + sign * n);
  else dt.setUTCFullYear(dt.getUTCFullYear() + sign * n);
  const p = (x: number) => String(x).padStart(2, "0");
  return `${dt.getUTCFullYear()}/${p(dt.getUTCMonth() + 1)}/${p(dt.getUTCDate())}`;
}

export function buildQuery(a: AdvancedSearch): string {
  const parts: string[] = [];
  if (a.from.trim()) parts.push(`from:${operand(a.from)}`);
  if (a.to.trim()) parts.push(`to:${operand(a.to)}`);
  if (a.subject.trim()) parts.push(`subject:${operand(a.subject)}`);
  parts.push(...words(a.hasWords));
  parts.push(...words(a.notWords).map((w) => `-${w}`));
  const size = a.size.trim();
  if (/^\d+$/.test(size)) parts.push(`${a.sizeOp}:${size}${a.sizeUnit}`);
  if (/^\d{4}\/\d{2}\/\d{2}$/.test(a.date)) {
    if (a.within) {
      // Gmail: "within 1 week of 2026/03/10" = after 03/03 and before 03/17
      parts.push(`after:${shiftDate(a.date, a.within, -1)}`, `before:${shiftDate(a.date, a.within, 1)}`);
    } else {
      parts.push(`before:${a.date}`);
    }
  } else if (a.within) {
    parts.push(`newer_than:${a.within}`);
  }
  if (a.where) parts.push(`in:${a.where}`);
  if (a.hasAttachment) parts.push("has:attachment");
  if (a.unread) parts.push("is:unread");
  return parts.join(" ");
}
