import i18n from "i18next";
import ICU from "i18next-icu";
import { initReactI18next } from "react-i18next";
import en from "./locales/en.json";
import vi from "./locales/vi.json";

// UI strings live only in locales/<lang>.json (ICU message format: plurals and named variables, never
// string concatenation). English is the source and fallback; a new language is a new file plus one line here.
export const resources = { en: { translation: en }, vi: { translation: vi } } as const;
export const supportedLanguages = Object.keys(resources);

void i18n
  .use(ICU)
  .use(initReactI18next)
  .init({
    resources,
    lng: pickLanguage(navigator.languages),
    fallbackLng: "en",
    interpolation: { escapeValue: false },
    returnNull: false,
  });

/** Best UI language for a list of BCP 47 tags: exact, then base language, then English. */
export function pickLanguage(tags: readonly string[]): string {
  for (const tag of tags) {
    if (supportedLanguages.includes(tag)) return tag;
    const base = tag.split("-")[0] ?? "";
    if (supportedLanguages.includes(base)) return base;
  }
  return "en";
}

/** Writing direction of a locale (for dir="rtl" and Ant Design's direction). */
export function directionOf(locale: string): "ltr" | "rtl" {
  const base = locale.split("-")[0] ?? "";
  return ["ar", "he", "fa", "ur", "ps", "yi", "dv", "ku"].includes(base) ? "rtl" : "ltr";
}

export default i18n;
