import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import en from "./i18n/locales/en.json";
import vi from "./i18n/locales/vi.json";

const root = join(__dirname);
const locales: Record<string, unknown> = { en, vi };

function files(dir: string, ext: RegExp): string[] {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? files(p, ext) : ext.test(name) ? [p] : [];
  });
}

function flatten(obj: unknown, prefix = ""): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === "string") out[key] = v;
    else Object.assign(out, flatten(v, key));
  }
  return out;
}

const sources = files(root, /\.(ts|tsx)$/).filter((f) => !f.endsWith(".test.ts") && !f.endsWith(".test.tsx"));

describe("translations", () => {
  const enKeys = Object.keys(flatten(en)).sort();

  it("every language has exactly the keys of English", () => {
    for (const [lang, table] of Object.entries(locales)) {
      expect(Object.keys(flatten(table)).sort(), lang).toEqual(enKeys);
    }
  });

  it("every literal t('...') key used in code exists", () => {
    const used = new Set<string>();
    for (const f of sources) {
      for (const m of readFileSync(f, "utf8").matchAll(/\bt\(\s*"([a-zA-Z0-9_.]+)"/g)) used.add(m[1]!);
    }
    const missing = [...used].filter((k) => !enKeys.includes(k));
    expect(missing).toEqual([]);
  });

  it("interpolation variables match across languages", () => {
    // ICU arguments are "{name}" or "{name, type...}"; plural branches like "{Inbox (#)}" are not arguments.
    const vars = (s: string) => [...s.matchAll(/\{(\w+)\s*[,}]/g)].map((m) => m[1]).sort();
    const enFlat = flatten(en);
    for (const [lang, table] of Object.entries(locales)) {
      for (const [k, v] of Object.entries(flatten(table))) {
        expect(new Set(vars(v)), `${lang}:${k}`).toEqual(new Set(vars(enFlat[k]!)));
      }
    }
  });
});

describe("source conventions", () => {
  const tsx = sources.filter((f) => f.endsWith(".tsx"));

  it("no colour literals in TSX (colours come from theme tokens)", () => {
    const offenders = tsx.filter((f) => /#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(/.test(readFileSync(f, "utf8")));
    expect(offenders).toEqual([]);
  });

  it("no `any` type", () => {
    const offenders = sources.filter((f) => /:\s*any\b|<any>|as any\b/.test(readFileSync(f, "utf8")));
    expect(offenders).toEqual([]);
  });

  it("no hard-coded text between JSX tags", () => {
    const offenders: string[] = [];
    for (const f of tsx) {
      // Text after a real tag end (not "=>" / "->"), without code characters, before the next tag.
      for (const m of readFileSync(f, "utf8").matchAll(/(?<![=-])>\s*([^<>{}\n()"'=&?;:]*\p{L}{2,}[^<>{}\n()"'=&?;:]*)\s*</gu)) {
        offenders.push(`${f}: ${m[1]!.trim()}`);
      }
    }
    expect(offenders).toEqual([]);
  });

  it("no physical left/right in styles (RTL-safe logical properties)", () => {
    const css = files(root, /\.css$/).map((f) => readFileSync(f, "utf8")).join("\n");
    const code = sources.map((f) => readFileSync(f, "utf8")).join("\n");
    expect(css + code).not.toMatch(/\b(margin|padding)-(left|right)\b|\b(marginLeft|marginRight|paddingLeft|paddingRight)\b|text-align:\s*(left|right)/);
  });

  it("HTTP only through src/api", () => {
    const offenders = sources.filter((f) => !f.includes(join("src", "api")) && !f.endsWith("config.ts") && /\bfetch\(/.test(readFileSync(f, "utf8")));
    expect(offenders).toEqual([]);
  });
});
