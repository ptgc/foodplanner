import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, saveRecipe, test, uniqueStamp } from "./fixtures";
import { noisyPng, solidPng } from "./png";

test.use({ storageState: adminState });
test.setTimeout(90_000);

type Named = { id: string; name: string };
type Tag = Named & { kind: "Category" | "Tag" };
type Unit = { id: string; code: string };

// The UI asks the API in English (en-US browser), so look names up the same way to find them on screen.
const english = { "Accept-Language": "en" };

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("an Adult creates a recipe through the form, sees it on the detail page and in the grid, edits it and deletes it", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string; role: string }[];
  const h = households[0]!.id;
  expect(["Owner", "Adult"], "the seeded admin can write recipes").toContain(households[0]!.role);

  const ingredients = (await (await request.get(`/api/households/${h}/ingredients?limit=20`, { headers: english })).json()) as Named[];
  const units = (await (await request.get("/api/units", { headers: english })).json()) as Unit[];
  const tags = (await (await request.get(`/api/households/${h}/tags`, { headers: english })).json()) as Tag[];
  const [first, second] = [ingredients[0]!, ingredients[1]!];
  const gram = units.find((u) => u.code === "g") ?? units[0]!;
  const tag = tags.find((x) => x.kind === "Tag")!;
  const title = `E2E Form ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;

  let recipeId: string | undefined;
  try {
    // The grid offers "New recipe" to an Adult.
    await page.goto("/recipes");
    await page.getByTestId("recipe-new-link").click();
    await expect(page).toHaveURL(/\/recipes\/new$/);
    await expect(page.getByRole("heading", { name: "New recipe", level: 1 })).toBeVisible();

    // Submitting the empty form is blocked on the client and the error summary takes focus.
    await page.getByRole("button", { name: "Save recipe" }).click();
    await expect(page.getByTestId("error-summary")).toBeFocused();
    await expect(page.getByLabel("Title")).toHaveAttribute("aria-invalid", "true");

    await page.getByLabel("Title").fill(title);
    await page.getByLabel("Description (optional)").fill("Made by the e2e test");
    await page.getByLabel("Servings", { exact: true }).fill("4");
    await page.getByLabel("Preparation (minutes)").fill("10");
    await page.getByLabel("Cooking (minutes)").fill("20");

    // Line 1: an ingredient from the catalog with an amount and a unit.
    const rows = page.getByTestId("ingredient-row");
    await rows.nth(0).getByLabel("Ingredient", { exact: true }).fill(first.name);
    await rows.nth(0).getByRole("button", { name: first.name, exact: true }).click();
    await rows.nth(0).getByLabel("Amount").fill("250");
    await rows.nth(0).getByLabel("Unit").selectOption(gram.id);

    // Line 2: a "to taste" line has neither amount nor unit.
    await page.getByRole("button", { name: "Add ingredient" }).click();
    await expect(rows).toHaveCount(2);
    await rows.nth(1).getByLabel("Ingredient", { exact: true }).fill(second.name);
    await rows.nth(1).getByRole("button", { name: second.name, exact: true }).click();
    await rows.nth(1).getByRole("checkbox", { name: "To taste" }).check();
    await expect(rows.nth(1).getByLabel("Amount")).toHaveCount(0);

    const steps = page.getByTestId("step-row");
    await steps.nth(0).getByRole("textbox").fill("First step");
    await page.getByRole("button", { name: "Add step" }).click();
    await steps.nth(1).getByRole("textbox").fill("Second step");

    await page.getByRole("button", { name: tag.name, exact: true }).click();
    await page.getByLabel("Picture file").setInputFiles({ name: "soup.png", mimeType: "image/png", buffer: solidPng() });
    await expect(page.getByTestId("picture-preview")).toBeVisible();

    if (mobile) {
      await noHorizontalScroll(page);
      for (const name of ["Save recipe", "Add step", "Add ingredient", "Move step 1 down", "Remove step 1", "Move ingredient 1 down"]) {
        const box = await page.getByRole("button", { name, exact: true }).boundingBox();
        expect(box!.height, name).toBeGreaterThanOrEqual(44);
        expect(box!.width, name).toBeGreaterThanOrEqual(44);
      }
    }

    await saveRecipe(page, page.getByRole("button", { name: "Save recipe" }));
    await expect(page).toHaveURL(/\/recipes\/[0-9a-f-]{36}$/, { timeout: 15_000 }); // the picture upload follows the save
    recipeId = page.url().split("/").pop();

    // Detail page: title, times, servings, tag, structured ingredients, steps, picture, edit control.
    await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
    await expect(page.getByText("30 min")).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: first.name }).filter({ hasText: "250 g" })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: second.name })).toBeVisible();
    await expect(page.getByText(tag.name, { exact: true }).first()).toBeVisible();
    const steps0 = page.locator(".recipe-steps li");
    await expect(steps0).toHaveText(["First step", "Second step"]);
    await expect.poll(() => page.locator(".recipe-image img").evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0)).toBe(true);
    if (mobile) await noHorizontalScroll(page);

    // The servings scaler changes the shown amounts only.
    await page.getByRole("button", { name: "More servings" }).click();
    await expect(page.getByTestId("servings-value")).toHaveText("5");
    // The API scales exactly (312.5 g) and rounds for display to whole grams, half away from zero.
    await expect(page.getByRole("listitem").filter({ hasText: first.name }).filter({ hasText: "313 g" })).toBeVisible();

    // It is in the grid, with its picture.
    await page.goto("/recipes");
    await page.getByLabel("Search recipes").fill(title);
    const card = page.getByTestId("recipe-card").filter({ hasText: title });
    await expect(card).toHaveCount(1);
    await expect.poll(() => card.locator("img").evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0)).toBe(true);

    // Edit: reorder the steps, change the servings.
    await card.click();
    await page.getByTestId("recipe-edit-link").click();
    await expect(page).toHaveURL(/\/edit$/);
    await expect(page.getByLabel("Title")).toHaveValue(title);
    await page.getByRole("button", { name: "Move step 1 down" }).click();
    await page.getByLabel("Servings", { exact: true }).fill("6");
    await saveRecipe(page, page.getByRole("button", { name: "Save recipe" }));
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipeId}$`), { timeout: 15_000 });
    await expect(page.locator(".recipe-steps li")).toHaveText(["Second step", "First step"]);
    await expect(page.getByTestId("servings-value")).toHaveText("6");

    // Delete with confirmation.
    await page.getByTestId("recipe-edit-link").click();
    await page.getByRole("button", { name: "Delete recipe" }).click();
    await expect(page.getByText(`Delete "${title}"? This cannot be undone.`)).toBeVisible();
    await page.getByRole("button", { name: "Yes, delete it" }).click();
    await expect(page).toHaveURL(/\/recipes$/);
    await page.getByLabel("Search recipes").fill(title);
    await expect(page.getByTestId("recipe-card")).toHaveCount(0);
    recipeId = undefined;
  } finally {
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});

