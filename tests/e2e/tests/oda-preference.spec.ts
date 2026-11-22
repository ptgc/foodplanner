import type { APIRequestContext, Locator, Page } from "@playwright/test";
import { adminState, expect, memberCredentials, signIn, test, uniqueStamp, uniqueWeek } from "./fixtures";

// T-052: the household's chosen Oda product per ingredient. The real oda.com is never called. As in oda.spec.ts only the Oda
// boundary of the API (start, cart) is faked with page.route. Foodplanner's own endpoints (the list, the oda-preferences
// GET/PUT/DELETE, the ingredient, the session, the roles, kid mode) are the real API and the real database. The fake cart
// learns the household's choice from the PUT/DELETE requests that pass through to the real API (and only when it answered 2xx).
const baseMonday = Date.UTC(2033, 2, 7); // 2033-03-07 is a Monday
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

type Stored = { ingredientId: string; ingredientName: string; setAt: string };

/** What the real database holds for the household's choices, read through the real API. */
async function storedChoices(request: APIRequestContext, h: string): Promise<Stored[]> {
  const res = await request.get(`/api/households/${h}/oda-preferences`);
  expect(res.status()).toBe(200);
  return (await res.json()) as Stored[];
}

/**
 * The faked Oda boundary (start, cart). The cart plays the spec's rules for one ingredient, using the choice that the real
 * oda-preferences endpoints accepted; those endpoints themselves are never answered here.
 */
class FakeOda {
  choice: number | null = null;
  soldOut = new Set<number>();
  starts = 0;
  carts: { added: number; source: string }[] = [];

  constructor(
    private readonly ingredientId: string,
    private readonly ingredientName: string,
  ) {}

