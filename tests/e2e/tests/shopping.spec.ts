import type { APIRequestContext, Page, TestInfo } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });

// Each test works in its own far-future week with its own manual items, so it does not depend on demo data
// and projects and `--repeat-each` copies can run side by side (uniqueWeek). Items are removed afterwards.
const baseMonday = Date.UTC(2031, 2, 3); // 2031-03-03 is a Monday

function weekFor(testInfo: TestInfo, n: number): string {
  return uniqueWeek(testInfo, baseMonday, n);
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

/**
 * Resolves when the page's SignalR socket has answered two invocations (JoinHousehold, then GetState: JSON protocol
 * completion messages, `"type":3`). Call it before `goto`, await it after.
 */
function hubReady(page: Page): Promise<void> {
  return new Promise((resolve) => {
    let completions = 0;
    page.on("websocket", (ws) => {
      if (!ws.url().includes("/api/hubs/shopping-list")) return;
      ws.on("framereceived", ({ payload }) => {
        if (String(payload).includes('"type":3') && ++completions === 2) resolve();
      });
    });
  });
}

/** ISO year of a Monday (the year of its Thursday). */
const isoYear = (monday: string) => new Date(Date.parse(monday) + 3 * 86_400_000).getUTCFullYear();

const box = (page: Page, label: string) => page.getByRole("checkbox", { name: label });

test("shopping list shows the week, large rows and the add form", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 0);
  const h = await householdId(page.request);
  const label = `E2E melk ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    await page.goto(`/shopping?week=${week}`);
    await expect(page.getByRole("heading", { name: "Shopping list", level: 1 })).toBeVisible();
    await expect(page.getByTestId("shop-week")).toContainText(new RegExp(`Week \\d+, ${isoYear(week)}`));
    await expect(box(page, label)).toBeVisible();
    const row = page.getByTestId("shop-item").filter({ hasText: label });
    expect((await row.boundingBox())!.height).toBeGreaterThanOrEqual(56);
    await expect(page.getByRole("button", { name: "Generate from meal plan" })).toBeEnabled();
    if ((page.viewportSize()?.width ?? 0) <= 390) {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
    }

    // Add and remove through the UI.
    const added = `E2E ost ${uniqueStamp(testInfo)}`;
    await page.getByLabel("Item", { exact: true }).fill(added);
    await page.getByRole("button", { name: "Add", exact: true }).click();
    await expect(box(page, added)).toBeVisible();
    await page.getByRole("button", { name: `Remove ${added}` }).click();
    await expect(box(page, added)).toHaveCount(0);
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("Live update visible across two sessions", async ({ page: pageA, browser }, testInfo) => {
  const week = weekFor(testInfo, 1);
  const h = await householdId(pageA.request);
  const label = `E2E live ${uniqueStamp(testInfo)}`;
  const contextB = await browser.newContext({ storageState: adminState, viewport: pageA.viewportSize() ?? undefined });
  try {
    await addItem(pageA.request, h, week, label);
    const pageB = await contextB.newPage();

    // Act only once both sessions are live: the item is on screen (REST) and the hub joined the household and
    // read its state (the SignalR connect is asynchronous, a change made before it is only seen after a reload).
    const liveA = hubReady(pageA);
    const liveB = hubReady(pageB);
    await pageA.goto(`/shopping?week=${week}`);
    await pageB.goto(`/shopping?week=${week}`);
    await expect(box(pageA, label)).not.toBeChecked();
    await expect(box(pageB, label)).not.toBeChecked();
    await Promise.all([liveA, liveB]);

    await box(pageA, label).check();
    // Server push (150 ms debounce) then a GetState round trip; generous only because this machine runs many jobs.
    await expect(box(pageB, label)).toBeChecked({ timeout: 15_000 });

    await box(pageB, label).uncheck();
    await expect(box(pageA, label)).not.toBeChecked({ timeout: 15_000 });
  } finally {
    await contextB.close();
    await removeItems(pageA.request, h, week);
  }
});

test("Works offline for read and check-off with sync on reconnect", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 2);
  const h = await householdId(page.request);
  const stamp = uniqueStamp(testInfo);
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

test("Copy puts the unchecked items on the clipboard and leaves out checked ones", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 3);
  const h = await householdId(page.request);
  const stamp = uniqueStamp(testInfo);
  const wanted = `E2E kopi ${stamp}`;
  const done = `E2E ferdig ${stamp}`;
  try {
    await addItem(page.request, h, week, wanted);
    const doneId = await addItem(page.request, h, week, done);
    const res = await page.request.put(`/api/households/${h}/shopping-lists/${week}/items/${doneId}/check`, {
      headers: await csrf(page.request),
      data: { checked: true },
    });
    expect(res.ok()).toBe(true);

    await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
    await page.goto(`/shopping?week=${week}`);
    const copy = page.getByRole("button", { name: "Copy", exact: true });
    const share = page.getByRole("button", { name: "Share", exact: true });
    await expect(box(page, wanted)).toBeVisible();
    for (const b of [copy, share]) expect((await b.boundingBox())!.height).toBeGreaterThanOrEqual(44);

    await copy.click();
    await expect(page.getByTestId("copy-toast")).toHaveText("Copied to the clipboard");
    const text = await page.evaluate(() => navigator.clipboard.readText());
    expect(text).toContain(`- ${wanted}`);
    expect(text).not.toContain(done);
    if ((page.viewportSize()?.width ?? 0) <= 390) {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
    }
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("Share hands the title and text to navigator.share", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 4);
  const h = await householdId(page.request);
  const label = `E2E del ${uniqueStamp(testInfo)}`;
  type Shared = { title: string; text: string };
  try {
    await addItem(page.request, h, week, label);
    await page.addInitScript(() => {
      const shared: { title: string; text: string }[] = [];
      (window as unknown as { __shared: typeof shared }).__shared = shared;
      navigator.canShare = () => true;
      navigator.share = async (data) => {
        shared.push({ title: String(data?.title), text: String(data?.text) });
      };
    });
    await page.goto(`/shopping?week=${week}`);
    await expect(box(page, label)).toBeVisible();
    await page.getByRole("button", { name: "Share", exact: true }).click();
    await expect.poll(() => page.evaluate(() => (window as unknown as { __shared: Shared[] }).__shared.length)).toBe(1);
    const [call] = await page.evaluate(() => (window as unknown as { __shared: Shared[] }).__shared);
    expect(call!.title).toMatch(/^Shopping list, week \d+$/);
    expect(call!.text).toContain(`- ${label}`);
  } finally {
    await removeItems(page.request, h, week);
  }
});
