import { adminCredentials, expect, signIn, test } from "./fixtures";

test("signed-out visit to an app route goes to /login and returns after sign-in", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page);
  await expect(page).toHaveURL(/\/$/);
});

test("wrong password shows the generic error", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill(adminCredentials().email);
  await page.getByLabel("Password").fill("definitely-wrong-password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("alert")).toContainText("Sign in failed");
  await expect(page).toHaveURL(/\/login/);
});

test("sign in, see Home and email, reload stays signed in, logout returns to /login", async ({ page }) => {
  const { email } = adminCredentials();
  await signIn(page);

  const shell = page.getByRole("banner");
  await expect(shell.getByTestId("user-email")).toHaveText(email);
  await expect(shell.getByText("Home", { exact: true })).toBeVisible();

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

test("fixture: adminPage is signed in", async ({ adminPage }) => {
  await expect(adminPage.getByRole("button", { name: "Log out" })).toBeVisible();
});
