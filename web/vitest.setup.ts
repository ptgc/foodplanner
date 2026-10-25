import "@testing-library/jest-dom/vitest";
import { cleanup, configure } from "@testing-library/react";
import { afterEach, beforeEach } from "vitest";
import { setLocale, type Locale } from "./app/i18n";

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
