import type { APIRequestContext, Page } from "@playwright/test";
import { expect, signIn, test } from "./fixtures";

// Each test works in its own far-future week with its own manual items, so it does not depend on demo data
// and the two projects (mobile-390, desktop) can run side by side. Items are removed afterwards.
const baseMonday = Date.UTC(2031, 2, 3); // 2031-03-03 is a Monday

function weekFor(project: string, n: number): string {
  const offsetWeeks = n + (project === "desktop" ? 10 : 0);
  return new Date(baseMonday + offsetWeeks * 7 * 86_400_000).toISOString().slice(0, 10);
}

async function householdId(request: APIRequestContext): Promise<string> {
  const res = await request.get("/api/households");
  expect(res.ok()).toBe(true);
  const households = (await res.json()) as { id: string; name: string }[];
  expect(households.length).toBeGreaterThan(0);
  return households[0]!.id;
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  const { token } = (await res.json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

async function addItem(request: APIRequestContext, h: string, week: string, label: string): Promise<string> {
  const res = await request.post(`/api/households/${h}/shopping-lists/${week}/items`, {
    headers: await csrf(request),
    data: { label, quantity: null, unitId: null },
  });
  expect(res.status()).toBe(201);
  return ((await res.json()) as { id: string }).id;
}

async function removeItems(request: APIRequestContext, h: string, week: string) {
  const res = await request.get(`/api/households/${h}/shopping-lists/${week}`);
  const list = (await res.json()) as { main: { id: string; isManual: boolean }[] };
  for (const item of list.main.filter((i) => i.isManual)) {
    await request.delete(`/api/households/${h}/shopping-lists/${week}/items/${item.id}`, { headers: await csrf(request) });
  }
}

async function isChecked(request: APIRequestContext, h: string, week: string, id: string): Promise<boolean> {
  const res = await request.get(`/api/households/${h}/shopping-lists/${week}`);
  const list = (await res.json()) as { main: { id: string; checked: boolean }[] };
  return list.main.find((i) => i.id === id)?.checked ?? false;
}

const box = (page: Page, label: string) => page.getByRole("checkbox", { name: label });

test("shopping list shows the week, large rows and the add form", async ({ adminPage: page }, testInfo) => {
  const week = weekFor(testInfo.project.name, 0);
  const h = await householdId(page.request);
  const label = `E2E melk ${Date.now()}`;
  try {
    await addItem(page.request, h, week, label);
    await page.goto(`/shopping?week=${week}`);
    await expect(page.getByRole("heading", { name: "Shopping list", level: 1 })).toBeVisible();
    await expect(page.getByTestId("shop-week")).toContainText(/Week \d+, 2031/);
    await expect(box(page, label)).toBeVisible();
    const row = page.getByTestId("shop-item").filter({ hasText: label });
    expect((await row.boundingBox())!.height).toBeGreaterThanOrEqual(56);
    await expect(page.getByRole("button", { name: "Generate from meal plan" })).toBeEnabled();
    if ((page.viewportSize()?.width ?? 0) <= 390) {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
    }

    // Add and remove through the UI.
    const added = `E2E ost ${Date.now()}`;
    await page.getByLabel("Item", { exact: true }).fill(added);
    await page.getByRole("button", { name: "Add", exact: true }).click();
    await expect(box(page, added)).toBeVisible();
    await page.getByRole("button", { name: `Remove ${added}` }).click();
    await expect(box(page, added)).toHaveCount(0);
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("Live update visible across two sessions", async ({ adminPage: pageA, browser }, testInfo) => {
  const week = weekFor(testInfo.project.name, 1);
  const h = await householdId(pageA.request);
  const label = `E2E live ${Date.now()}`;
  const contextB = await browser.newContext({ viewport: pageA.viewportSize() ?? undefined });
  try {
    await addItem(pageA.request, h, week, label);
    const pageB = await contextB.newPage();
    await signIn(pageB);

    await pageA.goto(`/shopping?week=${week}`);
    await pageB.goto(`/shopping?week=${week}`);
    await expect(box(pageA, label)).not.toBeChecked();
    await expect(box(pageB, label)).not.toBeChecked();

    await box(pageA, label).check();
    await expect(box(pageB, label)).toBeChecked({ timeout: 5000 });

    await box(pageB, label).uncheck();
    await expect(box(pageA, label)).not.toBeChecked({ timeout: 5000 });
  } finally {
    await contextB.close();
    await removeItems(pageA.request, h, week);
  }
});

test("Works offline for read and check-off with sync on reconnect", async ({ adminPage: page }, testInfo) => {
  const week = weekFor(testInfo.project.name, 2);
  const h = await householdId(page.request);
  const stamp = Date.now();
  const first = `E2E offline a ${stamp}`;
  const second = `E2E offline b ${stamp}`;
  try {
    const firstId = await addItem(page.request, h, week, first);
    const secondId = await addItem(page.request, h, week, second);
    await page.goto(`/shopping?week=${week}`);
    await expect(box(page, first)).toBeVisible();

    // 1. Browser goes offline: a check shows as pending, and syncs when the connection returns.
    await page.context().setOffline(true);
    await box(page, first).check();
    await expect(page.getByTestId("pending-sync")).toContainText("1 change waiting to sync");
    await expect(page.getByTestId("offline-banner")).toBeVisible();
    await expect(box(page, first)).toBeChecked();
    expect(await isChecked(page.request, h, week, firstId)).toBe(false);
    await page.context().setOffline(false);
    await expect(page.getByTestId("pending-sync")).toHaveCount(0, { timeout: 15_000 });
    expect(await isChecked(page.request, h, week, firstId)).toBe(true);

    // 2. API unreachable (page still served): check, reload, the cached list and the pending check survive.
    await page.route("**/api/**", (route) => route.abort());
    await box(page, second).check();
    await expect(page.getByTestId("pending-sync")).toBeVisible();
    await page.reload();
    await expect(box(page, second)).toBeChecked();
    await expect(page.getByTestId("pending-sync")).toContainText("1 change waiting to sync");
    await expect(page.getByTestId("offline-banner")).toBeVisible();
    expect(await isChecked(page.request, h, week, secondId)).toBe(false);

    await page.unroute("**/api/**");
    await expect(page.getByTestId("pending-sync")).toHaveCount(0, { timeout: 15_000 });
    await expect(page.getByTestId("offline-banner")).toHaveCount(0);
    expect(await isChecked(page.request, h, week, secondId)).toBe(true);
  } finally {
    await page.unroute("**/api/**").catch(() => undefined);
    await page.context().setOffline(false);
    await removeItems(page.request, h, week);
  }
});
