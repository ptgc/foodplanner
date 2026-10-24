import { useMemo, useSyncExternalStore } from "react";
import { en } from "./en";
import { nb } from "./nb";

/**
 * Tiny in-house i18n (see web/README.md "Internationalisation" for why and how to add text).
 * Locales: nb and en. English is the fallback; a key missing everywhere shows a readable English placeholder.
 */

export type Locale = "nb" | "en";
export const locales: readonly Locale[] = ["nb", "en"];

type Base<K extends string> = K extends `${infer B}.${"one" | "other"}` ? B : K;
/** Every translatable message, by key (plural messages appear once, without `.one`/`.other`). */
export type MessageKey = Base<keyof typeof en>;

export type Params = Record<string, string | number>;
export type Dictionary = Readonly<Record<string, string | undefined>>;

const dictionaries: Record<Locale, Dictionary> = { en, nb };

/** "shopping.generateFromPlan" -> "Generate from plan": what a user sees if a key exists in no locale. */
export function placeholderFor(key: string): string {
  const last = key.split(".").pop() ?? key;
  const words = last.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function interpolate(message: string, params: Params | undefined): string {
  if (!params) return message;
  return message.replace(/\{(\w+)\}/g, (whole, name: string) => (name in params ? String(params[name]) : whole));
}

/** Pure lookup: the locale, then English, then the placeholder. `dicts` is injectable for tests. */
export function translate(
  locale: Locale,
  key: string,
  params?: Params,
  dicts: Record<Locale, Dictionary> = dictionaries,
): string {
  let suffix = "";
  if (params && typeof params.count === "number") {
    // en/nb have one + other; PluralRules also covers any later locale.
    const category = new Intl.PluralRules(locale).select(params.count);
    suffix = category === "one" ? ".one" : ".other";
  }
  const candidates = [key + suffix, key];
  for (const dict of [dicts[locale], dicts.en]) {
    for (const k of candidates) {
      const found = dict[k];
      if (found !== undefined) return interpolate(found, params);
    }
  }
  return interpolate(placeholderFor(key), params);
}

/** Browser languages in order of preference -> app locale. nb/no/nn -> nb; otherwise en. */
export function detectLocale(languages: readonly string[]): Locale {
  for (const language of languages) {
    const base = language.toLowerCase().split("-")[0];
    if (base === "nb" || base === "no" || base === "nn") return "nb";
    if (base === "en") return "en";
  }
  return "en";
}

export const localeStorageKey = "foodplanner.locale";

function isLocale(value: unknown): value is Locale {
  return value === "nb" || value === "en";
}

function readStoredLocale(): Locale | null {
  try {
    const stored = window.localStorage.getItem(localeStorageKey);
    return isLocale(stored) ? stored : null;
  } catch {
    return null; // storage blocked
  }
}

function initialLocale(): Locale {
  if (typeof window === "undefined") return "en"; // build-time prerender
  const languages = navigator.languages?.length ? navigator.languages : [navigator.language ?? "en"];
  return readStoredLocale() ?? detectLocale(languages);
}

let current: Locale = initialLocale();
const listeners = new Set<() => void>();

function applyDocumentLanguage() {
  if (typeof document !== "undefined") document.documentElement.lang = current;
}

export function getLocale(): Locale {
  return current;
}

/** Switch language. `remember` stores it on this device (a user choice); detection never stores. */
export function setLocale(locale: Locale, remember = true): void {
  current = locale;
  if (remember) {
    try {
      window.localStorage.setItem(localeStorageKey, locale);
    } catch {
      // Private mode: the choice then lasts for this page only.
    }
  }
  applyDocumentLanguage();
  listeners.forEach((listener) => listener());
}

/** Forget the stored choice and detect again. Used by tests. */
export function resetLocale(): void {
  try {
    window.localStorage.removeItem(localeStorageKey);
  } catch {
    // Nothing to clear.
  }
  setLocale(initialLocale(), false);
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}

export function formatNumber(locale: Locale, value: number, options?: Intl.NumberFormatOptions): string {
  return new Intl.NumberFormat(locale, options).format(value);
}

/** Two day-month dates, e.g. "19. okt. - 25. okt." / "Oct 19 - Oct 25". */
export function formatDayMonth(locale: Locale, date: Date): string {
  return new Intl.DateTimeFormat(locale, { day: "numeric", month: "short" }).format(date);
}

export type I18n = {
  locale: Locale;
  t: (key: MessageKey, params?: Params) => string;
  formatNumber: (value: number, options?: Intl.NumberFormatOptions) => string;
  formatDayMonth: (date: Date) => string;
};

export function createI18n(locale: Locale): I18n {
  return {
    locale,
    t: (key, params) => translate(locale, key, params),
    formatNumber: (value, options) => formatNumber(locale, value, options),
    formatDayMonth: (date) => formatDayMonth(locale, date),
  };
}

/** Components call this; they re-render when the language changes. */
export function useI18n(): I18n {
  // The server (prerender) snapshot is "en", so hydration matches the prerendered HTML; the client then switches.
  const locale = useSyncExternalStore(subscribe, getLocale, () => "en" as Locale);
  return useMemo(() => createI18n(locale), [locale]);
}

applyDocumentLanguage();

/** Proper nouns are not translated. */
export const APP_NAME = "Foodplanner";
