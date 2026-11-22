import type { APIRequestContext, Page, Route, TestInfo } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });

// T-054: "Add to Oda cart". The real oda.com is never called, and neither does the local API need an Oda client:
// the API boundary of the two Oda endpoints is faked with page.route. "Oda" is played by the same origin: the fake
// `start` answers with an authorize address that is the app's own /oda/callback with a code (or an error), which is
// exactly where the real Oda would send the browser back. Everything else (the list, the text export) is the real API.
const baseMonday = Date.UTC(2032, 2, 1); // 2032-03-01 is a Monday

// One far-apart block of weeks per test (uniqueWeek only has 4 slots per repeat), so the 5 tests never share a week.
const weekFor = (testInfo: TestInfo, n: number) => uniqueWeek(testInfo, baseMonday + n * 200 * 7 * 86_400_000, 0);

async function householdId(request: APIRequestContext): Promise<string> {
  const res = await request.get("/api/households");
  expect(res.ok()).toBe(true);
  return ((await res.json()) as { id: string }[])[0]!.id;
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function addItem(request: APIRequestContext, h: string, week: string, label: string): Promise<void> {
  const res = await request.post(`/api/households/${h}/shopping-lists/${week}/items`, {
    headers: await csrf(request),
    data: { label, quantity: null, unitId: null },
  });
  expect(res.status()).toBe(201);
}

async function removeItems(request: APIRequestContext, h: string, week: string) {
  const res = await request.get(`/api/households/${h}/shopping-lists/${week}`);
  const list = (await res.json()) as { main: { id: string; isManual: boolean }[] };
  for (const item of list.main.filter((i) => i.isManual)) {
    await request.delete(`/api/households/${h}/shopping-lists/${week}/items/${item.id}`, { headers: await csrf(request) });
  }
}

type Fake = { starts: unknown[]; carts: unknown[] };

/** Fakes the two Oda endpoints. `redirectQuery` is what "Oda" appends to the callback; `cart` answers the one oda/cart call. */
async function fakeOda(page: Page, redirectQuery: string, cart: (route: Route) => Promise<void>): Promise<Fake> {
  const fake: Fake = { starts: [], carts: [] };
  await page.route(/\/api\/households\/[^/]+\/shopping-lists\/[^/]+\/oda\/start$/, async (route) => {
    fake.starts.push(route.request().postDataJSON());
    await route.fulfill({ json: { authorizeUrl: `${new URL(page.url()).origin}/oda/callback?${redirectQuery}` } });
  });
  await page.route(/\/api\/households\/[^/]+\/shopping-lists\/[^/]+\/oda\/cart$/, async (route) => {
    fake.carts.push(route.request().postDataJSON());
    await cart(route);
  });
  return fake;
}

// T-052: every added line of the real response carries the choice fields; the tests give only what they care about.
const addedDefaults = { source: "first-hit", ingredientId: null, productId: 1, hasPreference: false, preferenceProblem: null, alternatives: [] };

const cartBody = (over: Record<string, unknown>) => ({
  status: "done",
  cartUrl: "https://oda.example/cart",
  notAdded: [],
  cartMessages: [],
  connectionClosed: true,
  connectionMessage: null,
  usuallyBought: { status: "skipped", count: 0 },
  ...over,
  added: ((over.added as Record<string, unknown>[] | undefined) ?? []).map((line) => ({ ...addedDefaults, ...line })),
});

async function sendToOda(page: Page, week: string, labels: string[]) {
  await page.goto(`/shopping?week=${week}`);
  const button = page.getByRole("button", { name: "Add to Oda cart" });
  await expect(button).toBeVisible();
  expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
  await button.click();
  const dialog = page.getByRole("dialog", { name: "Add to your Oda cart?" });
  await expect(dialog).toBeVisible();
  for (const label of labels) await expect(dialog.getByTestId("oda-items")).toContainText(label);
  await expect(dialog).toContainText("Nothing is ordered or paid");
  await expect(dialog).toContainText("Sending twice adds everything twice");
  for (const b of await dialog.getByRole("button").all()) expect((await b.boundingBox())!.height).toBeGreaterThanOrEqual(44);
  await dialog.getByRole("button", { name: "Continue to Oda" }).click();
}

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
}

/** Nothing about Oda (code, state, product names, messages, cart link) may be in browser storage. */
async function storageHoldsNoOdaData(page: Page, ...forbidden: string[]) {
  const stored = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
  for (const text of forbidden) expect(stored).not.toContain(text);
}

