import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// URL import is not exercised here: it would fetch from the real internet. It is covered by the Vitest tests
// (every ProblemDetails code of the fetch) and the API tests (SSRF-safe fetch against a local test server).

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

/** Letters only, so the parser sees one plain word that no catalog entry resembles. */
function inventedWord(stamp: string): string {
  return "Zqx" + stamp.replace(/\d/g, (d) => "abcdefghij"[Number(d)]!).replace(/-/g, "");
}

test("an Adult pastes a recipe, fixes the line the catalog cannot match by creating an own ingredient, and saves it", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string; role: string }[];
  const h = households[0]!.id;
  expect(["Owner", "Adult"]).toContain(households[0]!.role);
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;

  const stamp = uniqueStamp(testInfo);
  const title = `E2E Import ${testInfo.project.name} ${stamp}`;
  const invented = inventedWord(stamp);
  let recipeId: string | undefined;

  try {
    // Entry point from the recipe grid.
    await page.goto("/recipes");
    await page.getByTestId("recipe-import-link").click();
    await expect(page).toHaveURL(/\/recipes\/import$/);
    await expect(page.getByRole("heading", { name: "Import a recipe", level: 1 })).toBeVisible();

    // Step 1: paste text.
    await page.getByRole("tab", { name: "Pasted text" }).click();
    await page
      .getByLabel("Recipe text")
      .fill(
        [
          title,
          "Servings: 4",
          "Ingredients",
          "2 dl melk",
          `3 stk ${invented}`,
          "Method",
          "Heat the milk gently.",
          `Add the ${invented} and serve.`,
        ].join("\n"),
      );
    await page.getByRole("button", { name: "Read recipe" }).click();

    // Step 2: review. One line matches, one is clearly marked.
    await expect(page).toHaveURL(/\/recipes\/import\?draft=/);
    await expect(page.getByRole("heading", { name: "Check the recipe", level: 1 })).toBeVisible();
    await expect(page.getByLabel("Title")).toHaveValue(title);
    const milk = page.getByTestId("import-line").filter({ hasText: "2 dl melk" });
    const unknown = page.getByTestId("import-line").filter({ hasText: invented });
    await expect(milk.getByTestId("line-status")).toContainText("Matched");
    await expect(unknown.getByTestId("line-status")).toContainText("Needs attention");
    await expect(page.getByTestId("attention-summary")).toContainText("1 line needs attention");
    const saveButton = page.getByRole("button", { name: "Save recipe", exact: true });
    await expect(saveButton).toBeDisabled();

    if (mobile) {
      await noHorizontalScroll(page);
      for (const button of [saveButton, page.getByRole("button", { name: "Discard draft" }), unknown.getByRole("button", { name: /^Remove/ })]) {
        expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      }
    }

    // The summary link moves focus to the line.
    await page.getByTestId("attention-summary").getByRole("button").click();
    await expect(unknown).toBeFocused();

    // Fix it inline: search, nothing found, create the household's own ingredient, use the line.
    await unknown.getByLabel("Ingredient", { exact: true }).fill(invented);
    await unknown.getByRole("button", { name: `Create "${invented}" as your own ingredient` }).click();
    await unknown.getByLabel("Measured in").selectOption("Count");
    await unknown.getByRole("button", { name: "Create ingredient" }).click();
    await expect(unknown.getByText(`Chosen: ${invented}`)).toBeVisible();
    await unknown.getByRole("button", { name: "Use this line" }).click();
    await expect(unknown.getByTestId("line-status")).toContainText("Matched");
    await expect(page.getByTestId("attention-summary")).toContainText("All ingredient lines are matched.");
    await expect(saveButton).toBeEnabled();

    // Save lands on the new recipe with structured lines.
    await saveButton.click();
    await expect(page).toHaveURL(/\/recipes\/(?!import)[0-9a-f-]{36}$/, { timeout: 30_000 });
    recipeId = page.url().split("/").pop();
    await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: "Milk" }).or(page.getByRole("listitem").filter({ hasText: "Melk" })).filter({ hasText: "2 dl" })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: invented }).filter({ hasText: "3" })).toBeVisible();
    await expect(page.locator(".recipe-steps li")).toHaveText(["Heat the milk gently.", `Add the ${invented} and serve.`]);
    if (mobile) await noHorizontalScroll(page);

    // T-075: a pasted text has no source, so no link. A URL import cannot run here (no internet), so the source is set
    // through the edit form; the API test covers that saving a URL import sets it. The detail page then links to the original.
    await expect(page.getByTestId("recipe-source-link")).toHaveCount(0);
    await page.getByTestId("recipe-edit-link").click();
    await expect(page.getByRole("heading", { name: "Edit recipe", level: 1 })).toBeVisible();
    const source = page.getByLabel("Link (optional)");
    await source.fill("javascript:alert(1)");
    await page.getByRole("button", { name: "Save recipe" }).click();
    await expect(source).toHaveAttribute("aria-invalid", "true");
    await source.fill("https://www.matprat.no/oppskrifter/e2e/");
    await page.getByRole("button", { name: "Save recipe" }).click();
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipeId}$`), { timeout: 30_000 });
    const original = page.getByTestId("recipe-source-link");
    await expect(original).toHaveText("Original recipe: matprat.no ↗");
    await expect(original).toHaveAttribute("href", "https://www.matprat.no/oppskrifter/e2e/");
    await expect(original).toHaveAttribute("target", "_blank");
    await expect(original).toHaveAttribute("rel", "noopener noreferrer");
    if (mobile) {
      expect((await original.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      await noHorizontalScroll(page);
    }
  } finally {
    // The recipe is deleted. There is no endpoint to delete an ingredient, so the household's own ingredient named
    // "Zqx..." (unique per run) stays behind.
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});

test("a draft can be resumed from the list and discarded", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const h = households[0]!.id;
  const title = `E2E Draft ${testInfo.project.name} ${uniqueStamp(testInfo)}`;

  await page.goto("/recipes/import");
  await page.getByRole("tab", { name: "Pasted text" }).click();
  await page.getByLabel("Recipe text").fill([title, "Ingredients", "2 dl melk", "Method", "Stir."].join("\n"));
  await page.getByRole("button", { name: "Read recipe" }).click();
  await expect(page.getByRole("heading", { name: "Check the recipe", level: 1 })).toBeVisible();
  const draftId = new URL(page.url()).searchParams.get("draft")!;

  try {
    // Back to step 1: the draft is listed; Continue reopens it.
    await page.goto("/recipes/import");
    const item = page.getByTestId("import-draft").filter({ hasText: title });
    await expect(item).toBeVisible();
    await item.getByRole("link", { name: /^Continue/ }).click();
    await expect(page.getByLabel("Title")).toHaveValue(title);

    // Discard asks first.
    await page.getByRole("button", { name: "Discard draft" }).click();
    await page.getByRole("button", { name: "Keep the draft" }).click();
    await expect(page.getByLabel("Title")).toBeVisible();
    await page.getByRole("button", { name: "Discard draft" }).click();
    await page.getByRole("button", { name: "Yes, discard" }).click();
    await expect(page).toHaveURL(/\/recipes\/import$/);
    await expect(page.getByTestId("import-draft").filter({ hasText: title })).toHaveCount(0);
  } finally {
    await request.delete(`/api/households/${h}/imports/${draftId}`, { headers: await csrf(request) });
  }
});
