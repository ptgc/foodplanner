import { request as playwrightRequest, type APIRequestContext, type Page } from "@playwright/test";
import { adminState, expect, memberCredentials, signIn, test, uniqueStamp, uniqueWeek } from "./fixtures";

// The page is NOT signed in as the seeded admin (the architect's real account). It signs in as the seeded e2e member and joins the
// test household as an Adult; the PIN therefore belongs to that e2e-only account. The admin API context only sets up and deletes.
test.use({ storageState: { cookies: [], origins: [] } });
// Both tests put a PIN on the same e2e member account, so they must not run at the same time (the suite runs with one worker;
// serial mode keeps that true even if someone raises the worker count).
test.describe.configure({ mode: "serial" });
test.setTimeout(180_000);

// T-091: kid mode on a shared device. An adult makes a kid profile and a PIN, switches the device to the kid; the kid likes dishes
// and plans dinners within the household rules; the PIN brings the adult back. Everything runs in the test's own household (named
// with the run tag), set up and torn down through a SEPARATE api context signed in as the adult, because the page's own cookie
// becomes a kid-mode cookie (which cannot delete a household). A PIN belongs to the account and no endpoint removes it, so each test
// puts a fresh random PIN on the e2e member (proof: the member's password) and nothing is ever set on the admin.
const baseMonday = Date.UTC(2039, 2, 7); // 2039-03-07 is a Monday
/** A random 4-digit PIN the server accepts (not all equal, not a straight run), different from `not` when given. */
function randomPin(not?: string): string {
  for (;;) {
    const pin = String(Math.floor(Math.random() * 10_000)).padStart(4, "0");
    const d = [...pin].map(Number);
    const steps = d.slice(1).map((x, i) => x - d[i]!);
    const weak = d.every((x) => x === d[0]) || steps.every((s) => s === 1) || steps.every((s) => s === -1);
    if (!weak && pin !== not) return pin;
  }
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get("/api/auth/antiforgery");
  return { "X-XSRF-TOKEN": ((await res.json()) as { token: string }).token };
}

const dayOf = (week: string, offset: number) => new Date(Date.parse(week) + offset * 86_400_000).toISOString().slice(0, 10);
const tile = (page: Page, date: string) => page.locator(`[data-testid="kid-day"][data-date="${date}"]`);

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow, "no horizontal scroll").toBeLessThanOrEqual(0);
}

async function atLeast(locator: ReturnType<Page["locator"]>, size: number, what: string) {
  const box = (await locator.boundingBox())!;
  expect(box.width, `${what} width`).toBeGreaterThanOrEqual(size);
  expect(box.height, `${what} height`).toBeGreaterThanOrEqual(size);
}

/** Screenshots for the task report: only when KID_SHOTS_DIR is set, and only at 390px. */
async function shot(page: Page, name: string) {
  const dir = process.env.KID_SHOTS_DIR;
  if (dir && (page.viewportSize()?.width ?? 0) <= 390) await page.screenshot({ path: `${dir}/${name}.png` });
}

type Household = { id: string; recipes: Record<"beef" | "chicken" | "fish" | "vegetarian" | "pancakes", { id: string; title: string }> };

