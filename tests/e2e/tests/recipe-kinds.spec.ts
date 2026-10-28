import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(150_000);

// T-076: amount kind, purpose, optional and alternatives. Paste a recipe with "salt og pepper", "smør til steking" and an
// optional cheese, check what the review read, save, see the grouped prep list, plan it and find the lines under
// "check you have" on the shopping list. Runs in its own far-future week.
const baseMonday = Date.UTC(2033, 2, 7); // 2033-03-07 is a Monday

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

test("pasted kinds, purposes and optional lines are read, saved, grouped on the recipe and checked on the shopping list", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  const h = households[0]!.id;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 1);
  const title = `E2E Kinds ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
  let recipeId: string | undefined;
  let entryId: string | undefined;

  // A pantry confirmation from another test would hide a staple from "check you have".
  const ingredients = (await (await request.get(`/api/households/${h}/ingredients`)).json()) as { id: string; name: string }[];
  for (const name of ["Salt", "Black pepper", "Butter"]) {
    const found = ingredients.find((i) => i.name === name);
    if (found) await request.delete(`/api/households/${h}/pantry/${found.id}`, { headers: await csrf(request) });
  }

  try {
    await page.goto("/recipes/import");
    await page.getByRole("tab", { name: "Pasted text" }).click();
    await page
      .getByLabel("Recipe text")
      .fill([title, "Servings: 4", "Ingredients", "salt og sort pepper", "2 ss smør til steking", "1 dl revet ost (kan sløyfes)", "Method", "Fry the fish.", "Serve."].join("\n"));
    await page.getByRole("button", { name: "Read recipe" }).click();
    await expect(page.getByRole("heading", { name: "Check the recipe", level: 1 })).toBeVisible();

    // The review shows what was read: kind, purpose, optional.
    const lines = page.getByTestId("import-line");
    const salt = lines.filter({ hasText: "salt og sort pepper" }).first();
    await expect(salt).toContainText("To taste");
    const butter = lines.filter({ hasText: "2 ss smør til steking" });
    await expect(butter.getByTestId("line-status")).toContainText("Matched");
    await expect(butter).toContainText("2 ss");
    await expect(butter).toContainText("For frying");
    const cheese = lines.filter({ hasText: "revet ost" });
    await expect(cheese).toContainText("optional");
    await expect(cheese).toContainText("1 dl");
    if (mobile) await noHorizontalScroll(page);

    // The editor of a line can change the kind and purpose (the quantity fields follow the kind).
    await cheese.getByRole("button", { name: /^Change/ }).click();
    await expect(cheese.getByLabel("Type of amount")).toHaveValue("Measured");
    await expect(cheese.getByLabel("Used for")).toHaveValue("Main");
    await expect(cheese.getByRole("checkbox", { name: "Optional (can be left out)" })).toBeChecked();
    await cheese.getByLabel("Type of amount").selectOption("AsNeeded");
    await expect(cheese.getByLabel("Amount", { exact: true })).toHaveCount(0);
    await cheese.getByLabel("Type of amount").selectOption("Measured");
    await cheese.getByRole("button", { name: "Close" }).click();

    // "salt og sort pepper" became two lines; any line that still needs a person is fixed with its first suggestion.
    const saveButton = page.getByRole("button", { name: "Save recipe", exact: true });
    for (let guard = 0; guard < 4 && (await saveButton.isDisabled()); guard++) {
      const attention = page.locator('[data-testid="import-line"][data-status!="Matched"]').first();
      await attention.getByRole("group").getByRole("button").first().click();
      await expect(attention.getByTestId("line-status")).toContainText("Matched");
    }
    await expect(saveButton).toBeEnabled();
    await saveButton.click();

    // The recipe page groups the prep list by purpose.
    await expect(page).toHaveURL(/\/recipes\/(?!import)[0-9a-f-]{36}$/, { timeout: 30_000 });
    recipeId = page.url().split("/").pop();
    await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
    const frying = page.getByTestId("prep-frying");
    await expect(frying.getByRole("heading", { name: "For frying" })).toBeVisible();
    await expect(frying).toContainText("Butter");
    const seasoning = page.getByTestId("prep-seasoning");
    await expect(seasoning.getByRole("heading", { name: "To taste" })).toBeVisible();
    await expect(seasoning).toContainText("Salt");
    await expect(page.getByTestId("prep-main")).toContainText("(optional)");
    if (mobile) await noHorizontalScroll(page);

    // Plan it, make the shopping list: the to-taste, frying and optional lines are under "check you have", with no amount.
    const created = await request.post(`/api/households/${h}/meal-plans/${week}/entries`, {
      headers: await csrf(request),
      data: { date: week, recipeId, servings: 4 },
    });
    expect(created.status(), await created.text()).toBe(201);
    entryId = ((await created.json()) as { id: string }).id;
    const generated = await request.post(`/api/households/${h}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
    expect(generated.ok()).toBe(true);

    await page.goto(`/shopping?week=${week}`);
    const check = page.getByRole("region", { name: "Check you have these" });
    await expect(check).toBeVisible();
    for (const name of ["Salt", "Black pepper", "Butter", "Cheese"]) {
      await expect(check.getByTestId("shop-item").filter({ hasText: name }).first(), name).toBeVisible();
    }
    await expect(page.getByRole("region", { name: "To buy" }).getByTestId("shop-item").filter({ hasText: /Cheese|Butter|Salt/ })).toHaveCount(0);
    if (mobile) await noHorizontalScroll(page);
  } finally {
    if (entryId) await request.delete(`/api/households/${h}/meal-plans/${week}/entries/${entryId}`, { headers: await csrf(request) });
    await request.post(`/api/households/${h}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});
