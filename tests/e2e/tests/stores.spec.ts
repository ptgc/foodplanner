import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-087: the household registers a store on the household page and sets its section order with the up/down buttons (dairy
// before produce), picks it on the shopping list, and the list shows dairy first; clearing the choice goes back to the usual
// order. Works in its own household (named with the run tag) that is deleted in `finally`; the run's teardown (T-083) deletes it too.
const baseMonday = Date.UTC(2037, 2, 2); // 2037-03-02 is a Monday

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

/** A name made of letters only that still carries the run tag (the stamp's digits become letters). */
function inventedName(stamp: string, word: string): string {
  return "Zqx" + word + stamp.replace(/\d/g, (d) => "abcdefghij"[Number(d)]!).replace(/-/g, "");
}

test("a store with dairy before produce is picked on the shopping list and the list shows dairy first", async ({ page }, testInfo) => {
  const request = page.request;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  const stamp = uniqueStamp(testInfo);
  const milk = inventedName(stamp, "milk");
  const apple = inventedName(stamp, "apple");
  const store = `E2E Rema ${stamp}`;
  let h: string | undefined;

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E stores ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
    await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);

    // Two own ingredients in different store sections, both on the week's list through the weekly groceries.
    for (const [name, dimension, section] of [[milk, "Volume", "Dairy"], [apple, "Count", "FruitAndVegetables"]] as const) {
      const ingredient = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name, dimension, isStaple: false } });
      expect(ingredient.status(), await ingredient.text()).toBe(201);
      const id = ((await ingredient.json()) as { id: string }).id;
      const placed = await request.put(`/api/households/${h}/ingredients/${id}`, { headers: await csrf(request), data: { name, section } });
      expect(placed.status(), await placed.text()).toBe(200);
      const weekly = await request.post(`/api/households/${h}/weekly-groceries`, { headers: await csrf(request), data: { ingredientId: id } });
      expect(weekly.status(), await weekly.text()).toBe(201);
    }
    const put = await request.post(`/api/households/${h}/shopping-lists/${week}/weekly`, { headers: await csrf(request) });
    expect(put.status(), await put.text()).toBe(200);

    // Household page: the store, with the dairy section moved above produce using the buttons.
    await page.goto("/household");
    const card = page.getByTestId("stores");
    await expect(card.getByRole("heading", { name: "Stores", level: 2 })).toBeVisible();
    await expect(card.getByTestId("stores-empty")).toBeVisible();
    await card.getByLabel("Store name").fill(store);
    const moveUp = card.getByRole("button", { name: "Move Dairy and juice up" });
    if (mobile) expect((await moveUp.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
    for (let i = 0; i < 3; i++) await moveUp.click(); // 4th -> 1st
    await expect(card.getByTestId("store-order-row").first()).toContainText("Dairy and juice");
    if (mobile) await noHorizontalScroll(page);
    await card.getByRole("button", { name: "Add store" }).click();
    await expect(card.getByTestId("store-row").filter({ hasText: store })).toContainText("Dairy and juice › Fruit and vegetables");

    // Shopping list: the usual order first (produce before dairy), then the store (dairy first), then back.
    await page.goto(`/shopping?week=${week}`);
    const toBuy = page.getByRole("region", { name: "To buy" });
    const sections = () => toBuy.locator("[data-section]").evaluateAll((els) => els.map((e) => e.getAttribute("data-section")));
    await expect(toBuy.getByRole("checkbox", { name: milk })).toBeVisible();
    await expect.poll(sections).toEqual(["FruitAndVegetables", "Dairy"]);

    const picker = page.getByLabel("Store", { exact: true });
    if (mobile) expect((await picker.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
    await picker.selectOption({ label: store });
    await expect.poll(sections).toEqual(["Dairy", "FruitAndVegetables"]);
    if (mobile) await noHorizontalScroll(page);

    // Remembered on the server: a reload keeps the choice and the order.
    await page.reload();
    await expect(page.getByLabel("Store", { exact: true })).toHaveValue(/.+/);
    await expect.poll(sections).toEqual(["Dairy", "FruitAndVegetables"]);

    await page.getByLabel("Store", { exact: true }).selectOption({ label: "No store (usual order)" });
    await expect.poll(sections).toEqual(["FruitAndVegetables", "Dairy"]);
  } finally {
    // Deleting the household removes its stores, list and own ingredients with it.
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});
