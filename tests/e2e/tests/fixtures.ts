import { expect, test as base, type Locator, type Page, type TestInfo } from "@playwright/test";

/** Credentials of the seeded local admin (T-029). scripts/e2e.sh and e2e.ps1 fill them from user secrets. */
export function adminCredentials(): { email: string; password: string } {
  const email = process.env.E2E_ADMIN_EMAIL;
  const password = process.env.E2E_ADMIN_PASSWORD;
  if (!email || !password) {
    throw new Error("Set E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD (run via scripts/e2e.sh or scripts/e2e.ps1).");
  }
  return { email, password };
}

/** Credentials of the seeded member (T-045): confirmed, in no household; the invite flow adds them to one. */
export function memberCredentials(): { email: string; password: string } {
  const email = process.env.E2E_MEMBER_EMAIL;
  const password = process.env.E2E_MEMBER_PASSWORD;
  if (!email || !password) {
    throw new Error("Set E2E_MEMBER_EMAIL and E2E_MEMBER_PASSWORD (run via scripts/e2e.sh or scripts/e2e.ps1, after local-secrets and local-seed).");
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
 * this waits and retries instead of failing. Signs in as the admin unless `credentials` say otherwise.
 */
export async function signIn(page: Page, returnTo = "/", credentials = adminCredentials()) {
  const { email, password } = credentials;
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

/**
 * A Monday that no other running test uses. Projects (mobile-390, desktop), `--repeat-each` copies and the tests of
 * a spec all run in parallel against the same household, and a spec's cleanup deletes everything in its week, so two
 * tests sharing a week delete each other's data. Slots: 4 tests per repeat, 100 weeks per project.
 */
export function uniqueWeek(testInfo: TestInfo, firstMonday: number, n: number): string {
  const projectSlot = testInfo.project.name === "desktop" ? 100 : 0;
  const weeks = projectSlot + testInfo.repeatEachIndex * 4 + n;
  return new Date(firstMonday + weeks * 7 * 86_400_000).toISOString().slice(0, 10);
}

/** A name that differs between parallel copies of the same test (Date.now() alone can collide). */
export function uniqueStamp(testInfo: TestInfo): string {
  return `${Date.now()}-${testInfo.parallelIndex}-${testInfo.repeatEachIndex}`;
}

/**
 * Clicks `button` and waits for the recipe save request it sends (POST on create, PUT on edit) to answer with
 * 2xx, so a slow or refused save fails there with its status instead of as a later URL timeout. Image upload
 * and navigation follow the save, so callers still assert the URL afterwards (with a local, longer timeout).
 */
export async function saveRecipe(page: Page, button: Locator): Promise<void> {
  const [response] = await Promise.all([
    page.waitForResponse(
      (r) =>
        /\/api\/households\/[^/]+\/recipes(\/[^/]+)?$/.test(new URL(r.url()).pathname) &&
        ["POST", "PUT"].includes(r.request().method()),
      { timeout: 30_000 },
    ),
    button.click(),
  ]);
  expect(response.status(), await response.text()).toBeLessThan(300);
}

export const test = base;
export { expect };
