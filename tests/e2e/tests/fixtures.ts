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

export async function signIn(page: Page, returnTo = "/") {
  const { email, password } = adminCredentials();
  await page.goto(returnTo);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByTestId("user-email")).toHaveText(email);
}

type Fixtures = { adminPage: Page };

/** `adminPage` is a page already signed in as the seeded admin. */
export const test = base.extend<Fixtures>({
  adminPage: async ({ page }, use) => {
    await signIn(page);
    await use(page);
  },
});

export { expect };
