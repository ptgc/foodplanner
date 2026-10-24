import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, beforeEach } from "vitest";
import { setLocale, type Locale } from "./app/i18n";

// The project (en or nb) decides the language of the whole test run; nothing is stored on the "device".
const testLocale = (process.env.TEST_LOCALE ?? "en") as Locale;

beforeEach(() => {
  setLocale(testLocale, false);
});

afterEach(() => {
  cleanup();
});
