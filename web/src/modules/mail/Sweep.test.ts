import { describe, expect, it } from "vitest";
import { sweepQuery } from "./Sweep";

describe("sweep", () => {
  it("searches the sender, quoting only when needed, and adds the age limit", () => {
    expect(sweepQuery("an@doitac.test", "all")).toBe("from:an@doitac.test");
    expect(sweepQuery("an@doitac.test", "allAndBlock")).toBe("from:an@doitac.test");
    expect(sweepQuery("an@doitac.test", "older")).toBe("from:an@doitac.test older_than:10d");
    expect(sweepQuery("\"a b\"@x.test", "all")).toBe("from:\"a b@x.test\"");
  });
});
