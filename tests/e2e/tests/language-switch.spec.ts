import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, test, uniqueStamp, uniqueWeek } from "./fixtures";

test.use({ storageState: adminState });
test.setTimeout(180_000);

// T-078: switching nb <-> en must change every translatable string on the screen at once, without a reload: the UI
// text and the catalog names (ingredients, tags, store sections) that come from the API per Accept-Language.
// Text the household typed (the recipe title) stays as typed (Q-013). Each screen is opened in one language, the
// switch is made on that screen (no navigation), and both a UI string and a catalog name must change.
const baseMonday = Date.UTC(2038, 5, 7); // 2038-06-07 is a Monday

type Named = { id: string; name: string; kind?: string; isGlobal?: boolean };

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const { token } = (await (await request.get("/api/auth/antiforgery")).json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

async function getIn<T>(request: APIRequestContext, url: string, language: "en" | "nb"): Promise<T> {
  const res = await request.get(url, { headers: { "Accept-Language": language } });
  expect(res.status(), url).toBe(200);
  return (await res.json()) as T;
}

type Fixture = {
  household: string;
  recipeId: string;
  title: string;
  rice: { en: string; nb: string };
  milk: { en: string; nb: string };
  tag: { en: string; nb: string };
};

/** A recipe with rice and milk (catalog names differ between nb and en) and one global tag, created through the API. */
async function createFixture(request: APIRequestContext, title: string): Promise<Fixture> {
  const household = ((await (await request.get("/api/households")).json()) as { id: string }[])[0]!.id;
  const find = async (language: "en" | "nb", term: string) =>
    (await getIn<Named[]>(request, `/api/households/${household}/ingredients?search=${term}`, language)).find((i) => i.name === term && i.isGlobal)!;
  const rice = await find("en", "Rice");
  const milk = await find("en", "Milk");
  expect(rice && milk, "the catalog has Rice and Milk").toBeTruthy();
  const riceNb = await find("nb", "Ris");
  const milkNb = await find("nb", "Melk");
  expect([riceNb?.id, milkNb?.id], "the same ingredients in nb").toEqual([rice.id, milk.id]);

  const tagsEn = await getIn<Named[]>(request, `/api/households/${household}/tags`, "en");
  const tagsNb = await getIn<Named[]>(request, `/api/households/${household}/tags`, "nb");
  const tag = tagsEn.find((t) => t.isGlobal && tagsNb.find((n) => n.id === t.id)!.name !== t.name)!;
  expect(tag, "a global tag whose name differs between nb and en").toBeTruthy();

  const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
  const gram = units.find((u) => u.code === "g")!;
  const created = await request.post(`/api/households/${household}/recipes`, {
    headers: await csrf(request),
    data: {
      title,
      description: null,
      servings: 4,
      prepMinutes: 5,
      cookMinutes: 10,
      steps: [{ text: "Forste steg" }, { text: "Andre steg" }],
      ingredients: [rice, milk].map((i) => ({ ingredientId: i.id, quantity: 200, unitId: gram.id, note: null })),
      tagIds: [tag.id],
    },
  });
  expect(created.status(), await created.text()).toBe(201);
  const recipeId = ((await created.json()) as { id: string }).id;
  return {
    household,
    recipeId,
    title,
    rice: { en: rice.name, nb: riceNb.name },
    milk: { en: milk.name, nb: milkNb.name },
    tag: { en: tag.name, nb: tagsNb.find((n) => n.id === tag.id)!.name },
  };
}

async function deleteRecipe(request: APIRequestContext, f: Fixture) {
  await request.delete(`/api/households/${f.household}/recipes/${f.recipeId}`, { headers: await csrf(request) });
}

/** The language switch in the header, by its stable id (its label changes with the language). */
async function switchTo(page: Page, language: "en" | "nb") {
  await page.locator("#language").selectOption(language);
  await expect(page.locator("html")).toHaveAttribute("lang", language);
}

const noHorizontalScroll = async (page: Page) =>
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth), "no horizontal scroll").toBeLessThanOrEqual(0);

