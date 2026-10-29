import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(180_000);

// T-093: a pantry for everything, with amounts; the shopping list subtracts what the household has. The example of the task:
// tacos need 200 g cheese and 8 tortillas. The pantry has cheese 300 g and tortillas without an amount, so both are checked
// and nothing is bought; with cheese 100 g the other 100 g is bought. The pantry is cleared by a household-wide action and
// by a per-item "We have this", so the test works in its OWN household (named with the run tag): the architect's real pantry
// is never touched. The household, with everything in it, is deleted in `finally`; the run's teardown (T-083) deletes it too
// if that fails.
const baseMonday = Date.UTC(2036, 5, 2); // 2036-06-02 is a Monday

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

test("the shopping list subtracts the pantry: the tacos example, We have this, and the pantry screen", async ({ page }, testInfo) => {
  const request = page.request;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  const stamp = uniqueStamp(testInfo);
  const cheese = inventedName(stamp, "cheese");
  const tortillas = inventedName(stamp, "tortillas");
  let h: string | undefined;

  const touchTarget = async (locator: ReturnType<Page["locator"]>) => {
    if (!mobile) return;
    expect((await locator.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
  };

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E pantry ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
    // Make it the active household before the app loads.
    await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);

    // The tacos: 200 g cheese and 8 tortillas for 4, planned for the week.
    const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
    const unit = (code: string) => units.find((u) => u.code === code)!.id;
    const ids: Record<string, string> = {};
    for (const [name, dimension] of [[cheese, "Mass"], [tortillas, "Count"]] as const) {
      const res = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name, dimension, isStaple: false } });
      expect(res.status(), await res.text()).toBe(201);
      ids[name] = ((await res.json()) as { id: string }).id;
    }
    const recipe = await request.post(`/api/households/${h}/recipes`, {
      headers: await csrf(request),
      data: {
        title: `Tacos ${stamp}`,
        description: null,
        servings: 4,
        prepMinutes: 5,
        cookMinutes: 10,
        steps: [{ text: "Fyll lefsene" }],
        ingredients: [
          { ingredientId: ids[cheese], quantity: 200, unitId: unit("g"), note: null },
          { ingredientId: ids[tortillas], quantity: 8, unitId: unit("stk"), note: null },
        ],
        tagIds: [],
      },
    });
    expect(recipe.status(), await recipe.text()).toBe(201);
    const recipeId = ((await recipe.json()) as { id: string }).id;
    const entry = await request.post(`/api/households/${h}/meal-plans/${week}/entries`, {
      headers: await csrf(request),
      data: { date: week, recipeId, servings: 4 },
    });
    expect(entry.status(), await entry.text()).toBe(201);

    const toBuy = page.getByRole("region", { name: "To buy" });
    const checkYouHave = page.getByRole("region", { name: "Check you have these" });
    const generate = async () => {
      await page.goto(`/shopping?week=${week}`);
      await page.getByRole("button", { name: "Generate from meal plan" }).click();
      await expect(page.getByRole("button", { name: "Generate from meal plan" })).toBeEnabled();
    };
    const row = (region: ReturnType<Page["getByRole"]>, name: string) => region.getByTestId("shop-item").filter({ hasText: name });

    // 1. Nothing at home: both are bought in full.
    await generate();
    await expect(row(toBuy, cheese)).toContainText("200 g");
    await expect(row(toBuy, tortillas)).toContainText("8 stk");
    await expect(checkYouHave).toHaveCount(0);

    // 2. The pantry screen: cheese 300 g, tortillas without an amount.
    await page.getByTestId("pantry-link").click();
    await expect(page.getByRole("heading", { name: "Pantry", level: 1 })).toBeVisible();
    await expect(page.getByTestId("pantry-empty")).toBeVisible();
    const add = async (name: string, amount?: { quantity: string; unit: string }) => {
      await page.getByLabel("Ingredient", { exact: true }).fill(name);
      await page.getByRole("button", { name }).click();
      await expect(page.getByText(`Chosen: ${name}`)).toBeVisible();
      if (amount) {
        await page.getByLabel("Amount (optional)").fill(amount.quantity);
        await page.getByLabel("Unit").selectOption({ label: amount.unit });
      }
      const addButton = page.getByRole("button", { name: "Add", exact: true });
      await touchTarget(addButton);
      await addButton.click();
      await expect(page.getByTestId("pantry-row").filter({ hasText: name })).toHaveCount(1);
    };
    await add(cheese, { quantity: "300", unit: "g" });
    await add(tortillas);
    const pantryRow = (name: string) => page.getByTestId("pantry-row").filter({ hasText: name });
    await expect(pantryRow(cheese).getByTestId("pantry-amount")).toHaveText("300 g");
    await expect(pantryRow(tortillas).getByTestId("pantry-amount")).toHaveText("Some, no amount");
    if (mobile) {
      await noHorizontalScroll(page);
      await touchTarget(page.getByRole("button", { name: `Edit ${cheese}` }));
      await touchTarget(page.getByRole("button", { name: `Remove ${cheese} from the pantry` }));
    }

    // 3. The list: nothing is bought; both are under "Check you have" with the note.
    await generate();
    await expect(checkYouHave).toBeVisible();
    await expect(row(checkYouHave, cheese)).toContainText("300 g at home (need 200 g)");
    await expect(row(checkYouHave, tortillas)).toContainText("At home, check there are enough (need 8 stk)");
    await expect(toBuy).toHaveCount(0);
    if (mobile) await noHorizontalScroll(page);

    // 4. Edit the cheese to 100 g on the pantry screen: 100 g goes on the buy list, with "100 g at home".
    await page.goto("/pantry");
    await page.getByRole("button", { name: `Edit ${cheese}` }).click();
    const form = pantryRow(cheese);
    await form.getByLabel("Amount (optional)").fill("100");
    await touchTarget(form.getByRole("button", { name: "Save" }));
    await form.getByRole("button", { name: "Save" }).click();
    await expect(pantryRow(cheese).getByTestId("pantry-amount")).toHaveText("100 g");
    await generate();
    await expect(row(toBuy, cheese)).toContainText("100 g");
    await expect(row(toBuy, cheese).getByTestId("shop-at-home")).toHaveText("100 g at home");
    await expect(row(checkYouHave, tortillas)).toBeVisible();
    await expect(row(checkYouHave, cheese)).toHaveCount(0);

    // 5. Remove the tortillas from the pantry, then say "We have this" on the shopping list with an amount.
    await page.goto("/pantry");
    await page.getByRole("button", { name: `Remove ${tortillas} from the pantry` }).click();
    await expect(pantryRow(tortillas)).toHaveCount(0);
    await generate();
    await expect(row(toBuy, tortillas)).toContainText("8 stk");
    const weHave = row(toBuy, tortillas).getByTestId("we-have-this");
    await touchTarget(weHave);
    await weHave.click();
    const have = row(toBuy, tortillas).getByTestId("have-form");
    await have.getByLabel("How much? (optional)").fill("10");
    await have.getByLabel("Unit").selectOption({ label: "stk" });
    await touchTarget(have.getByRole("button", { name: "Save" }));
    await have.getByRole("button", { name: "Save" }).click();
    await expect(row(checkYouHave, tortillas)).toContainText("10 stk at home (need 8 stk)");
    await expect(row(toBuy, tortillas)).toHaveCount(0);
    await expect(row(toBuy, cheese)).toContainText("100 g");

    // 6. The pantry screen shows it, and an adult clears the whole pantry: both are bought in full again.
    await page.goto("/pantry");
    await expect(pantryRow(tortillas).getByTestId("pantry-amount")).toHaveText("10 stk");
    await page.getByRole("button", { name: "Clear the pantry" }).click();
    const dialog = page.getByRole("dialog");
    await touchTarget(dialog.getByRole("button", { name: "Clear it" }));
    await dialog.getByRole("button", { name: "Clear it" }).click();
    await expect(page.getByTestId("pantry-empty")).toBeVisible();
    await generate();
    await expect(row(toBuy, cheese)).toContainText("200 g");
    await expect(row(toBuy, tortillas)).toContainText("8 stk");
    await expect(checkYouHave).toHaveCount(0);
  } finally {
    // Deleting the household removes its pantry, list, recipe and own ingredients with it.
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});
