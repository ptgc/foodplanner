import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-123: the super admin sets a household's plan ("Abonnement" tab) in a write session. The test works in its OWN household (named
// with the run tag, deleted in `finally`, like support.spec.ts); one support session per admin, so one project only.

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("the admin sets photo imports to unlimited in a write session and the access log shows plan.set", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile-390", "one support session per admin: this spec runs in one project");
  const request = page.request;
  const name = `E2E plan ${uniqueStamp(testInfo)}`;
  let household: string | undefined;

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name } });
    expect(created.status(), await created.text()).toBe(201);
    household = ((await created.json()) as { id: string }).id;

    // A write session needs the admin's own password.
    await page.goto("/support");
    await page.getByLabel("Search by name or household id").fill(name);
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await page.getByRole("button", { name: `Choose ${name}` }).click();
    await page.getByRole("radio", { name: /^Write/ }).check();
    await page.getByLabel("Your password").fill(process.env.E2E_ADMIN_PASSWORD!);
    await page.getByLabel("Note: what is wrong (10 to 500 characters)").fill("End-to-end test of the household plan");
    await page.getByRole("button", { name: "Start session" }).click();
    await expect(page.getByTestId("support-banner")).toContainText("Write");

    // The panel shows the default plan; a default plan has nothing to reset.
    await page.getByRole("button", { name: "Subscription" }).click();
    await expect(page.getByTestId("support-subscription-source")).toHaveText("Default");
    await expect(page.getByTestId("support-subscription-photoImportsPerDay")).toHaveText("2");
    await expect(page.getByRole("button", { name: "Reset to default" })).toHaveCount(0);
    await noHorizontalScroll(page);

    // Every control of the form is at least 44px high.
    const form = page.getByRole("form", { name: "Edit plan" });
    for (const control of await form.locator("input[type=text], input[type=number], button").all()) {
      expect((await control.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
    }

    // Photo imports unlimited, with a label; Save asks for a confirmation that names the household and the effect.
    await form.getByLabel("Label (optional, at most 40 characters)").fill("Unlimited");
    await form.getByRole("checkbox", { name: "Unlimited: Photo imports per day" }).check();
    await expect(form.getByLabel("Limit: Photo imports per day")).toBeDisabled();
    await form.getByRole("button", { name: "Save plan" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(name);
    await expect(dialog).toContainText("photo imports Unlimited");
    await dialog.getByRole("button", { name: "Yes, do it" }).click();

    // The saved plan is shown, and the overview shows its label.
    await expect(page.getByTestId("support-subscription-source")).toHaveText("Custom");
    await expect(page.getByTestId("support-subscription-photoImportsPerDay")).toContainText("Unlimited");
    await expect(page.getByRole("button", { name: "Reset to default" })).toBeVisible();
    await page.getByRole("button", { name: "Overview" }).click();
    await expect(page.getByTestId("support-plan")).toHaveText("Unlimited");

    // The access log shows the change.
    await page.getByRole("button", { name: "Access log" }).click();
    await expect(page.getByTestId("support-log-row").filter({ hasText: "plan.set" }).first()).toBeVisible();

    await page.getByRole("button", { name: "End session" }).click();
    await expect(page.getByRole("heading", { name: "Start a support session" })).toBeVisible();
  } finally {
    await request.delete("/api/support/sessions/current", { headers: await csrf(request) });
    if (household) await request.delete(`/api/households/${household}`, { headers: await csrf(request) });
  }
});
