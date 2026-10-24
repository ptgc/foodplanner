import { adminCredentials, adminState, expect, expectActiveHousehold, signIn, test } from "./fixtures";

// Login is rate limited (10/min per email), so the login-flow tests are kept few: the full flow runs in both
// projects, the wrong-password check once. Everything else reuses the session from auth.setup.ts.
test.setTimeout(90_000);

test("wrong password shows the generic error", async ({ page }) => {
  test.skip(test.info().project.name !== "desktop", "one failed login per run is enough");
  await page.goto("/login");
  await page.getByLabel("Email").fill(adminCredentials().email);
  await page.getByLabel("Password").fill("definitely-wrong-password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("alert")).toContainText("Sign in failed");
  await expect(page).toHaveURL(/\/login/);
});

test("signed-out visit goes to /login and returns after sign-in, shows Home and email, reload stays signed in, logout returns to /login", async ({ page }) => {
  const { email } = adminCredentials();
  await page.goto("/");
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page);
  await expect(page).toHaveURL(/\/$/);

  const shell = page.getByRole("banner");
  await expect(shell.getByTestId("user-email")).toHaveText(email);
  await expectActiveHousehold(page, "Home");

  if ((page.viewportSize()?.width ?? 0) <= 390) {
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
  }
  const logout = page.getByRole("button", { name: "Log out" });
  const box = await logout.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);

  // The existing status page stays reachable after login.
  await expect(page.getByRole("heading", { name: "API status" })).toBeVisible();

  await page.reload();
  await expect(page.getByTestId("user-email")).toHaveText(email);

  await logout.click();
  await expect(page).toHaveURL(/\/login/);
  await page.reload();
  await expect(page).toHaveURL(/\/login/);
});

test.describe("saved session", () => {
  test.use({ storageState: adminState });

  test("storageState from the setup project is signed in without logging in again", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByTestId("user-email")).toHaveText(adminCredentials().email);
    await expect(page.getByRole("button", { name: "Log out" })).toBeVisible();
  });
});
