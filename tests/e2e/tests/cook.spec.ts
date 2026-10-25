import type { APIRequestContext, Locator } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });

type Ingredient = { id: string; name: string; isStaple: boolean };

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const { token } = (await (await request.get("/api/auth/antiforgery")).json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

/** Creates a 4-serving recipe through the API: 500 g of one ingredient, another one to taste, three steps. */
async function createRecipe(request: APIRequestContext, title: string): Promise<{ id: string; household: string; ingredients: [Ingredient, Ingredient] }> {
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const household = households[0]!.id;
  const all = (await (await request.get(`/api/households/${household}/ingredients`)).json()) as Ingredient[];
  const plain = all.filter((i) => !i.isStaple);
  expect(plain.length).toBeGreaterThan(1);
  const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
  const gram = units.find((u) => u.code === "g")!;
  const res = await request.post(`/api/households/${household}/recipes`, {
    headers: await csrf(request),
    data: {
      title,
      description: null,
      servings: 4,
      prepMinutes: 5,
      cookMinutes: 10,
      steps: [{ text: "Forste steg" }, { text: "Andre steg" }, { text: "Tredje steg" }],
      ingredients: [
        { ingredientId: plain[0]!.id, quantity: 500, unitId: gram.id, note: null },
        { ingredientId: plain[1]!.id, quantity: null, unitId: null, note: null },
      ],
      tagIds: [],
    },
  });
  expect(res.status(), await res.text()).toBe(201);
  return { id: ((await res.json()) as { id: string }).id, household, ingredients: [plain[0]!, plain[1]!] };
}

async function expectTouchTarget(locator: Locator, minimum: number) {
  const box = (await locator.boundingBox())!;
  expect(box.height, "height").toBeGreaterThanOrEqual(minimum);
  expect(box.width, "width").toBeGreaterThanOrEqual(minimum);
}

test("opens a recipe, cooks it step by step, scales the servings via the API and exits", async ({ page, request }, testInfo) => {
  const title = `Cook ${uniqueStamp(testInfo)}`;
  const recipe = await createRecipe(request, title);
  try {
    await page.goto(`/recipes/${recipe.id}`);
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await page.getByRole("link", { name: "Start cooking" }).click();
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipe.id}/cook$`));

    const progress = page.getByTestId("cook-progress");
    const step = page.getByTestId("cook-step");
    const next = page.getByRole("button", { name: "Next step" });
    const previous = page.getByRole("button", { name: "Previous step" });

    await expect(progress).toHaveText("Step 1 of 3");
    await expect(step).toContainText("Forste steg");
    await expect(previous).toBeDisabled();

    // Targets: the step buttons are at least 56px, the other controls at least 44px, at 390px without sideways scroll.
    await expectTouchTarget(next, 56);
    await expectTouchTarget(page.getByRole("button", { name: "Show ingredients" }), 56);
    await expectTouchTarget(page.getByTestId("cook-exit"), 44);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
    // Very large text.
    const fontSize = await step.locator(".cook-text").evaluate((el) => parseFloat(getComputedStyle(el).fontSize));
    expect(fontSize).toBeGreaterThanOrEqual(24);

    await next.click();
    await expect(progress).toHaveText("Step 2 of 3");
    await expect(step).toContainText("Andre steg");
    await expectTouchTarget(previous, 56);
    await previous.click();
    await expect(progress).toHaveText("Step 1 of 3");
    await page.keyboard.press("ArrowRight");
    await expect(progress).toHaveText("Step 2 of 3");

    // Ingredients for the chosen servings come from the API (scaled by the Domain logic): 4 -> 6 servings = 750 g.
    await page.getByRole("button", { name: "Show ingredients" }).click();
    const list = page.locator(".cook-ingredient-list");
    await expect(list).toContainText("500 g");
    await expect(list).toContainText(recipe.ingredients[0].name);
    const more = page.getByRole("button", { name: "More servings" });
    await expectTouchTarget(more, 44);
    await expectTouchTarget(page.getByRole("button", { name: "Fewer servings" }), 44);
    await more.click();
    await more.click();
    await expect(page.getByTestId("servings-value")).toHaveText("6");
    await expect(list).toContainText("750 g");
    await expect(list).toContainText(recipe.ingredients[1].name); // to taste: still listed, no amount
    await more.click();
    await more.click();
    await expect(list).toContainText("1 kg"); // 1000 g reads as 1 kg
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);

    await next.click();
    await expect(progress).toHaveText("Step 3 of 3");
    await expect(step).toContainText("Tredje steg");
    await page.getByRole("button", { name: "Finish" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Bon appetit!" })).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);

    await page.getByRole("button", { name: "Back to the steps" }).click();
    await expect(progress).toHaveText("Step 3 of 3");
    await page.getByTestId("cook-exit").click();
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipe.id}$`));
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
  } finally {
    await request.delete(`/api/households/${recipe.household}/recipes/${recipe.id}`, { headers: await csrf(request) });
  }
});

test("the recipe page scales through the API too and carries its servings into cooking mode", async ({ page, request }, testInfo) => {
  const title = `Cook scale ${uniqueStamp(testInfo)}`;
  const recipe = await createRecipe(request, title);
  try {
    await page.goto(`/recipes/${recipe.id}`);
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    const more = page.getByRole("button", { name: "More servings" });
    await more.click();
    await more.click();
    await expect(page.locator(".recipe-ingredients")).toContainText("750 g");
    await page.getByRole("link", { name: "Start cooking" }).click();
    await expect(page.getByTestId("cook-step")).toBeVisible();
    await page.getByRole("button", { name: "Show ingredients" }).click();
    await expect(page.getByTestId("servings-value")).toHaveText("6");
    await expect(page.locator(".cook-ingredient-list")).toContainText("750 g");
  } finally {
    await request.delete(`/api/households/${recipe.household}/recipes/${recipe.id}`, { headers: await csrf(request) });
  }
});

test("swipes between steps on a touch screen", async ({ page, request }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile-390", "touch input is only configured for the mobile project");
  const title = `Cook swipe ${uniqueStamp(testInfo)}`;
  const recipe = await createRecipe(request, title);
  try {
    await page.goto(`/recipes/${recipe.id}/cook`);
    const progress = page.getByTestId("cook-progress");
    await expect(progress).toHaveText("Step 1 of 3");
    const area = page.locator(".cook");
    const swipe = (fromX: number, toX: number) =>
      area.evaluate(
        (el, [from, to]) => {
          const touch = (x: number) => new Touch({ identifier: 1, target: el, clientX: x, clientY: 300 });
          el.dispatchEvent(new TouchEvent("touchstart", { bubbles: true, touches: [touch(from!)], changedTouches: [touch(from!)] }));
          el.dispatchEvent(new TouchEvent("touchend", { bubbles: true, touches: [], changedTouches: [touch(to!)] }));
        },
        [fromX, toX],
      );
    await swipe(300, 80);
    await expect(progress).toHaveText("Step 2 of 3");
    await swipe(80, 300);
    await expect(progress).toHaveText("Step 1 of 3");
  } finally {
    await request.delete(`/api/households/${recipe.household}/recipes/${recipe.id}`, { headers: await csrf(request) });
  }
});
