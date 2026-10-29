import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-092: the household configures its weekly groceries (bread, milk) on the household page; one button on the shopping list
// puts them on the week, under their store sections; pressing again changes nothing. The button puts the WHOLE set on the
// list, so the test works in its own household (named with the run tag): the architect's real household and the other
// projects running in parallel are never touched. The household, with everything in it, is deleted in `finally`; the run's
// teardown (T-083) deletes it too if that fails.
const baseMonday = Date.UTC(2036, 2, 3); // 2036-03-03 is a Monday

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

test("weekly groceries are configured on the household page and put on the list with one tap, once", async ({ page }, testInfo) => {
  const request = page.request;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  const stamp = uniqueStamp(testInfo);
  const milk = inventedName(stamp, "milk");
  const bread = inventedName(stamp, "bread");
  let h: string | undefined;

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E weekly ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
    // Make it the active household before the app loads.
    await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);

    for (const [name, dimension, section] of [[milk, "Volume", "Dairy"], [bread, "Count", "DryGoods"]] as const) {
      const ingredient = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name, dimension, isStaple: false } });
      expect(ingredient.status(), await ingredient.text()).toBe(201);
      const id = ((await ingredient.json()) as { id: string }).id;
      const placed = await request.put(`/api/households/${h}/ingredients/${id}`, { headers: await csrf(request), data: { name, section } });
      expect(placed.status(), await placed.text()).toBe(200);
    }
    const listItems = async () => {
      const res = await request.get(`/api/households/${h}/shopping-lists/${week}`);
      expect(res.ok()).toBe(true);
      return ((await res.json()) as { main: { id: string; name: string }[] }).main;
    };

    // Household page: add bread (no amount) and milk (1 l).
    await page.goto("/household");
    const card = page.getByTestId("weekly-groceries");
    await expect(card.getByRole("heading", { name: "Weekly groceries", level: 2 })).toBeVisible();
    await expect(card.getByTestId("weekly-empty")).toBeVisible();
    const add = async (name: string, amount?: { quantity: string; unit: string }) => {
      await card.getByLabel("Ingredient").fill(name);
      await card.getByRole("button", { name }).click();
      await expect(card.getByText(`Chosen: ${name}`)).toBeVisible();
      if (amount) {
        await card.getByLabel("Amount (optional)").fill(amount.quantity);
        await card.getByLabel("Unit").selectOption({ label: amount.unit });
      }
      const addButton = card.getByRole("button", { name: "Add", exact: true });
      if (mobile) expect((await addButton.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
      await addButton.click();
      await expect(card.getByTestId("weekly-row").filter({ hasText: name })).toHaveCount(1);
    };
    await add(bread);
    await add(milk, { quantity: "1", unit: "l" });
    await expect(card.getByTestId("weekly-row").filter({ hasText: milk })).toContainText("1 l");
    await expect(card.getByTestId("weekly-row").filter({ hasText: bread })).toContainText("No amount");
    if (mobile) {
      await noHorizontalScroll(page);
      const edit = await card.getByRole("button", { name: `Edit ${milk}` }).boundingBox();
      expect(edit!.height, "touch target").toBeGreaterThanOrEqual(44);
    }

    // Shopping list: one tap, both items under their store sections.
    await page.goto(`/shopping?week=${week}`);
    const button = page.getByTestId("add-weekly");
    await expect(button).toBeEnabled();
    await button.click();
    const toBuy = page.getByRole("region", { name: "To buy" });
    await expect(toBuy.locator('[data-section="Dairy"]').getByRole("checkbox", { name: milk })).toBeVisible();
    await expect(toBuy.locator('[data-section="DryGoods"]').getByRole("checkbox", { name: bread })).toBeVisible();
    await expect(toBuy.locator('[data-section="Dairy"]')).toContainText("1 l");
    await expect(toBuy.getByTestId("shop-weekly-badge")).toHaveCount(2);
    if (mobile) await noHorizontalScroll(page);
    const first = await listItems();
    expect(first.map((i) => i.name).sort()).toEqual([bread, milk].sort());

    // Press again: nothing is duplicated and nothing changes.
    await button.click();
    await expect(button).toBeEnabled();
    const second = await listItems();
    expect(second.map((i) => i.id).sort()).toEqual(first.map((i) => i.id).sort());
    await expect(toBuy.getByRole("checkbox", { name: milk })).toHaveCount(1);
    await expect(toBuy.getByRole("checkbox", { name: bread })).toHaveCount(1);
    await expect(toBuy.locator('[data-section="Dairy"]')).toContainText("1 l");

    // A weekly item can be checked off and removed one by one.
    await toBuy.getByRole("checkbox", { name: bread }).check();
    await expect(toBuy.getByRole("checkbox", { name: bread })).toBeChecked();
    await toBuy.getByRole("button", { name: `Remove the weekly amount of ${bread}` }).click();
    await expect(toBuy.getByRole("checkbox", { name: bread })).toHaveCount(0);
    await expect(toBuy.getByRole("checkbox", { name: milk })).toBeVisible();
  } finally {
    // Deleting the household removes its weekly groceries, list and own ingredients with it.
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});
