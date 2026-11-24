import { describe, expect, it } from "vitest";
import { mergeText, textToHtml } from "./MailMerge";

describe("mail merge", () => {
  it("fills the fields per contact", () => {
    const c = { displayName: "Lan Trần", email: "lan@x.test" };
    expect(mergeText("Chào {{name}} ({{ fullName }}) <{{email}}> {{other}}", c)).toBe("Chào Lan (Lan Trần) <lan@x.test> {{other}}");
    expect(mergeText("Hi {{name}}", { displayName: "", email: "a@x.test" })).toBe("Hi a@x.test");
  });

  it("keeps merged values as text, never markup", () => {
    expect(textToHtml("Hello <b>{{x}}</b>\nline two\n\nNext")).toBe("<p>Hello &lt;b&gt;{{x}}&lt;/b&gt;<br>line two</p><p>Next</p>");
  });
});
