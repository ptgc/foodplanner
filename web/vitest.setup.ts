import "@testing-library/jest-dom/vitest";
import { cleanup, configure } from "@testing-library/react";
import { afterEach, beforeEach, vi } from "vitest";
import { registerMessages, setLocale, type Locale } from "./app/i18n";
import { en } from "./app/i18n/en";
import { kidEn } from "./app/i18n/kid-en";
import { kidNb } from "./app/i18n/kid-nb";
import { nb } from "./app/i18n/nb";

// No test talks to a real hub. Screens that listen for plan changes (usePlan) get a hub that connects and stays quiet;
// a test that needs hub events mocks "@microsoft/signalr" itself, which takes precedence over this.
vi.mock("@microsoft/signalr", () => {
  class Builder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return {
        on() {},
        onreconnecting() {},
        onreconnected() {},
        onclose() {},
        async start() {},
        async stop() {},
        async invoke() {
          return undefined;
        },
      };
    }
  }
  return { HubConnectionBuilder: Builder, LogLevel: { None: 6 } };
});

// Tests have every message loaded up front, so setLocale switches synchronously (the real app loads them on demand, T-106).
registerMessages("en", "core", en);
registerMessages("en", "kid", kidEn);
registerMessages("nb", "core", nb);
registerMessages("nb", "kid", kidNb);

// The project (en or nb) decides the language of the whole test run; nothing is stored on the "device".
const testLocale = (process.env.TEST_LOCALE ?? "en") as Locale;

// findBy*/waitFor return the moment the element is there, so a long limit costs nothing when a test passes.
// The 1 s default is too short for screens that chain 4-5 faked fetches and renders while the CPU is busy.
configure({ asyncUtilTimeout: 5000 });

beforeEach(() => {
  setLocale(testLocale, false);
});

afterEach(() => {
  cleanup();
});
