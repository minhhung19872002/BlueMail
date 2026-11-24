import { describe, expect, it } from "vitest";
import { authWarnings, avatarColor, avatarPalette, dateGroup, initials, isExternal, looksLike, parseSpamReport, senderDetails, senderParts } from "./mailView";
import en from "../../i18n/locales/en.json";
import vi from "../../i18n/locales/vi.json";

describe("senderParts", () => {
  it("shows 'me', keeps CJK names whole and collapses long lists", () => {
    const parts = senderParts(
      [
        { name: "Olivia Bennett", address: "olivia@acme.test", unread: false },
        { name: "田中 健二", address: "kenji@bluemail.test", unread: true },
        { name: null, address: "lan@bluemail.test", unread: false },
        { name: "Trần Thị Lan", address: "x@doitac.test", unread: false },
      ],
      "LAN@bluemail.test",
    );
    expect(parts.map((p) => p.text)).toEqual(["Olivia", "lan@bluemail.test", "Lan"]);
    expect(parts[1]!.me).toBe(true);
    expect(senderParts([{ name: "田中 健二", address: "k@x.test", unread: true }], "a@b.test")[0]).toEqual({ text: "田中 健二", unread: true, me: false });
  });
});

describe("authWarnings", () => {
  it("reads the stored results, tolerates garbage", () => {
    expect(authWarnings(JSON.stringify({ spf: "pass", dkim: [{ result: "pass" }], dmarc: "pass" }))).toEqual([]);
    expect(authWarnings(JSON.stringify({ spf: "fail", dkim: [{ result: "fail" }], dmarc: "fail" }))).toEqual(["dmarc", "spf", "dkim"]);
    expect(authWarnings("{not json")).toEqual([]);
    expect(authWarnings(null)).toEqual([]);
  });
});

describe("lookalike domains", () => {
  const ours = ["bluemail.test", "acme-global.test"];
  it("flags homoglyphs and small edits, not our own or unrelated domains", () => {
    expect(looksLike("bluemaіl.test", ours)).toBe("bluemail.test"); // Cyrillic і
    expect(looksLike("blue-mail.test", ours)).toBe("bluemail.test");
    expect(looksLike("acme-gl0bal.test", ours)).toBe("acme-global.test");
    expect(looksLike("bluemail.test", ours)).toBeNull();
    expect(looksLike("mx.bluemail.test", ours)).toBeNull();
    expect(looksLike("doitac.test", ours)).toBeNull();
  });
  it("external senders", () => {
    expect(isExternal("a@doitac.test", ours)).toBe(true);
    expect(isExternal("a@BlueMail.test", ours)).toBe(false);
  });
});

describe("spam report", () => {
  it("reads the scorer's report and ignores anything else", () => {
    const r = parseSpamReport(JSON.stringify({ spam: { score: 9.6, threshold: 5, hits: [{ code: "URL_IP_LITERAL", score: 2, detail: null }] }, findings: [] }));
    expect(r?.score).toBe(9.6);
    expect(r?.hits[0]?.code).toBe("URL_IP_LITERAL");
    expect(parseSpamReport(JSON.stringify({ findings: [] }))).toBeNull();
    expect(parseSpamReport("{not json")).toBeNull();
    expect(parseSpamReport(null)).toBeNull();
  });

  it("every scorer rule code has an explanation in every language", () => {
    const codes = [
      "SPF_PASS", "SPF_NONE", "SPF_SOFTFAIL", "SPF_FAIL", "DKIM_NONE", "DKIM_INVALID", "DKIM_VALID_ALIGNED", "DMARC_PASS", "DMARC_FAIL",
      "DMARC_FAIL_QUARANTINE", "ARC_FAIL", "RDNS_NONE", "HELO_NO_DOT", "HELO_IP_MISMATCH", "HELO_FORGED_OURS", "LISTED", "HDR_MISSING_DATE",
      "HDR_DATE_FUTURE", "HDR_DATE_OLD", "HDR_MISSING_MSGID", "FROM_NAME_SPOOFS_DOMAIN", "REPLYTO_FREEMAIL", "FROM_LOOKALIKE_DOMAIN",
      "FROM_MIXED_SCRIPT_DOMAIN", "SUBJ_ALL_CAPS", "SUBJ_EXCLAIM", "HTML_IMAGE_ONLY", "PHISHING_PHRASE", "LINK_TEXT_MISMATCH", "URL_IP_LITERAL",
      "URL_SHORTENER", "URL_PUNYCODE", "ATTACHMENT_SUSPICIOUS", "SENDER_KNOWN", "SENDER_DOMAIN_NEW",
      "BAYES_999", "BAYES_99", "BAYES_95", "BAYES_80", "BAYES_05", "BAYES_00",
    ];
    for (const locale of [en, vi]) {
      const rules = (locale as unknown as { spam: { rule: Record<string, string> } }).spam.rule;
      expect(codes.filter((c) => !rules[c])).toEqual([]);
    }
  });
});

