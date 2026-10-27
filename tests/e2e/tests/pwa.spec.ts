import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import type { APIRequestContext, Page } from "@playwright/test";
import { adminState, expect, signIn, test, uniqueStamp, uniqueWeek } from "./fixtures";
import { solidPng } from "./png";

// T-041: install, offline shell, recently viewed recipes offline, update banner. Every other spec runs with the
// service worker blocked (playwright.config.ts) so route mocks and cache-free reads behave as before; here it is on.
test.use({ storageState: adminState, serviceWorkers: "allow" });

// A Monday far away from the weeks other specs use (shopping.spec starts at 2031-03-03); 2037-01-05 is a Monday.
const baseMonday = Date.UTC(2037, 0, 5);

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const { token } = (await (await request.get("/api/auth/antiforgery")).json()) as { token: string };
  return { "X-XSRF-TOKEN": token };
}

async function householdId(request: APIRequestContext): Promise<string> {
  const households = (await (await request.get("/api/households")).json()) as { id: string }[];
  return households[0]!.id;
}

/** A recipe with a picture, through the API. */
async function createRecipe(request: APIRequestContext, title: string): Promise<{ id: string; household: string }> {
  const household = await householdId(request);
  const ingredients = (await (await request.get(`/api/households/${household}/ingredients`)).json()) as { id: string; isStaple: boolean }[];
  const units = (await (await request.get("/api/units")).json()) as { id: string; code: string }[];
  const res = await request.post(`/api/households/${household}/recipes`, {
    headers: await csrf(request),
    data: {
      title,
      description: null,
      servings: 4,
      prepMinutes: 5,
      cookMinutes: 10,
      steps: [{ text: "Forste steg" }, { text: "Andre steg" }],
      ingredients: [{ ingredientId: ingredients.find((i) => !i.isStaple)!.id, quantity: 500, unitId: units.find((u) => u.code === "g")!.id, note: null }],
      tagIds: [],
    },
  });
  expect(res.status(), await res.text()).toBe(201);
  const id = ((await res.json()) as { id: string }).id;
  const image = await request.put(`/api/households/${household}/recipes/${id}/image`, {
    headers: await csrf(request),
    multipart: { file: { name: "soup.png", mimeType: "image/png", buffer: solidPng() } },
  });
  expect(image.status(), await image.text()).toBeLessThan(300);
  return { id, household };
}

async function deleteRecipe(request: APIRequestContext, household: string, id: string) {
  await request.delete(`/api/households/${household}/recipes/${id}`, { headers: await csrf(request) });
}

/** Waits until the service worker is active and controls this page (the first install claims the page). */
async function controlled(page: Page) {
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
    if (!navigator.serviceWorker.controller) {
      await new Promise<void>((resolve) => navigator.serviceWorker.addEventListener("controllerchange", () => resolve(), { once: true }));
    }
  });
}

const cacheKeys = (page: Page) =>
  page.evaluate(async () => {
    const out: Record<string, string[]> = {};
    for (const name of await caches.keys()) out[name] = (await (await caches.open(name)).keys()).map((r) => new URL(r.url).pathname + new URL(r.url).search);
    return out;
  });

const imageLoaded = (page: Page) =>
  page.locator(".recipe-image img").first().evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0);

