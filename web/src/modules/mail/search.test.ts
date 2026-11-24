import { describe, expect, it } from "vitest";
import { buildQuery, emptySearch, operand, readRecent, RecentMax, rememberSearch, searchSuggestions } from "./search";

describe("advanced search", () => {
  it("quotes values that are not one plain word", () => {
    expect(operand("an@acme.vn")).toBe("an@acme.vn");
    expect(operand("hợp đồng")).toBe('"hợp đồng"');
    expect(operand('say "hi"')).toBe('"say hi"');
    expect(operand("OR")).toBe('"OR"');
    expect(operand("-x")).toBe('"-x"');
    expect(operand("a:b")).toBe('"a:b"');
  });

  it("builds an operator query from the form", () => {
    expect(buildQuery({ ...emptySearch, from: "an@acme.vn", subject: "hợp đồng", hasWords: "bao gia", notWords: "nhap thu" })).toBe(
      'from:an@acme.vn subject:"hợp đồng" bao gia -nhap -thu',
    );
    expect(buildQuery({ ...emptySearch, size: "10", sizeOp: "larger", sizeUnit: "M", hasAttachment: true, where: "inbox", unread: true })).toBe(
      "larger:10M in:inbox has:attachment is:unread",
    );
    expect(buildQuery({ ...emptySearch, size: "ten" })).toBe("");
  });

  it("turns 'within' into a date window", () => {
    expect(buildQuery({ ...emptySearch, within: "7d" })).toBe("newer_than:7d");
    expect(buildQuery({ ...emptySearch, within: "7d", date: "2026/03/10" })).toBe("after:2026/03/03 before:2026/03/17");
    expect(buildQuery({ ...emptySearch, within: "1m", date: "2026/01/31" })).toBe("after:2025/12/31 before:2026/03/03");
    expect(buildQuery({ ...emptySearch, date: "2026/03/10" })).toBe("before:2026/03/10");
  });
});

describe("search suggestions", () => {
  const src = {
    recent: ["from:an@acme.vn", "báo giá"],
    labels: [
      { name: "INBOX", systemKey: "INBOX" },
      { name: "Hợp đồng", systemKey: null },
      { name: "Dự án Hà Nội", systemKey: null },
    ],
    contacts: [
      { name: "Nguyễn Văn An", address: "an@acme.vn" },
      { name: null, address: "hoa@bluemail.test" },
    ],
  };

  it("shows recent searches for an empty box", () => {
    expect(searchSuggestions("", src).map((s) => s.query)).toEqual(["from:an@acme.vn", "báo giá"]);
  });

  it("matches labels and contacts without accents", () => {
    expect(searchSuggestions("hop dong", src)).toEqual([{ kind: "label", label: "Hợp đồng", query: 'label:"Hợp đồng"' }]);
    expect(searchSuggestions("van an", src).map((s) => s.query)).toEqual(["from:an@acme.vn"]);
    expect(searchSuggestions("ha noi", src)[0]?.query).toBe('label:"Dự án Hà Nội"');
  });

  it("never suggests system labels and drops duplicates", () => {
    expect(searchSuggestions("inbox", src)).toEqual([]);
    expect(searchSuggestions("an@", src).map((s) => s.query)).toEqual(["from:an@acme.vn"]);
  });

  it("keeps recent searches newest first, unique, at most ten", () => {
    const store = new Map<string, string>();
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) };
    for (let i = 0; i < 12; i++) rememberSearch("mb", `q${i}`, storage);
    rememberSearch("mb", "q5", storage);
    const recent = readRecent("mb", storage);
    expect(recent[0]).toBe("q5");
    expect(recent).toHaveLength(RecentMax);
    expect(new Set(recent).size).toBe(RecentMax);
  });
});