async function setUpHousehold(admin: APIRequestContext, stamp: string): Promise<Household> {
  const created = await admin.post("/api/households", { headers: await csrf(admin), data: { name: `E2E kid ${stamp}` } });
  expect(created.status(), await created.text()).toBe(201);
  const h = ((await created.json()) as { id: string }).id;

  const tags = (await (await admin.get(`/api/households/${h}/tags`)).json()) as { id: string; name: string; kind: string }[];
  const dinner = tags.find((t) => /^(Middag|Dinner)$/.test(t.name) && t.kind === "Category")!;
  const kidFriendly = tags.find((t) => /^(Barnevennlig|Kid friendly)$/.test(t.name))!;
  const ingredients = (await (await admin.get(`/api/households/${h}/ingredients`)).json()) as { id: string; isStaple: boolean }[];
  const ingredient = ingredients.find((i) => !i.isStaple)!;
  const units = (await (await admin.get("/api/units")).json()) as { id: string; code: string }[];
  const gram = units.find((u) => u.code === "g") ?? units[0]!;

  const recipe = async (name: string, foodType: string, tagIds: string[]) => {
    const title = `${name} ${stamp}`;
    const res = await admin.post(`/api/households/${h}/recipes`, {
      headers: await csrf(admin),
      data: {
        title,
        description: null,
        servings: 4,
        prepMinutes: 5,
        cookMinutes: 10,
        steps: [{ text: `Lag ${title}` }],
        ingredients: [{ ingredientId: ingredient.id, quantity: 250, unitId: gram.id, note: null }],
        tagIds,
        foodType,
      },
    });
    expect(res.status(), await res.text()).toBe(201);
    return { id: ((await res.json()) as { id: string }).id, title };
  };
  const recipes = {
    beef: await recipe("Biff", "Beef", [dinner.id]),
    chicken: await recipe("Kylling", "Chicken", [dinner.id]),
    fish: await recipe("Laks", "Fish", [dinner.id]),
    vegetarian: await recipe("Grønnsakssuppe", "Vegetarian", [dinner.id]),
    pancakes: await recipe("Pannekaker", "Vegetarian", [dinner.id, kidFriendly.id]),
  };

  // The household rules: at least one fish and one vegetarian dinner a week.
  const rules = await admin.put(`/api/households/${h}/meal-mix-rules`, {
    headers: await csrf(admin),
    data: {
      rules: [
        { foodType: "Fish", minPerWeek: 1 },
        { foodType: "Vegetarian", minPerWeek: 1 },
      ],
    },
  });
  expect(rules.status(), await rules.text()).toBe(200);
  return { id: h, recipes };
}

/** Gives the signed-in e2e member this PIN through the API, proving with their password (works with or without an earlier PIN). */
async function setMemberPin(request: APIRequestContext, pin: string, password: string) {
  // One call is enough: with no PIN yet the proof is ignored, with one it is checked. The PIN endpoints share the
  // pin-unlock rate limit (10 a minute per user), which the sign-in flow above also uses, so a 429 waits and tries again.
  for (let attempt = 0; ; attempt++) {
    const res = await request.put("/api/auth/pin", { headers: await csrf(request), data: { pin, currentPin: null, password } });
    if (res.status() === 429 && attempt < 3) {
      await new Promise((resolve) => setTimeout(resolve, Number(res.headers()["retry-after"] ?? 30) * 1000));
      continue;
    }
    expect(res.status(), await res.text()).toBe(204);
    return;
  }
}

