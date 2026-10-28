import { defineConfig, devices } from "@playwright/test";

// The stack must already be running (scripts/local-up). BASE_URL points at the web container.
const baseURL = process.env.BASE_URL ?? "http://localhost:3000";

export default defineConfig({
  testDir: "./tests",
  // T-083: the setup tags the run; the teardown fails the run if anything carrying the tag is left.
  globalSetup: "./global-setup.ts",
  globalTeardown: "./global-teardown.ts",
  outputDir: "./test-results",
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  use: {
    baseURL,
    headless: true,
    locale: "en-US",
    // T-041: the service worker is blocked by default so route mocks and fresh reads behave as in every earlier spec;
    // tests/pwa.spec.ts turns it on.
    serviceWorkers: "block",
    trace: "retain-on-failure",
  },
  projects: [
    // Signs in once as the seeded admin and saves the session to .auth/admin.json (gitignored) for the others.
    { name: "setup", testMatch: /auth\.setup\.ts/ },
    // Mobile first: 390px wide is the design target (CLAUDE.md rule 10).
    {
      name: "mobile-390",
      use: { ...devices["Desktop Chrome"], viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true },
      testIgnore: /nb\.spec\.ts/,
      dependencies: ["setup"],
    },
    {
      name: "desktop",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } },
      testIgnore: /nb\.spec\.ts/,
      dependencies: ["setup"],
    },
    // Norwegian: the browser language is nb-NO (T-038). Only nb.spec.ts runs here; every other spec asserts English.
    {
      name: "mobile-390-nb",
      use: { ...devices["Desktop Chrome"], locale: "nb-NO", viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true },
      testMatch: /nb\.spec\.ts/,
      dependencies: ["setup"],
    },
  ],
});
