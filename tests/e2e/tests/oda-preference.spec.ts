import type { APIRequestContext, Locator, Page } from "@playwright/test";
import { adminState, expect, memberCredentials, signIn, test, uniqueStamp, uniqueWeek } from "./fixtures";

// T-052: the household's chosen Oda product per ingredient. The real oda.com is never called. As in oda.spec.ts the Oda
// boundary of the API (start, cart) is faked with page.route; here the preference endpoints are faked too, with a small
// in-memory model that plays the contract of the spec (the picked product is used when it is available, a sold-out pick
// falls back to the first available hit and says so). The shopping list, the session and the roles are the real API.
const baseMonday = Date.UTC(2033, 2, 7); // 2033-03-07 is a Monday
const tomato = "33333333-3333-3333-3333-333333333333";
const hitIds = [4001, 4002, 4003, 4004, 4005];
const hitNames = [
  "Cherrytomater Favoritt 250 g",
  "Cherrytomater Rød i boks med en ganske lang produktbeskrivelse som må brytes på flere linjer 500 g",
  "Cherrytomater Økologisk 200 g",
  "Cherrytomater Mini 150 g",
  "Cherrytomater Rosa 250 g",
];

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function householdId(request: APIRequestContext): Promise<string> {
  const res = await request.get("/api/households");
  expect(res.ok()).toBe(true);
  return ((await res.json()) as { id: string }[])[0]!.id;
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

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
}

async function atLeast44(locator: Locator, what: string) {
  for (const element of await locator.all()) {
    const box = (await element.boundingBox())!;
    expect(box.height, `${what} height`).toBeGreaterThanOrEqual(43.5);
  }
}

/** The faked Oda (start, cart) and preference endpoints, with what the browser asked for. */
class FakeShop {
  choice: number | null = null;
  soldOut = new Set<number>();
  starts = 0;
  carts: { added: number; source: string }[] = [];
  puts: unknown[] = [];
  deletes = 0;
  lists = 0;

  async install(page: Page) {
    await page.route(/\/api\/households\/[^/]+\/shopping-lists\/[^/]+\/oda\/start$/, async (route) => {
      this.starts++;
      await route.fulfill({ json: { authorizeUrl: `${new URL(page.url()).origin}/oda/callback?code=fake-code-${this.starts}&state=fake-state-${this.starts}` } });
    });
    await page.route(/\/api\/households\/[^/]+\/shopping-lists\/[^/]+\/oda\/cart$/, async (route) => {
      const available = hitIds.filter((id) => !this.soldOut.has(id));
      let source = "first-hit";
      let problem: string | null = null;
      let added = available[0]!;
      if (this.choice !== null) {
        if (available.includes(this.choice)) {
          added = this.choice;
          source = "preferred";
        } else problem = "unavailable";
      }
      this.carts.push({ added, source });
      await route.fulfill({
        json: {
          status: "done",
          cartUrl: "https://oda.example/cart",
          added: [
            {
              itemId: "00000000-0000-0000-0000-000000000052",
              name: "Cherry tomatoes",
              productName: hitNames[hitIds.indexOf(added)],
              quantity: 1,
              source,
              ingredientId: tomato,
              productId: added,
              hasPreference: this.choice !== null,
              preferenceProblem: problem,
              alternatives: available.map((id) => ({
                productId: id,
                productName: hitNames[hitIds.indexOf(id)],
                isAdded: id === added,
                isPreferred: id === this.choice,
              })),
            },
          ],
          notAdded: [],
          cartMessages: [],
          connectionClosed: true,
          connectionMessage: null,
          usuallyBought: { status: "skipped", count: 0 },
        },
      });
    });
    await page.route(/\/api\/households\/[^/]+\/oda-preferences(\/[^/]+)?$/, async (route) => {
      const method = route.request().method();
      if (method === "GET") {
        this.lists++;
        await route.fulfill({
          json: this.choice === null ? [] : [{ ingredientId: tomato, ingredientName: "Cherry tomatoes", setAt: "2033-03-08T10:00:00Z" }],
        });
      } else if (method === "PUT") {
        const body = route.request().postDataJSON() as { odaProductId: number };
        this.puts.push(body);
        this.choice = body.odaProductId;
        await route.fulfill({ json: { ingredientId: tomato, ingredientName: "Cherry tomatoes", setAt: "2033-03-08T10:00:00Z" } });
      } else {
        this.deletes++;
        this.choice = null;
        await route.fulfill({ status: 204 });
      }
    });
  }
}

