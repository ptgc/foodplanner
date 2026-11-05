import { useMemo, useSyncExternalStore } from "react";
import type { en } from "./en";
import type { kidEn } from "./kid-en";

/**
 * Tiny in-house i18n (see web/README.md "Internationalisation" for why and how to add text).
 * Locales: nb and en. English is the fallback when it is loaded; a key missing everywhere shows a readable English placeholder.
 *
 * Messages are loaded per locale and per group (T-106): only the active locale's messages are fetched ("core" always,
 * "kid" with the kid screens). The other locale is fetched when the user switches to it. The app renders nothing but a
 * loader until the active locale's core messages are in (useMessagesReady), so no key is ever shown untranslated.
 */

export type Locale = "nb" | "en";
export const locales: readonly Locale[] = ["nb", "en"];

type Base<K extends string> = K extends `${infer B}.${"one" | "other"}` ? B : K;
/** Every translatable message, by key (plural messages appear once, without `.one`/`.other`). */
export type MessageKey = Base<keyof typeof en | keyof typeof kidEn>;

export type Params = Record<string, string | number>;
export type Dictionary = Readonly<Record<string, string | undefined>>;

/** "core": the whole grown-up app. "kid": kid mode and kid profiles, loaded with those screens only. */
export type MessageGroup = "core" | "kid";

const loaders: Record<Locale, Record<MessageGroup, () => Promise<Dictionary>>> = {
  en: {
    core: () => import("./en").then((m) => m.en),
    kid: () => import("./kid-en").then((m) => m.kidEn),
  },
  nb: {
    core: () => import("./nb").then((m) => m.nb),
    kid: () => import("./kid-nb").then((m) => m.kidNb),
  },
};

const groupDictionaries: Record<Locale, Partial<Record<MessageGroup, Dictionary>>> = { en: {}, nb: {} };
const merged: Partial<Record<Locale, Dictionary>> = {};
const pending = new Map<string, Promise<void>>();
/** Groups some screen has asked for; a language switch loads these for the new locale too. */
const wantedGroups = new Set<MessageGroup>(["core"]);

/** Make a loaded dictionary known. Loading does this; tests call it for every locale up front (vitest.setup.ts). */
export function registerMessages(locale: Locale, group: MessageGroup, dictionary: Dictionary): void {
  groupDictionaries[locale][group] = dictionary;
  merged[locale] = { ...groupDictionaries[locale].core, ...groupDictionaries[locale].kid };
  listeners.forEach((listener) => listener());
}

export function isMessagesLoaded(locale: Locale, group: MessageGroup = "core"): boolean {
  return groupDictionaries[locale][group] !== undefined;
}

/**
 * Load one group for a locale (default: the active one); resolves at once if it is already in. A failed load is not
 * remembered, so the next call retries.
 */
export function loadMessages(group: MessageGroup, locale: Locale = current): Promise<void> {
  wantedGroups.add(group);
  if (isMessagesLoaded(locale, group)) return Promise.resolve();
  const id = `${locale}/${group}`;
  let promise = pending.get(id);
  if (!promise) {
    promise = loaders[locale][group]()
      .then((dictionary) => registerMessages(locale, group, dictionary))
      .finally(() => pending.delete(id));
    pending.set(id, promise);
  }
  return promise;
}

const loadAllWanted = (locale: Locale) => Promise.all([...wantedGroups].map((group) => loadMessages(group, locale))).then(() => undefined);

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
  dicts: Partial<Record<Locale, Dictionary>> = merged,
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
      const found = dict?.[k];
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

let switchRequest = 0;

/**
 * Switch language. `remember` stores it on this device (a user choice); detection never stores. If the locale's messages
 * are not loaded yet they are fetched first and the screen keeps the old language until they are in (no flash of
 * untranslated text); the promise resolves when the switch has happened, and rejects (staying on the old language)
 * if the messages could not be loaded. The latest request wins when the user switches quickly.
 */
export function setLocale(locale: Locale, remember = true): Promise<void> {
  const request = ++switchRequest;
  const apply = () => {
    if (request !== switchRequest) return;
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
  };
  if ([...wantedGroups].every((group) => isMessagesLoaded(locale, group))) {
    apply(); // everything is in: the switch is synchronous
    return Promise.resolve();
  }
  return loadAllWanted(locale).then(apply);
}

/** Forget the stored choice and detect again. Used by tests. */
export function resetLocale(): void {
  try {
    window.localStorage.removeItem(localeStorageKey);
  } catch {
    // Nothing to clear.
  }
  void setLocale(initialLocale(), false);
}

/** True once the active locale's core messages are loaded. The root of the app shows only a loader before that. */
export function useMessagesReady(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => isMessagesLoaded(current),
    () => false,
  );
}

export type MessagesStatus = "loading" | "ready" | "failed";

/** "failed": neither the active language nor English could be loaded (offline without a cached copy). */
export function useMessagesStatus(): MessagesStatus {
  return useSyncExternalStore(
    subscribe,
    () => (isMessagesLoaded(current) ? "ready" : loadFailed ? "failed" : "loading"),
    () => "loading" as MessagesStatus,
  );
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

/** The active locale only. Hooks that fetch catalog names list it as a dependency so a switch refetches (T-078). */
export function useLocale(): Locale {
  return useSyncExternalStore(subscribe, getLocale, () => "en" as Locale);
}

applyDocumentLanguage();

// Start fetching the active locale's messages as soon as this module runs, in parallel with the rest of the app. If the
// device's language cannot be loaded (a stale cache, a network blip), English is tried; if that fails too the status is
// "failed" and the root offers a retry (retryLoadingMessages).
let loadFailed = false;

function startLoading(): void {
  loadFailed = false;
  listeners.forEach((listener) => listener());
  loadMessages("core")
    .catch(() => (current !== "en" ? setLocale("en", false) : Promise.reject(new Error("messages unavailable"))))
    .catch(() => {
      loadFailed = true;
      listeners.forEach((listener) => listener());
    });
}

/** Try again after the status became "failed". */
export function retryLoadingMessages(): void {
  startLoading();
}

if (typeof window !== "undefined") startLoading();

/** Proper nouns are not translated. */
export const APP_NAME = "Foodplanner";
