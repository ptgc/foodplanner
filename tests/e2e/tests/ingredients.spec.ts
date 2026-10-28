import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

/** Letters only (the matcher and the name rules see one plain word), different for every parallel copy. */
function inventedName(stamp: string): string {
  return "Zqx" + stamp.replace(/\d/g, (d) => "abcdefghij"[Number(d)]!).replace(/-/g, "");
}

async function household(request: APIRequestContext): Promise<string> {
  const households = (await (await request.get("/api/households")).json()) as { id: string; role: string }[];
  expect(["Owner", "Adult"]).toContain(households[0]!.role);
  return households[0]!.id;
}

async function createOwn(request: APIRequestContext, h: string, name: string): Promise<string> {
  const res = await request.post(`/api/households/${h}/ingredients`, {
    headers: await csrf(request),
    data: { name, dimension: "Mass", isStaple: false },
  });
  expect(res.status(), await res.text()).toBe(201);
  return ((await res.json()) as { id: string }).id;
}

test("an Adult merges a clumsy own ingredient into a catalog one and the recipe shows the catalog ingredient", async ({ page }, testInfo) => {
  const request = page.request;
  const h = await household(request);
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const name = inventedName(uniqueStamp(testInfo));
  const ingredientId = await createOwn(request, h, name);
  let recipeId: string | undefined;

  try {
    const catalog = (await (await request.get(`/api/households/${h}/ingredients?search=Rice`, { headers: { "Accept-Language": "en" } })).json()) as {
      id: string;
      name: string;
      isGlobal: boolean;
    }[];
    const rice = catalog.find((i) => i.name === "Rice" && i.isGlobal);
    expect(rice, "the catalog has Rice").toBeTruthy();
    const unit = ((await (await request.get("/api/units")).json()) as { id: string; code: string }[]).find((u) => u.code === "g")!;
    const created = await request.post(`/api/households/${h}/recipes`, {
      headers: await csrf(request),
      data: {
        title: `E2E Merge ${testInfo.project.name} ${uniqueStamp(testInfo)}`,
        servings: 4,
        ingredients: [{ ingredientId, quantity: 200, unitId: unit.id, note: null }],
      },
    });
    expect(created.status(), await created.text()).toBe(201);
    recipeId = ((await created.json()) as { id: string }).id;

    await page.goto(`/recipes/${recipeId}`);
    await expect(page.getByText(name).first()).toBeVisible();

    // Household settings link to the screen.
    await page.goto("/household");
    await page.getByRole("link", { name: "Manage your ingredients" }).click();
    await expect(page).toHaveURL(/\/ingredients$/);
    await expect(page.getByRole("heading", { name: "Your ingredients", level: 1 })).toBeVisible();

    await page.getByLabel("Search your ingredients").fill(name);
    const row = page.getByTestId("ingredient-row").filter({ hasText: name });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText("Recipes 1");

    if (mobile) {
      await noHorizontalScroll(page);
      for (const button of await row.getByRole("button").all()) {
        expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      }
    }

    // Deleting a used ingredient is refused with the counts and a hint to merge.
    await row.getByRole("button", { name: `Delete ${name}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete ingredient" }).click();
    await expect(row.getByRole("alert")).toContainText("Merge it into another ingredient");
    await expect(row.getByRole("alert")).toContainText("recipes 1");

    // Merge into the catalog's Rice.
    await row.getByRole("button", { name: `Merge ${name} into another ingredient` }).click();
    await row.getByLabel(`Find the ingredient to replace ${name} with`).fill("Rice");
    await row.getByRole("button", { name: "Merge into Rice", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(`Replace ${name} with Rice?`);
    await dialog.getByRole("button", { name: "Merge", exact: true }).click();
    await expect(page.getByTestId("ingredients-notice")).toContainText(`${name} was merged into Rice.`);
    await expect(page.getByTestId("ingredient-row").filter({ hasText: name })).toHaveCount(0);

    // The recipe now has the catalog ingredient.
    await page.goto(`/recipes/${recipeId}`);
    await expect(page.getByText("Rice").first()).toBeVisible();
    await expect(page.getByText(name)).toHaveCount(0);
  } finally {
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
    await request.delete(`/api/households/${h}/ingredients/${ingredientId}`, { headers: await csrf(request) });
  }
});

test("an Adult renames and deletes an unused own ingredient", async ({ page }, testInfo) => {
  const request = page.request;
  const h = await household(request);
  const name = inventedName(uniqueStamp(testInfo));
  const renamed = `${name}x`;
  const ingredientId = await createOwn(request, h, name);

  try {
    await page.goto("/ingredients");
    await page.getByLabel("Search your ingredients").fill(name);
    const row = page.getByTestId("ingredient-row").filter({ hasText: name });
    await expect(row).toContainText("Not used anywhere");

    await row.getByRole("button", { name: `Rename ${name}` }).click();
    await row.getByLabel(`New name for ${name}`).fill(renamed);
    await row.getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("ingredients-notice")).toContainText(`Renamed to ${renamed}.`);

    await page.getByLabel("Search your ingredients").fill(renamed);
    const renamedRow = page.getByTestId("ingredient-row").filter({ hasText: renamed });
    await expect(renamedRow).toHaveCount(1);
    await renamedRow.getByRole("button", { name: `Delete ${renamed}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete ingredient" }).click();
    await expect(page.getByTestId("ingredients-notice")).toContainText(`${renamed} was deleted.`);
    await expect(page.getByTestId("ingredient-row")).toHaveCount(0);

    const gone = await request.get(`/api/households/${h}/ingredients?search=${renamed}`);
    expect(await gone.json()).toEqual([]);
  } finally {
    await request.delete(`/api/households/${h}/ingredients/${ingredientId}`, { headers: await csrf(request) });
  }
});
