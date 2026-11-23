import { describe, expect, it } from "vitest";
import { contrastPairs, palettes } from "./tokens";

// WCAG 2.1 relative luminance and contrast ratio, computed - not eyeballed (PROMPT 14.14).
function luminance(hex: string): number {
  const n = parseInt(hex.slice(1), 16);
  const channels = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0]! + 0.7152 * channels[1]! + 0.0722 * channels[2]!;
}

export function contrast(a: string, b: string): number {
  const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (l1 + 0.05) / (l2 + 0.05);
}

describe("theme contrast", () => {
  for (const mode of ["light", "dark"] as const) {
    for (const [fg, bg] of contrastPairs) {
      it(`${mode}: ${fg} on ${bg} >= 4.5:1`, () => {
        expect(contrast(palettes[mode][fg], palettes[mode][bg])).toBeGreaterThanOrEqual(4.5);
      });
    }
  }
});

describe("label palette", () => {
  it("white text is readable on every label colour (WCAG AA 4.5:1)", async () => {
    const { labelPalette } = await import("../modules/mail/labels");
    for (const c of labelPalette) expect(contrast(c, "#ffffff"), c).toBeGreaterThanOrEqual(4.5);
  });
});
