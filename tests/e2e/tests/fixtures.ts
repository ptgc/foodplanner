import { expect, test as base, type Page } from "@playwright/test";

/** Credentials of the seeded local admin (T-029). scripts/e2e.sh and e2e.ps1 fill them from user secrets. */
export function adminCredentials(): { email: string; password: string } {
  const email = process.env.E2E_ADMIN_EMAIL;
  const password = process.env.E2E_ADMIN_PASSWORD;
  if (!email || !password) {
    throw new Error("Set E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD (run via scripts/e2e.sh or scripts/e2e.ps1).");
  }
  return { email, password };
}

/**
 * Session saved once per run by tests/auth.setup.ts. Use `test.use({ storageState: adminState })` (or pass it to
 * `browser.newContext`) for tests that only need a signed-in user, so they do not hit the login rate limit.
 */
export const adminState = ".auth/admin.json";

/**
 * Signs in through the UI. Login is rate limited per email (429, 10/min), so when the server answers 429
 * this waits and retries instead of failing.
 */
export async function signIn(page: Page, returnTo = "/") {
  const { email, password } = adminCredentials();
  await page.goto(returnTo);
  for (let attempt = 0; ; attempt++) {
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(password);
    const [response] = await Promise.all([
      page.waitForResponse((r) => r.request().method() === "POST" && /\/api\/auth\/login/i.test(r.url())),
      page.getByRole("button", { name: "Sign in" }).click(),
    ]);
    if (response.status() !== 429 || attempt >= 4) break;
    await page.waitForTimeout(15_000);
  }
  await expect(page.getByTestId("user-email")).toHaveText(email);
}

/** Asserts the shell shows `name` as the active household: plain text, or the selected option of the switcher. */
export async function expectActiveHousehold(page: Page, name: string) {
  const shell = page.getByRole("banner");
  const switcher = shell.getByLabel("Household");
  if ((await switcher.count()) > 0) {
    await expect(switcher.locator("option:checked")).toHaveText(name);
  } else {
    await expect(shell.getByTestId("household-name")).toHaveText(name);
  }
}

export const test = base;
export { expect };
