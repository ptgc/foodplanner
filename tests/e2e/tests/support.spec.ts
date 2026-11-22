import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, memberCredentials, signIn, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-096: the system admin support pages. The seeded admin is on Support:SystemAdminEmails (local-secrets copies Seed:AdminEmail
// there), so /support is theirs; the seeded member is not. The test works in its OWN household (named with the run tag), which
// is deleted in `finally` together with the support session: a support session is per ADMIN, so the spec runs in one project only
// (a second project starting a session would replace this one's), and the run's teardown (T-083) deletes the household too if
// that fails.

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("the admin starts a read session, sees the members and ends the session; a member sees nothing", async ({ page, browser }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile-390", "one support session per admin: this spec runs in one project");
  const request = page.request;
  const stamp = uniqueStamp(testInfo);
  const name = `E2E support ${stamp}`;
  let household: string | undefined;

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name } });
    expect(created.status(), await created.text()).toBe(201);
    household = ((await created.json()) as { id: string }).id;

    await page.goto("/support");
    await expect(page.getByRole("heading", { name: "Support", level: 1 })).toBeVisible();
    await noHorizontalScroll(page);

    // Find the household in the list and choose it.
    await page.getByLabel("Search by name or household id").fill(name);
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await page.getByRole("button", { name: `Choose ${name}` }).click();
    await expect(page.getByTestId("support-chosen")).toContainText(name);

    // The reason is required: the button is there, the note is what makes the API accept it.
    await page.getByLabel("Note: what is wrong (10 to 500 characters)").fill("End-to-end test of the support pages");
    const start = page.getByRole("button", { name: "Start session" });
    expect((await start.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
    await start.click();

    // The banner always names the household, the mode and the reason, and offers the end button.
    const banner = page.getByTestId("support-banner");
    await expect(banner).toContainText(name);
    await expect(banner).toContainText("Read");
    const end = page.getByRole("button", { name: "End session" });
    expect((await end.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
    await noHorizontalScroll(page);

    // The overview and the members of that ONE household; a read session has no write button.
    await page.getByRole("button", { name: "Overview" }).click();
    await expect(page.getByTestId("support-plan")).toHaveText("No plan set");
    await page.getByRole("button", { name: "Members" }).click();
    await expect(page.getByTestId("support-member-row")).toHaveCount(1);
    await expect(page.getByTestId("support-member-row")).toContainText(process.env.E2E_ADMIN_EMAIL!);
    await expect(page.getByRole("button", { name: "Make Owner" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Clear PIN lock" })).toHaveCount(0);

    // The access log shows what was just done.
    await page.getByRole("button", { name: "Access log" }).click();
    await expect(page.getByTestId("support-log-row").first()).toBeVisible();

    // A reload keeps the session; ending it brings the start form back.
    await page.reload();
    await expect(page.getByTestId("support-banner")).toContainText(name);
    await page.getByRole("button", { name: "End session" }).click();
    await expect(page.getByRole("heading", { name: "Start a support session" })).toBeVisible();

    // The household sees that support looked: the admin is an Owner of the new household, so the "Support access" API answers.
    const access = await request.get(`/api/households/${household}/support-access`);
    expect(access.status()).toBe(200);
    const rows = ((await access.json()) as { items: { action: string; mode: string }[] }).items;
    expect(rows.map((r) => r.action)).toContain("session.start");
    expect(rows.map((r) => r.action)).toContain("view.members");

    // A signed-in account that is not a system admin sees a plain "not found", and the API refuses the support routes.
    const context = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: { width: 390, height: 844 } });
    try {
      const memberPage = await context.newPage();
      await signIn(memberPage, "/support", memberCredentials());
      await expect(memberPage.getByRole("heading", { name: "Page not found" })).toBeVisible();
      const refused = await memberPage.request.get("/api/support/households");
      expect(refused.status()).toBe(403);
    } finally {
      await context.close();
    }
  } finally {
    await request.delete("/api/support/sessions/current", { headers: await csrf(request) });
    if (household) await request.delete(`/api/households/${household}`, { headers: await csrf(request) });
  }
});
