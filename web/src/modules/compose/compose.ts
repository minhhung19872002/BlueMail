// Compose logic kept free of React so it is unit-tested (PROMPT 6.4).

export interface Recipient {
  name: string | null;
  address: string;
}

/** RFC 5321/6531 mailbox, permissive on the local part (UTF-8 allowed), strict on structure. */
export function isValidAddress(address: string): boolean {
  if (address.length > 320) return false;
  const at = address.lastIndexOf("@");
  if (at <= 0 || at === address.length - 1) return false;
  const local = address.slice(0, at);
  const domain = address.slice(at + 1);
  if (local.length > 64 || /[\s<>(),;:\\"[\]]/u.test(local) || local.startsWith(".") || local.endsWith(".") || local.includes("..")) return false;
  const labels = domain.split(".");
  return labels.length >= 2 && labels.every((l) => l.length > 0 && l.length <= 63 && /^[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?$/u.test(l));
}

/**
 * Splits pasted text into recipients: "Name <a@b>", bare addresses, separated by comma, semicolon or newline.
 * Quoted names may contain commas.
 */
export function parseRecipients(text: string): { valid: Recipient[]; invalid: string[] } {
  const parts: string[] = [];
  let cur = "";
  let quoted = false;
  let angle = false;
  for (const ch of text) {
    if (ch === '"') quoted = !quoted;
    else if (ch === "<") angle = true;
    else if (ch === ">") angle = false;
    if (!quoted && !angle && (ch === "," || ch === ";" || ch === "\n")) {
      parts.push(cur);
      cur = "";
    } else cur += ch;
  }
  parts.push(cur);
  const valid: Recipient[] = [];
  const invalid: string[] = [];
  for (const raw of parts.map((p) => p.trim()).filter(Boolean)) {
    const m = /^(?:"?([^"<]*?)"?\s*)?<([^>]+)>$/u.exec(raw);
    const address = (m ? m[2]! : raw).trim();
    const name = m && m[1] ? m[1].trim() || null : null;
    if (isValidAddress(address)) valid.push({ name, address });
    else invalid.push(raw);
  }
  return { valid, invalid };
}

export function domainOf(address: string): string {
  return address.slice(address.lastIndexOf("@") + 1).toLowerCase();
}

export type SendWarning = "noSubject" | "missingAttachment" | "manyExternal" | "longReplyAll";

// Words people write when they mean to attach something, in the UI languages and a few common others.
const attachWords = [
  "attached", "attachment", "attaching", "enclosed", "đính kèm", "tệp kèm", "gửi kèm", "anbei", "anhang", "ci-joint", "pièce jointe",
  "adjunto", "allegato", "anexo", "添付", "附件", "첨부", "вложени", "مرفق",
];

/** Text the sender wrote, without the quoted original (so an old "attached" in the quote does not trigger). */
export function ownText(html: string): string {
  const doc = new DOMParser().parseFromString(html, "text/html");
  doc.querySelectorAll("blockquote, .bm-quote").forEach((q) => q.remove());
  return (doc.body.textContent ?? "").toLowerCase();
}

export function sendWarnings(input: {
  subject: string;
  html: string;
  attachments: number;
  recipients: Recipient[];
  ourDomains: string[];
  mode: string;
}): SendWarning[] {
  const out: SendWarning[] = [];
  if (!input.subject.trim()) out.push("noSubject");
  if (input.attachments === 0) {
    const text = ownText(input.html);
    if (attachWords.some((w) => text.includes(w))) out.push("missingAttachment");
  }
  const ours = input.ourDomains.map((d) => d.toLowerCase());
  const external = input.recipients.filter((r) => !ours.includes(domainOf(r.address))).length;
  if (external >= 25) out.push("manyExternal");
  if (input.mode === "replyAll" && input.recipients.length >= 15) out.push("longReplyAll");
  return out;
}

/** Wall-clock time in the user's zone -> UTC instant (handles DST by checking the offset at that moment). */
export function zonedToUtc(year: number, month: number, day: number, hour: number, minute: number, timeZone: string): Date {
  const guess = Date.UTC(year, month - 1, day, hour, minute);
  const offsetAt = (t: number) => {
    const parts = new Intl.DateTimeFormat("en-US", {
      timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit",
    }).formatToParts(new Date(t));
    const get = (k: string) => Number(parts.find((p) => p.type === k)!.value);
    return Date.UTC(get("year"), get("month") - 1, get("day"), get("hour") % 24, get("minute")) - t;
  };
  const first = guess - offsetAt(guess);
  return new Date(guess - offsetAt(first));
}

function zonedParts(d: Date, timeZone: string) {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", weekday: "short",
  }).formatToParts(d);
  const get = (k: string) => parts.find((p) => p.type === k)!.value;
  return { year: Number(get("year")), month: Number(get("month")), day: Number(get("day")), hour: Number(get("hour")), weekday: get("weekday") };
}

export interface ScheduleSuggestion {
  key: "tomorrowMorning" | "thisAfternoon" | "mondayMorning";
  at: Date;
}

/** Gmail's suggestions (PROMPT 6.4): tomorrow 8:00, this afternoon 13:00 (if still ahead), next Monday 8:00 — in the user's zone. */
export function scheduleSuggestions(now: Date, timeZone: string): ScheduleSuggestion[] {
  const p = zonedParts(now, timeZone);
  const day = (offset: number) => {
    const d = new Date(Date.UTC(p.year, p.month - 1, p.day + offset));
    return { y: d.getUTCFullYear(), m: d.getUTCMonth() + 1, d: d.getUTCDate() };
  };
  const out: ScheduleSuggestion[] = [];
  if (p.hour < 12) {
    const t = day(0);
    out.push({ key: "thisAfternoon", at: zonedToUtc(t.y, t.m, t.d, 13, 0, timeZone) });
  }
  const tm = day(1);
  out.push({ key: "tomorrowMorning", at: zonedToUtc(tm.y, tm.m, tm.d, 8, 0, timeZone) });
  const weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const toMonday = ((8 - weekdays.indexOf(p.weekday)) % 7) || 7;
  const mo = day(toMonday);
  out.push({ key: "mondayMorning", at: zonedToUtc(mo.y, mo.m, mo.d, 8, 0, timeZone) });
  return out;
}

/** Inline images: the editor shows blob: URLs; the stored HTML references uploads as cid:{id}@upload. */
export function toStoredHtml(html: string): string {
  const doc = new DOMParser().parseFromString(html, "text/html");
  doc.querySelectorAll("img[data-upload]").forEach((img) => {
    img.setAttribute("src", `cid:${img.getAttribute("data-upload")}@upload`);
  });
  return doc.body.innerHTML;
}

export function uploadIdsIn(html: string): string[] {
  return [...new Set([...html.matchAll(/cid:([0-9a-f]{32})@upload/gi)].map((m) => m[1]!.toLowerCase()))];
}

export function toEditorHtml(html: string, urlFor: (uploadId: string) => string | undefined): string {
  const doc = new DOMParser().parseFromString(html, "text/html");
  doc.querySelectorAll("img").forEach((img) => {
    const m = /^cid:([0-9a-f]{32})@upload$/i.exec(img.getAttribute("src") ?? "");
    if (!m) return;
    img.setAttribute("data-upload", m[1]!.toLowerCase());
    const url = urlFor(m[1]!.toLowerCase());
    if (url) img.setAttribute("src", url);
  });
  return doc.body.innerHTML;
}
