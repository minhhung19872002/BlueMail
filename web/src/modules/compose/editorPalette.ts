// Colours offered for text in the editor (the only colour literals of the compose module live here; TSX has none).
export const textColors = ["#1f1f1f", "#5f6368", "#c5221f", "#b06000", "#188038", "#1a73e8", "#7627bb", "#b80672"] as const;

export const fontFamilies = [
  { key: "sans", css: "system-ui, -apple-system, 'Segoe UI', Roboto, 'Noto Sans', Arial, sans-serif" },
  { key: "serif", css: "Georgia, 'Times New Roman', 'Noto Serif', serif" },
  { key: "mono", css: "ui-monospace, 'Cascadia Mono', Consolas, 'Noto Sans Mono', monospace" },
] as const;

export const fontSizes = [
  { key: "small", css: "12px" },
  { key: "normal", css: "" },
  { key: "large", css: "18px" },
  { key: "huge", css: "24px" },
] as const;
