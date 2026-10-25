import { expect, test, type Locator, type Page } from "@playwright/test";

// T-072. The recording email sender is in memory inside the API, and production code has no way to read it (no
// backdoor). So the emailed link is not read here: the full register -> mail -> set password -> login flow is covered
// in Api.Tests with the recording sender. These specs cover the screens: /register against the live API (the real
// 202, no account created that anyone can use), and /set-password with confirm-email mocked at the network layer.

async function expectTouchTarget(locator: Locator) {
  const box = await locator.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
}

async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("register shows the same message for any address and fits the screen", async ({ page }) => {
  await page.goto("/login");
  await page.getByRole("link", { name: "Create account" }).click();
  await expect(page).toHaveURL(/\/register$/);
  await expect(page.getByRole("heading", { name: "Create account" })).toBeVisible();
  await expectTouchTarget(page.getByLabel("Email"));
  await expectTouchTarget(page.getByRole("button", { name: "Send link" }));
  await expectNoHorizontalScroll(page);

  await page.getByLabel("Email").fill(`e2e-${Date.now()}@example.test`);
  await page.getByRole("button", { name: "Send link" }).click();
  await expect(page.getByRole("status")).toContainText("If this address can be registered");
  await expectNoHorizontalScroll(page);
});

test("set password: rules, mismatch, then success goes to login with a message", async ({ page }) => {
  const posted: unknown[] = [];
  await page.route("**/api/auth/confirm-email", async (route) => {
    posted.push(route.request().postDataJSON());
    await route.fulfill({ status: 204 });
  });

  const opened = await page.goto("/set-password?userId=11111111-1111-1111-1111-111111111111&token=abc");
  await expect(page.getByRole("heading", { name: "Choose your password" })).toBeVisible();
  // The one-time token must not stay in the address bar or history, and must not leak through Referer.
  await expect(page).toHaveURL(/\/set-password$/);
  expect(page.url()).not.toContain("token");
  expect(opened?.headers()["referrer-policy"]).toBe("no-referrer");
  await expect(page.locator('meta[name="referrer"]')).toHaveAttribute("content", "no-referrer");
  await page.reload();
  await expect(page.getByLabel("New password")).toBeVisible(); // still usable after a reload (history state)
  await expect(page.getByText("At least 10 characters.")).toBeVisible();
  await expectTouchTarget(page.getByLabel("New password"));
  await expectTouchTarget(page.getByLabel("Repeat the password"));
  await expectTouchTarget(page.getByRole("button", { name: "Set password" }));
  await expectNoHorizontalScroll(page);

  await page.getByLabel("New password").fill("a long enough password");
  await page.getByLabel("Repeat the password").fill("something different");
  await page.getByRole("button", { name: "Set password" }).click();
  await expect(page.getByRole("alert")).toContainText("not the same");
  expect(posted).toHaveLength(0);

  await page.getByLabel("Repeat the password").fill("a long enough password");
  await page.getByRole("button", { name: "Set password" }).click();
  await expect(page).toHaveURL(/\/login\?passwordSet=1$/);
  await expect(page.getByRole("status")).toContainText("Your password is set");
  expect(posted).toEqual([
    { userId: "11111111-1111-1111-1111-111111111111", token: "abc", password: "a long enough password" },
  ]);
});

test("set password: an invalid link shows a message and a way to get a new one", async ({ page }) => {
  await page.route("**/api/auth/confirm-email", (route) =>
    route.fulfill({
      status: 400,
      contentType: "application/problem+json",
      body: JSON.stringify({ type: "urn:foodplanner:problem:confirmation-invalid", title: "x", status: 400 }),
    }),
  );
  await page.goto("/set-password?userId=11111111-1111-1111-1111-111111111111&token=old");
  await page.getByLabel("New password").fill("a long enough password");
  await page.getByLabel("Repeat the password").fill("a long enough password");
  await page.getByRole("button", { name: "Set password" }).click();
  await expect(page.getByRole("alert")).toContainText("not valid any more");
  const again = page.getByRole("link", { name: "Get a new link" });
  await expectTouchTarget(again);
  await again.click();
  await expect(page).toHaveURL(/\/register$/);
});