test("kid mode: profile and PIN as adult, likes, a week within the rules, the PIN back", async ({ page, baseURL }, testInfo) => {
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  const stamp = uniqueStamp(testInfo);
  const admin = await playwrightRequest.newContext({ baseURL, storageState: adminState });
  let h: string | undefined;

  try {
    const household = await setUpHousehold(admin, stamp);
    h = household.id;
    const { beef, chicken, fish, vegetarian, pancakes } = household.recipes;
    const member = await joinAsMember(page, admin, h);
    const oldPin = randomPin();
    const pin = randomPin(oldPin);
    const wrongPin = randomPin(pin);
    await setMemberPin(page.request, oldPin, member.password);

    // The adult has planned meat for Monday to Friday.
    const mondayEntry = await admin.post(`/api/households/${h}/meal-plans/${week}/entries`, {
      headers: await csrf(admin),
      data: { date: dayOf(week, 0), recipeId: beef.id, servings: null },
    });
    expect(mondayEntry.status(), await mondayEntry.text()).toBe(201);
    const adultEntryId = ((await mondayEntry.json()) as { id: string }).id;
    for (let i = 1; i < 5; i++) {
      const res = await admin.post(`/api/households/${h}/meal-plans/${week}/entries`, {
        headers: await csrf(admin),
        data: { date: dayOf(week, i), recipeId: i % 2 ? chicken.id : beef.id, servings: null },
      });
      expect(res.status(), await res.text()).toBe(201);
    }

    // ---- The adult: a kid profile and a PIN, on the household page ----
    await page.goto("/household");
    const profiles = page.getByTestId("kid-profiles");
    await expect(profiles.getByRole("heading", { name: "Kid profiles", level: 2 })).toBeVisible();
    await profiles.getByTestId("kid-profile-add").click();
    const form = profiles.getByTestId("kid-profile-form");
    await form.getByLabel("Name").fill("Mia");
    await form.getByRole("radio", { name: "Fox" }).check({ force: true });
    if (mobile) {
      await atLeast(form.locator(".kid-avatar-option").first(), 44, "avatar option");
      await atLeast(form.getByRole("button", { name: "Add profile" }), 44, "add profile button");
      await noHorizontalScroll(page);
    }
    await form.getByRole("button", { name: "Add profile" }).click();
    await expect(profiles.getByTestId("kid-profile-row")).toHaveCount(1);
    await expect(profiles.getByTestId("kid-profile-row")).toContainText("Mia");

    const pinCard = page.getByTestId("kid-pin-card");
    await pinCard.getByTestId("pin-open").click();
    const pinForm = pinCard.getByTestId("pin-setup");
    // A PIN exists (set above), so changing it needs the current one: without it the form asks for it.
    await pinForm.getByLabel("New PIN (4 digits)").fill(pin);
    await pinForm.getByRole("button", { name: "Save PIN" }).click();
    await expect(pinForm.getByTestId("pin-setup-error")).toHaveText("You already have a PIN. Enter it below to change it.");
    await pinForm.getByLabel("Current PIN").fill(oldPin);
    await pinForm.getByRole("button", { name: "Save PIN" }).click();
    await expect(pinCard.getByTestId("pin-saved")).toBeVisible();

    // ---- Switch to the kid ----
    await page.getByTestId("switch-profile").click();
    const picker = page.getByRole("dialog", { name: "Who are you?" });
    await expect(picker.getByTestId("profile-tile")).toHaveCount(1);
    if (mobile) await atLeast(picker.getByTestId("profile-tile"), 96, "profile tile");
    await shot(page, "03-profile-picker");
    await picker.getByRole("button", { name: "Switch to Mia" }).click();

    const kidShell = page.getByTestId("kid-shell");
    await expect(kidShell).toBeVisible();
    await expect(page.getByTestId("user-email")).toHaveCount(0);
    await expect(page.getByRole("navigation", { name: "Main", exact: true })).toHaveCount(0);
    const header = page.getByRole("banner");
    await expect(header.getByRole("button")).toHaveCount(2);
    await expect(header.getByRole("button", { name: /Switch profile/ })).toBeVisible();
    await expect(header.getByRole("button", { name: "Back to the grown-up" })).toBeVisible();

    // ---- Dishes: kid friendly first, big hearts, no time/tags/servings ----
    const dishes = page.getByTestId("kid-dish");
    await expect(dishes).toHaveCount(5);
    await expect(dishes.first()).toHaveAttribute("aria-label", pancakes.title);
    const feedText = (await page.getByTestId("kid-home").textContent()) ?? "";
    expect(feedText).not.toMatch(/\bmin\b|servings|Kid friendly|Barnevennlig|Beef|Fish/i);
    const heart = dishes.filter({ hasText: fish.title }).getByTestId("kid-heart");
    if (mobile) {
      await atLeast(heart, 64, "heart");
      await atLeast(header.getByRole("button", { name: "Back to the grown-up" }), 56, "key");
      await atLeast(header.getByTestId("kid-switch"), 56, "avatar");
      await noHorizontalScroll(page);
    }
    await shot(page, "01-kid-home");
    await expect(heart).toHaveAttribute("aria-pressed", "false");
    await heart.click();
    await expect(heart).toHaveAttribute("aria-pressed", "true");
    await expect.poll(async () => {
      const list = (await (await admin.get(`/api/households/${h}/recipes?likedByProfileId=${await profileId(admin, h!)}`)).json()) as { items: { id: string }[] };
      return list.items.map((r) => r.id);
    }).toEqual([fish.id]);
    // The heart is a server fact: a reload shows it filled, and tapping again empties it.
    await page.reload();
    const heartAfter = page.getByTestId("kid-dish").filter({ hasText: fish.title }).getByTestId("kid-heart");
    await expect(heartAfter).toHaveAttribute("aria-pressed", "true");
    await heartAfter.click();
    await expect(heartAfter).toHaveAttribute("aria-pressed", "false");
    await heartAfter.click(); // liked again, so the adult sees it below
    await expect(heartAfter).toHaveAttribute("aria-pressed", "true");
    await expect.poll(async () => {
      const list = (await (await admin.get(`/api/households/${h}/recipes?likedByProfileId=${await profileId(admin, h!)}`)).json()) as { items: { id: string }[] };
      return list.items.map((r) => r.id);
    }).toEqual([fish.id]);

    // ---- The week: Monday to Friday are meat, so Saturday offers only fish and vegetarian ----
    await page.goto(`/plan?week=${week}`);
    const saturday = dayOf(week, 5);
    const sunday = dayOf(week, 6);
    await expect(page.getByTestId("kid-day")).toHaveCount(7);
    if (mobile) {
      await atLeast(tile(page, saturday), 100, "day tile");
      await noHorizontalScroll(page);
    }
    await shot(page, "02-kid-week");
    await expect(page.getByTestId("kid-week-lock")).toHaveAttribute("data-locked", "false");

    // A kid cannot remove a grown-up's pick: no remove button, and the API refuses too.
    await tile(page, dayOf(week, 0)).click();
    const mondaySheet = page.getByRole("dialog");
    await expect(mondaySheet.getByTestId("kid-entry")).toHaveCount(1);
    await expect(mondaySheet.getByTestId("kid-remove")).toHaveCount(0);
    await mondaySheet.getByRole("button", { name: "Close" }).click();
    const refused = await page.request.delete(`/api/households/${h}/meal-plans/${week}/entries/${adultEntryId}`, { headers: await csrf(page.request) });
    expect(refused.status(), await refused.text()).toBe(403);

    await tile(page, saturday).click();
    const grid = page.getByTestId("kid-pick-grid");
    await expect(grid).toBeVisible();
    const choose = (title: string) => grid.getByRole("button", { name: `Choose ${title}` });
    await expect(choose(fish.title)).toBeVisible();
    await expect(choose(vegetarian.title)).toBeVisible();
    await expect(choose(pancakes.title)).toBeVisible();
    await expect(choose(beef.title)).toHaveCount(0);
    await expect(choose(chicken.title)).toHaveCount(0);
    await expect(grid.getByTestId("kid-pick")).toHaveCount(3);
    if (mobile) await atLeast(choose(fish.title), 100, "dish tile");
    await shot(page, "04-kid-pick");
    await choose(fish.title).click();
    await expect(page.getByTestId("kid-pick-sheet")).toHaveCount(0);
    await expect(tile(page, saturday)).toHaveAttribute("data-filled", "true");

    const planned = (await (await admin.get(`/api/households/${h}/meal-plans/${week}`)).json()) as {
      entries: { date: string; recipeId: string; pickedByProfile: { displayName: string } | null }[];
    };
    expect(planned.entries.find((e) => e.date === saturday)).toMatchObject({ recipeId: fish.id, pickedByProfile: { displayName: "Mia" } });

    // Sunday: the fish is in, so only the vegetarian dishes fit.
    await tile(page, sunday).click();
    await expect(grid.getByTestId("kid-pick")).toHaveCount(2);
    await expect(choose(vegetarian.title)).toBeVisible();
    await expect(choose(pancakes.title)).toBeVisible();
    await expect(choose(fish.title)).toHaveCount(0);
    await page.getByRole("dialog").getByRole("button", { name: "Close" }).click();

    // Her own pick can be removed (and the day opens up again).
    await tile(page, saturday).click();
    await page.getByRole("dialog").getByTestId("kid-remove").click();
    await expect(tile(page, saturday)).toHaveAttribute("data-filled", "false");

    // ---- Back to the adult: a wrong PIN, then the right one ----
    await page.getByTestId("kid-unlock").click();
    const pinSheet = page.getByRole("dialog", { name: "Enter your PIN" });
    const key = (digit: string) => pinSheet.getByRole("group", { name: "Keypad" }).getByRole("button", { name: digit, exact: true });
    if (mobile) await atLeast(key("5"), 64, "keypad key");
    await shot(page, "05-pin-sheet");
    for (const d of wrongPin) await key(d).click();
    await expect(pinSheet.getByTestId("pin-pips")).toBeVisible();
    await expect(pinSheet.getByTestId("pin-dots")).toHaveClass(/kid-shake/);
    await expect(pinSheet.getByTestId("pin-status")).toContainText("Wrong PIN");
    await shot(page, "06-pin-wrong");
    await expect(page.getByTestId("kid-shell")).toBeVisible();
    for (const d of pin) await key(d).click();
    await expect(page.getByTestId("user-email")).toBeVisible();
    await expect(page.getByTestId("kid-shell")).toHaveCount(0);
    await expect(page.getByRole("navigation", { name: "Main", exact: true })).toBeVisible();

    // The adult sees what the kid likes: the avatar on the card and the filter chip.
    await page.goto("/recipes");
    const card = page.getByTestId("recipe-card").filter({ hasText: fish.title });
    await expect(card.getByTestId("recipe-card-liked")).toHaveAccessibleName("Liked by Mia");
    await page.getByRole("group", { name: "Liked by" }).getByRole("button", { name: /Mia/ }).click();
    await expect(page.getByTestId("recipe-card")).toHaveCount(1);
    await expect(page.getByTestId("recipe-card")).toContainText(fish.title);
  } finally {
    // Deleting the household removes its profiles, likes, rules, plan and recipes. (The admin api context has no kid mode.)
    if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
    await admin.dispose();
  }
});

