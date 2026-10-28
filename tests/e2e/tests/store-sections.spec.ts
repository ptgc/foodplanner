import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-085: the shopping list follows the store layout. A week with rice (dry goods), milk (dairy), tomato (produce) and an
// own ingredient set to "Frozen" on the Ingredients screen shows produce before frozen before dairy before dry goods,
// and manual items last under "Other". Runs in its own far-future week.
const baseMonday = Date.UTC(2034, 2, 6); // 2034-03-06 is a Monday

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

function inventedName(stamp: string): string {
  return "Zqx" + stamp.replace(/\d/g, (d) => "abcdefghij"[Number(d)]!).replace(/-/g, "");
}

test("a planned week shows produce before frozen before dairy before dry goods, and the section is set on the Ingredients screen", async ({ page }, testInfo) => {
  const request = page.request;
  const h = ((await (await request.get("/api/households")).json()) as { id: string }[])[0]!.id;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  const stamp = uniqueStamp(testInfo);
  const own = inventedName(stamp);
  let ownId: string | undefined;
  let recipeId: string | undefined;
  let entryId: string | undefined;

  const find = async (search: string) => {
    const found = (await (await request.get(`/api/households/${h}/ingredients?search=${search}`, { headers: { "Accept-Language": "en" } })).json()) as {
      id: string;
      name: string;
      isGlobal: boolean;
      section: string;
    }[];
    const hit = found.find((i) => i.name === search && i.isGlobal);
    expect(hit, `the catalog has ${search}`).toBeTruthy();
    return hit!;
  };

  try {
    const created = await request.post(`/api/households/${h}/ingredients`, {
      headers: await csrf(request),
      data: { name: own, dimension: "Mass", isStaple: false },
    });
    expect(created.status(), await created.text()).toBe(201);
    const ownBody = (await created.json()) as { id: string; section: string };
    ownId = ownBody.id;
    expect(ownBody.section).toBe("Other"); // the default

    const [rice, milk, tomato] = [await find("Rice"), await find("Milk"), await find("Tomato")];
    expect([rice.section, milk.section, tomato.section]).toEqual(["DryGoods", "Dairy", "FruitAndVegetables"]);
    const gram = ((await (await request.get("/api/units")).json()) as { id: string; code: string }[]).find((u) => u.code === "g")!;

    // The own ingredient moves to Frozen on the Ingredients screen.
    await page.goto("/ingredients");
    await page.getByLabel("Search your ingredients").fill(own);
    const picker = page.getByLabel(`Store section for ${own}`);
    await expect(picker).toHaveValue("Other");
    await picker.selectOption("Frozen");
    await expect(page.getByTestId("ingredients-notice")).toContainText(`${own} is now in Frozen.`);
    await expect(page.getByLabel(`Store section for ${own}`)).toHaveValue("Frozen");
    if (mobile) {
      await noHorizontalScroll(page);
      const box = await page.getByLabel(`Store section for ${own}`).boundingBox();
      expect(box!.height, "touch target").toBeGreaterThanOrEqual(44);
    }

    const recipe = await request.post(`/api/households/${h}/recipes`, {
      headers: await csrf(request),
      data: {
        title: `E2E Sections ${testInfo.project.name} ${stamp}`,
        servings: 4,
        // Deliberately not in store order.
        ingredients: [rice, milk, { id: ownId }, tomato].map((i) => ({ ingredientId: i.id, quantity: 200, unitId: gram.id, note: null })),
      },
    });
    expect(recipe.status(), await recipe.text()).toBe(201);
    recipeId = ((await recipe.json()) as { id: string }).id;
    const entry = await request.post(`/api/households/${h}/meal-plans/${week}/entries`, {
      headers: await csrf(request),
      data: { date: week, recipeId, servings: 4 },
    });
    expect(entry.status(), await entry.text()).toBe(201);
    entryId = ((await entry.json()) as { id: string }).id;
    const generated = await request.post(`/api/households/${h}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
    expect(generated.ok()).toBe(true);
    const manual = await request.post(`/api/households/${h}/shopping-lists/${week}/items`, {
      headers: await csrf(request),
      data: { label: `Batterier ${stamp}`, quantity: null, unitId: null },
    });
    expect(manual.status()).toBe(201);

    await page.goto(`/shopping?week=${week}`);
    const toBuy = page.getByRole("region", { name: "To buy" });
    await expect(toBuy).toBeVisible();
    await expect(toBuy.getByTestId("shop-group")).toHaveCount(5);
    await expect(toBuy.getByRole("heading", { level: 3 })).toHaveText(["Fruit and vegetables", "Frozen", "Dairy and juice", "Dry goods", "Other"]);

    // Items appear in the same order as the headings.
    const order = await toBuy.getByTestId("shop-item").evaluateAll((rows) => rows.map((r) => r.textContent ?? ""));
    const at = (needle: string) => order.findIndex((text) => text.includes(needle));
    expect(at("Tomato"), "produce first").toBeGreaterThanOrEqual(0);
    expect(at("Tomato")).toBeLessThan(at(own));
    expect(at(own)).toBeLessThan(at("Milk"));
    expect(at("Milk")).toBeLessThan(at("Rice"));
    expect(at("Rice")).toBeLessThan(at("Batterier"));

    // Checking off still works and the item stays inside its section.
    await page.getByRole("checkbox", { name: "Milk" }).check();
    await expect(page.getByRole("checkbox", { name: "Milk" })).toBeChecked();
    await expect(toBuy.locator('[data-section="Dairy"]').getByRole("checkbox", { name: "Milk" })).toBeChecked();
    if (mobile) await noHorizontalScroll(page);
  } finally {
    if (entryId) await request.delete(`/api/households/${h}/meal-plans/${week}/entries/${entryId}`, { headers: await csrf(request) });
    const list = await request.get(`/api/households/${h}/shopping-lists/${week}`);
    if (list.ok()) {
      const body = (await list.json()) as { main: { id: string; isManual: boolean }[] };
      for (const item of body.main.filter((i) => i.isManual)) {
        await request.delete(`/api/households/${h}/shopping-lists/${week}/items/${item.id}`, { headers: await csrf(request) });
      }
    }
    await request.post(`/api/households/${h}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
    if (ownId) await request.delete(`/api/households/${h}/ingredients/${ownId}`, { headers: await csrf(request) });
  }
});
