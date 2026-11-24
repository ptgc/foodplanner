import type { APIRequestContext, Locator, Page } from "@playwright/test";
import { adminState, expect, memberCredentials, signIn, test, uniqueStamp } from "./fixtures";

// T-130: "Sync with Oda". The real oda.com is never called. As in oda.spec.ts only the Oda boundary of the API (oda-sync/start and
// oda-sync/review, the two calls that would reach Oda) is faked with page.route. Everything else is the real API and the real database:
// the household, its recipes and ingredients, the bulk PUT of oda-preferences, the list of choices, the roles and kid mode. The fake
// review reads the household's real choices to leave out the ingredients that have one, as the real selection does.

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
}

async function atLeast44(locator: Locator, what: string) {
  await locator.first().waitFor();
  const all = await locator.all();
  expect(all.length, `${what}: something to measure`).toBeGreaterThan(0);
  for (const element of all) {
    const box = (await element.boundingBox())!;
    expect(box.height, `${what} height`).toBeGreaterThanOrEqual(43.5);
  }
}

type Choice = { ingredientId: string; ingredientName: string };

async function choices(request: APIRequestContext, h: string): Promise<Choice[]> {
  const res = await request.get(`/api/households/${h}/oda-preferences`);
  expect(res.status()).toBe(200);
  return (await res.json()) as Choice[];
}

type Plan = { id: string; name: string; kind: "usual" | "suggestion" | "missing" };

/** The faked Oda boundary. `searched` lists the ingredient names each review run searched (what Oda would have seen). */
class FakeSync {
  starts: { includeStaples: boolean; includeExisting: boolean; offset: number }[] = [];
  reviews = 0;
  searched: string[][] = [];
  partialFirst = true;

  constructor(
    private readonly request: APIRequestContext,
    private readonly plan: Plan[],
  ) {}

  private async selected(includeExisting: boolean): Promise<Plan[]> {
    const have = new Set((await choices(this.request, this.h)).map((c) => c.ingredientId));
    return this.plan.filter((p) => includeExisting || !have.has(p.id));
  }

  h = "";

  async install(page: Page) {
    await page.route(/\/api\/households\/[^/]+\/oda-sync\/start$/, async (route) => {
      const body = route.request().postDataJSON() as { includeStaples?: boolean; includeExisting?: boolean; offset?: number };
      const options = { includeStaples: body.includeStaples === true, includeExisting: body.includeExisting === true, offset: body.offset ?? 0 };
      this.starts.push(options);
      const rows = await this.selected(options.includeExisting);
      if (rows.length === 0) {
        await route.fulfill({ status: 409, contentType: "application/problem+json", json: { type: "urn:foodplanner:problem:oda-sync-nothing-to-sync", title: "x", status: 409 } });
        return;
      }
      await route.fulfill({
        json: { authorizeUrl: `${new URL(page.url()).origin}/oda/callback?code=fake-code-${this.starts.length}&state=fake-state-${this.starts.length}`, ingredientCount: rows.length, notCheckedCount: 0 },
      });
    });
    await page.route(/\/api\/households\/[^/]+\/oda-sync\/review$/, async (route) => {
      this.reviews++;
      const options = this.starts.at(-1)!;
      const rows = await this.selected(options.includeExisting);
      this.searched.push(rows.map((r) => r.name));
      const partial = this.partialFirst && this.reviews === 1;
      const option = (productId: number, productName: string, isUsual = false) => ({ productId, productName, isUsual, isCurrent: false });
      const usualPlan = rows.filter((r) => r.kind === "usual");
      const suggestionPlan = rows.filter((r) => r.kind === "suggestion");
      const missingPlan = rows.filter((r) => r.kind === "missing");
      await route.fulfill({
        json: {
          status: partial ? "partial" : "done",
          stopped: partial ? "time" : null,
          usuallyBought: "used",
          rows: [
            ...usualPlan.map((r) => ({
              ingredientId: r.id, ingredientName: r.name, category: "usually-bought", reason: null, hasExistingChoice: false, selectedProductId: 9002, preticked: true,
              options: [option(9001, "Fake e2e product with a rather long name that has to wrap on a narrow phone screen 1 kg"), option(9002, "Fake e2e usual product 500 g", true)],
            })),
            ...suggestionPlan.map((r) => ({
              ingredientId: r.id, ingredientName: r.name, category: "suggestion", reason: null, hasExistingChoice: false, selectedProductId: 9101, preticked: false,
              options: [option(9101, "Fake e2e suggestion 250 g"), option(9102, "Fake e2e other suggestion 1 kg")],
            })),
            ...missingPlan.map((r) => ({
              ingredientId: r.id, ingredientName: r.name, category: "not-found", reason: "no-match", hasExistingChoice: false, selectedProductId: null, preticked: false, options: [],
            })),
          ],
          notCheckedCount: partial ? 2 : 0,
          connectionClosed: true,
          connectionMessage: null,
        },
      });
    });
  }
}

