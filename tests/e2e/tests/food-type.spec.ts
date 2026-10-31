import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, saveRecipe, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-089: a recipe gets its food type from its main ingredients (a recipe with salmon is Fish), the list can be filtered by
// type, and an adult can override the type by hand.
const english = { "Accept-Language": "en" };

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("the type is derived from the ingredients, the list filters by it and an override wins", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const h = households[0]!.id;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const title = `E2E Type ${testInfo.project.name} ${uniqueStamp(testInfo)}`;

  const units = (await (await request.get("/api/units", { headers: english })).json()) as { id: string; code: string }[];
  const gram = units.find((u) => u.code === "g")!;

  let recipeId: string | undefined;
  try {
    // Create through the form: the type is left on automatic.
    await page.goto("/recipes/new");
    await page.getByLabel("Title").fill(title);
    const select = page.getByTestId("food-type-field").getByRole("combobox");
    await expect(select).toHaveValue("");
    await expect(select.locator("option").first()).toHaveText("Automatic (from the ingredients)");
    const rows = page.getByTestId("ingredient-row");
    await rows.nth(0).getByLabel("Ingredient", { exact: true }).fill("Salmon");
    await rows.nth(0).getByRole("button", { name: "Salmon", exact: true }).click();
    await rows.nth(0).getByLabel("Amount", { exact: true }).fill("400");
    await rows.nth(0).getByLabel("Unit").selectOption(gram.id);
    await page.getByTestId("step-row").nth(0).getByRole("textbox").fill("Bake it");
    if (mobile) {
      await noHorizontalScroll(page);
      const box = await select.boundingBox();
      expect(box!.height, "food type select").toBeGreaterThanOrEqual(44);
    }

    await saveRecipe(page, page.getByRole("button", { name: "Save recipe" }));
    await expect(page).toHaveURL(/\/recipes\/[0-9a-f-]{36}$/, { timeout: 15_000 });
    recipeId = page.url().split("/").pop();
    await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
    await expect(page.getByTestId("recipe-food-type")).toHaveText("Fish");

    // The list: filter by Fish finds it, filter by Vegan does not (a narrow search keeps the page short).
    await page.goto(`/recipes?q=${encodeURIComponent(title)}`);
    const group = page.getByRole("group", { name: "Food type" });
    const card = page.getByTestId("recipe-card").filter({ hasText: title });
    await expect(card).toBeVisible();
    await expect(card.getByTestId("recipe-card-food-type")).toHaveText("Fish");
    if (mobile) {
      await noHorizontalScroll(page);
      for (const chip of await group.getByRole("button").all()) {
        const box = await chip.boundingBox();
        expect(box!.height, await chip.innerText()).toBeGreaterThanOrEqual(44);
      }
    }

    await group.getByRole("button", { name: "Vegan" }).click();
    await expect(page).toHaveURL(/type=Vegan/);
    await expect(page.getByTestId("recipes-empty")).toBeVisible();
    await group.getByRole("button", { name: "Fish" }).click(); // Vegan or Fish
    await expect(page).toHaveURL(/type=Vegan&type=Fish/);
    await expect(card).toBeVisible();
    await group.getByRole("button", { name: "Vegan" }).click();
    await expect(page).toHaveURL(/type=Fish$/);
    await expect(card).toBeVisible();

    // Override by hand: Beef wins over the derived Fish, and the filter follows.
    await page.goto(`/recipes/${recipeId}/edit`);
    const edit = page.getByTestId("food-type-field").getByRole("combobox");
    await expect(edit.locator("option").first()).toHaveText("Automatic: Fish");
    await edit.selectOption("Beef");
    await saveRecipe(page, page.getByRole("button", { name: "Save recipe" }));
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipeId}$`), { timeout: 15_000 });
    await expect(page.getByTestId("recipe-food-type")).toHaveText("Beef");

    await page.goto(`/recipes?q=${encodeURIComponent(title)}&type=Fish`);
    await expect(page.getByTestId("recipes-empty")).toBeVisible();
    await page.goto(`/recipes?q=${encodeURIComponent(title)}&type=Beef`);
    await expect(card).toBeVisible();
  } finally {
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});
