import { expect, test } from "@playwright/test";

// Auth fixtures: T-028 adds sign-in as the seeded admin here (a fixture extending `test`).

test("app shell loads", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveTitle("Foodplanner");
  await expect(page.getByRole("main")).toBeVisible();
  if ((page.viewportSize()?.width ?? 0) <= 390) {
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
  }
});

test("API health is reachable through the web proxy", async ({ request }) => {
  const live = await request.get("/api/health");
  expect(live.status()).toBe(200);
  const ready = await request.get("/api/health/ready");
  expect(ready.status()).toBe(200);
});
