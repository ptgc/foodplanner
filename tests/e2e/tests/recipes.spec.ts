import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test } from "./fixtures";
import { solidPng } from "./png";

test.use({ storageState: adminState });

type Tag = { id: string; name: string; kind: "Category" | "Tag" };

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

async function createRecipe(
  request: APIRequestContext,
  h: string,
  recipe: { title: string; tagIds: string[]; ingredientId: string; unitId: string; prep: number; cook: number },
): Promise<string> {
  const res = await request.post(`/api/households/${h}/recipes`, {
    headers: await csrf(request),
    data: {
      title: recipe.title,
      description: null,
      servings: 4,
      prepMinutes: recipe.prep,
      cookMinutes: recipe.cook,
      steps: [{ text: `Lag ${recipe.title}` }],
      ingredients: [{ ingredientId: recipe.ingredientId, quantity: 250, unitId: recipe.unitId, note: null }],
      tagIds: recipe.tagIds,
    },
  });
  expect(res.status(), await res.text()).toBe(201);
  return ((await res.json()) as { id: string }).id;
}

async function uploadImage(request: APIRequestContext, h: string, recipeId: string) {
  const res = await request.put(`/api/households/${h}/recipes/${recipeId}/image`, {
    headers: await csrf(request),
    multipart: { file: { name: "tiny.png", mimeType: "image/png", buffer: solidPng() } },
  });
  expect(res.status(), await res.text()).toBe(200);
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("filter by tag and search, then open a recipe", async ({ page }, testInfo) => {
  const request = page.request;
  const h = await householdId(request);
  const run = `${testInfo.project.name}-${Date.now()}`;
  const nameA = `E2E Pannekaker ${run}`;
  const nameB = `E2E Pasta ${run}`;

  const tags = (await (await request.get(`/api/households/${h}/tags`)).json()) as Tag[];
  const plain = tags.filter((t) => t.kind === "Tag");
  expect(plain.length, "needs two seeded tags").toBeGreaterThanOrEqual(2);
  const [tagA, tagB] = [plain[0]!, plain[1]!];
  const ingredients = (await (await request.get(`/api/households/${h}/ingredients`)).json()) as { id: string; name: string }[];
  const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
  const unit = units.find((u) => u.code === "g") ?? units[0]!;

  const created: string[] = [];
  try {
    created.push(
      await createRecipe(request, h, { title: nameA, tagIds: [tagA.id], ingredientId: ingredients[0]!.id, unitId: unit.id, prep: 5, cook: 10 }),
      await createRecipe(request, h, { title: nameB, tagIds: [tagB.id], ingredientId: ingredients[0]!.id, unitId: unit.id, prep: 20, cook: 15 }),
    );

    await uploadImage(request, h, created[0]!);

    await page.goto("/recipes");
    await expect(page.getByRole("heading", { name: "Recipes", level: 1 })).toBeVisible();

    // Both recipes of this run match the search; the grid shows only them.
    await page.getByLabel("Search recipes").fill(run);
    const cardA = page.getByTestId("recipe-card").filter({ hasText: nameA });
    const cardB = page.getByTestId("recipe-card").filter({ hasText: nameB });
    await expect(page.getByTestId("recipe-card")).toHaveCount(2);
    await expect(page).toHaveURL(/q=/);
    await expect(cardA.getByText("15 min")).toBeVisible();
    // The recipe with a picture shows it (lazy, scrolled into view); the other one a placeholder tile.
    await expect.poll(() => cardA.locator("img").evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0)).toBe(true);
    await expect(cardB.locator("img")).toHaveCount(0);
    await expect(cardB.getByText("35 min")).toBeVisible();

    if ((page.viewportSize()?.width ?? 0) <= 390) {
      await noHorizontalScroll(page);
      const a = (await cardA.boundingBox())!;
      const b = (await cardB.boundingBox())!;
      expect(Math.abs(a.y - b.y), "two columns at 390px: cards side by side").toBeLessThan(2);
      expect(b.x).toBeGreaterThan(a.x + a.width - 2);
      expect(a.height).toBeGreaterThanOrEqual(44);
      const chip = await page.getByRole("button", { name: tagA.name, exact: true }).boundingBox();
      expect(chip!.height).toBeGreaterThanOrEqual(44);
    }

    // Tag chip: only the recipe with that tag stays.
    await page.getByRole("button", { name: tagA.name, exact: true }).click();
    await expect(page.getByRole("button", { name: tagA.name, exact: true })).toHaveAttribute("aria-pressed", "true");
    await expect(page.getByTestId("recipe-card")).toHaveCount(1);
    await expect(cardA).toBeVisible();
    await expect(page).toHaveURL(new RegExp(`tag=${tagA.id}`));

    // The filters survive a reload (they live in the URL).
    await page.reload();
    await expect(page.getByTestId("recipe-card")).toHaveCount(1);
    await expect(cardA).toBeVisible();
    await expect(page.getByLabel("Search recipes")).toHaveValue(run);

    // Two tags are AND: no recipe has both, so we get the friendly empty state and a way out.
    await page.getByRole("button", { name: tagB.name, exact: true }).click();
    await expect(page.getByText("No recipes match")).toBeVisible();
    await page.getByRole("button", { name: "Clear filters" }).click();
    await expect(page.getByLabel("Search recipes")).toHaveValue("");
    await expect(page).toHaveURL(/\/recipes$/);

    // Search alone narrows to the other recipe.
    await page.getByLabel("Search recipes").fill(nameB);
    await expect(page.getByTestId("recipe-card")).toHaveCount(1);
    await expect(cardB).toBeVisible();

    // Max time chip: B takes 35 min, so 30 min hides it.
    await page.getByRole("button", { name: "Up to 30 min" }).click();
    await expect(page.getByText("No recipes match")).toBeVisible();
    await page.getByRole("button", { name: "Up to 60 min" }).click();
    await expect(cardB).toBeVisible();

    // Open it.
    await cardB.click();
    await expect(page).toHaveURL(new RegExp(`/recipes/${created[1]}$`));
    await expect(page.getByRole("heading", { name: nameB, level: 1 })).toBeVisible();
    await expect(page.getByText("35 min")).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: ingredients[0]!.name }).filter({ hasText: "250 g" })).toBeVisible();
    await expect(page.getByText(`Lag ${nameB}`)).toBeVisible();
    await page.getByRole("link", { name: "All recipes" }).click();
    await expect(page.getByRole("heading", { name: "Recipes", level: 1 })).toBeVisible();
  } finally {
    for (const id of created) {
      await request.delete(`/api/households/${h}/recipes/${id}`, { headers: await csrf(request) });
    }
  }
});

test("the recipe page is reachable from the main navigation", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Recipes" }).click();
  await expect(page).toHaveURL(/\/recipes$/);
  await expect(page.getByRole("heading", { name: "Recipes", level: 1 })).toBeVisible();
  await noHorizontalScroll(page);
});