async function send(page: Page, week: string) {
  await page.goto(`/shopping?week=${week}`);
  await page.getByRole("button", { name: "Add to Oda cart" }).click();
  const dialog = page.getByRole("dialog", { name: "Add to your Oda cart?" });
  await dialog.getByRole("button", { name: "Continue to Oda" }).click();
  await expect(page.getByTestId("oda-result")).toBeVisible();
}

test.describe("an Adult chooses a product", () => {
  test.use({ storageState: adminState });

  test("picks, reuses, falls back, removes and clears the choice; the choices page makes no Oda call", async ({ page }, testInfo) => {
    const week = uniqueWeek(testInfo, baseMonday, 0);
    const h = await householdId(page.request);
    const label = `E2E tomat ${uniqueStamp(testInfo)}`;
    const mobile = testInfo.project.name.startsWith("mobile");
    const fake = new FakeShop();
    try {
      await addItem(page.request, h, week, label);
      await fake.install(page);

      // The choices link is on the shopping page for an Adult.
      await page.goto(`/shopping?week=${week}`);
      await expect(page.getByTestId("oda-choices-link")).toBeVisible();

      // (1) A fill with five hits: open the picker and choose the third.
      await send(page, week);
      const line = page.getByTestId("oda-added").locator("li").first();
      await expect(line).not.toContainText("Your Oda choice");
      const open = line.getByRole("button", { name: "Choose another product" });
      await atLeast44(open, "open button");
      await open.click();
      const rows = line.getByTestId("oda-alternative");
      await expect(rows).toHaveCount(5);
      await atLeast44(rows, "alternative row");
      await atLeast44(rows.getByRole("button"), "use button");
      await expect(line).toContainText("This does not change your Oda cart");
      if (mobile) await noHorizontalScroll(page);
      await rows.nth(2).getByRole("button", { name: /Use this next time/ }).click();
      await expect(rows.nth(2)).toContainText("Saved for next time");
      await expect(line.getByTestId("oda-choice")).toHaveText("Your Oda choice");
      expect(fake.puts).toEqual([{ odaProductId: 4003 }]);
      await atLeast44(line.getByRole("button", { name: /Remove choice/ }), "remove button");
      await expect(rows).toHaveCount(5); // the list stays open
      if (mobile) await noHorizontalScroll(page);

      // (2) The next fill adds the chosen product and labels it.
      await send(page, week);
      expect(fake.carts.at(-1)).toEqual({ added: 4003, source: "preferred" });
      await expect(page.getByTestId("oda-added").getByTestId("oda-choice")).toHaveText("Your Oda choice");

      // (3) The chosen product is sold out: the first available hit is added and the line says why.
      fake.soldOut.add(4003);
      await send(page, week);
      expect(fake.carts.at(-1)).toEqual({ added: 4001, source: "first-hit" });
      await expect(page.getByTestId("oda-choice-problem")).toHaveText("Your choice was sold out");
      expect(fake.choice).toBe(4003); // the fill never changes the choice

      // (4) Remove the choice on the line; the next fill uses the first hit and shows no choice label.
      const removeButton = page.getByTestId("oda-added").getByRole("button", { name: /Remove choice/ });
      await atLeast44(removeButton, "remove button");
      await removeButton.click();
      await expect(page.getByTestId("oda-removed")).toBeVisible();
      expect(fake.deletes).toBe(1);
      fake.soldOut.clear();
      await send(page, week);
      expect(fake.carts.at(-1)).toEqual({ added: 4001, source: "first-hit" });
      await expect(page.getByTestId("oda-choice")).toHaveCount(0);

      // (5) Choose again, then look at the choices page: no Oda call, the row is there, Clear removes it.
      await page.getByTestId("oda-added").getByRole("button", { name: "Choose another product" }).click();
      await page.getByTestId("oda-alternative").nth(1).getByRole("button", { name: /Use this next time/ }).click();
      await expect(page.getByTestId("oda-alternative").nth(1)).toContainText("Saved for next time");
      const odaCallsBefore = fake.starts + fake.carts.length;
      await page.getByTestId("oda-choices-link").click();
      await expect(page.getByRole("heading", { name: "Your Oda choices", level: 1 })).toBeVisible();
      const row = page.getByTestId("oda-choice-row");
      await expect(row).toHaveCount(1);
      await expect(row).toContainText("Cherry tomatoes");
      await expect(row).toContainText("Your Oda choice");
      await expect(page.getByText("Cherrytomater")).toHaveCount(0); // no product name is shown
      const clear = row.getByRole("button", { name: /Clear/ });
      await atLeast44(clear, "clear button");
      if (mobile) await noHorizontalScroll(page);
      await clear.click();
      await expect(page.getByTestId("oda-choices-cleared")).toHaveText("Choice cleared");
      await expect(page.getByTestId("oda-choices-empty")).toBeVisible();
      expect(fake.choice).toBeNull();
      expect(fake.starts + fake.carts.length).toBe(odaCallsBefore); // reading and clearing choices needs no Oda call

      // Nothing about the products is kept in the browser.
      const stored = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
      for (const text of ["Cherrytomater", "4003", "4001"]) expect(stored).not.toContain(text);
    } finally {
      await removeItems(page.request, h, week);
    }
  });
});

