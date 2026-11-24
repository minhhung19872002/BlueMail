import { describe, expect, it } from "vitest";
import { withMailbox } from "./mailbox";

describe("shared mailbox scope", () => {
  it("adds the open mailbox to mail calls only", () => {
    expect(withMailbox("/api/v1/mail/conversations?limit=50", "m1")).toBe("/api/v1/mail/conversations?limit=50&mailboxId=m1");
    expect(withMailbox("/api/v1/mail/labels", "m1")).toBe("/api/v1/mail/labels?mailboxId=m1");
    expect(withMailbox("/api/v1/mail/mailboxes", "m1")).toBe("/api/v1/mail/mailboxes");
    expect(withMailbox("/api/v1/mail/p/abc", "m1")).toBe("/api/v1/mail/p/abc");
    expect(withMailbox("/api/v1/calendar/events", "m1")).toBe("/api/v1/calendar/events");
    expect(withMailbox("/api/v1/mail/labels?mailboxId=x", "m1")).toBe("/api/v1/mail/labels?mailboxId=x");
    expect(withMailbox("/api/v1/mail/labels", null)).toBe("/api/v1/mail/labels");
  });
});