  get odaCalls() {
    return this.starts + this.carts.length;
  }

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
              name: this.ingredientName,
              productName: hitNames[hitIds.indexOf(added)],
              quantity: 1,
              source,
              ingredientId: this.ingredientId,
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
    // Foodplanner's own endpoints go to the real API; only what it accepted is noted for the fake cart.
    await page.route(/\/api\/households\/[^/]+\/oda-preferences\/[^/]+$/, async (route) => {
      const request = route.request();
      const body = request.method() === "PUT" ? (request.postDataJSON() as { odaProductId: number }) : null;
      const response = await route.fetch();
      if (response.ok()) this.choice = body ? body.odaProductId : null;
      await route.fulfill({ response });
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
    const ingredientName = `Zz E2E tomat ${uniqueStamp(testInfo)}`.slice(0, 90);
    const mobile = testInfo.project.name.startsWith("mobile");
    let ingredientId: string | null = null;
    try {
      // A real ingredient of the household (removed again below; deleting it also removes its choice in the same save).
      const created = await page.request.post(`/api/households/${h}/ingredients`, {
        headers: await csrf(page.request),
        data: { name: ingredientName, isStaple: false, dimension: "Mass" },
      });
      expect(created.status(), await created.text()).toBe(201);
      ingredientId = ((await created.json()) as { id: string }).id;
      const fake = new FakeOda(ingredientId, ingredientName);
      await addItem(page.request, h, week, label);
      await fake.install(page);

      // The choices link is on the shopping page for an Adult.
      await page.goto(`/shopping?week=${week}`);
      await expect(page.getByTestId("oda-choices-link")).toBeVisible();
      expect(await storedChoices(page.request, h)).toEqual([]);

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
      expect(fake.choice).toBe(4003); // the real API accepted the PUT
      const stored = await storedChoices(page.request, h);
      expect(stored.map((s) => s.ingredientId)).toEqual([ingredientId]); // and the real database holds it
      expect(JSON.stringify(stored)).not.toContain("4003"); // but the list never returns the product id
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
      expect((await storedChoices(page.request, h)).length).toBe(1); // a fill never changes the choice

      // (4) Remove the choice on the line; the next fill uses the first hit and shows no choice label.
      const removeButton = page.getByTestId("oda-added").getByRole("button", { name: /Remove choice/ });
      await atLeast44(removeButton, "remove button");
      await removeButton.click();
      await expect(page.getByTestId("oda-removed")).toBeVisible();
      expect(await storedChoices(page.request, h)).toEqual([]);
      fake.soldOut.clear();
      await send(page, week);
      expect(fake.carts.at(-1)).toEqual({ added: 4001, source: "first-hit" });
      await expect(page.getByTestId("oda-choice")).toHaveCount(0);

      // (5) Choose again, then look at the choices page: no Oda call, the stored row is there, Clear removes it from the database.
      await page.getByTestId("oda-added").getByRole("button", { name: "Choose another product" }).click();
      await page.getByTestId("oda-alternative").nth(1).getByRole("button", { name: /Use this next time/ }).click();
      await expect(page.getByTestId("oda-alternative").nth(1)).toContainText("Saved for next time");
      expect((await storedChoices(page.request, h)).map((s) => s.ingredientName)).toEqual([ingredientName]);
      const odaCallsBefore = fake.odaCalls;
      await page.getByTestId("oda-choices-link").click();
      await expect(page.getByRole("heading", { name: "Your Oda choices", level: 1 })).toBeVisible();
      const row = page.getByTestId("oda-choice-row");
      await expect(row).toHaveCount(1);
      await expect(row).toContainText(ingredientName);
      await expect(row).toContainText("Your Oda choice");
      await expect(page.getByText("Cherrytomater")).toHaveCount(0); // no product name is shown
      const clear = row.getByRole("button", { name: /Clear/ });
      await atLeast44(clear, "clear button");
      if (mobile) await noHorizontalScroll(page);
      await clear.click();
      await expect(page.getByTestId("oda-choices-cleared")).toHaveText("Choice cleared");
      await expect(page.getByTestId("oda-choices-empty")).toBeVisible();
      expect(await storedChoices(page.request, h)).toEqual([]);
      expect(fake.odaCalls).toBe(odaCallsBefore); // reading and clearing choices needs no Oda call

      // Nothing about the products is kept in the browser.
      const kept = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
      for (const text of ["Cherrytomater", "4003", "4001"]) expect(kept).not.toContain(text);

      // A choice goes with its ingredient: deleting the ingredient needs no clearing first.
      await page.request.put(`/api/households/${h}/oda-preferences/${ingredientId}`, { headers: await csrf(page.request), data: { odaProductId: 4002 } });
      expect(await storedChoices(page.request, h)).toHaveLength(1);
      const deleted = await page.request.delete(`/api/households/${h}/ingredients/${ingredientId}`, { headers: await csrf(page.request) });
      expect(deleted.status()).toBe(204);
      ingredientId = null;
      expect(await storedChoices(page.request, h)).toEqual([]);
    } finally {
      await removeItems(page.request, h, week);
      if (ingredientId) await page.request.delete(`/api/households/${h}/oda-preferences/${ingredientId}`, { headers: await csrf(page.request) });
      if (ingredientId) await page.request.delete(`/api/households/${h}/ingredients/${ingredientId}`, { headers: await csrf(page.request) });
    }
  });
});

/** A random 4-digit PIN the server accepts (not all equal, not a straight run). */
function randomPin(): string {
  for (;;) {
    const pin = String(Math.floor(Math.random() * 10_000)).padStart(4, "0");
    const d = [...pin].map(Number);
    const steps = d.slice(1).map((x, i) => x - d[i]!);
    const weak = d.every((x) => x === d[0]) || steps.every((s) => s === 1) || steps.every((s) => s === -1);
    if (!weak) return pin;
  }
}

