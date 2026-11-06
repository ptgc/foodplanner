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
    await expect(card.getByRole("heading", { name: "Regular groceries", level: 2 })).toBeVisible();
    await expect(card.getByTestId("weekly-empty")).toBeVisible();
    const add = async (name: string, amount?: { quantity: string; unit: string }) => {
      await card.getByLabel("Ingredient", { exact: true }).fill(name);
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
    await toBuy.getByRole("button", { name: `Remove the regular amount of ${bread}` }).click();
    await expect(toBuy.getByRole("checkbox", { name: bread })).toHaveCount(0);
    await expect(toBuy.getByRole("checkbox", { name: milk })).toBeVisible();
  } finally {
    // Deleting the household removes its weekly groceries, list and own ingredients with it.
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});

// T-109: a regular grocery that is bought every second week is due in alternate weeks only. The server decides which weeks
// (counted from when it was added), so the test asks the API for the first due week and checks the page there and in the
// week after. Same isolation as above: a run-tagged household, deleted in `finally`.
test("a regular grocery every second week is due in one week, not the next, and can be added anyway", async ({ page }, testInfo) => {
  const request = page.request;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const stamp = uniqueStamp(testInfo);
  const cheese = inventedName(stamp, "cheese");
  let h: string | undefined;

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E regular ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
    await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);
    const ingredient = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name: cheese, dimension: "Mass", isStaple: false } });
    expect(ingredient.status(), await ingredient.text()).toBe(201);

    await page.goto("/household");
    const card = page.getByTestId("weekly-groceries");
    await card.getByLabel("Ingredient", { exact: true }).fill(cheese);
    await card.getByRole("button", { name: cheese }).click();
    await card.getByLabel("How often").selectOption("EveryTwoWeeks");
    await card.getByRole("button", { name: "Add", exact: true }).click();
    const row = card.getByTestId("weekly-row").filter({ hasText: cheese });
    await expect(row).toContainText("Every second week");
    await expect(row).toContainText("Next: week");
    if (mobile) {
      await noHorizontalScroll(page);
      const move = await row.getByRole("button", { name: `Move ${cheese} to next week` }).boundingBox();
      expect(move!.height, "touch target").toBeGreaterThanOrEqual(44);
      const select = await row.getByLabel(`How often for ${cheese}`).boundingBox();
      expect(select!.height, "touch target").toBeGreaterThanOrEqual(44);
    }

    // The week the server says is next due, and the one after it.
    const read = await request.get(`/api/households/${h}/weekly-groceries`);
    const due = ((await read.json()) as { nextDueWeek: string }[])[0]!.nextDueWeek;
    const after = new Date(Date.parse(`${due}T00:00:00Z`) + 7 * 86_400_000).toISOString().slice(0, 10);

    await page.goto(`/shopping?week=${due}`);
    await expect(page.getByTestId("add-weekly")).toBeEnabled();
    await expect(page.getByTestId("weekly-not-due")).toHaveCount(0);

    await page.goto(`/shopping?week=${after}`);
    await expect(page.getByTestId("weekly-none-due")).toBeVisible();
    await expect(page.getByTestId("add-weekly")).toBeDisabled();
    const notDue = page.getByTestId("weekly-not-due");
    await notDue.getByText("Not due this week (1)").click();
    const anyway = notDue.getByRole("button", { name: `Add ${cheese} anyway` });
    if (mobile) expect((await anyway.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
    await anyway.click();
    await expect(page.getByRole("region", { name: "To buy" }).getByRole("checkbox", { name: cheese })).toBeVisible();
    await expect(page.getByTestId("weekly-not-due")).toHaveCount(0);
    if (mobile) await noHorizontalScroll(page);
  } finally {
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});
