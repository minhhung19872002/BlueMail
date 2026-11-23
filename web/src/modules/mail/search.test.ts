import { describe, expect, it } from "vitest";
import { buildQuery, emptySearch, operand } from "./search";

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