test("T-081: a book source is set in the edit form and shown as plain text, not a link", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const h = households[0]!.id;
  const ingredients = (await (await request.get(`/api/households/${h}/ingredients?limit=20`, { headers: english })).json()) as Named[];
  const units = (await (await request.get("/api/units", { headers: english })).json()) as Unit[];
  const gram = units.find((u) => u.code === "g") ?? units[0]!;
  const title = `E2E Book ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const created = await request.post(`/api/households/${h}/recipes`, {
    headers: await csrf(request),
    data: {
      title,
      servings: 4,
      steps: [{ text: "Cook" }],
      ingredients: [{ ingredientId: ingredients[0]!.id, quantity: 100, unitId: gram.id, note: null }],
    },
  });
  expect(created.status(), await created.text()).toBe(201);
  const recipeId = ((await created.json()) as { id: string }).id;
  try {
    await page.goto(`/recipes/${recipeId}/edit`);
    await expect(page.getByRole("heading", { name: "Edit recipe", level: 1 })).toBeVisible();
    const book = page.getByTestId("source-kind-book");
    if (mobile) expect((await book.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await book.click();
    await page.getByLabel("Book or other source").fill("Kaptein Sabeltann, Matskatter fra De Syv Hav");
    if (mobile) await noHorizontalScroll(page);
    await saveRecipe(page, page.getByRole("button", { name: "Save recipe" }));
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipeId}$`), { timeout: 30_000 });

    await expect(page.getByTestId("recipe-source-text")).toHaveText("Source: Kaptein Sabeltann, Matskatter fra De Syv Hav");
    await expect(page.getByTestId("recipe-source-link")).toHaveCount(0);
    await expect(page.getByTestId("recipe-source-text").locator("a")).toHaveCount(0);
  } finally {
    await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});

// T-080: nginx capped request bodies at its 1 MB default, so every normal phone photo failed with an HTML 413.
test("a phone-sized photo uploads through the web container, and a too-large one is refused", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const h = households[0]!.id;
  const title = `E2E Photo size ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
  let recipeId: string | undefined;
  try {
    await page.goto("/recipes/new");
    await page.getByLabel("Title").fill(title);
    await page.getByLabel("Servings", { exact: true }).fill("4");
    const ingredients = (await (await request.get(`/api/households/${h}/ingredients?limit=20`, { headers: english })).json()) as Named[];
    const row = page.getByTestId("ingredient-row").first();
    await row.getByLabel("Ingredient", { exact: true }).fill(ingredients[0]!.name);
    await row.getByRole("button", { name: ingredients[0]!.name, exact: true }).click();
    await row.getByRole("checkbox", { name: "To taste" }).check();
    await page.getByTestId("step-row").first().getByRole("textbox").fill("Step");

    // Over the 10 MB the form allows: stopped with the translated message before anything is sent.
    await page.getByLabel("Picture file").setInputFiles({ name: "big.png", mimeType: "image/png", buffer: noisyPng(12 * 1024 * 1024) });
    await expect(page.getByText("The picture can be at most 10 MB.", { exact: true })).toBeVisible();

    // A ~3 MB picture goes through nginx and is shown.
    await page.getByLabel("Picture file").setInputFiles({ name: "photo.png", mimeType: "image/png", buffer: noisyPng(3 * 1024 * 1024) });
    await expect(page.getByTestId("picture-preview")).toBeVisible();
    await saveRecipe(page, page.getByRole("button", { name: "Save recipe" }));
    await expect(page).toHaveURL(/\/recipes\/[0-9a-f-]{36}$/, { timeout: 15_000 });
    recipeId = page.url().split("/").pop();
    await expect.poll(() => page.locator(".recipe-image img").evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0)).toBe(true);

    // The same 12 MB straight at the upload route, past the form: still refused with 413 (nginx's limit is 11 MB).
    const tooBig = await request.put(`/api/households/${h}/recipes/${recipeId}/image`, {
      headers: await csrf(request),
      multipart: { file: { name: "big.png", mimeType: "image/png", buffer: noisyPng(12 * 1024 * 1024) } },
    });
    expect(tooBig.status()).toBe(413);
  } finally {
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});