test.describe("a Child", () => {
  test.use({ storageState: { cookies: [], origins: [] } });
  test.setTimeout(120_000);

  test("sees neither the Oda button nor the choices link and cannot use the endpoints", async ({ browser }, testInfo) => {
    const name = `Zz E2E choices ${uniqueStamp(testInfo)}`;
    const adminContext = await browser.newContext({ storageState: adminState, baseURL: testInfo.project.use.baseURL });
    const memberContext = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: testInfo.project.use.viewport ?? undefined });
    const admin = adminContext.request;
    let created: string | null = null;
    try {
      const make = await admin.post("/api/households", { headers: await csrf(admin), data: { name } });
      expect(make.status()).toBe(201);
      created = ((await make.json()) as { id: string }).id;
      const invite = await admin.post(`/api/households/${created}/invites`, { headers: await csrf(admin), data: { role: "Child" } });
      expect(invite.status()).toBe(201);
      const token = ((await invite.json()) as { token: string }).token;

      const page = await memberContext.newPage();
      await signIn(page, `/invite/${token}`, memberCredentials());
      await page.getByRole("button", { name: "Join household" }).click();
      await expect(page.getByText("You joined the household as Child.")).toBeVisible();

      await page.goto("/shopping");
      await expect(page.getByRole("heading", { name: "Shopping list", level: 1 })).toBeVisible();
      await expect(page.getByRole("button", { name: "Add to Oda cart" })).toHaveCount(0);
      await expect(page.getByTestId("oda-choices-link")).toHaveCount(0);

      await page.goto("/shopping/oda-choices");
      await expect(page.getByTestId("oda-choices-adults-only")).toBeVisible();
      await expect(page.getByTestId("oda-choice-row")).toHaveCount(0);

      // The API refuses a Child (403). An API built before T-052 has no such route and answers 404; any API that has it must say 403.
      const base = `/api/households/${created}/oda-preferences`;
      const headers = await csrf(page.request);
      const statuses = [
        (await page.request.get(base)).status(),
        (await page.request.put(`${base}/${tomato}`, { headers, data: { odaProductId: 1 } })).status(),
        (await page.request.delete(`${base}/${tomato}`, { headers })).status(),
      ];
      for (const status of statuses) expect([403, 404]).toContain(status);
      if (process.env.E2E_API_HAS_PREFERENCES === "1") expect(statuses).toEqual([403, 403, 403]);
    } finally {
      if (created) {
        // The Child leaves with the household: the admin deletes it.
        await admin.delete(`/api/households/${created}`, { headers: await csrf(admin) });
      }
      await memberContext.close();
      await adminContext.close();
    }
  });
});