describe("date groups", () => {
  const tz = "Asia/Ho_Chi_Minh";
  // Wednesday 2026-11-25 10:00 in Hà Nội.
  const now = new Date("2026-11-25T03:00:00Z");
  const at = (local: string) => new Date(local + "+07:00");

  it("puts messages under Outlook-like headings in the user's time zone", () => {
    expect(dateGroup(at("2026-11-25T00:30:00"), now, tz)).toBe("today");
    expect(dateGroup(at("2026-11-24T23:59:00"), now, tz)).toBe("yesterday");
    expect(dateGroup(at("2026-11-23T08:00:00"), now, tz)).toBe("thisWeek"); // Monday
    expect(dateGroup(at("2026-11-22T08:00:00"), now, tz)).toBe("lastWeek"); // Sunday before
    expect(dateGroup(at("2026-11-16T08:00:00"), now, tz)).toBe("lastWeek");
    expect(dateGroup(at("2026-11-10T08:00:00"), now, tz)).toBe("thisMonth");
    expect(dateGroup(at("2026-10-31T08:00:00"), now, tz)).toBe("lastMonth");
    expect(dateGroup(at("2026-09-30T08:00:00"), now, tz)).toBe("older");
  });

  it("follows the week start of the profile", () => {
    // Sunday 22nd is in this week when weeks start on Sunday.
    expect(dateGroup(at("2026-11-22T08:00:00"), now, tz, 0)).toBe("thisWeek");
  });

  it("uses the zone, not the browser clock: 23:30 UTC is already tomorrow in Hà Nội", () => {
    expect(dateGroup(new Date("2026-11-24T17:30:00Z"), now, tz)).toBe("today");
  });

  it("has a translation for every heading", () => {
    for (const g of ["today", "yesterday", "thisWeek", "lastWeek", "thisMonth", "lastMonth", "older"]) {
      expect((en.mail.group as Record<string, string>)[g]).toBeTruthy();
      expect((vi.mail.group as Record<string, string>)[g]).toBeTruthy();
    }
  });
});

describe("avatars", () => {
  it("takes the first and last word, or the address", () => {
    expect(initials("Chị Hạnh", "hanh@x.test")).toBe("CH");
    expect(initials("Nguyễn Văn An", "an@x.test")).toBe("NA");
    expect(initials("田中 健二", "kenji@x.test")).toBe("田");
    expect(initials(null, "noreply-dmarc@google.com")).toBe("NO");
    expect(initials("\"Olivia\"", "o@x.test")).toBe("O");
  });

  it("gives one sender always the same readable colour", () => {
    expect(avatarColor("An@Doitac.test")).toBe(avatarColor("an@doitac.test"));
    expect(avatarPalette).toContain(avatarColor("x@y.test"));
  });
});

describe("sender details", () => {
  it("takes mailed-by from a passing SPF, signed-by from passing DKIM, and TLS", () => {
    const json = JSON.stringify({
      spf: "pass", spfDomain: "Doitac.test",
      dkim: [{ result: "pass", domain: "doitac.test", selector: "s1", reason: "" }, { result: "fail", domain: "evil.test", selector: "x", reason: "bad" },
        { result: "pass", domain: "doitac.test", selector: "s2", reason: "" }],
      tls: true,
    });
    expect(senderDetails(json)).toEqual({ mailedBy: "doitac.test", signedBy: ["doitac.test"], tls: true });
  });

  it("knows nothing without a summary or with a failing SPF", () => {
    expect(senderDetails(null)).toEqual({ mailedBy: null, signedBy: [], tls: null });
    expect(senderDetails("{oops")).toEqual({ mailedBy: null, signedBy: [], tls: null });
    expect(senderDetails(JSON.stringify({ spf: "fail", spfDomain: "x.test" })).mailedBy).toBeNull();
  });
});
