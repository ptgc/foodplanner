import { adminCredentials, adminState, expect, test } from "./fixtures";

// Runs only in the "mobile-390-nb" project (browser language nb-NO). The other projects run the English specs.
test.use({ storageState: adminState });

test("the app follows the browser language: shell, home and shopping list are in Norwegian at 390px", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByTestId("user-email")).toHaveText(adminCredentials().email);
  await expect(page.locator("html")).toHaveAttribute("lang", "nb");

  await expect(page.getByRole("button", { name: "Logg ut" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Handleliste" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Hjem" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "API-status" })).toBeVisible();

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);

  await page.goto("/shopping?week=2031-06-02");
  await expect(page.getByRole("heading", { name: "Handleliste", level: 1 })).toBeVisible();
  await expect(page.getByTestId("shop-week")).toContainText(/Uke \d+, 2031/);
  await expect(page.getByRole("heading", { name: "Legg til en vare" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Lag fra ukemenyen" })).toBeEnabled();
  await expect(page.getByLabel("Vare", { exact: true })).toBeVisible();
});

test("the recipe grid is in Norwegian at 390px", async ({ page }) => {
  await page.goto("/recipes?q=ingen-treff-pa-dette-xyz");
  await expect(page.getByRole("navigation", { name: "Hovedmeny" }).getByRole("link", { name: "Oppskrifter" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Oppskrifter", level: 1 })).toBeVisible();
  await expect(page.getByLabel("Søk i oppskrifter")).toHaveValue("ingen-treff-pa-dette-xyz");
  await expect(page.getByRole("button", { name: "Maks 30 min" })).toBeVisible();
  await expect(page.getByText("Ingen oppskrifter passer")).toBeVisible();
  await expect(page.getByRole("button", { name: "Fjern filtre" })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll at 390px").toBeLessThanOrEqual(0);
});

test("the language switch is a 44px target, switches the app and is remembered on this device", async ({ page }) => {
  await page.goto("/");
  const select = page.getByRole("banner").getByLabel("Språk");
  const box = await select.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);

  await select.selectOption("en");
  await expect(page.getByRole("button", { name: "Log out" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "en");

  await page.reload();
  await expect(page.getByRole("button", { name: "Log out" })).toBeVisible(); // remembered, despite nb-NO browser
  await expect(page.locator("html")).toHaveAttribute("lang", "en");

  await page.getByRole("banner").getByLabel("Language").selectOption("nb");
  await expect(page.getByRole("button", { name: "Logg ut" })).toBeVisible();
});

test("the login page is Norwegian and has the language switch", async ({ browser }) => {
  const context = await browser.newContext({ locale: "nb-NO", viewport: { width: 390, height: 844 } });
  try {
    const page = await context.newPage();
    await page.goto("/login");
    await expect(page.getByRole("heading", { name: "Logg inn" })).toBeVisible();
    await expect(page.getByLabel("E-post")).toBeVisible();
    await expect(page.getByLabel("Passord")).toBeVisible();
    await expect(page.getByLabel("Språk")).toBeVisible();
  } finally {
    await context.close();
  }
});
