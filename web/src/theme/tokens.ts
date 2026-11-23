import { theme, type ThemeConfig } from "antd";

// The only place colours are defined (PROMPT 12, 14.13). Components read them through Ant Design tokens
// or the CSS custom properties written by applyCssVariables(); TSX files never contain colour literals.
// Pairs are checked for >= 4.5:1 contrast by tokens.test.ts in both modes.

export type Mode = "light" | "dark";

export interface Palette {
  bg: string;
  surface: string;
  surfaceRaised: string;
  text: string;
  textMuted: string;
  primary: string;
  onPrimary: string;
  border: string;
  selected: string;
  onSelected: string;
  danger: string;
  /** Stars and importance markers (icons: WCAG 1.4.11 needs 3:1; we keep 4.5:1). */
  warning: string;
}

export const palettes: Record<Mode, Palette> = {
  light: {
    bg: "#f6f8fc",
    surface: "#ffffff",
    surfaceRaised: "#eef2f9",
    text: "#1f1f1f",
    textMuted: "#4d5562",
    primary: "#0b57d0",
    onPrimary: "#ffffff",
    border: "#c7cfdb",
    selected: "#d3e3fd",
    onSelected: "#041e49",
    danger: "#b3261e",
    warning: "#a35200",
  },
  dark: {
    bg: "#111418",
    surface: "#1b1f24",
    surfaceRaised: "#252a31",
    text: "#e6e8eb",
    textMuted: "#b4bcc7",
    primary: "#a8c7fa",
    onPrimary: "#062e6f",
    border: "#4a525d",
    selected: "#004a77",
    onSelected: "#d3e3fd",
    danger: "#f2b8b5",
    warning: "#fdd663",
  },
};

/** Text/background pairs that must meet WCAG AA (4.5:1). */
export const contrastPairs: [keyof Palette, keyof Palette][] = [
  ["text", "bg"],
  ["text", "surface"],
  ["text", "surfaceRaised"],
  ["textMuted", "surface"],
  ["textMuted", "bg"],
  ["onPrimary", "primary"],
  ["onSelected", "selected"],
  ["primary", "surface"],
  ["danger", "surface"],
  ["warning", "surface"],
];

export function antdTheme(mode: Mode, density: "default" | "comfortable" | "compact"): ThemeConfig {
  const p = palettes[mode];
  const algorithms = [mode === "dark" ? theme.darkAlgorithm : theme.defaultAlgorithm];
  if (density === "compact") algorithms.push(theme.compactAlgorithm);
  return {
    algorithm: algorithms,
    token: {
      colorPrimary: p.primary,
      colorLink: p.primary, // AntD's default link blue is 4.1:1 on white; the primary colour passes 4.5:1
      colorBgBase: p.surface,
      colorBgLayout: p.bg,
      colorTextBase: p.text,
      // AntD derives secondary/description text with alpha, too faint on white (2.8:1); use the checked muted colour.
      colorTextSecondary: p.textMuted,
      colorTextDescription: p.textMuted,
      colorBorder: p.border,
      colorError: p.danger,
      borderRadius: 8,
      fontFamily: fontStack,
      sizeUnit: density === "comfortable" ? 5 : 4,
    },
  };
}

/** System fonts per script so every language renders with native glyphs (PROMPT 14.20). */
export const fontStack =
  'system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans", "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans CJK KR", ' +
  '"Noto Sans Arabic", "Noto Sans Hebrew", "Noto Sans Thai", "Helvetica Neue", Arial, sans-serif';

export function applyCssVariables(mode: Mode): void {
  const root = document.documentElement;
  for (const [k, v] of Object.entries(palettes[mode])) root.style.setProperty(`--bm-${k}`, v);
  root.style.setProperty("color-scheme", mode);
}