test("kid mode: a locked week shows a padlock and refuses picks, the adult can unlock it", async ({ page, baseURL }, testInfo) => {
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const week = uniqueWeek(testInfo, baseMonday, 1);
  const stamp = uniqueStamp(testInfo);
  const admin = await playwrightRequest.newContext({ baseURL, storageState: adminState });
  let h: string | undefined;

  try {
    const household = await setUpHousehold(admin, stamp);
    h = household.id;
    const member = await joinAsMember(page, admin, h);
    await setMemberPin(page.request, randomPin(), member.password);
    const created = await admin.post(`/api/households/${h}/profiles`, { headers: await csrf(admin), data: { displayName: "Ola", avatar: "owl" } });
    expect(created.status(), await created.text()).toBe(201);
    const profile = ((await created.json()) as { id: string }).id;

    // The adult locks the week on the planner.
    await page.goto(`/plan?week=${week}`);
    const lock = page.getByTestId("planner-lock-toggle");
    await expect(lock).toHaveText("Lock the week");
    if (mobile) await atLeast(lock, 44, "lock toggle");
    await lock.click();
    await expect(page.getByTestId("planner-lock")).toHaveAttribute("data-locked", "true");
    await expect(page.getByTestId("planner-lock")).toContainText("Locked: kids cannot change this week.");

    // The device becomes Ola's (through the API: the switch itself is covered by the first test).
    const activated = await page.request.post(`/api/households/${h}/profiles/${profile}/activate`, { headers: await csrf(page.request) });
    expect(activated.status(), await activated.text()).toBe(204);
    await page.goto(`/plan?week=${week}`);
    await expect(page.getByTestId("kid-week-lock")).toHaveAttribute("data-locked", "true");
    await expect(page.getByTestId("kid-week-lock")).toHaveAccessibleName("The week is locked");
    if (mobile) {
      await atLeast(page.getByTestId("kid-week-lock"), 56, "padlock");
      await noHorizontalScroll(page);
    }
    await shot(page, "07-kid-week-locked");
    const saturday = dayOf(week, 5);
    await expect(tile(page, saturday)).toHaveAttribute("aria-disabled", "true");
    await tile(page, saturday).click({ force: true }); // aria-disabled, but a child still taps it
    await expect(page.getByTestId("kid-pick-sheet")).toHaveCount(0); // nothing opens, no error text
    await expect(page.getByRole("alert")).toHaveCount(0);
    await expect(page.getByTestId("kid-week-lock")).toHaveClass(/kid-shake/);

    // The server refuses a kid's pick in a locked week with 409.
    const refused = await page.request.post(`/api/households/${h}/meal-plans/${week}/entries`, {
      headers: await csrf(page.request),
      data: { date: saturday, recipeId: household.recipes.fish.id, servings: null },
    });
    expect(refused.status()).toBe(409);

    // Unlocking (as the adult, in another context) shows on the kid's screen without a reload.
    const unlocked = await admin.delete(`/api/households/${h}/meal-plans/${week}/lock`, { headers: await csrf(admin) });
    expect(unlocked.status(), await unlocked.text()).toBe(204);
    await expect(page.getByTestId("kid-week-lock")).toHaveAttribute("data-locked", "false", { timeout: 15_000 });
    await expect(tile(page, saturday)).not.toHaveAttribute("aria-disabled", "true");
  } finally {
    if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
    await admin.dispose();
  }
});

