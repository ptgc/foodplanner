import { defineConfig, devices } from "@playwright/test";

// The stack must already be running (scripts/local-up). BASE_URL points at the web container.
const baseURL = process.env.BASE_URL ?? "http://localhost:3000";

export default defineConfig({
  testDir: "./tests",
  outputDir: "./test-results",
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  use: {
    baseURL,
    headless: true,
    trace: "retain-on-failure",
  },
  projects: [
    // Signs in once as the seeded admin and saves the session to .auth/admin.json (gitignored) for the others.
    { name: "setup", testMatch: /auth\.setup\.ts/ },
    // Mobile first: 390px wide is the design target (CLAUDE.md rule 10).
    {
      name: "mobile-390",
      use: { ...devices["Desktop Chrome"], viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true },
      dependencies: ["setup"],
    },
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } }, dependencies: ["setup"] },
  ],
});
