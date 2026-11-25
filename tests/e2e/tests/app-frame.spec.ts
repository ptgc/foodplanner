import { adminCredentials, adminState, expect, openProfile, test } from "./fixtures";

// T-137: the slim top bar, the bottom tab bar, the profile sheet and "More". Runs signed in as the seeded admin,
// who is also a system admin, so "Support" shows under "More".
test.use({ storageState: adminState });

test("the top bar is slim, the tab bar sits at the bottom of the screen, and the active tab is marked", async ({ page }) => {
  await page.goto("/recipes");
  const tabs = page.getByRole("navigation", { name: "Main" });
  await expect(tabs.getByRole("link")).toHaveText(["Today", "Plan", "Recipes", "Shopping", "More"]);
  await expect(tabs.getByRole("link", { name: "Recipes" })).toHaveAttribute("aria-current", "page");
  await expect(tabs.locator("[aria-current]")).toHaveCount(1);

  const header = await page.getByRole("banner").boundingBox();
  expect(header?.height ?? 99, "slim top bar").toBeLessThanOrEqual(56);
  const bar = await tabs.boundingBox();
  const viewport = page.viewportSize()!;
  expect((bar?.y ?? 0) + (bar?.height ?? 0), "tab bar rests on the bottom edge").toBeGreaterThanOrEqual(viewport.height - 1);
  for (const link of await tabs.getByRole("link").all()) {
    expect((await link.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
  }
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);

  // The tab bar stays within reach while the page scrolls.
  await page.goto("/shopping");
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  const after = await tabs.boundingBox();
  expect((after?.y ?? 0) + (after?.height ?? 0)).toBeGreaterThanOrEqual(viewport.height - 1);
});

test("Today shows the week plan for now", async ({ page }) => {
  await page.goto("/recipes");
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Today" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Meal plan", level: 1 })).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Today" })).toHaveAttribute("aria-current", "page");
});

test("the profile sheet holds the email, household, language, switch profile and log out", async ({ page }) => {
  await page.goto("/plan");
  const header = page.getByRole("banner");
  await expect(header.getByTestId("user-email")).toHaveCount(0);
  await expect(header.getByRole("button")).toHaveCount(1);

  const sheet = await openProfile(page);
  await expect(sheet.getByTestId("user-email")).toHaveText(adminCredentials().email);
  await expect(sheet.getByLabel("Language")).toBeVisible();
  await expect(sheet.getByTestId("switch-profile")).toBeVisible();
  await expect(sheet.getByRole("button", { name: "Log out" })).toBeVisible();

  await page.keyboard.press("Escape");
  await expect(sheet).toHaveCount(0);
  await expect(page.getByTestId("profile-button")).toBeFocused();
});

test("More links every screen without a tab", async ({ page }) => {
  await page.goto("/more");
  const main = page.getByRole("main");
  const screens: [string, RegExp][] = [
    ["Pantry", /\/pantry$/],
    ["What can I make?", /\/what-can-i-make$/],
    ["Household", /\/household$/],
    ["Ingredients", /\/ingredients$/],
    ["Support", /\/support$/],
    ["Privacy", /\/privacy$/],
  ];
  for (const [name, url] of screens) {
    await page.goto("/more");
    const link = main.getByRole("link", { name, exact: true });
    expect((await link.boundingBox())?.height ?? 0, `${name} is a 44px target`).toBeGreaterThanOrEqual(44);
    await link.click();
    await expect(page).toHaveURL(url);
  }

  // From the support page the API status is one tap away, and More stays the marked tab there.
  await page.goto("/support");
  await expect(page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "More" })).toHaveAttribute("aria-current", "page");
  await page.getByRole("main").getByRole("link", { name: "API status" }).click();
  await expect(page).toHaveURL(/\/status$/);
  await expect(page.getByRole("heading", { name: "API status" })).toBeVisible();
});
