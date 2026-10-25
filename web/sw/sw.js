/* Foodplanner service worker (T-041). Hand-written on purpose: ~100 lines, no dependency to pin, and the strategies are
 * few. `npm run build` runs scripts/build-sw.mjs, which fills the two placeholders below and writes build/client/sw.js.
 *
 *   Request                                          Strategy
 *   -----------------------------------------------  -------------------------------------------------------------
 *   navigation (any page)                            precached /index.html (the SPA shell); /offline.html if missing
 *   /assets/*, icons, manifest, offline.html         precache (cache first, by this build's cache)
 *   GET /api/households/{h}/recipes/{r}/image/...    CacheFirst, runtime cache, capped (immutable, versioned urls)
 *   GET /api/households/{h}/recipes, .../recipes/{r}, NetworkFirst (3 s timeout), runtime cache, capped
 *       .../tags
 *   everything else (auth, antiforgery, shopping list, NOT intercepted: the browser goes to the network as if there
 *   SignalR hub, plan, any POST/PUT/DELETE, other      were no service worker. The shopping list stays with T-036's
 *   origins)                                           localStorage cache and queue.
 *
 * The runtime caches hold per-user data. The page deletes them on log out and on login (app/pwa/clear-caches.ts),
 * so a shared device never shows the previous user's recipes.
 */
const PRECACHE_URLS = /*PRECACHE_URLS*/ [];
const BUILD_ID = "/*BUILD_ID*/";

const PRECACHE = `fp-precache-${BUILD_ID}`;
// Names must match app/pwa/clear-caches.ts (prefix "fp-runtime-").
const API_CACHE = "fp-runtime-api";
const IMAGE_CACHE = "fp-runtime-images";
const API_MAX_ENTRIES = 60;
const IMAGE_MAX_ENTRIES = 150;
const API_TIMEOUT_MS = 3000;

const imagePath = /^\/api\/households\/[^/]+\/recipes\/[^/]+\/image\/[^/]+\/[^/]+$/;
const recipeApiPath = /^\/api\/households\/[^/]+\/(recipes(\/[^/]+)?|tags)$/;

self.addEventListener("install", (event) => {
  // The new worker waits (the page shows "New version available") until the page sends SKIP_WAITING.
  event.waitUntil(caches.open(PRECACHE).then((cache) => cache.addAll(PRECACHE_URLS)));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      for (const name of await caches.keys()) {
        if (name.startsWith("fp-precache-") && name !== PRECACHE) await caches.delete(name);
      }
      await self.clients.claim();
    })(),
  );
});

self.addEventListener("message", (event) => {
  if (event.data && event.data.type === "SKIP_WAITING") void self.skipWaiting();
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (url.pathname.startsWith("/api/")) {
    if (request.mode === "navigate") return; // e.g. an API docs page: not the SPA
    if (imagePath.test(url.pathname)) event.respondWith(cacheFirst(request, IMAGE_CACHE, IMAGE_MAX_ENTRIES));
    else if (recipeApiPath.test(url.pathname)) event.respondWith(networkFirst(request, API_CACHE, API_MAX_ENTRIES));
    // else: not intercepted.
  } else if (request.mode === "navigate") {
    event.respondWith(shell());
  } else if (PRECACHE_URLS.includes(url.pathname)) {
    event.respondWith(precached(request, url.pathname));
  }
});

async function shell() {
  const cache = await caches.open(PRECACHE);
  return (await cache.match("/index.html")) || (await cache.match("/offline.html")) || Response.error();
}

async function precached(request, pathname) {
  const cache = await caches.open(PRECACHE);
  return (await cache.match(pathname)) || fetch(request);
}

async function cacheFirst(request, cacheName, maxEntries) {
  const cache = await caches.open(cacheName);
  const hit = await cache.match(request);
  if (hit) return hit;
  const response = await fetch(request);
  if (response.ok) {
    await cache.put(request, response.clone());
    await trim(cache, maxEntries);
  }
  return response;
}

async function networkFirst(request, cacheName, maxEntries) {
  const cache = await caches.open(cacheName);
  const network = fetch(request).then(async (response) => {
    if (response.ok) {
      await cache.put(request, response.clone());
      await trim(cache, maxEntries);
    }
    return response;
  });
  network.catch(() => undefined); // handled below; avoids an unhandled rejection when the timeout wins
  const timeout = new Promise((resolve) => setTimeout(resolve, API_TIMEOUT_MS, "timeout"));
  try {
    // A fast answer wins. After the timeout the cached copy is used if there is one; otherwise keep waiting.
    const first = await Promise.race([network, timeout]);
    if (first !== "timeout") return first;
    return (await cache.match(request)) || (await network);
  } catch (error) {
    const hit = await cache.match(request);
    if (hit) return hit;
    throw error;
  }
}

/** Cap: drop the oldest entries (the cache lists keys in insertion order; a refreshed entry is re-inserted). */
async function trim(cache, maxEntries) {
  const keys = await cache.keys();
  for (let i = 0; i < keys.length - maxEntries; i++) await cache.delete(keys[i]);
}
