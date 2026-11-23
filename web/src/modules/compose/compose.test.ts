import { describe, expect, it } from "vitest";
import { isValidAddress, parseRecipients, scheduleSuggestions, sendWarnings, toEditorHtml, toStoredHtml, uploadIdsIn, zonedToUtc } from "./compose";

describe("addresses", () => {
  it("accepts international addresses (RFC 6531) and rejects broken ones", () => {
    expect(isValidAddress("lan@bluemail.test")).toBe(true);
    expect(isValidAddress("người.gửi@ví-dụ.vn")).toBe(true);
    expect(isValidAddress("用户@例子.广告")).toBe(true);
    expect(isValidAddress("a@b")).toBe(false);
    expect(isValidAddress("a b@x.test")).toBe(false);
    expect(isValidAddress("a..b@x.test")).toBe(false);
    expect(isValidAddress("@x.test")).toBe(false);
    expect(isValidAddress("a@-x.test")).toBe(false);
  });

  it("splits pasted lists, keeping quoted commas", () => {
    const r = parseRecipients('"Lê, Minh" <minh@x.test>; lan@x.test\nTrần An <an@x.test>, broken@, ');
    expect(r.valid).toEqual([
      { name: "Lê, Minh", address: "minh@x.test" },
      { name: null, address: "lan@x.test" },
      { name: "Trần An", address: "an@x.test" },
    ]);
    expect(r.invalid).toEqual(["broken@"]);
  });
});

describe("send warnings", () => {
  const base = { subject: "Hi", html: "<p>x</p>", attachments: 0, recipients: [{ name: null, address: "a@bluemail.test" }], ourDomains: ["bluemail.test"], mode: "new" };
  it("warns about an empty subject and a promised attachment, not about the quote", () => {
    expect(sendWarnings({ ...base, subject: " " })).toEqual(["noSubject"]);
    expect(sendWarnings({ ...base, html: "<p>Em gửi kèm báo giá</p>" })).toEqual(["missingAttachment"]);
    expect(sendWarnings({ ...base, html: "<p>Đã nhận</p><blockquote>see attached file</blockquote>" })).toEqual([]);
    expect(sendWarnings({ ...base, html: "<p>see attached</p>", attachments: 1 })).toEqual([]);
  });
  it("warns about large external sends and long reply-all", () => {
    const many = Array.from({ length: 30 }, (_, i) => ({ name: null, address: `p${i}@other.test` }));
    expect(sendWarnings({ ...base, recipients: many })).toContain("manyExternal");
    expect(sendWarnings({ ...base, recipients: many, mode: "replyAll" })).toContain("longReplyAll");
  });
});

describe("scheduling", () => {
  it("converts wall-clock time in a zone to UTC, across DST", () => {
    expect(zonedToUtc(2026, 11, 24, 8, 0, "Asia/Ho_Chi_Minh").toISOString()).toBe("2026-11-24T01:00:00.000Z");
    expect(zonedToUtc(2026, 7, 1, 8, 0, "Europe/London").toISOString()).toBe("2026-07-01T07:00:00.000Z");
    expect(zonedToUtc(2026, 12, 1, 8, 0, "Europe/London").toISOString()).toBe("2026-12-01T08:00:00.000Z");
  });
  it("suggests this afternoon only in the morning, tomorrow 8:00 and next Monday 8:00", () => {
    // Monday 2026-11-23 09:30 in Hanoi
    const morning = scheduleSuggestions(new Date("2026-11-23T02:30:00Z"), "Asia/Ho_Chi_Minh");
    expect(morning.map((s) => [s.key, s.at.toISOString()])).toEqual([
      ["thisAfternoon", "2026-11-23T06:00:00.000Z"],
      ["tomorrowMorning", "2026-11-24T01:00:00.000Z"],
      ["mondayMorning", "2026-11-30T01:00:00.000Z"],
    ]);
    const evening = scheduleSuggestions(new Date("2026-11-23T13:00:00Z"), "Asia/Ho_Chi_Minh");
    expect(evening.map((s) => s.key)).toEqual(["tomorrowMorning", "mondayMorning"]);
  });
});

describe("inline images", () => {
  const id = "0123456789abcdef0123456789abcdef";
  it("round-trips between editor blob URLs and cid references", () => {
    const stored = toStoredHtml(`<p>x</p><img src="blob:http://x/1" data-upload="${id}">`);
    expect(stored).toContain(`src="cid:${id}@upload"`);
    expect(uploadIdsIn(stored)).toEqual([id]);
    expect(toEditorHtml(stored, () => "blob:http://x/2")).toContain('src="blob:http://x/2"');
  });
});
