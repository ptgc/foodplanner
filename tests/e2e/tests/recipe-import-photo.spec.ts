import type { APIRequestContext, Page, Route } from "@playwright/test";
import { adminState, expect, test, uniqueStamp } from "./fixtures";
import { solidPng } from "./png";

test.use({ storageState: adminState });
test.setTimeout(120_000);

// Recipe from a photo (T-119). The compose stack has the photo import off (and a real call would cost money), so every
// photo endpoint is faked at the network level with page.route: photo-usage, the POST with the photos, the draft, its
// picture, the PUT and the save. Nothing here reaches Claude. Only the recipe the save "creates" is real (made through
// the API and deleted at the end), so the redirect after saving lands on a real recipe page.

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

const problem = (route: Route, status: number, code: string, extra: Record<string, unknown> = {}) =>
  route.fulfill({
    status,
    contentType: "application/problem+json",
    body: JSON.stringify({ type: `urn:foodplanner:problem:${code}`, title: "x", status, ...extra }),
  });

function draftBody(title: string, patch: Record<string, unknown> = {}) {
  return {
    id: "11111111-2222-3333-4444-555555555555",
    source: "Photo",
    sourceUrl: null,
    status: "Draft",
    title,
    description: "Bruk gjerne ferske urter.",
    servings: 4,
    prepMinutes: null,
    cookMinutes: 30,
    imageUrl: null,
    steps: ["Kok opp melken."],
    lines: [
      {
        position: 0,
        original: "2 dl melk",
        ingredientText: null,
        quantity: 2,
        quantityLow: null,
        unitText: null,
        unitId: null,
        unitCode: "dl",
        unitName: "desiliter",
        ingredientId: "22222222-2222-2222-2222-222222222222",
        ingredientName: "Melk",
        confidence: 1,
        note: null,
        status: "Matched",
        suggestions: [],
        amountKind: "Measured",
        purpose: "Main",
        isOptional: false,
        alternatives: [],
        suggestedProteinClass: null,
        aiSuggested: true,
      },
    ],
    warnings: [],
    canSave: true,
    savedRecipeId: null,
    createdAt: new Date().toISOString(),
    aiAssist: "Applied",
    sourceName: "Kaptein Sabeltann",
    suggestedImage: { url: "/api/households/x/imports/11111111-2222-3333-4444-555555555555/picture" },
    useSuggestedImage: true,
    photoUsage: { remainingToday: 0, dailyLimit: 2, unlimited: false },
    ...patch,
  };
}

test("the entry is hidden when the server has photo import off", async ({ page }) => {
  await page.route(/\/imports\/photo-usage$/, (route) =>
    route.fulfill({ json: { enabled: false, unlimited: false, dailyLimit: 2, remainingToday: 0 } }),
  );
  await page.goto("/recipes");
  await expect(page.getByTestId("recipe-import-link")).toBeVisible();
  await expect(page.getByRole("link", { name: "From photo" })).toHaveCount(0);
});

