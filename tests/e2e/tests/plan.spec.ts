import type { APIRequestContext, Locator, Page } from "@playwright/test";
import { adminState, expect, test } from "./fixtures";

test.use({ storageState: adminState });

// Each test plans in its own far-future week (2032), so it does not depend on demo data and the two projects
// (mobile-390, desktop) can run side by side. Entries, recipes and the generated list are cleaned up afterwards.
const baseMonday = Date.UTC(2032, 2, 1); // 2032-03-01 is a Monday

function weekFor(project: string, n: number): string {
  const offsetWeeks = n + (project === "desktop" ? 10 : 0);
  return new Date(baseMonday + offsetWeeks * 7 * 86_400_000).toISOString().slice(0, 10);
}

const dayOf = (week: string, offset: number) => new Date(Date.parse(week) + offset * 86_400_000).toISOString().slice(0, 10);

async function householdId(request: APIRequestContext): Promise<string> {
  const res = await request.get("/api/households");
  expect(res.ok()).toBe(true);
  const households = (await res.json()) as { id: string }[];
  expect(households.length).toBeGreaterThan(0);
  return households[0]!.id;
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  const { token } = (await res.json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

type Ingredient = { id: string; name: string; isStaple: boolean };

/** Two ingredients that are not staples (staples go to "check you have" and may be hidden). */
async function twoIngredients(request: APIRequestContext, h: string): Promise<[Ingredient, Ingredient]> {
  const all = (await (await request.get(`/api/households/${h}/ingredients`)).json()) as Ingredient[];
  const plain = all.filter((i) => !i.isStaple);
  expect(plain.length).toBeGreaterThan(1);
  return [plain[0]!, plain[1]!];
}

async function createRecipe(request: APIRequestContext, h: string, title: string, ingredientId: string): Promise<string> {
  const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
  const unit = units.find((u) => u.code === "g") ?? units[0]!;
  const res = await request.post(`/api/households/${h}/recipes`, {
    headers: await csrf(request),
    data: {
      title,
      description: null,
      servings: 4,
      prepMinutes: 5,
      cookMinutes: 10,
      steps: [{ text: `Lag ${title}` }],
      ingredients: [{ ingredientId, quantity: 250, unitId: unit.id, note: null }],
      tagIds: [],
    },
  });
  expect(res.status(), await res.text()).toBe(201);
  return ((await res.json()) as { id: string }).id;
}

type PlanResponse = { entries: { id: string; date: string; recipeTitle: string; servings: number }[] };

async function planOf(request: APIRequestContext, h: string, week: string): Promise<PlanResponse> {
  const res = await request.get(`/api/households/${h}/meal-plans/${week}`);
  expect(res.ok()).toBe(true);
  return (await res.json()) as PlanResponse;
}

async function cleanUp(request: APIRequestContext, h: string, week: string, recipeIds: string[]) {
  for (const entry of (await planOf(request, h, week)).entries) {
    await request.delete(`/api/households/${h}/meal-plans/${week}/entries/${entry.id}`, { headers: await csrf(request) });
  }
  // Regenerating from the now empty week clears the generated items again.
  await request.post(`/api/households/${h}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
  for (const id of recipeIds) {
    await request.delete(`/api/households/${h}/recipes/${id}`, { headers: await csrf(request) });
  }
}

const day = (page: Page, date: string) => page.locator(`[data-testid="planner-day"][data-date="${date}"]`);
const plus = (page: Page, date: string) => day(page, date).locator(".planner-add");

/** One tap on a touch project (counted), a click on desktop. */
function tapper(page: Page, hasTouch: boolean) {
  const counter = { taps: 0 };
  const tap = async (target: Locator) => {
    counter.taps++;
    if (hasTouch) await target.tap();
    else await target.click();
  };
  return { counter, tap };
}

test("Kid can pick a meal in <=3 taps", async ({ page }, testInfo) => {
  test.skip(!testInfo.project.use.hasTouch, "counts touch taps at 390px");
  const week = weekFor(testInfo.project.name, 0);
  const h = await householdId(page.request);
  const [ingredient] = await twoIngredients(page.request, h);
  const title = `0 E2E plan tap ${Date.now()}`;
  const recipeId = await createRecipe(page.request, h, title, ingredient.id);
  try {
    await page.goto(`/plan?week=${week}`);
    await expect(page.getByRole("heading", { name: "Meal plan", level: 1 })).toBeVisible();
    const wednesday = dayOf(week, 2);
    await expect(day(page, wednesday)).toBeVisible();

    // Kid-sized targets and no sideways scroll at 390px.
    expect((await plus(page, wednesday).boundingBox())!.height).toBeGreaterThanOrEqual(56);
    expect((await plus(page, wednesday).boundingBox())!.width).toBeGreaterThanOrEqual(56);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);

    const { counter, tap } = tapper(page, true);
    await tap(plus(page, wednesday)); // 1: the day's "+"
    const card = page.getByRole("button", { name: `Add ${title}` });
    await expect(card).toBeVisible();
    expect((await card.boundingBox())!.height).toBeGreaterThanOrEqual(56);
    await tap(card); // 2: the recipe, which adds it straight away

    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(day(page, wednesday).getByTestId("planner-entry").filter({ hasText: title })).toBeVisible();
    expect(counter.taps, "taps needed to add a meal").toBeLessThanOrEqual(3);
    expect(counter.taps).toBe(2);
    testInfo.annotations.push({ type: "taps", description: String(counter.taps) });

    const stored = await planOf(page.request, h, week);
    expect(stored.entries.map((e) => [e.date, e.recipeTitle])).toEqual([[wednesday, title]]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
  } finally {
    await cleanUp(page.request, h, week, [recipeId]);
  }
});

test("Playwright test for picking flow: pick, change servings, remove, generate the list", async ({ page }, testInfo) => {
  const week = weekFor(testInfo.project.name, 1);
  const h = await householdId(page.request);
  const [ingA, ingB] = await twoIngredients(page.request, h);
  const stamp = Date.now();
  const titleA = `0 E2E plan A ${stamp}`;
  const titleB = `0 E2E plan B ${stamp}`;
  const recipeIds = [await createRecipe(page.request, h, titleA, ingA.id), await createRecipe(page.request, h, titleB, ingB.id)];
  const { tap } = tapper(page, !!testInfo.project.use.hasTouch);
  try {
    await page.goto(`/plan?week=${week}`);
    const tuesday = dayOf(week, 1);
    const thursday = dayOf(week, 3);

    // Pick two meals on two days.
    await tap(plus(page, tuesday));
    await tap(page.getByRole("button", { name: `Add ${titleA}` }));
    await expect(day(page, tuesday).getByTestId("planner-entry").filter({ hasText: titleA })).toBeVisible();
    await tap(plus(page, thursday));
    await tap(page.getByRole("button", { name: `Add ${titleB}` }));
    const entryB = day(page, thursday).getByTestId("planner-entry").filter({ hasText: titleB });
    await expect(entryB).toBeVisible();

    // The picker shows who picked: the avatar has an accessible name.
    const entryA = day(page, tuesday).getByTestId("planner-entry").filter({ hasText: titleA });
    await expect(entryA.getByRole("img", { name: /^Picked by / })).toBeVisible();
    await expect(entryA.getByTestId("servings")).toHaveText("4 servings");

    // Servings: one more, then the server agrees.
    await tap(entryA.getByRole("button", { name: `More servings of ${titleA}` }));
    await expect(entryA.getByTestId("servings")).toHaveText("5 servings");
    await expect
      .poll(async () => (await planOf(page.request, h, week)).entries.find((e) => e.recipeTitle === titleA)?.servings)
      .toBe(5);

    // Remove the second meal.
    await tap(entryB.getByRole("button", { name: new RegExp(`^Remove ${titleB} from`) }));
    await expect(entryB).toHaveCount(0);
    expect((await planOf(page.request, h, week)).entries.map((e) => e.recipeTitle)).toEqual([titleA]);

    // Generate the shopping list: it opens with the remaining meal's ingredient only.
    await tap(page.getByRole("button", { name: "Make shopping list" }));
    await expect(page).toHaveURL(new RegExp(`/shopping\\?week=${week}$`));
    await expect(page.getByRole("heading", { name: "Shopping list", level: 1 })).toBeVisible();
    await expect(page.getByTestId("shop-item").filter({ hasText: ingA.name }).first()).toBeVisible();
    await expect(page.getByTestId("shop-item").filter({ hasText: ingB.name })).toHaveCount(0);
  } finally {
    await cleanUp(page.request, h, week, recipeIds);
  }
});
