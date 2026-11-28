import { describe, expect, it } from "vitest";
import { readReceiptPrompt } from "./ReadReceipt";

const asked = { readReceiptTo: "an@doitac.test", readReceiptSent: false, from: { name: "An", address: "An@doitac.test" }, draft: false };
const mine = ["minh@bluemail.test", "sales@bluemail.test"];

describe("read receipt prompt", () => {
  it("asks by default and sends without asking only with ALWAYS", () => {
    expect(readReceiptPrompt(asked, "ASK", mine, false)).toBe("ask");
    expect(readReceiptPrompt(asked, "ALWAYS", mine, false)).toBe("auto");
    expect(readReceiptPrompt(asked, "NEVER", mine, false)).toBeNull();
  });

  it("asks even with ALWAYS when the receipt would go to someone other than the sender", () => {
    expect(readReceiptPrompt({ ...asked, readReceiptTo: "tracker@elsewhere.test" }, "ALWAYS", mine, false)).toBe("ask");
  });

  it("stays quiet when nothing is asked, it was answered, it is in Spam, a draft, or my own request", () => {
    expect(readReceiptPrompt({ ...asked, readReceiptTo: null }, "ASK", mine, false)).toBeNull();
    expect(readReceiptPrompt({ ...asked, readReceiptSent: true }, "ASK", mine, false)).toBeNull();
    expect(readReceiptPrompt(asked, "ALWAYS", mine, true)).toBeNull();
    expect(readReceiptPrompt({ ...asked, draft: true }, "ASK", mine, false)).toBeNull();
    expect(readReceiptPrompt({ ...asked, readReceiptTo: "Sales@bluemail.test" }, "ASK", mine, false)).toBeNull();
  });
});
