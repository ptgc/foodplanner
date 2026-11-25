import { adminCredentials, adminState, closeProfile, expect, expectActiveHousehold, openProfile, signIn, test } from "./fixtures";

// Login is rate limited (10/min per email), so the login-flow tests are kept few: the full flow runs in both
// projects, the wrong-password check once. Everything else reuses the session from auth.setup.ts.
test.setTimeout(90_000);

test("wrong password shows the generic error", async ({ page }) => {
  test.skip(test.info().project.name !== "desktop", "one failed login per run is enough");
  await page.goto("/login");
  await page.getByLabel("Email").fill(adminCredentials().email);
  await page.getByLabel("Password").fill("definitely-wrong-password");
  await page.getByRole("button", { name: "Sign in" }).click();
  // Login hashing can take several seconds under a full parallel run, so wait longer than the 5 s default.
  await expect(page.getByRole("alert")).toContainText("Sign in failed", { timeout: 15_000 });
  await expect(page).toHaveURL(/\/login/);
});

test("signed-out visit goes to /login and returns after sign-in, shows Home and email, reload stays signed in, logout returns to /login", async ({ page }) => {
  const { email } = adminCredentials();
  await page.goto("/");
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page);
  // "I dag" shows the week plan until the start screen exists (T-138).
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Meal plan", level: 1 })).toBeVisible();

  const sheet = await openProfile(page);
  await expect(sheet.getByTestId("user-email")).toHaveText(email);
  await closeProfile(page);
  await expect(page.getByRole("banner").getByTestId("user-email")).toHaveCount(0);
  await expectActiveHousehold(page, "Home");

  if ((page.viewportSize()?.width ?? 0) <= 390) {
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
  }
  // The bottom tab bar and the profile button are 44px targets.
  for (const target of [page.getByTestId("profile-button"), ...(await page.getByRole("navigation", { name: "Main" }).getByRole("link").all())]) {
    const tapBox = await target.boundingBox();
    expect(tapBox?.height ?? 0).toBeGreaterThanOrEqual(44);
    expect(tapBox?.width ?? 0).toBeGreaterThanOrEqual(44);
  }

  await page.reload();
  const reloaded = await openProfile(page);
  await expect(reloaded.getByTestId("user-email")).toHaveText(email);

  const logout = reloaded.getByRole("button", { name: "Log out" });
  const box = await logout.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
  await logout.click();
  await expect(page).toHaveURL(/\/login/);
  await page.reload();
  await expect(page).toHaveURL(/\/login/);
});

test.describe("saved session", () => {
  test.use({ storageState: adminState });

  test("storageState from the setup project is signed in without logging in again", async ({ page }) => {
    await page.goto("/");
    const sheet = await openProfile(page);
    await expect(sheet.getByTestId("user-email")).toHaveText(adminCredentials().email);
    await expect(sheet.getByRole("button", { name: "Log out" })).toBeVisible();
  });
});
