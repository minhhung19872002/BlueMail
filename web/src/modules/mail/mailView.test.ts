import { describe, expect, it } from "vitest";
import { authWarnings, isExternal, looksLike, senderParts } from "./mailView";

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
