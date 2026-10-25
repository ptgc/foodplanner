import { defineConfig } from "vitest/config";

// Separate from vite.config.ts so tests do not load the React Router build plugin.
// Every suite runs twice, once per locale (T-038): tests read texts via app/test-i18n.ts, so they pass in both.
const project = (locale: "en" | "nb") => ({
  extends: true as const,
  test: { name: locale, env: { TEST_LOCALE: locale } },
});

export default defineConfig({
  esbuild: { jsx: "automatic" },
  test: {
    environment: "jsdom",
    // Heavy form tests take 2-3 s on an idle machine (many user-event typings); the 5 s default is hit when the
    // machine is busy. A passing test still ends as soon as it is done, so this only delays real failures.
    testTimeout: 20_000,
    setupFiles: ["./vitest.setup.ts"],
    include: ["app/**/*.test.{ts,tsx}", "eslint-rules/**/*.test.ts"],
    projects: [project("en"), project("nb")],
  },
});