// T-105: in kid mode the shopping list is look only: big tiles, checked items done, no controls, and the API refuses writes.
test("kid mode: the shopping list is read only", async ({ page, baseURL }, testInfo) => {
  const mobile = (page.viewportSize()?.width ?? 0) <= 390;
  const stamp = uniqueStamp(testInfo);
  const admin = await playwrightRequest.newContext({ baseURL, storageState: adminState });
  let h: string | undefined;
  // The kid view shows the current week, worked out in the browser's local time like the app does.
  const now = new Date();
  const monday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - ((now.getDay() + 6) % 7));
  const week = `${monday.getFullYear()}-${String(monday.getMonth() + 1).padStart(2, "0")}-${String(monday.getDate()).padStart(2, "0")}`;

  try {
    const created = await admin.post("/api/households", { headers: await csrf(admin), data: { name: `E2E kid list ${stamp}` } });
    expect(created.status(), await created.text()).toBe(201);
    h = ((await created.json()) as { id: string }).id;
    const member = await joinAsMember(page, admin, h);
    await setMemberPin(page.request, randomPin(), member.password);
    const profile = await admin.post(`/api/households/${h}/profiles`, { headers: await csrf(admin), data: { displayName: "Mia", avatar: "fox" } });
    expect(profile.status(), await profile.text()).toBe(201);
    const profileId = ((await profile.json()) as { id: string }).id;

    const add = async (label: string) => {
      const res = await admin.post(`/api/households/${h}/shopping-lists/${week}/items`, { headers: await csrf(admin), data: { label, quantity: null, unitId: null } });
      expect(res.status(), await res.text()).toBe(201);
      return ((await res.json()) as { id: string }).id;
    };
    await add(`Melk ${stamp}`);
    const doneId = await add(`Brod ${stamp}`);
    const checked = await admin.put(`/api/households/${h}/shopping-lists/${week}/items/${doneId}/check`, { headers: await csrf(admin), data: { checked: true } });
    expect(checked.ok()).toBe(true);

    const activated = await page.request.post(`/api/households/${h}/profiles/${profileId}/activate`, { headers: await csrf(page.request) });
    expect(activated.status(), await activated.text()).toBe(204);

    const writes: string[] = [];
    page.on("request", (r) => {
      if (r.method() !== "GET" && !r.url().includes("/api/hubs/")) writes.push(`${r.method()} ${new URL(r.url()).pathname}`);
    });
    await page.goto("/");
    await page.getByRole("link", { name: "Shopping list" }).click();
    const tiles = page.getByTestId("kid-shop-tile");
    await expect(tiles).toHaveCount(2);
    await expect(tiles.filter({ hasText: `Melk ${stamp}` }).getByTestId("kid-shop-check")).toHaveCount(0);
    await expect(tiles.filter({ hasText: `Brod ${stamp}` }).getByTestId("kid-shop-check")).toHaveCount(1);
    await expect(page.getByRole("main").getByRole("button")).toHaveCount(0);
    await expect(page.getByRole("checkbox")).toHaveCount(0);
    await expect(page.getByRole("textbox")).toHaveCount(0);
    if (mobile) {
      await atLeast(tiles.first(), 44, "tile");
      await noHorizontalScroll(page);
    }
    await shot(page, "08-kid-shopping");

    // A tap on a tile does nothing, and the grown-up pages are out of reach by address.
    await tiles.first().click();
    await page.goto("/pantry");
    await expect(page.getByTestId("kid-home")).toBeVisible();
    await page.goto("/shopping");
    await expect(page.getByTestId("kid-shopping")).toBeVisible();
    expect(writes).toEqual([]);

    // The server refuses the write too.
    const refused = await page.request.put(`/api/households/${h}/shopping-lists/${week}/items/${doneId}/check`, {
      headers: await csrf(page.request),
      data: { checked: false },
    });
    expect(refused.status()).toBe(403);
  } finally {
    if (h) await admin.delete(`/api/households/${h}`, { headers: await csrf(admin) });
    await admin.dispose();
  }
});

/** Signs the page in as the e2e member and joins the household through an Adult invitation (the flow household.spec covers). */
async function joinAsMember(page: Page, admin: APIRequestContext, h: string): Promise<{ email: string; password: string }> {
  const member = memberCredentials();
  const invite = await admin.post(`/api/households/${h}/invites`, { headers: await csrf(admin), data: { role: "Adult" } });
  expect(invite.status(), await invite.text()).toBe(201);
  const { token } = (await invite.json()) as { token: string };
  await signIn(page, `/invite/${token}`, member);
  await page.getByRole("button", { name: "Join household" }).click();
  await expect(page.getByText("You joined the household as Adult.")).toBeVisible();
  return member;
}

async function profileId(admin: APIRequestContext, h: string): Promise<string> {
  const list = (await (await admin.get(`/api/households/${h}/profiles`)).json()) as { id: string }[];
  return list[0]!.id;
}
