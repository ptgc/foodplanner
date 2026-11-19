import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(180_000);

// T-100: "What can I make?" from the pantry. Works in its OWN household (named with the run tag), so the architect's real
// pantry, plan and list are never touched. The household, with everything in it, is deleted in `finally`; the run's
// teardown (T-083) deletes it too if that fails.
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

test("the pantry suggests a ready recipe and one missing a thing; cook today, then add the missing to the list", async ({ page }, testInfo) => {
  const request = page.request;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const stamp = uniqueStamp(testInfo);
  const pasta = inventedName(stamp, "pasta");
  const cheese = inventedName(stamp, "cheese");
  let h: string | undefined;

  const touchTarget = async (locator: ReturnType<Page["locator"]>) => {
    if (!mobile) return;
    expect((await locator.boundingBox())!.height, "touch target").toBeGreaterThanOrEqual(44);
  };

  try {
    const created = await request.post("/api/households", { headers: await csrf(request), data: { name: `E2E suggest ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const me = (await (await request.get("/api/auth/me")).json()) as { userId: string };
    await page.addInitScript(([key, id]) => window.localStorage.setItem(key!, id!), [`foodplanner.activeHousehold.${me.userId}`, h]);

    const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
    const unit = (code: string) => units.find((u) => u.code === code)!.id;
    const ids: Record<string, string> = {};
    for (const name of [pasta, cheese]) {
      const res = await request.post(`/api/households/${h}/ingredients`, { headers: await csrf(request), data: { name, dimension: "Mass", isStaple: false } });
      expect(res.status(), await res.text()).toBe(201);
      ids[name] = ((await res.json()) as { id: string }).id;
    }
    const makeRecipe = async (title: string, lines: string[]) => {
      const res = await request.post(`/api/households/${h}/recipes`, {
        headers: await csrf(request),
        data: {
          title,
          description: null,
          servings: 4,
          prepMinutes: 5,
          cookMinutes: 10,
          steps: [{ text: "Cook" }],
          ingredients: lines.map((name) => ({ ingredientId: ids[name], quantity: 200, unitId: unit("g"), note: null })),
          tagIds: [],
        },
      });
      expect(res.status(), await res.text()).toBe(201);
    };
    // 1. The empty pantry (no recipes yet either) shows the friendly state.
    await page.goto("/what-can-i-make");
    await expect(page.getByRole("heading", { name: "What can I make?", level: 1 })).toBeVisible();
    await expect(page.getByTestId("suggest-empty-pantry")).toBeVisible();

    const readyTitle = `Plain ${stamp}`;
    const almostTitle = `Cheesy ${stamp}`;
    await makeRecipe(readyTitle, [pasta]);
    await makeRecipe(almostTitle, [pasta, cheese]);

    // 2. The pantry has pasta (500 g), no cheese.
    const put = await request.put(`/api/households/${h}/pantry/${ids[pasta]}`, {
      headers: await csrf(request),
      data: { quantity: 500, unitId: unit("g"), expiresAt: null },
    });
    expect(put.status(), await put.text()).toBeLessThan(300);

    // The entry point on the pantry screen.
    await page.goto("/pantry");
    const link = page.getByTestId("suggest-link");
    await touchTarget(link);
    await link.click();
    await expect(page.getByRole("heading", { name: "What can I make?", level: 1 })).toBeVisible();

    const ready = page.getByTestId("suggest-ready");
    const almost = page.getByTestId("suggest-almost");
    const readyCard = ready.getByTestId("suggest-card").filter({ hasText: readyTitle });
    const almostCard = almost.getByTestId("suggest-card").filter({ hasText: almostTitle });
    await expect(readyCard).toHaveCount(1);
    await expect(almostCard).toHaveCount(1);
    await expect(almostCard.getByTestId("suggest-missing")).toContainText(`missing: 200 g ${cheese}`);
    if (mobile) await noHorizontalScroll(page);

    // 3. Cook the one that misses cheese today: the badge shows, then add the missing to the list.
    const cook = almostCard.getByRole("button", { name: `Cook today: ${almostTitle}` });
    await touchTarget(cook);
    await cook.click();
    await expect(almostCard.getByTestId("suggest-planned-note")).toHaveText("Added to today's plan.");
    await expect(almostCard.getByTestId("suggest-planned-today")).toBeVisible();

    const addMissing = almostCard.getByRole("button", { name: `Add missing to the shopping list: ${almostTitle}` });
    await touchTarget(addMissing);
    await addMissing.click();
    const note = almostCard.getByTestId("suggest-added-note");
    await expect(note).toContainText("Added to this week's shopping list");
    await expect(note).toContainText(cheese);
    await touchTarget(note.getByRole("link", { name: "Open the shopping list" }));
    if (mobile) await noHorizontalScroll(page);

    // 4. The shopping list now has the cheese.
    await note.getByRole("link", { name: "Open the shopping list" }).click();
    await expect(page.getByTestId("shop-item").filter({ hasText: cheese })).toBeVisible();
  } finally {
    // Deleting the household removes its pantry, plan, list, recipes and own ingredients with it.
    if (h) await request.delete(`/api/households/${h}`, { headers: await csrf(request) });
  }
});