test("success: confirmation, one oda/cart call, code gone from the address bar, result with the cart link", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 0);
  const h = await householdId(page.request);
  const label = `E2E melk ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    const fake = await fakeOda(page, "code=fake-code-1&state=fake-state-1", (route) =>
      route.fulfill({
        json: cartBody({ added: [{ itemId: "00000000-0000-0000-0000-000000000001", name: label, productName: "Tine Lettmelk 1 l", quantity: 1 }] }),
      }),
    );
    await sendToOda(page, week, [label]);

    const result = page.getByTestId("oda-result");
    await expect(result).toBeVisible();
    await expect(page).toHaveURL(/\/oda\/callback$/); // code and state removed from the address bar
    expect(fake.starts).toEqual([{ confirmed: true }]);
    expect(fake.carts).toEqual([{ state: "fake-state-1", code: "fake-code-1", error: null }]);
    await expect(result).toContainText("All items are in your Oda cart.");
    await expect(page.getByTestId("oda-added")).toContainText("Tine Lettmelk 1 l");
    const cartLink = page.getByRole("link", { name: "Open the Oda cart" });
    await expect(cartLink).toHaveAttribute("href", "https://oda.example/cart");
    expect((await cartLink.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await expect(page.getByTestId("oda-not-closed")).toHaveCount(0);
    await storageHoldsNoOdaData(page, "fake-code-1", "fake-state-1", "Tine Lettmelk", "oda.example");
    if (testInfo.project.name.startsWith("mobile")) await noHorizontalScroll(page);

    // A reload must not send anything again (quantities are deltas): the code is already gone.
    await page.reload();
    await expect(page.getByTestId("oda-nothing")).toBeVisible();
    expect(fake.carts).toHaveLength(1);
    await page.getByRole("link", { name: "Back to the shopping list" }).click();
    await expect(page.getByRole("heading", { name: "Shopping list", level: 1 })).toBeVisible();
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("partial: shows what was not added with reasons, Oda's messages and the connection-not-closed note", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 1);
  const h = await householdId(page.request);
  const label = `E2E kaffe ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    await fakeOda(page, "code=fake-code-2&state=fake-state-2", (route) =>
      route.fulfill({
        json: cartBody({
          status: "partial",
          added: [{ itemId: "00000000-0000-0000-0000-000000000002", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1 }],
          notAdded: [
            { itemId: "00000000-0000-0000-0000-000000000003", name: "Kaffe", reason: "no-match" },
            { itemId: "00000000-0000-0000-0000-000000000004", name: "Egg", reason: "unavailable" },
          ],
          cartMessages: ['Egg is sold out <b>today</b>'],
          connectionClosed: false,
          connectionMessage: "Remove Foodplanner under Privacy & security",
        }),
      }),
    );
    await sendToOda(page, week, [label]);

    const result = page.getByTestId("oda-result");
    await expect(result).toContainText("Some items are in your Oda cart.");
    await expect(page.getByTestId("oda-not-added")).toContainText("Kaffe: no matching product");
    await expect(page.getByTestId("oda-not-added")).toContainText("Egg: not available");
    // Oda's text is shown as text, not as markup.
    await expect(page.getByTestId("oda-messages")).toContainText("Egg is sold out <b>today</b>");
    await expect(page.getByTestId("oda-messages").locator("b")).toHaveCount(0);
    await expect(page.getByTestId("oda-not-closed")).toContainText("Privacy & security in your Oda account");
    await expect(page.getByRole("link", { name: "Open the Oda cart" })).toBeVisible();
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("denied at Oda: the denial is passed on once and nothing was added", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 2);
  const h = await householdId(page.request);
  const label = `E2E ost ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    const fake = await fakeOda(page, "error=access_denied&state=fake-state-3", (route) =>
      route.fulfill({ json: cartBody({ status: "cancelled", cartUrl: null }) }),
    );
    await sendToOda(page, week, [label]);

    await expect(page.getByTestId("oda-result")).toContainText("You stopped at Oda. Nothing was added.");
    expect(fake.carts).toEqual([{ state: "fake-state-3", code: null, error: "access_denied" }]);
    await expect(page).toHaveURL(/\/oda\/callback$/);
    await expect(page.getByRole("link", { name: "Open the Oda cart" })).toHaveCount(0);
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("failure: shows what was already added and offers the real text export right there", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 3);
  const h = await householdId(page.request);
  const label = `E2E smør ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    const fake = await fakeOda(page, "code=fake-code-4&state=fake-state-4", (route) =>
      route.fulfill({
        status: 502,
        contentType: "application/problem+json",
        json: {
          type: "urn:foodplanner:problem:oda-unavailable",
          title: "Oda is not available",
          status: 502,
          added: [{ itemId: "00000000-0000-0000-0000-000000000005", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1 }],
          connectionClosed: false,
          fallback: { export: "text" },
        },
      }),
    );
    await sendToOda(page, week, [label]);

    const failed = page.getByTestId("oda-failed");
    await expect(failed).toContainText("Oda is not available right now.");
    await expect(page.getByTestId("oda-added")).toContainText("Tine Lettmelk 1 l");
    await expect(page.getByTestId("oda-not-closed")).toBeVisible();
    expect(fake.carts).toHaveLength(1); // never retried

    // The text export (T-050/T-051) is the real API and works from the failure screen.
    const fallback = page.getByTestId("oda-text-fallback");
    for (const name of ["Share", "Copy"]) {
      const b = fallback.getByRole("button", { name });
      await expect(b).toBeVisible();
      expect((await b.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    }
    const exported = page.waitForResponse((r) => r.url().includes(`/shopping-lists/${week}/export`) && r.ok());
    await fallback.getByRole("button", { name: "Copy" }).click();
    await exported;
    await storageHoldsNoOdaData(page, "fake-code-4", "fake-state-4", "Tine Lettmelk");
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("expired sign-in (400 oda-state-invalid) says try again", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 4);
  const h = await householdId(page.request);
  const label = `E2E egg ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    await fakeOda(page, "code=fake-code-5&state=fake-state-5", (route) =>
      route.fulfill({ status: 400, contentType: "application/problem+json", json: { type: "urn:foodplanner:problem:oda-state-invalid", title: "x", status: 400 } }),
    );
    await sendToOda(page, week, [label]);
    await expect(page.getByTestId("oda-failed")).toContainText("sign-in at Oda expired");
    await expect(page.getByTestId("oda-text-fallback")).toHaveCount(0);
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("usually bought (T-114): the line chosen from the history has the label, the others do not, and nothing is stored", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 5);
  const h = await householdId(page.request);
  const label = `E2E tomat ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    await fakeOda(page, "code=fake-code-6&state=fake-state-6", (route) =>
      route.fulfill({
        json: cartBody({
          added: [
            {
              itemId: "00000000-0000-0000-0000-000000000006",
              name: "Tomat",
              productName: "Cherrytomater Favoritt med en ganske lang produktbeskrivelse 250 g",
              quantity: 1,
              source: "usually-bought",
            },
            { itemId: "00000000-0000-0000-0000-000000000007", name: "Melk", productName: "Tine Lettmelk 1 l", quantity: 1, source: "first-hit" },
          ],
          usuallyBought: { status: "used", count: 1 },
        }),
      }),
    );
    await sendToOda(page, week, [label]);

    const added = page.getByTestId("oda-added");
    await expect(added).toBeVisible();
    const usual = added.getByTestId("oda-usual");
    await expect(usual).toHaveCount(1);
    await expect(usual).toHaveText("Usually bought");
    await expect(added.locator("li", { hasText: "Tomat" }).getByTestId("oda-usual")).toBeVisible();
    await expect(added.locator("li", { hasText: "Melk" }).getByTestId("oda-usual")).toHaveCount(0);
    await expect(page.getByTestId("oda-usual-unavailable")).toHaveCount(0);
    await expect(page.getByTestId("oda-not-closed")).toHaveCount(0);
    await storageHoldsNoOdaData(page, "Cherrytomater", "usually-bought", "fake-code-6");
    if (testInfo.project.name.startsWith("mobile")) {
      await noHorizontalScroll(page);
      const box = (await usual.boundingBox())!;
      expect(box.x + box.width).toBeLessThanOrEqual(page.viewportSize()!.width);
    }
  } finally {
    await removeItems(page.request, h, week);
  }
});

test("usually bought (T-114): when the history could not be read, one plain line says so", async ({ page }, testInfo) => {
  const week = weekFor(testInfo, 6);
  const h = await householdId(page.request);
  const label = `E2E smør ${uniqueStamp(testInfo)}`;
  try {
    await addItem(page.request, h, week, label);
    await fakeOda(page, "code=fake-code-7&state=fake-state-7", (route) =>
      route.fulfill({
        json: cartBody({
          added: [{ itemId: "00000000-0000-0000-0000-000000000008", name: "Smør", productName: "Meierismør 250 g", quantity: 1, source: "first-hit" }],
          usuallyBought: { status: "unavailable", count: 0 },
        }),
      }),
    );
    await sendToOda(page, week, [label]);

    await expect(page.getByTestId("oda-usual-unavailable")).toContainText("could not be read");
    await expect(page.getByTestId("oda-usual")).toHaveCount(0);
  } finally {
    await removeItems(page.request, h, week);
  }
});
