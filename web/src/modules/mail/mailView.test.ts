import { describe, expect, it } from "vitest";
import { authWarnings, isExternal, looksLike, parseSpamReport, senderParts } from "./mailView";
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
