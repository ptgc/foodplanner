import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { localeStorageKey } from "./index";

type I18nModule = typeof import("./index");

/** A fresh copy of the i18n module, as the browser has it at start: nothing registered (vitest.setup.ts registers everything for the other suites). */
async function startApp(stored: "nb" | "en"): Promise<I18nModule> {
  window.localStorage.setItem(localeStorageKey, stored);
  vi.resetModules();
  return import("./index");
}

describe("message loading (T-106)", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  afterEach(() => {
    window.localStorage.clear();
    vi.doUnmock("./nb");
    vi.resetModules();
  });

  it("loads only the active locale's core messages: not the other language, not the kid messages", async () => {
    const i18n = await startApp("nb");
    expect(i18n.isMessagesLoaded("nb")).toBe(false);
    await i18n.loadMessages("core");

    expect(i18n.isMessagesLoaded("nb")).toBe(true);
    expect(i18n.isMessagesLoaded("en")).toBe(false);
    expect(i18n.isMessagesLoaded("nb", "kid")).toBe(false);
    expect(i18n.isMessagesLoaded("en", "kid")).toBe(false);
  });

  it("starts loading by itself when the app starts, and useMessagesReady turns true when it is done", async () => {
    const i18n = await startApp("nb");
    const { result } = renderHook(() => i18n.useMessagesReady());
    expect(result.current).toBe(false); // the app shows only the loader
    await waitFor(() => expect(result.current).toBe(true));
    expect(i18n.translate("nb", "common.loading")).toBe("Laster...");
  });

  it("shows a readable placeholder, not the raw key, for a message that is not loaded yet", async () => {
    const i18n = await startApp("nb");
    expect(i18n.translate("nb", "kid.nav.home")).toBe("Home");
    await i18n.loadMessages("kid");
    expect(i18n.translate("nb", "kid.nav.home")).toBe("Retter");
  });

  it("shares one request between callers that ask for the same messages", async () => {
    const i18n = await startApp("en");
    const first = i18n.loadMessages("kid");
    const second = i18n.loadMessages("kid");
    expect(second).toBe(first);
    await first;
    expect(i18n.isMessagesLoaded("en", "kid")).toBe(true);
  });

  it("loads the other language on switch and keeps the old one until it is in", async () => {
    const i18n = await startApp("en");
    await i18n.loadMessages("core");

    const switching = i18n.setLocale("nb");
    expect(i18n.getLocale()).toBe("en"); // nothing flips to untranslated text
    expect(document.documentElement.lang).toBe("en");
    await switching;

    expect(i18n.getLocale()).toBe("nb");
    expect(document.documentElement.lang).toBe("nb");
    expect(window.localStorage.getItem(localeStorageKey)).toBe("nb");
    expect(i18n.translate("nb", "common.loading")).toBe("Laster...");
  });

  it("switches at once when the language is already loaded", async () => {
    const i18n = await startApp("en");
    await i18n.setLocale("nb");
    await i18n.setLocale("en");
    void i18n.setLocale("nb");
    expect(i18n.getLocale()).toBe("nb"); // synchronous, no await
  });

  it("loads the kid messages for the new language too when a kid screen has been used", async () => {
    const i18n = await startApp("en");
    await i18n.loadMessages("kid");
    await i18n.setLocale("nb");

    expect(i18n.isMessagesLoaded("nb", "kid")).toBe(true);
    expect(i18n.translate("nb", "kid.nav.home")).toBe("Retter");
  });

  it("the last of two quick switches wins", async () => {
    const i18n = await startApp("en");
    await i18n.loadMessages("core");
    const toNb = i18n.setLocale("nb");
    const backToEn = i18n.setLocale("en");
    await Promise.all([toNb, backToEn]);
    expect(i18n.getLocale()).toBe("en");
  });

  it("reports a failed start when neither the language nor English can be loaded, and a retry recovers", async () => {
    vi.resetModules();
    window.localStorage.setItem(localeStorageKey, "nb");
    vi.doMock("./nb", () => {
      throw new Error("offline");
    });
    vi.doMock("./en", () => {
      throw new Error("offline");
    });
    const i18n = await import("./index");
    const { result } = renderHook(() => i18n.useMessagesStatus());
    expect(result.current).toBe("loading");
    await waitFor(() => expect(result.current).toBe("failed"));

    vi.doUnmock("./nb");
    vi.doUnmock("./en");
    act(() => i18n.retryLoadingMessages());
    await waitFor(() => expect(result.current).toBe("ready"));
    expect(i18n.getLocale()).toBe("nb");
    vi.doUnmock("./en");
  });

  it("falls back to English when only the stored language cannot be loaded", async () => {
    vi.resetModules();
    window.localStorage.setItem(localeStorageKey, "nb");
    vi.doMock("./nb", () => {
      throw new Error("offline");
    });
    const i18n = await import("./index");
    const { result } = renderHook(() => i18n.useMessagesStatus());
    await waitFor(() => expect(result.current).toBe("ready"));
    expect(i18n.getLocale()).toBe("en");
  });

  it("stays in the old language, and rejects, when the new messages cannot be loaded; a later try can succeed", async () => {
    vi.resetModules();
    window.localStorage.setItem(localeStorageKey, "en");
    vi.doMock("./nb", () => {
      throw new Error("network down");
    });
    const i18n = await import("./index");
    await i18n.loadMessages("core");

    await expect(i18n.setLocale("nb")).rejects.toThrow();
    expect(i18n.getLocale()).toBe("en");
    expect(window.localStorage.getItem(localeStorageKey)).toBe("en"); // the choice is not stored
    expect(i18n.isMessagesLoaded("nb")).toBe(false);

    vi.doUnmock("./nb");
    await act(async () => {
      await i18n.setLocale("nb");
    });
    expect(i18n.getLocale()).toBe("nb");
  });
});
