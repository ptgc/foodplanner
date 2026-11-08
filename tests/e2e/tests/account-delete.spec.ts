import type { APIRequestContext, Page } from "@playwright/test";
import { adminCredentials, adminState, expect, test, uniqueStamp } from "./fixtures";

// T-095 delete-account screen. This runs as the seeded admin on the architect's local stack, so it NEVER submits a
// real delete: POST /api/account/delete is answered by the test (route interception). The preview GET is the real one.
// Wrong passwords are never sent to the API either (they would count towards the admin's sign-in lockout).
test.use({ storageState: adminState });

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  const { token } = (await res.json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

async function createHousehold(request: APIRequestContext, name: string) {
  const res = await request.post("/api/households", { data: { name }, headers: await csrf(request) });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/** The run's household is removed whatever the test did (the run tag in the name lets the teardown find leftovers too). */
async function deleteHouseholdIfPresent(request: APIRequestContext, name: string) {
  const list = await request.get("/api/households");
  if (!list.ok()) return;
  const found = ((await list.json()) as { id: string; name: string }[]).find((h) => h.name === name);
  if (found) await request.delete(`/api/households/${found.id}`, { headers: await csrf(request) });
}

async function expectTouchFriendly(page: Page) {
  const controls = page.locator("main button:visible, main input:visible, main a:visible, nav a:visible");
  const heights = await controls.evaluateAll((els) =>
    els.map((el) => ({ name: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName, height: el.getBoundingClientRect().height })),
  );
  expect(heights.length).toBeGreaterThan(0);
  for (const { name, height } of heights) expect(height, `height of "${name}"`).toBeGreaterThanOrEqual(43.5);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

test("delete account screen: preview, confirmation, mocked errors and the mocked success (never a real delete)", async ({ page }, testInfo) => {
  test.setTimeout(90_000);
  const name = `Zz E2E ${uniqueStamp(testInfo)} acct`;
  const { email } = adminCredentials();
  const deleteBodies: Record<string, unknown>[] = [];
  let answer: () => { status: number; headers?: Record<string, string>; body?: unknown } = () => ({ status: 204 });

  // The only write: answered here, so the real account (the admin) is never deleted.
  await page.route("**/api/account/delete", async (route) => {
    deleteBodies.push(route.request().postDataJSON() as Record<string, unknown>);
    const { status, headers, body } = answer();
    await route.fulfill({
      status,
      headers: { "content-type": "application/problem+json", ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  });

  try {
    await createHousehold(page.request, name); // the admin is its only member: it is deleted with the account

    // The real preview decides whether the admin could delete at all (the local "Home" may have other members).
    // When it blocks, the form is shown by patching that one answer, so the form can still be exercised.
    const real = (await (await page.request.get("/api/account/deletion-preview")).json()) as { canDelete: boolean };
    if (!real.canDelete) {
      await page.route("**/api/account/deletion-preview", async (route) => {
        const response = await route.fetch();
        const body = (await response.json()) as Record<string, unknown>;
        await route.fulfill({ response, json: { ...body, canDelete: true, blockingHouseholds: [] } });
      });
    }

    // --- Entry on the household page leads to the screen ---
    await page.goto("/household");
    await page.getByTestId("delete-account-link").click();
    await expect(page).toHaveURL(/\/account\/delete$/);
    await expect(page.getByRole("heading", { name: "Delete my account", level: 1 })).toBeVisible();

    // --- Preview: the new household is listed as deleted with the account; export is linked ---
    const toDelete = page.getByTestId("households-to-delete");
    await expect(toDelete).toContainText(name);
    await expect(toDelete).toContainText("1 member");
    await expect(page.getByTestId("export-links").getByRole("link", { name: `Export ${name}` })).toBeVisible();
    await expectTouchFriendly(page);

    // --- The button needs the password and the typed email (case and spaces ignored) ---
    const button = page.getByTestId("delete-submit");
    await expect(button).toBeDisabled();
    await page.getByLabel("Your password").fill("not-the-real-password");
    await expect(button).toBeDisabled();
    const confirmation = page.getByLabel(/Type your email address to confirm/);
    await expect(confirmation).toBeVisible();
    await confirmation.fill("someone.else@example.test");
    await expect(button).toBeDisabled();
    await confirmation.fill(`  ${email.toUpperCase()} `);
    await expect(button).toBeEnabled();
    await expectTouchFriendly(page);

    // --- Mocked refusals ---
    answer = () => ({ status: 403, body: { status: 403, type: "urn:foodplanner:problem:password-incorrect" } });
    await button.click();
    await expect(page.getByTestId("delete-error")).toHaveText("The password is not correct.");
    await expect(page.getByLabel("Your password")).toHaveValue("");
    await expect(button).toBeDisabled(); // the password has to be typed again

    await page.getByLabel("Your password").fill("not-the-real-password");
    answer = () => ({ status: 429, headers: { "Retry-After": "30" }, body: { status: 429, type: "urn:foodplanner:problem:rate-limited" } });
    await button.click();
    await expect(page.getByTestId("delete-error")).toHaveText("Too many attempts. Wait 30 seconds and try again.");

    answer = () => ({ status: 409, body: { status: 409, type: "urn:foodplanner:problem:account-deletion-changed" } });
    await button.click();
    await expect(page.getByTestId("delete-error")).toContainText("Something changed since you opened this page");
    await expect(toDelete).toContainText(name); // the preview was read again

    // --- Mocked success: sent the shown household ids, then the sign-in page says the account was deleted ---
    answer = () => ({ status: 204 });
    await button.click();
    await expect(page).toHaveURL(/\/login\?accountDeleted=1$/);
    await expect(page.getByTestId("account-deleted-note")).toHaveText("Your account was deleted.");
    expect(deleteBodies.length).toBe(4);
    const last = deleteBodies[3]!;
    expect(last.confirmation).toBe(`  ${email.toUpperCase()} `);
    // Exactly the households the preview listed for deletion (the admin may own other solo households locally).
    const shown = (await (await page.request.get("/api/account/deletion-preview")).json()) as { householdsToDelete: { id: string; name: string }[] };
    expect(shown.householdsToDelete.map((h) => h.name)).toContain(name);
    expect([...(last.householdIds as string[])].sort()).toEqual(shown.householdsToDelete.map((h) => h.id).sort());
    expect(await page.evaluate(() => Object.keys(window.localStorage).filter((k) => k.startsWith("foodplanner.activeHousehold.")))).toEqual([]);
  } finally {
    await deleteHouseholdIfPresent(page.request, name);
  }
});