test.describe("an Adult syncs with Oda", () => {
  test.use({ storageState: adminState });

  test("starts, reviews, ticks, saves, and a second run skips what has a choice", async ({ page }, testInfo) => {
    const request = page.request;
    const stamp = uniqueStamp(testInfo);
    const mobile = testInfo.project.name.startsWith("mobile");
    let h: string | null = null;
    try {
      const made = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E sync ${stamp}` } });
      expect(made.status(), await made.text()).toBe(201);
      h = ((await made.json()) as { id: string }).id;
      const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
      await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);

      const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
      const gram = units.find((u) => u.code === "g")!.id;
      const plan: Plan[] = [];
      for (const [kind, label] of [["usual", "Usual"], ["suggestion", "Suggest"], ["missing", "Missing"]] as const) {
        const name = `Zz E2E ${label} ${stamp}`.slice(0, 90);
        const created = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name, dimension: "Mass", isStaple: false } });
        expect(created.status(), await created.text()).toBe(201);
        plan.push({ id: ((await created.json()) as { id: string }).id, name, kind });
      }

      const recipe = await request.post(`/api/households/${h}/recipes`, {
        headers: await csrf(request),
        data: { title: `E2E sync recipe ${stamp}`, servings: 4, ingredients: plan.map((p) => ({ ingredientId: p.id, quantity: 100, unitId: gram, note: null })) },
      });
      expect(recipe.status(), await recipe.text()).toBe(201);

      const fake = new FakeSync(request, plan);
      fake.h = h;
      await fake.install(page);

      // The button and the confirmation: the count from start, two boxes off.
      await page.goto("/shopping/oda-choices");
      const button = page.getByRole("button", { name: "Sync with Oda" });
      await atLeast44(button, "sync button");
      await button.click();
      const dialog = page.getByRole("dialog", { name: "Sync with Oda?" });
      await expect(dialog.getByText("3 ingredients will be searched at Oda.")).toBeVisible();
      await expect(dialog.getByRole("checkbox", { name: /Also staples/ })).not.toBeChecked();
      await expect(dialog.getByRole("checkbox", { name: /already have a choice/ })).not.toBeChecked();
      await atLeast44(dialog.getByRole("checkbox").locator("xpath=ancestor::label"), "confirmation checkbox row");
      await dialog.getByRole("button", { name: "Continue to Oda" }).click();

      // Back from Oda: the review. The usual product is ticked, the suggestion is not, the missing one has no control.
      await expect(page.getByRole("heading", { name: "Sync with Oda", level: 1 })).toBeVisible();
      await expect(page.getByTestId("oda-sync-usually-bought")).toBeVisible();
      expect(fake.reviews).toBe(1);
      const usualBox = page.getByTestId("oda-sync-usually-bought").getByRole("checkbox");
      const suggestionBox = page.getByTestId("oda-sync-suggestion").getByRole("checkbox");
      await expect(usualBox).toBeChecked();
      await expect(suggestionBox).not.toBeChecked();
      await expect(page.getByTestId("oda-sync-not-found")).toContainText(plan[2]!.name);
      await expect(page.getByTestId("oda-sync-not-found").getByRole("checkbox")).toHaveCount(0);
      await expect(page.getByTestId("oda-sync-partial")).toContainText("Time ran out");
      await expect(page.getByText("Save before you leave: the list is not kept.")).toBeVisible();

      // Touch targets and no sideways scroll at 390px.
      await atLeast44(page.getByTestId("oda-sync-row").locator(".oda-sync-label, .oda-sync-notfound"), "row");
      await atLeast44(page.getByRole("button", { name: /Choose another for/ }), "choose another");
      await atLeast44(page.getByRole("button", { name: /Tick all|Untick all/ }), "tick all");
      await atLeast44(page.getByRole("button", { name: "Check the next ones" }), "check the next ones");
      await atLeast44(page.getByRole("button", { name: /Save choices/ }), "save");
      await page.getByRole("button", { name: /Choose another for/ }).first().click();
      await atLeast44(page.getByTestId("oda-sync-option").locator("label"), "option row");
      if (mobile) await noHorizontalScroll(page);

      // Tick the suggestion and save: both ingredients become choices in the real database.
      await suggestionBox.check();
      await page.getByRole("button", { name: "Save choices (2)" }).click();
      await expect(page.getByTestId("oda-sync-saved")).toContainText("Saved 2 choices.");
      expect((await choices(request, h)).map((c) => c.ingredientName).sort()).toEqual([plan[0]!.name, plan[1]!.name].sort());
      const kept = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
      for (const text of ["Fake e2e", "9001", "9002", "9101", "fake-code", "fake-state"]) expect(kept).not.toContain(text);

      await page.getByRole("link", { name: "Your Oda choices" }).click();
      await expect(page.getByTestId("oda-choice-row")).toHaveCount(2);
      await expect(page.getByTestId("oda-choices-list")).toContainText(plan[0]!.name);
      await expect(page.getByTestId("oda-choices-list")).toContainText(plan[1]!.name);

      // A second run without "already have a choice" searches only what has none.
      await page.getByRole("button", { name: "Sync with Oda" }).click();
      const second = page.getByRole("dialog", { name: "Sync with Oda?" });
      await expect(second.getByText("1 ingredient will be searched at Oda.")).toBeVisible();
      await second.getByRole("button", { name: "Continue to Oda" }).click();
      await expect(page.getByTestId("oda-sync-not-found")).toContainText(plan[2]!.name);
      expect(fake.searched).toHaveLength(2);
      expect(fake.searched[1]).toEqual([plan[2]!.name]);
      await expect(page.getByTestId("oda-sync-usually-bought")).toHaveCount(0);
    } finally {
      if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
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

  /** The three sync calls of the page's session (the real API: start and review never reach Oda when refused). */
  async function threeCalls(page: Page, h: string) {
    const headers = await csrf(page.request);
    return [
      await page.request.post(`/api/households/${h}/oda-sync/start`, { headers, data: { confirmed: true } }),
      await page.request.post(`/api/households/${h}/oda-sync/review`, { headers, data: { state: "x", code: "y" } }),
      await page.request.put(`/api/households/${h}/oda-preferences`, {
        headers,
        data: { items: [{ ingredientId: "55555555-5555-5555-5555-555555555555", odaProductId: 1, replace: false }] },
      }),
    ];
  }

  test("a Child sees no sync button and gets 403 on the three calls", async ({ browser }, testInfo) => {
    const adminContext = await browser.newContext({ storageState: adminState, baseURL: testInfo.project.use.baseURL });
    const memberContext = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: testInfo.project.use.viewport ?? undefined });
    const admin = adminContext.request;
    let h: string | null = null;
    try {
      const page = await memberContext.newPage();
      h = await joinedMember(page, admin, `Zz E2E sync child ${uniqueStamp(testInfo)}`, "Child");

      await page.goto("/shopping/oda-choices");
      await expect(page.getByTestId("oda-choices-adults-only")).toBeVisible();
      await expect(page.getByRole("button", { name: "Sync with Oda" })).toHaveCount(0);

      for (const answer of await threeCalls(page, h)) {
        expect(answer.status()).toBe(403);
        expect(((await answer.json()) as { type: string }).type).toBe("urn:foodplanner:problem:forbidden");
      }
      expect(await choices(admin, h)).toEqual([]);
    } finally {
      if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
      await memberContext.close();
      await adminContext.close();
    }
  });

  test("a kid-mode session sees no sync button and gets 403 kid-mode on the three calls", async ({ browser }, testInfo) => {
    const adminContext = await browser.newContext({ storageState: adminState, baseURL: testInfo.project.use.baseURL });
    const memberContext = await browser.newContext({ storageState: { cookies: [], origins: [] }, viewport: testInfo.project.use.viewport ?? undefined });
    const admin = adminContext.request;
    let h: string | null = null;
    try {
      const page = await memberContext.newPage();
      h = await joinedMember(page, admin, `Zz E2E sync kid ${uniqueStamp(testInfo)}`, "Adult");
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

      await page.goto("/shopping/oda-choices");
      await expect(page.getByTestId("kid-shell")).toBeVisible();
      await expect(page.getByRole("button", { name: "Sync with Oda" })).toHaveCount(0);

      for (const answer of await threeCalls(page, h)) {
        expect(answer.status()).toBe(403);
        expect(((await answer.json()) as { type: string }).type).toBe("urn:foodplanner:problem:kid-mode");
      }
      expect(await choices(admin, h)).toEqual([]);
    } finally {
      if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
      await memberContext.close();
      await adminContext.close();
    }
  });
});