// Both tests sign in as the seeded e2e member, who gets a PIN in the second one: they must not run at the same time.
test.describe("a Child and a kid-mode session", () => {
  test.describe.configure({ mode: "serial" });
  test.use({ storageState: { cookies: [], origins: [] } });
  test.setTimeout(120_000);

  /** Creates a household as the admin, invites the e2e member with `role` and signs the page in as that member, joined. */
  async function joinedMember(page: Page, admin: APIRequestContext, name: string, role: "Child" | "Adult") {
    const make = await admin.post("/api/households", { headers: await csrf(admin), data: { name } });
    expect(make.status(), await make.text()).toBe(201);
    const h = ((await make.json()) as { id: string }).id;
    const invite = await admin.post(`/api/households/${h}/invites`, { headers: await csrf(admin), data: { role } });
    expect(invite.status(), await invite.text()).toBe(201);
    const { token } = (await invite.json()) as { token: string };
    await signIn(page, `/invite/${token}`, memberCredentials());
    await page.getByRole("button", { name: "Join household" }).click();
    await expect(page.getByText(`You joined the household as ${role}.`)).toBeVisible();
    return h;
  }

  /** The three oda-preferences calls of the page's session. */
  async function threeCalls(page: Page, h: string) {
    const base = `/api/households/${h}/oda-preferences`;
    const ingredient = "55555555-5555-5555-5555-555555555555";
    const headers = await csrf(page.request);
    const answers = [
      await page.request.get(base),
      await page.request.put(`${base}/${ingredient}`, { headers, data: { odaProductId: 1 } }),
      await page.request.delete(`${base}/${ingredient}`, { headers }),
    ];
    return answers;
  }

  async function seesNoOda(page: Page) {
    await page.goto("/shopping");
    await expect(page.getByTestId("kid-shell").or(page.getByRole("heading", { name: "Shopping list", level: 1 }))).toBeVisible();
    await expect(page.getByRole("button", { name: "Add to Oda cart" })).toHaveCount(0);
    await expect(page.getByTestId("oda-choices-link")).toHaveCount(0);
  }

  test("a Child sees neither the Oda button nor the choices link and gets 403 on the three endpoints", async ({ browser }, testInfo) => {
    const adminContext = await browser.newContext({ storageState: adminState, baseURL: testInfo.project.use.baseURL });
    const memberContext = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: testInfo.project.use.viewport ?? undefined });
    const admin = adminContext.request;
    let h: string | null = null;
    try {
      const page = await memberContext.newPage();
      h = await joinedMember(page, admin, `Zz E2E choices ${uniqueStamp(testInfo)}`, "Child");

      await seesNoOda(page);
      await page.goto("/shopping/oda-choices");
      await expect(page.getByTestId("oda-choices-adults-only")).toBeVisible();
      await expect(page.getByTestId("oda-choice-row")).toHaveCount(0);

      for (const answer of await threeCalls(page, h)) {
        expect(answer.status()).toBe(403);
        expect(((await answer.json()) as { type: string }).type).toBe("urn:foodplanner:problem:forbidden");
      }
      expect(await storedChoices(admin, h)).toEqual([]);
    } finally {
      if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
      await memberContext.close();
      await adminContext.close();
    }
  });

  test("a kid-mode session sees neither the Oda button nor the choices link and gets 403 kid-mode on the three endpoints", async ({ browser }, testInfo) => {
    const adminContext = await browser.newContext({ storageState: adminState, baseURL: testInfo.project.use.baseURL });
    const memberContext = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: testInfo.project.use.viewport ?? undefined });
    const admin = adminContext.request;
    let h: string | null = null;
    try {
      const page = await memberContext.newPage();
      h = await joinedMember(page, admin, `Zz E2E kid choices ${uniqueStamp(testInfo)}`, "Adult");
      // As in kid-mode.spec.ts: the member's own PIN (proof: the password), a kid profile, and the device becomes the kid's.
      const pin = await page.request.put("/api/auth/pin", {
        headers: await csrf(page.request),
        data: { pin: randomPin(), currentPin: null, password: memberCredentials().password },
      });
      expect(pin.status(), await pin.text()).toBe(204);
      const profile = await page.request.post(`/api/households/${h}/profiles`, {
        headers: await csrf(page.request),
        data: { displayName: "Mia", avatar: "fox" },
      });
      expect(profile.status(), await profile.text()).toBe(201);
      const activated = await page.request.post(`/api/households/${h}/profiles/${((await profile.json()) as { id: string }).id}/activate`, {
        headers: await csrf(page.request),
      });
      expect(activated.status(), await activated.text()).toBe(204);

      await seesNoOda(page);
      await expect(page.getByTestId("kid-shell")).toBeVisible();

      for (const answer of await threeCalls(page, h)) {
        expect(answer.status()).toBe(403);
        expect(((await answer.json()) as { type: string }).type).toBe("urn:foodplanner:problem:kid-mode");
      }
      expect(await storedChoices(admin, h)).toEqual([]);
    } finally {
      if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
      await memberContext.close();
      await adminContext.close();
    }
  });
});
