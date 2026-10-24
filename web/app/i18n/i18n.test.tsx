import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { en } from "./en";
import {
  createI18n,
  detectLocale,
  getLocale,
  localeStorageKey,
  placeholderFor,
  resetLocale,
  setLocale,
  translate,
  type Dictionary,
  type Locale,
} from "./index";
import { LanguageSwitch } from "./language-switch";
import { nb } from "./nb";
import { problemKey, problemTypeOf } from "./problems";

// Small dictionaries so the fallback rules are tested independent of the real texts.
const dicts: Record<Locale, Dictionary> = {
  en: { "a.both": "Both (en)", "a.enOnly": "English only", "a.hello": "Hello {name}", "a.n.one": "1 thing", "a.n.other": "{count} things" },
  nb: { "a.both": "Begge (nb)", "a.hello": "Hei {name}", "a.n.one": "1 ting", "a.n.other": "{count} ting" },
};

describe("fallback", () => {
  it("uses the locale's own text when it has the key", () => {
    expect(translate("nb", "a.both", undefined, dicts)).toBe("Begge (nb)");
    expect(translate("en", "a.both", undefined, dicts)).toBe("Both (en)");
  });

  it("falls back to English when nb lacks the key", () => {
    expect(translate("nb", "a.enOnly", undefined, dicts)).toBe("English only");
  });

  it("shows a short English placeholder when the key is missing in both", () => {
    expect(translate("nb", "shopping.generateFromPlan", undefined, dicts)).toBe("Generate from plan");
    expect(translate("en", "x.checkYouHave", undefined, dicts)).toBe("Check you have");
    expect(placeholderFor("plain")).toBe("Plain");
  });

  it("interpolates parameters and picks plural forms per locale", () => {
    expect(translate("nb", "a.hello", { name: "Kari" }, dicts)).toBe("Hei Kari");
    expect(translate("en", "a.n", { count: 1 }, dicts)).toBe("1 thing");
    expect(translate("en", "a.n", { count: 3 }, dicts)).toBe("3 things");
    expect(translate("nb", "a.n", { count: 3 }, dicts)).toBe("3 ting");
  });

  it("every English key has a bokmål translation, and nb has no stray keys", () => {
    const missing = Object.keys(en).filter((key) => !(key in nb));
    const stray = Object.keys(nb).filter((key) => !(key in en));
    expect({ missing, stray }).toEqual({ missing: [], stray: [] });
  });

  it("keeps placeholders {name} identical in en and nb", () => {
    const params = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    for (const [key, text] of Object.entries(en)) {
      expect(params(nb[key as keyof typeof nb] ?? text), key).toEqual(params(text));
    }
  });
});

describe("formatting with Intl", () => {
  it("formats numbers and dates in the active locale", () => {
    expect(createI18n("en").formatNumber(1.5)).toBe("1.5");
    expect(createI18n("nb").formatNumber(1.5)).toBe("1,5");
    const monday = new Date(2026, 9, 19);
    expect(createI18n("nb").formatDayMonth(monday)).toMatch(/19\.\s*okt/);
    expect(createI18n("en").formatDayMonth(monday)).toMatch(/Oct\s*19/);
  });
});

describe("locale detection", () => {
  it("maps nb, no and nn to nb and everything else to en", () => {
    expect(detectLocale(["nb-NO"])).toBe("nb");
    expect(detectLocale(["no"])).toBe("nb");
    expect(detectLocale(["nn-NO"])).toBe("nb");
    expect(detectLocale(["en-GB"])).toBe("en");
    expect(detectLocale(["de-DE"])).toBe("en");
    expect(detectLocale([])).toBe("en");
  });

  it("uses the first supported language in the browser's preference order", () => {
    expect(detectLocale(["de-DE", "nb", "en"])).toBe("nb");
    expect(detectLocale(["sv", "en-US", "nb"])).toBe("en");
  });
});

describe("user override", () => {
  afterEach(() => {
    window.localStorage.clear();
    resetLocale();
  });

  it("is remembered per device, sets <html lang>, and wins over detection after a reload", async () => {
    window.localStorage.clear();
    setLocale("en", false);
    render(<LanguageSwitch />);
    const select = screen.getByRole("combobox", { name: createI18n("en").t("common.language") });
    await userEvent.selectOptions(select, "nb");

    expect(getLocale()).toBe("nb");
    expect(document.documentElement.lang).toBe("nb");
    expect(window.localStorage.getItem(localeStorageKey)).toBe("nb");
    expect(screen.getByRole("combobox", { name: nb["common.language"] })).toHaveValue("nb");

  });

  it("a stored choice wins over the browser language when the app starts", async () => {
    window.localStorage.setItem(localeStorageKey, "nb"); // jsdom reports en-US
    vi.resetModules();
    const fresh = await import("./index");
    expect(fresh.getLocale()).toBe("nb");
    expect(document.documentElement.lang).toBe("nb");

    window.localStorage.setItem(localeStorageKey, "fr"); // garbage is ignored
    vi.resetModules();
    expect((await import("./index")).getLocale()).toBe("en");
  });

  it("starts from the browser language when nothing is stored", async () => {
    window.localStorage.clear();
    vi.spyOn(window.navigator, "languages", "get").mockReturnValue(["nb-NO", "en"]);
    vi.resetModules();
    expect((await import("./index")).getLocale()).toBe("nb");
    vi.restoreAllMocks();
  });

  it("keeps working when storage is blocked", async () => {
    const original = Storage.prototype.setItem;
    Storage.prototype.setItem = () => {
      throw new Error("blocked");
    };
    try {
      setLocale("nb");
      expect(getLocale()).toBe("nb");
    } finally {
      Storage.prototype.setItem = original;
    }
  });
});

describe("ProblemDetails mapping", () => {
  it("maps every known urn:foodplanner:problem code to a message that exists in both locales", () => {
    const codes = [
      "login-failed",
      "rate-limited",
      "unauthenticated",
      "forbidden",
      "forbidden-origin",
      "antiforgery-invalid",
      "already-member",
      "invite-invalid",
      "confirmation-invalid",
    ];
    for (const code of codes) {
      const key = problemKey(`urn:foodplanner:problem:${code}`);
      expect(key, code).not.toBe("problem.generic");
      expect(en[key as keyof typeof en], code).toBeDefined();
      expect(nb[key as keyof typeof nb], code).toBeDefined();
    }
  });

  it("uses the generic message for unknown or missing types", () => {
    expect(problemKey("urn:foodplanner:problem:nope")).toBe("problem.generic");
    expect(problemKey("about:blank")).toBe("problem.generic");
    expect(problemKey(undefined)).toBe("problem.generic");
  });

  it("reads the type from a ProblemDetails body", () => {
    expect(problemTypeOf({ type: "urn:foodplanner:problem:forbidden", title: "x" })).toBe("urn:foodplanner:problem:forbidden");
    expect(problemTypeOf(undefined)).toBeUndefined();
    expect(problemTypeOf({ type: 5 })).toBeUndefined();
  });
});
