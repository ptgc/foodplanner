import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, expectActiveHousehold, memberCredentials, signIn, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  const { token } = (await res.json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

/** Deletes the test household through the API if it still exists (the test deletes it itself when it passes). */
async function deleteHouseholdIfPresent(request: APIRequestContext, name: string) {
  const list = await request.get("/api/households");
  if (!list.ok()) return;
  const found = ((await list.json()) as { id: string; name: string }[]).find((h) => h.name === name);
  if (found) await request.delete(`/api/households/${found.id}`, { headers: await csrf(request) });
}

/** 390px rule: every control in the main area and the nav is at least 44px high; the page does not scroll sideways. */
async function expectTouchFriendly(page: Page) {
  const controls = page.locator("main button:visible, main select:visible, main input:visible, nav a:visible");
  const heights = await controls.evaluateAll((els) => els.map((el) => ({ name: el.textContent?.trim() || el.getAttribute("aria-label") || el.tagName, height: el.getBoundingClientRect().height })));
  for (const { name, height } of heights) expect(height, `height of "${name}"`).toBeGreaterThanOrEqual(43.5);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

test("invite flow: create a household, invite a member who joins, change their role, they leave, delete the household", async ({ page, browser }, testInfo) => {
  test.setTimeout(120_000);
  // The list is ordered by name and other specs use the first household, so this one must sort after "Home".
  const name = `Zz E2E ${uniqueStamp(testInfo)}`;
  const member = memberCredentials();
  const memberContext = await browser.newContext({
    storageState: { cookies: [], origins: [] },
    viewport: page.viewportSize() ?? undefined,
  });
  try {
    // --- Admin creates a household and becomes its Owner ---
    await page.goto("/household");
    await expect(page.getByRole("heading", { name: "Household", level: 1 })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Household" })).toBeVisible();
    await page.getByLabel("Name of the new household").fill(name);
    await page.getByRole("button", { name: "Create household" }).click();
    await expect(page.getByTestId("household-notice")).toHaveText(`${name} was created and is now active.`);
    await expectActiveHousehold(page, name);
    await expect(page.getByTestId("household-settings-name")).toHaveText(name);
    await expect(page.getByTestId("household-role")).toHaveText("Owner");

    // --- Admin creates an Adult invitation and sees it listed ---
    await page.getByLabel("Role for the new invitation").selectOption("Adult");
    await page.getByRole("button", { name: "Create invitation" }).click();
    const linkField = page.getByTestId("invite-link");
    await expect(linkField).toBeVisible();
    const link = await linkField.inputValue();
    expect(new URL(link).pathname).toMatch(/^\/invite\/[A-Za-z0-9_-]+$/);
    await expect(page.getByTestId("invite-row")).toHaveCount(1);
    await expect(page.getByTestId("invite-row")).toContainText("Adult");
    await expectTouchFriendly(page);

    // --- The member opens the link signed out, signs in, and joins ---
    const memberPage = await memberContext.newPage();
    await signIn(memberPage, new URL(link).pathname, member);
    await expect(memberPage).toHaveURL(/\/invite\//);
    await expect(memberPage.getByRole("heading", { name: "Join a household" })).toBeVisible();
    await memberPage.getByRole("button", { name: "Join household" }).click();
    await expect(memberPage.getByText("You joined the household as Adult.")).toBeVisible();
    await expectActiveHousehold(memberPage, name);
    await memberPage.getByRole("link", { name: "Open the household" }).click();
    await expect(memberPage.getByTestId("household-role")).toHaveText("Adult");
    // An Adult sees no member list, invitations or delete, but can export and leave.
    await expect(memberPage.getByText("Only owners see the members and invitations of the household.")).toBeVisible();
    await expect(memberPage.getByTestId("member-list")).toHaveCount(0);
    await expect(memberPage.getByRole("button", { name: "Delete household" })).toHaveCount(0);
    await expect(memberPage.getByRole("button", { name: "Download export" })).toBeVisible();

    // The link is single use: the accepted invitation is not listed as open any more.
    await page.reload();
    await expect(page.getByTestId("household-settings-name")).toHaveText(name);
    await expect(page.getByTestId("invite-row")).toHaveCount(0);
    await expect(page.getByText("There are no open invitations.")).toBeVisible();

    // --- Admin sees the member and changes the role to Child ---
    const row = page.getByTestId("member-row").filter({ hasText: member.email });
    await expect(row).toHaveCount(1);
    await row.getByRole("combobox").selectOption("Child");
    await expect(page.getByTestId("household-notice")).toContainText(`${member.email} is now Child.`);
    await expectTouchFriendly(page);

    await memberPage.reload();
    await expect(memberPage.getByTestId("household-role")).toHaveText("Child");
    await expect(memberPage.getByRole("button", { name: "Download export" })).toHaveCount(0);

    // --- The member leaves after confirming ---
    await memberPage.getByRole("button", { name: "Leave household" }).click();
    const leaveDialog = memberPage.getByRole("dialog");
    await expect(leaveDialog).toBeVisible();
    await leaveDialog.getByRole("button", { name: "Cancel" }).click();
    await expect(leaveDialog).toHaveCount(0);
    await expect(memberPage.getByTestId("household-role")).toHaveText("Child"); // cancel left nothing
    await memberPage.getByRole("button", { name: "Leave household" }).click();
    await memberPage.getByRole("dialog").getByRole("button", { name: "Leave", exact: true }).click();
    await expect(memberPage.getByTestId("household-notice")).toHaveText(`You left ${name}.`);
    await expect(memberPage.getByText("You are not in a household")).toBeVisible();

    // --- The member is gone from the admin's list ---
    await page.reload();
    await expect(page.getByTestId("member-row").filter({ hasText: member.email })).toHaveCount(0);

    // --- Admin deletes the household, confirming by typing its name ---
    await page.getByRole("button", { name: "Delete household" }).click();
    const deleteDialog = page.getByRole("dialog");
    const confirm = deleteDialog.getByRole("button", { name: "Delete forever" });
    await expect(confirm).toBeDisabled();
    await deleteDialog.getByLabel(/Type the name of the household/).fill(name);
    await expect(confirm).toBeEnabled();
    await confirm.click();
    await expect(page.getByTestId("household-notice")).toHaveText(`${name} was deleted.`);
    await expectActiveHousehold(page, "Home");
    const households = await (await page.request.get("/api/households")).json();
    expect((households as { name: string }[]).map((h) => h.name)).not.toContain(name);
  } finally {
    await deleteHouseholdIfPresent(page.request, name);
    await memberContext.close();
  }
});