test("every main screen changes language in place: UI text and catalog names (nb <-> en)", async ({ page }, testInfo) => {
  const request = page.request;
  const stamp = uniqueStamp(testInfo);
  const f = await createFixture(request, `E2E Sprak ${testInfo.project.name} ${stamp}`);
  const week = uniqueWeek(testInfo, baseMonday, 0);
  let entryId: string | undefined;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;

  try {
    // Start in English.
    await page.goto("/");
    await switchTo(page, "en");

    // 1. Recipe grid: heading and the tag chip (catalog name).
    await page.goto("/recipes");
    await expect(page.getByRole("heading", { name: "Recipes", level: 1 })).toBeVisible();
    await expect(page.getByRole("button", { name: f.tag.en, exact: true }).first()).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("heading", { name: "Oppskrifter", level: 1 })).toBeVisible();
    await expect(page.getByRole("button", { name: f.tag.nb, exact: true }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: f.tag.en, exact: true })).toHaveCount(f.tag.en === f.tag.nb ? 1 : 0);
    await switchTo(page, "en");
    await expect(page.getByRole("button", { name: f.tag.en, exact: true }).first()).toBeVisible();

    // 2. Recipe detail: ingredient names and a UI string; the typed title does not change.
    await page.goto(`/recipes/${f.recipeId}`);
    await expect(page.getByRole("heading", { name: f.title, level: 1 })).toBeVisible();
    await expect(page.getByRole("link", { name: "Start cooking" })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: f.rice.en })).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("listitem").filter({ hasText: f.rice.nb })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: f.milk.nb })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: new RegExp(`\\b${f.rice.en}\\b`) })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Start cooking" })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: f.title, level: 1 })).toBeVisible();
    // A scaled amount (T-040) is also read in the active language.
    await page.getByRole("button", { name: /^(More servings|Flere porsjoner)$/ }).first().click();
    await expect(page.getByRole("listitem").filter({ hasText: f.rice.nb })).toBeVisible();
    await switchTo(page, "en");
    await expect(page.getByRole("listitem").filter({ hasText: f.rice.en })).toBeVisible();
    await expect(page.getByRole("listitem").filter({ hasText: f.milk.en })).toBeVisible();
    await expect(page.getByRole("link", { name: "Start cooking" })).toBeVisible();

    // 3. Cooking mode.
    await page.goto(`/recipes/${f.recipeId}/cook`);
    await page.getByRole("button", { name: "Show ingredients" }).click();
    await expect(page.locator("#cook-ingredients").getByText(f.rice.en)).toBeVisible();
    await expect(page.getByRole("button", { name: "Next step" })).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("button", { name: "Neste steg" })).toBeVisible();
    await expect(page.locator("#cook-ingredients").getByText(f.rice.nb)).toBeVisible();
    await expect(page.locator("#cook-ingredients").getByText(f.milk.nb)).toBeVisible();
    await switchTo(page, "en");
    await expect(page.getByRole("button", { name: "Next step" })).toBeVisible();
    await expect(page.locator("#cook-ingredients").getByText(f.rice.en)).toBeVisible();

    // 4. Plan: heading, and the picker's recipe grid with the tag chips.
    await page.goto(`/plan?week=${week}`);
    await expect(page.getByRole("heading", { name: "Meal plan", level: 1 })).toBeVisible();
    await page.getByRole("button", { name: /^Pick a meal for/ }).first().click();
    await expect(page.getByRole("button", { name: f.tag.en, exact: true }).first()).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("heading", { name: "Ukeplan", level: 1 })).toBeVisible();
    await expect(page.getByRole("button", { name: f.tag.nb, exact: true }).first()).toBeVisible();
    await switchTo(page, "en");
    await expect(page.getByRole("button", { name: f.tag.en, exact: true }).first()).toBeVisible();

    // 5. Shopping list: generated from a plan entry; item names, store sections and UI text.
    const entry = await request.post(`/api/households/${f.household}/meal-plans/${week}/entries`, {
      headers: await csrf(request),
      data: { date: week, recipeId: f.recipeId, servings: 4 },
    });
    expect(entry.status(), await entry.text()).toBe(201);
    entryId = ((await entry.json()) as { id: string }).id;
    const generated = await request.post(`/api/households/${f.household}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
    expect(generated.ok()).toBe(true);

    await page.goto(`/shopping?week=${week}`);
    await expect(page.getByRole("heading", { name: "Shopping list", level: 1 })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: f.rice.en })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Dry goods", level: 3 })).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("heading", { name: "Handleliste", level: 1 })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: f.rice.nb })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: f.milk.nb })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Tørrvarer", level: 3 })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: f.rice.en })).toHaveCount(0);
    await switchTo(page, "en");
    await expect(page.getByRole("checkbox", { name: f.rice.en })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Dry goods", level: 3 })).toBeVisible();

    // 6. Household: UI text only (members and invitations are people, not catalog).
    await page.goto("/household");
    await expect(page.getByRole("heading", { name: "Household", level: 1 })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Members", level: 2 })).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("heading", { name: "Husstand", level: 1 })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Medlemmer", level: 2 })).toBeVisible();
    await switchTo(page, "en");
    await expect(page.getByRole("heading", { name: "Members", level: 2 })).toBeVisible();

    // 7. Import review: the matched ingredient line (catalog name) and the page text.
    await page.goto("/recipes/import");
    await expect(page.getByRole("heading", { name: "Import a recipe", level: 1 })).toBeVisible();
    await page.getByRole("tab", { name: "Pasted text" }).click();
    await page
      .getByLabel("Recipe text")
      .fill([`E2E Import ${stamp}`, "Servings: 4", "Ingredients", "2 dl melk", "Method", "Heat the milk gently."].join("\n"));
    await page.getByRole("button", { name: "Read recipe" }).click();
    await expect(page.getByRole("heading", { name: "Check the recipe", level: 1 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("line-status").first()).toContainText("Matched");
    await expect(page.locator("strong", { hasText: f.milk.en }).first()).toBeVisible();
    await switchTo(page, "nb");
    await expect(page.getByRole("heading", { name: "Sjekk oppskriften", level: 1 })).toBeVisible();
    await expect(page.locator("strong", { hasText: f.milk.nb }).first()).toBeVisible();
    await switchTo(page, "en");
    await expect(page.locator("strong", { hasText: f.milk.en }).first()).toBeVisible();
    // The draft is discarded so it does not pile up.
    const drafts = (await (await request.get(`/api/households/${f.household}/imports`)).json()) as { id: string; title: string | null }[];
    for (const d of drafts.filter((x) => x.title?.includes(stamp))) {
      await request.delete(`/api/households/${f.household}/imports/${d.id}`, { headers: await csrf(request) });
    }
    if (mobile) await noHorizontalScroll(page);
  } finally {
    await switchTo(page, "en").catch(() => undefined);
    if (entryId) await request.delete(`/api/households/${f.household}/meal-plans/${week}/entries/${entryId}`, { headers: await csrf(request) });
    await request.post(`/api/households/${f.household}/shopping-lists/${week}/generate`, { headers: await csrf(request) });
    await deleteRecipe(request, f);
  }
});

test.describe("with the service worker active", () => {
  test.use({ serviceWorkers: "allow" });

  test("the worker never answers a recipe in the other language, online or offline", async ({ page, context }, testInfo) => {
    const request = page.request;
    const f = await createFixture(request, `E2E Sprak sw ${testInfo.project.name} ${uniqueStamp(testInfo)}`);
    const rice = (language: "en" | "nb") => page.getByRole("listitem").filter({ hasText: f.rice[language] });
    try {
      await page.goto(`/recipes/${f.recipeId}`);
      await page.evaluate(async () => {
        await navigator.serviceWorker.ready;
        if (!navigator.serviceWorker.controller) {
          await new Promise<void>((resolve) => navigator.serviceWorker.addEventListener("controllerchange", () => resolve(), { once: true }));
        }
      });
      await page.reload(); // from now on the worker sees the page's requests
      await switchTo(page, "en");
      await expect(rice("en")).toBeVisible();

      // Online switch: the new language is fetched through the worker and cached under its own key.
      await switchTo(page, "nb");
      await expect(rice("nb")).toBeVisible();
      await expect(rice("en")).toHaveCount(0);
      const keys = await page.evaluate(async () => (await (await caches.open("fp-runtime-api")).keys()).map((r) => new URL(r.url).search));
      expect(keys.some((k) => k.includes("__lang=en")) && keys.some((k) => k.includes("__lang=nb"))).toBe(true);

      // Offline: each language gets its own cached copy, never the other one.
      await context.setOffline(true);
      await switchTo(page, "en");
      await expect(rice("en")).toBeVisible();
      await expect(rice("nb")).toHaveCount(0);
      await switchTo(page, "nb");
      await expect(rice("nb")).toBeVisible();
      await expect(rice("en")).toHaveCount(0);
      await context.setOffline(false);
      await switchTo(page, "en");
    } finally {
      await context.setOffline(false);
      await deleteRecipe(request, f);
    }
  });
});

test("a reload after a switch shows the chosen language from the first paint of the data", async ({ page }, testInfo) => {
  const request = page.request;
  const f = await createFixture(request, `E2E Sprak reload ${testInfo.project.name} ${uniqueStamp(testInfo)}`);
  try {
    await page.goto(`/recipes/${f.recipeId}`);
    await switchTo(page, "nb");
    await expect(page.getByRole("listitem").filter({ hasText: f.rice.nb })).toBeVisible();
    await page.reload();
    await expect(page.getByRole("listitem").filter({ hasText: f.rice.nb })).toBeVisible();
    await switchTo(page, "en");
  } finally {
    await deleteRecipe(request, f);
  }
});
