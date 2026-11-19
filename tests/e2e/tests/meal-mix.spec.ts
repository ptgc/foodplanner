import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// T-090: the household sets "at least one fish and one vegetarian dinner a week"; after five meat dinners are planned, the
// planner greys out everything but fish and vegetarian for the last two days, with a short reason. The rules are household-wide,
// so the test works in its own household (named with the run tag) and never touches the architect's real one. The household,
// with everything in it, is deleted in `finally`; the run's teardown (T-083) deletes it too if that fails.
const baseMonday = Date.UTC(2037, 2, 2); // 2037-03-02 is a Monday

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

const dayOf = (week: string, offset: number) => new Date(Date.parse(week) + offset * 86_400_000).toISOString().slice(0, 10);
const day = (page: Page, date: string) => page.locator(`[data-testid="planner-day"][data-date="${date}"]`);

test("rules at least one fish and one vegetarian: after five meat dinners only those two are offered", async ({ page }, testInfo) => {
  const request = page.request;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  const stamp = uniqueStamp(testInfo);
  let h: string | undefined;

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E mix ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
    await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);

    const tags = (await (await request.get(`/api/households/${h}/tags`)).json()) as { id: string; name: string; kind: string }[];
    const dinner = tags.find((t) => /^(Middag|Dinner)$/.test(t.name) && t.kind === "Category")!; // the name follows the request language
    const ingredients = (await (await request.get(`/api/households/${h}/ingredients`)).json()) as { id: string; isStaple: boolean }[];
    const ingredient = ingredients.find((i) => !i.isStaple)!;
    const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
    const gram = units.find((u) => u.code === "g") ?? units[0]!;

    const recipe = async (name: string, foodType: string) => {
      const title = `${name} ${stamp}`;
      const res = await request.post(`/api/households/${h}/recipes`, {
        headers: await csrf(request),
        data: {
          title,
          description: null,
          servings: 4,
          prepMinutes: 5,
          cookMinutes: 10,
          steps: [{ text: `Lag ${title}` }],
          ingredients: [{ ingredientId: ingredient.id, quantity: 250, unitId: gram.id, note: null }],
          tagIds: [dinner.id],
          foodType,
        },
      });
      expect(res.status(), await res.text()).toBe(201);
      return { id: ((await res.json()) as { id: string }).id, title };
    };
    const beef = await recipe("Biff", "Beef");
    const chicken = await recipe("Kylling", "Chicken");
    const fish = await recipe("Laks", "Fish");
    const vegetarian = await recipe("Grønnsakssuppe", "Vegetarian");

    // The rules: set on the household page (at least one fish, at least one vegetarian).
    await page.goto("/household");
    const card = page.getByTestId("meal-mix-rules");
    await expect(card.getByRole("heading", { name: "Weekly meal mix", level: 2 })).toBeVisible();
    const more = (type: string) => card.getByRole("button", { name: `Increase the minimum for ${type}` });
    await more("Fish").click();
    await more("Vegetarian").click();
    if (mobile) {
      expect((await more("Fish").boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
      expect((await more("Fish").boundingBox())!.width, "touch target").toBeGreaterThanOrEqual(44);
      expect((await card.getByRole("button", { name: "Save the rules" }).boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
      await noHorizontalScroll(page);
    }
    await card.getByRole("button", { name: "Save the rules" }).click();
    await expect(card.getByTestId("mix-saved")).toBeVisible();
    const rules = (await (await request.get(`/api/households/${h}/meal-mix-rules`)).json()) as { rules: { foodType: string; minPerWeek: number }[] };
    expect(rules.rules).toEqual([
      { foodType: "Fish", minPerWeek: 1 },
      { foodType: "Vegetarian", minPerWeek: 1 },
    ]);

    // Five meat dinners, Monday to Friday.
    for (let i = 0; i < 5; i++) {
      const res = await request.post(`/api/households/${h}/meal-plans/${week}/entries`, {
        headers: await csrf(request),
        data: { date: dayOf(week, i), recipeId: beef.id, servings: null },
      });
      expect(res.status(), await res.text()).toBe(201);
    }

    await page.goto(`/plan?week=${week}`);
    const saturday = dayOf(week, 5);
    const sunday = dayOf(week, 6);
    await expect(day(page, saturday)).toBeVisible();

    const cardFor = (recipeTitle: string) => page.getByRole("button", { name: `Add ${recipeTitle}` });
    // Saturday: only fish and vegetarian are offered, meat is greyed out with the reason.
    await day(page, saturday).locator(".planner-add").click();
    for (const meat of [beef, chicken]) {
      await expect(cardFor(meat.title)).toHaveAttribute("data-allowed", "false");
      await expect(cardFor(meat.title).getByTestId("picker-reason")).toContainText("Does not fit the week's meal mix");
      await expect(cardFor(meat.title).getByTestId("picker-reason")).toContainText("Fish, Vegetarian");
    }
    for (const ok of [fish, vegetarian]) {
      await expect(cardFor(ok.title)).toHaveAttribute("data-allowed", "true");
      await expect(cardFor(ok.title).getByTestId("picker-reason")).toHaveCount(0);
    }
    if (mobile) {
      expect((await cardFor(fish.title).boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
      await noHorizontalScroll(page);
    }

    // Pick the fish: now only vegetarian fits Sunday.
    await cardFor(fish.title).click();
    await expect(day(page, saturday).getByTestId("planner-entry").filter({ hasText: fish.title })).toBeVisible();
    await day(page, sunday).locator(".planner-add").click();
    await expect(cardFor(vegetarian.title)).toHaveAttribute("data-allowed", "true");
    for (const blocked of [beef, chicken, fish]) {
      await expect(cardFor(blocked.title)).toHaveAttribute("data-allowed", "false");
    }
    await cardFor(vegetarian.title).click();
    await expect(day(page, sunday).getByTestId("planner-entry").filter({ hasText: vegetarian.title })).toBeVisible();
  } finally {
    // Deleting the household removes its rules, plan and recipes with it.
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});
