import { adminState, expect, test } from "./fixtures";

// T-113 privacy page: reachable from the household page when signed in, and from the sign-in screen when signed out.

async function expectFits(page: import("@playwright/test").Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  const heights = await page
    .locator("main a:visible, main button:visible")
    .evaluateAll((els) => els.map((el) => ({ name: el.textContent?.trim() ?? "", height: el.getBoundingClientRect().height })));
  for (const { name, height } of heights) expect(height, `height of "${name}"`).toBeGreaterThanOrEqual(43.5);
}

test.describe("signed in", () => {
  test.use({ storageState: adminState });

  test("the household page links to the privacy page, which links on to export and deletion", async ({ page }) => {
    await page.goto("/household");
    await page.getByTestId("privacy-link").click();
    await expect(page).toHaveURL(/\/privacy$/);
    await expect(page.getByRole("heading", { level: 1, name: "Privacy" })).toBeVisible();
    await expect(page.getByTestId("privacy-ai")).toContainText("may be sent to Anthropic");
    await expect(page.getByTestId("privacy-oda")).toContainText("no Oda sign-in token");
    await expect(page.getByTestId("privacy-export-link")).toBeVisible();
    await expect(page.getByTestId("privacy-delete-link")).toHaveAttribute("href", "/account/delete");
    await expectFits(page);
  });
});

test.describe("signed out", () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test("the sign-in screen links to the privacy page, readable without an account", async ({ page }) => {
    await page.goto("/login");
    await page.getByTestId("privacy-link").click();
    await expect(page).toHaveURL(/\/privacy$/);
    await expect(page.getByRole("heading", { level: 1, name: "Privacy" })).toBeVisible();
    await expect(page.getByTestId("privacy-export-link")).toHaveCount(0);
    await expectFits(page);
    await page.getByRole("link", { name: "Back to the app" }).click();
    await expect(page).toHaveURL(/\/login$/);
  });
});