test("manifest, icons and service worker make the app installable", async ({ page }) => {
  await page.goto("/recipes");
  await controlled(page);

  // Static files with the right content types and cache headers (nginx).
  const manifestResponse = await page.request.get("/manifest.webmanifest");
  expect(manifestResponse.headers()["content-type"]).toContain("application/manifest+json");
  expect(manifestResponse.headers()["cache-control"]).toBe("no-cache");
  const swResponse = await page.request.get("/sw.js");
  expect(swResponse.headers()["content-type"]).toMatch(/javascript/);
  expect(swResponse.headers()["cache-control"]).toBe("no-cache");
  expect((await page.request.get("/")).headers()["cache-control"]).toBe("no-cache");
  const asset = await page.evaluate(() => performance.getEntriesByType("resource").map((e) => e.name).find((n) => /\/assets\/.*\.js$/.test(n)));
  expect((await page.request.get(asset!)).headers()["cache-control"]).toContain("immutable");

  const manifest = (await manifestResponse.json()) as {
    name: string; short_name: string; start_url: string; display: string; theme_color: string; background_color: string;
    icons: { src: string; sizes: string; type: string; purpose?: string }[];
  };
  expect(manifest).toMatchObject({ name: "Foodplanner", short_name: "Foodplanner", start_url: "/", display: "standalone" });
  expect(manifest.theme_color).toMatch(/^#[0-9a-f]{6}$/i);
  expect(manifest.icons.map((i) => `${i.sizes} ${i.purpose}`)).toEqual(expect.arrayContaining(["192x192 any", "512x512 any", "512x512 maskable"]));
  for (const icon of [...manifest.icons.map((i) => i.src), "/icons/apple-touch-icon.png"]) {
    const res = await page.request.get(icon);
    expect(res.status(), icon).toBe(200);
    expect(res.headers()["content-type"], icon).toBe("image/png");
  }
  await expect(page.locator('link[rel="manifest"]')).toHaveAttribute("href", "/manifest.webmanifest");
  await expect(page.locator('link[rel="apple-touch-icon"]')).toHaveCount(1);
  await expect(page.locator('meta[name="theme-color"]')).toHaveCount(1);

  // The service worker is registered, active, controls the page and has precached the shell.
  const registration = await page.evaluate(async () => {
    const reg = await navigator.serviceWorker.getRegistration("/");
    return { scope: reg?.scope, active: reg?.active?.state, controlled: navigator.serviceWorker.controller !== null };
  });
  expect(registration).toEqual({ scope: new URL("/", page.url()).href, active: "activated", controlled: true });
  const caches = await cacheKeys(page);
  const precache = Object.entries(caches).find(([name]) => name.startsWith("fp-precache-"))?.[1] ?? [];
  expect(precache).toEqual(expect.arrayContaining(["/index.html", "/offline.html", "/manifest.webmanifest", "/icons/icon-192.png"]));

  // Chrome's own installability check (Lighthouse 12 removed the PWA category): the same errors DevTools shows
  // under Application > Manifest, via CDP Page.getInstallabilityErrors, and the parsed manifest via getAppManifest.
  const cdp = await page.context().newCDPSession(page);
  const parsed = (await cdp.send("Page.getAppManifest")) as { errors: { message: string; critical: boolean }[]; url?: string };
  expect(parsed.errors.filter((e) => e.critical)).toEqual([]);
  expect(parsed.url).toContain("/manifest.webmanifest");
  const installability = (await cdp.send("Page.getInstallabilityErrors")) as { installabilityErrors: { errorId: string }[] };
  expect(installability.installabilityErrors).toEqual([]);
});

/** Narrows the recipe list to `title` with the search box (the list is paged, so a new recipe is not always on page one). */
async function findInList(page: Page, title: string) {
  await page.getByLabel("Search recipes").fill(title);
  await expect(page.getByRole("link", { name: title }).first()).toBeVisible();
}

test("recently viewed recipes, their picture and the shell open offline; an unopened recipe shows the offline page", async ({ page, context, request }, testInfo) => {
  const title = `PWA ${uniqueStamp(testInfo)}`;
  const recipe = await createRecipe(request, title);
  try {
    // Online: list, then the recipe (this is what makes it "recently viewed").
    await page.goto("/recipes");
    await controlled(page);
    await page.reload(); // the first load was before the worker took over; from now on its requests go through it
    // The list is paged and sorted by title and the shared dev database holds many recipes: find ours by searching (T-082).
    await findInList(page, title);
    await page.getByRole("link", { name: title }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await expect.poll(() => imageLoaded(page)).toBe(true);
    const online = await cacheKeys(page);
    expect(Object.keys(online)).toEqual(expect.arrayContaining(["fp-runtime-api", "fp-runtime-images"]));
    expect(online["fp-runtime-api"]).toContain(`/api/households/${recipe.household}/recipes/${recipe.id}`);
    expect(online["fp-runtime-images"]!.some((u) => u.includes(`/recipes/${recipe.id}/image/`))).toBe(true);

    // Offline: reload the recipe page itself (the page cannot come from the network), then go back to the list in-app.
    await context.setOffline(true);
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await expect(page.getByText("Forste steg")).toBeVisible();
    await expect.poll(() => imageLoaded(page)).toBe(true);
    await page.getByRole("link", { name: "All recipes" }).click();
    await findInList(page, title); // the same search URL as online, so the cached list answers

    // A recipe never opened on this device: the translated offline page, with a way back.
    await page.goto("/recipes/00000000-0000-4000-8000-000000000041");
    const notice = page.getByTestId("offline-notice");
    await expect(notice).toContainText("You are offline");
    const back = notice.getByRole("link", { name: "Go to recipes" });
    expect((await back.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await back.click();
    await findInList(page, title);
  } finally {
    await context.setOffline(false);
    await deleteRecipe(request, recipe.household, recipe.id);
  }
});

test("the service worker leaves auth, antiforgery, plan and shopping list requests alone and caches no writes", async ({ page, context }, testInfo) => {
  const household = await householdId(page.request);
  const label = `E2E sw ${uniqueStamp(testInfo)}`;
  const week = uniqueWeek(testInfo, baseMonday, 0);
  await page.goto("/shopping?week=" + week);
  await controlled(page);
  await page.reload();
  await expect(page.getByRole("heading", { name: /shopping/i }).first()).toBeVisible();

  // Stale-data check: a list changed behind the page's back shows up on the next load, so the worker did not cache it.
  const add = await page.request.post(`/api/households/${household}/shopping-lists/${week}/items`, {
    headers: await csrf(page.request),
    data: { label, quantity: null, unitId: null },
  });
  expect(add.status()).toBe(201);
  const itemId = ((await add.json()) as { id: string }).id;
  try {
    await page.reload();
    await expect(page.getByRole("checkbox", { name: label })).toBeVisible();

    const caches = await cacheKeys(page);
    const runtime = Object.entries(caches).filter(([name]) => name.startsWith("fp-runtime-")).flatMap(([, urls]) => urls);
    for (const url of runtime) expect(url, url).toMatch(/^\/api\/households\/[^/]+\/(recipes|tags)/);
    expect(JSON.stringify(runtime)).not.toMatch(/auth|antiforgery|shopping|hubs|plan/);
  } finally {
    await context.setOffline(false);
    await page.request.delete(`/api/households/${household}/shopping-lists/${week}/items/${itemId}`, { headers: await csrf(page.request) });
  }
});

test("shopping list offline behaviour of T-036 works with the service worker too (shell now loads offline)", async ({ page, context }, testInfo) => {
  const household = await householdId(page.request);
  const label = `E2E sw offline ${uniqueStamp(testInfo)}`;
  const week = uniqueWeek(testInfo, baseMonday, 1);
  const add = await page.request.post(`/api/households/${household}/shopping-lists/${week}/items`, {
    headers: await csrf(page.request),
    data: { label, quantity: null, unitId: null },
  });
  const itemId = ((await add.json()) as { id: string }).id;
  try {
    await page.goto(`/shopping?week=${week}`);
    await controlled(page);
    await expect(page.getByRole("checkbox", { name: label })).toBeVisible();
    await page.reload(); // let the list be cached by the app (localStorage), as in normal use
    await expect(page.getByRole("checkbox", { name: label })).toBeVisible();

    await context.setOffline(true);
    await page.reload(); // the whole page offline: the shell comes from the service worker, the list from localStorage
    await expect(page.getByRole("checkbox", { name: label })).toBeVisible();
    await expect(page.getByTestId("offline-banner")).toBeVisible();
    await page.getByRole("checkbox", { name: label }).check();
    await expect(page.getByTestId("pending-sync")).toContainText("1 change waiting to sync");

    await context.setOffline(false);
    await expect(page.getByTestId("pending-sync")).toHaveCount(0, { timeout: 20_000 });
    const list = (await (await page.request.get(`/api/households/${household}/shopping-lists/${week}`)).json()) as { main: { id: string; checked: boolean }[] };
    expect(list.main.find((i) => i.id === itemId)?.checked).toBe(true);
  } finally {
    await context.setOffline(false);
    await page.request.delete(`/api/households/${household}/shopping-lists/${week}/items/${itemId}`, { headers: await csrf(page.request) });
  }
});

/**
 * A tiny reverse proxy in front of the web container whose /sw.js can be switched to a "newly deployed" build (same
 * worker, other BUILD_ID). Playwright's route() cannot intercept the browser's own fetch of a service worker script,
 * so the update flow is exercised through a real second origin instead of a mock.
 */
async function startProxy(): Promise<{ origin: string; deploy: () => void; close: () => Promise<void> }> {
  const target = process.env.BASE_URL ?? "http://localhost:3000";
  let deployed = false;
  const server = createServer((req, res) => {
    void (async () => {
      const upstream = await fetch(new URL(req.url ?? "/", target), { headers: { accept: String(req.headers.accept ?? "*/*"), "accept-language": "en-US" } });
      let body = Buffer.from(await upstream.arrayBuffer());
      if (deployed && req.url === "/sw.js") body = Buffer.from(body.toString("utf8").replace(/const BUILD_ID = "[^"]*";/, 'const BUILD_ID = "deployed-e2e";'));
      const headers: Record<string, string> = {};
      upstream.headers.forEach((value, key) => {
        if (!["content-encoding", "content-length", "transfer-encoding", "connection"].includes(key)) headers[key] = value;
      });
      res.writeHead(upstream.status, headers).end(body);
    })().catch(() => res.writeHead(502).end());
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return {
    origin: `http://localhost:${port}`,
    deploy: () => {
      deployed = true;
    },
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  };
}

test("a changed service worker shows 'New version available' and Reload activates it", async ({ browser }) => {
  const proxy = await startProxy();
  const context = await browser.newContext({ serviceWorkers: "allow", storageState: { cookies: [], origins: [] }, baseURL: proxy.origin, viewport: { width: 390, height: 844 } });
  const page = await context.newPage();
  try {
    await page.goto("/login"); // the banner lives in the root, so no sign-in is needed
    await controlled(page);
    await expect(page.getByTestId("update-banner")).toHaveCount(0);
    const oldCaches = Object.keys(await cacheKeys(page)).filter((n) => n.startsWith("fp-precache-"));
    expect(oldCaches).toHaveLength(1);

    // "Deploy": the next /sw.js differs, so the browser installs a new worker next to the running one and it waits.
    proxy.deploy();
    await page.evaluate(async () => {
      const reg = await navigator.serviceWorker.getRegistration("/");
      await reg!.update();
    });
    const banner = page.getByTestId("update-banner");
    await expect(banner).toContainText("New version available");
    expect(Object.keys(await cacheKeys(page))).toContain("fp-precache-deployed-e2e"); // installed and precached, not yet active
    const reload = banner.getByRole("button", { name: "Reload" });
    expect((await reload.boundingBox())!.height).toBeGreaterThanOrEqual(44);

    const navigated = page.waitForEvent("framenavigated");
    await reload.click();
    await navigated;
    await controlled(page);
    await expect(page.getByTestId("update-banner")).toHaveCount(0);
    // The new worker is active and the old version's precache is gone.
    await expect.poll(async () => Object.keys(await cacheKeys(page)).filter((n) => n.startsWith("fp-precache-"))).toEqual(["fp-precache-deployed-e2e"]);
    expect(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration("/"))!.waiting)).toBeNull();
  } finally {
    await context.close();
    await proxy.close();
  }
});

test("log out clears the recipes and pictures the service worker kept (shared device)", async ({ browser, request }, testInfo) => {
  const title = `PWA logout ${uniqueStamp(testInfo)}`;
  const recipe = await createRecipe(request, title);
  // A separate browser context: its own cookies, caches and service worker, so logging out cannot affect other tests.
  const owner = await browser.newContext({ serviceWorkers: "allow", storageState: { cookies: [], origins: [] }, viewport: { width: 390, height: 844 } });
  const page = await owner.newPage();
  try {
    await signIn(page, "/recipes");
    await controlled(page);
    await page.goto(`/recipes?q=${encodeURIComponent(title)}`); // through the worker now; the search keeps it on the first page
    await page.getByRole("link", { name: title }).first().click();
    await expect.poll(() => imageLoaded(page)).toBe(true);
    const before = await cacheKeys(page);
    expect(Object.keys(before)).toEqual(expect.arrayContaining(["fp-runtime-api", "fp-runtime-images"]));

    await page.getByRole("button", { name: "Log out" }).click();
    await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
    await expect.poll(async () => Object.keys(await cacheKeys(page)).filter((n) => n.startsWith("fp-runtime-"))).toEqual([]);
    // The app itself stays cached; it holds no user data.
    expect(Object.keys(await cacheKeys(page)).some((n) => n.startsWith("fp-precache-"))).toBe(true);
  } finally {
    await owner.close();
    await deleteRecipe(request, recipe.household, recipe.id);
  }
});