test("an Adult adds a recipe from photos: limit and errors, retry, review with the picture, save", async ({ page }, testInfo) => {
  const request = page.request;
  const households = (await (await request.get("/api/households")).json()) as { id: string; role: string }[];
  const h = households[0]!.id;
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const title = `E2E Photo ${testInfo.project.name} ${uniqueStamp(testInfo)}`;
  let recipeId: string | undefined;

  try {
    const created = await request.post(`/api/households/${h}/recipes`, {
      headers: await csrf(request),
      data: { title, description: null, servings: 4, prepMinutes: 5, cookMinutes: 10, steps: [{ text: "Stir." }], lines: [], tagIds: [] },
    });
    // The recipe the faked save points at. If the recipe API shape differs, this fails loudly here, not later.
    expect(created.status(), await created.text()).toBe(201);
    recipeId = ((await created.json()) as { id: string }).id;

    let draft = draftBody(title);
    const puts: Record<string, unknown>[] = [];
    let posts = 0;
    let uploaded = "";

    await page.route(/\/imports\/photo-usage$/, (route) =>
      route.fulfill({ json: { enabled: true, unlimited: false, dailyLimit: 2, remainingToday: 2 } }),
    );
    await page.route(/\/imports\/photo$/, async (route) => {
      posts++;
      uploaded = route.request().postData() ?? "";
      if (posts === 1) return problem(route, 422, "photo-import-unreadable", { remainingToday: 1, dailyLimit: 2 });
      return route.fulfill({ status: 201, json: draft });
    });
    await page.route(/\/imports\/[0-9a-f-]{36}\/picture$/, (route) => route.fulfill({ status: 200, contentType: "image/png", body: solidPng() }));
    await page.route(/\/imports\/[0-9a-f-]{36}\/save$/, (route) => route.fulfill({ status: 201, json: { recipeId, pictureSaved: false } }));
    await page.route(/\/imports\/[0-9a-f-]{36}$/, async (route) => {
      const method = route.request().method();
      if (method === "PUT") {
        const body = route.request().postDataJSON() as Record<string, unknown>;
        puts.push(body);
        if (typeof body.useSuggestedImage === "boolean") draft = { ...draft, useSuggestedImage: body.useSuggestedImage };
        return route.fulfill({ json: draft });
      }
      if (method === "GET") return route.fulfill({ json: draft });
      return route.continue();
    });

    // Entry on the recipe grid, at least 44 px tall.
    await page.goto("/recipes");
    const entry = page.getByTestId("recipe-photo-link");
    await expect(entry).toHaveText("From photo");
    expect((await entry.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await entry.click();
    await expect(page).toHaveURL(/\/recipes\/import\/photo$/);
    await expect(page.getByRole("heading", { name: "Recipe from photo", level: 1 })).toBeVisible();

    // Counter, privacy line, pickers and touch targets.
    await expect(page.getByTestId("photo-left")).toHaveText("2 of 2 photo imports left today");
    await expect(page.getByTestId("photo-privacy")).toContainText("sent to Anthropic (Claude)");
    await expect(page.getByTestId("photo-camera-input")).toHaveAttribute("capture", "environment");
    await expect(page.getByRole("button", { name: "Read the recipe" })).toBeDisabled();
    if (mobile) {
      await noHorizontalScroll(page);
      for (const name of ["Take a photo", "Choose photos", "Read the recipe"]) {
        expect((await page.getByRole("button", { name }).boundingBox())!.height, name).toBeGreaterThanOrEqual(44);
      }
    }

    // Five photos: the fifth is refused with a message; thumbnails have 44 px remove buttons.
    const photo = (n: number) => ({ name: `page-${n}.png`, mimeType: "image/png", buffer: solidPng() });
    await page.getByTestId("photo-files-input").setInputFiles([1, 2, 3, 4, 5].map(photo));
    await expect(page.getByTestId("photo-thumb")).toHaveCount(4);
    await expect(page.getByTestId("photo-too-many")).toContainText("at most 4 photos");
    if (mobile) {
      await noHorizontalScroll(page);
      expect((await page.getByRole("button", { name: "Remove photo 1" }).boundingBox())!.height).toBeGreaterThanOrEqual(44);
    }
    await page.getByRole("button", { name: "Remove photo 4" }).click();
    await expect(page.getByTestId("photo-thumb")).toHaveCount(3);
    await page.getByLabel("Book (optional)").fill("Kaptein Sabeltann");

    // First try: the photos are unreadable. The alert has the retake tips, the count follows, the photos stay.
    await page.getByRole("button", { name: "Read the recipe" }).click();
    const alert = page.getByTestId("photo-problem");
    await expect(alert).toContainText("Try a sharper, brighter photo");
    await expect(alert).toBeFocused();
    await expect(page.getByTestId("photo-left")).toHaveText("1 of 2 photo imports left today");
    await expect(page.getByTestId("photo-thumb")).toHaveCount(3);
    // The request was multipart with one `photos` part per photo, JPEGs after the client downsize, and the book name.
    expect(uploaded.match(/name="photos"/g)).toHaveLength(3);
    expect(uploaded).toContain('name="sourceName"');
    expect(uploaded).toContain("Kaptein Sabeltann");

    // Retry: success goes to the review.
    await page.getByRole("button", { name: "Read the recipe" }).click();
    await expect(page).toHaveURL(/\/recipes\/import\?draft=11111111-2222-3333-4444-555555555555$/);
    await expect(page.getByRole("heading", { name: "Check the recipe", level: 1 })).toBeVisible();
    await expect(page.getByTestId("ai-mark")).toHaveCount(1);
    await expect(page.getByTestId("ai-note")).toContainText("Claude read these lines from your photos");
    await expect(page.getByLabel("Book", { exact: true })).toHaveValue("Kaptein Sabeltann");

    // The suggested dish picture with its switch (on by default, 44 px), saved at once.
    const picture = page.getByRole("img", { name: "The dish picture Claude found on the page" });
    await expect(picture).toBeVisible();
    await expect.poll(() => picture.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
    const toggle = page.getByRole("switch", { name: "Use this picture for the recipe" });
    await expect(toggle).toBeChecked();
    expect((await toggle.locator("xpath=ancestor::label").boundingBox())!.height).toBeGreaterThanOrEqual(44);
    if (mobile) await noHorizontalScroll(page);
    await toggle.click();
    await expect.poll(() => puts.some((p) => p.useSuggestedImage === false)).toBe(true);
    await toggle.click();
    await expect.poll(() => puts.filter((p) => p.useSuggestedImage === true).length).toBeGreaterThan(0);

    // Edit the book and save: the PUT carries it, and pictureSaved false shows the notice on the recipe page.
    await page.getByLabel("Book", { exact: true }).fill("Fiskeboka");
    await page.getByRole("button", { name: "Save recipe" }).click();
    await expect(page).toHaveURL(new RegExp(`/recipes/${recipeId}$`));
    expect(puts.some((p) => p.sourceName === "Fiskeboka")).toBe(true);
    await expect(page.getByTestId("picture-not-saved")).toContainText("its picture could not be saved");
  } finally {
    if (recipeId) await request.delete(`/api/households/${h}/recipes/${recipeId}`, { headers: await csrf(request) });
  }
});

test("at zero left the screen says so, with no request, and Cancel aborts a slow read", async ({ page }) => {
  let posts = 0;
  let remaining = 0;
  await page.route(/\/imports\/photo-usage$/, (route) =>
    route.fulfill({ json: { enabled: true, unlimited: false, dailyLimit: 2, remainingToday: remaining } }),
  );
  await page.route(/\/imports\/photo$/, () => {
    posts++; // never answered: the request stays open until the page aborts it
  });
  const png = { name: "page.png", mimeType: "image/png", buffer: solidPng() };

  await page.goto("/recipes/import/photo");
  await expect(page.getByTestId("photo-limit")).toContainText("No photo imports are left today");
  await page.getByTestId("photo-files-input").setInputFiles(png);
  await expect(page.getByRole("button", { name: "Read the recipe" })).toBeDisabled();
  expect(posts).toBe(0);

  remaining = 1;
  await page.reload();
  await page.getByTestId("photo-files-input").setInputFiles(png);
  await page.getByRole("button", { name: "Read the recipe" }).click();
  await expect(page.getByTestId("photo-progress")).toContainText("Claude is reading the photos, this can take up to a minute");
  await expect(page.locator("form[aria-busy='true']")).toBeVisible();
  await page.getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByTestId("photo-cancelled")).toContainText("may already have counted");
  await expect(page.getByTestId("photo-thumb")).toHaveCount(1);
  expect(posts).toBe(1);
});
