# web

React Router v7 SPA (ssr: false), TypeScript strict.

- `npm ci` then `npm run dev` serves http://localhost:5173. `/api` is proxied to `API_PROXY_TARGET` (default `http://localhost:5000`).
  Start the API with `dotnet run --project src/Api --urls http://localhost:5000`.
- `npm run build` writes the static SPA to `build/client`. `npm run lint`, `npm run typecheck`, `npm test`.
- `npm run api:generate` regenerates `app/api/schema.d.ts` from `../openapi/openapi.v1.json`; `npm run api:check` fails if it is stale.
  Never hand-edit the generated file; use the `api` client in `app/api/client.ts`.
- The container build (`web/Dockerfile`) runs `npm ci` and `npm run build`, then serves `build/client` with nginx (SPA fallback, `/api` proxied to the api service).

## Shopping list screen (T-036, `/shopping`)
- Code: `app/routes/shopping.tsx` (UI, all text via `t()`, see Internationalisation), `app/shopping/use-shopping-list.ts` (state), `realtime.ts` (SignalR client following the contract in docs/domain.md "Shopping list sync"), `queue.ts` (offline check-off queue), `storage.ts` (localStorage cache and queue, every access in try/catch).
- Week is in the URL (`?week=yyyy-MM-dd`, a Monday; default the current ISO week). Writes go through REST; the hub only signals changes and serves `GetState`.
- Offline: the list of each household and week is cached in localStorage; check-offs are queued as intended states (`checked: true|false`, idempotent), one per item (latest wins), persisted, shown optimistically, replayed in order when the API answers again (online event, reconnect, or a 5 s retry), then the list is re-read. The last confirmed session (user and households) is also kept so a reload while the API is down can render the cached list; it is cleared on log out, together with cached lists and the queue.
- The service worker (T-041, see "PWA" below) only makes the app shell load offline; it never touches the shopping list API, so this localStorage cache and queue stay the single source of truth. Offline means "the API is unreachable": either the page is open, or (since T-041, once the worker is installed) the page itself is reloaded offline from the precache. Generate, add and remove need the API and are disabled offline.

## Recipe grid (T-024, `/recipes`, `/recipes/:id`)
- Code: `app/routes/recipes.tsx` (screen), `app/recipes/filters.ts` (filters <-> URL, pure), `use-recipes.ts` (list with `limit`/`offset` paging, tags), `recipe-image.tsx` (the only place that knows image urls), `recipes.css`; `app/routes/recipe-detail.tsx` is a minimal read-only page that T-025 replaces.
- Filters live in the URL: `?q=text&tag=<id>&tag=<id>&max=30`. Chips and "clear filters" add a history entry (back undoes them), typing replaces the entry after a 300 ms pause. Several tags are AND (T-023). Malformed values in the URL are ignored.
- Paging is a "Show more" button (offset paging, 12 per page). Two columns at 390px (cards are 2 x 173px), more on wider screens; chips, cards and buttons are at least 44px.
- `RecipeImage` shows the card/thumb image (lazy, async decode, `srcset` 160w/640w, 4:3 box so nothing shifts) or, without one (or if it fails to load), a coloured tile with the initial. Decorative (`alt=""`, the title is beside it).

### Performance budget (Lighthouse, mobile preset)
Measured with `lighthouse@12` (mobile preset: Moto G Power emulation, simulated slow 4G, 4x CPU), headless Chrome, against the Docker stack at http://localhost:3000/recipes, signed in by sending the saved Playwright session cookie (`--extra-headers`), 3 runs each. T-024 (2026-10-24) had 3 demo recipes in the household; T-041 (2026-10-25) re-measured with the service worker and manifest in place and 2 demo recipes (the browser profile is fresh per run, so the worker never serves the measured load).

| Metric | Before gzip (T-024) | After gzip (T-024) | T-041 (3 runs) | Budget |
|---|---|---|---|---|
| Performance score | 75-83 | 89-96 | 89-95 | >= 85 |
| LCP | 3.7-3.8 s | 2.4-2.5 s | 2.5-2.6 s | <= 2.5 s (watch) |
| FCP | 3.3-3.4 s | 2.0-2.2 s | 2.0 s | <= 2.0 s (watch) |
| TBT | 1-38 ms | 3-14 ms | 0 ms | <= 200 ms |
| CLS | 0.05-0.15 | 0.05-0.15 | 0.05-0.15 | <= 0.1 (one run in three is 0.15: the filter chips appear after the tags load; fix by reserving their space) |
| Transferred | 397 KB | 156 KB | 169-171 KB | <= 250 KB |
| Accessibility / Best practices | 100 / 100 | 100 / 100 | 100 / 100 | 100 / 100 |

The first run showed nginx sent the SPA uncompressed, so `web/nginx.conf` now enables gzip for text assets. Re-measure when images are many (`npx lighthouse@12 http://localhost:3000/recipes --extra-headers=<json with Cookie> --only-categories=performance`). Lighthouse 12 has no PWA category any more; installability is checked in Playwright (see "PWA" below).

### Bundle budget (`web/scripts/check-bundle-size.mjs`, run by verify.sh)
React Router already emits one chunk per route and loads it on navigation. Until T-041 the check summed every chunk (168.5 KB of 170 KB), which counted routes a visit never downloads and left no room for the service worker. It now measures three gzipped JS numbers read from the build (the React Router manifest tells which chunks belong to which route):

| Number | What it is | Now | Budget |
|---|---|---|---|
| Initial | what `index.html` loads before any route: router manifest, entry, root route, React, i18n, session | 124.9 KB | 140 KB |
| First load | the initial JS plus one route (and its layout); the budget applies to the heaviest route (`/shopping`, it carries SignalR) | 145.0 KB (others 126-139 KB) | 170 KB (the former total budget, unchanged) |
| Total | every chunk; the service worker precaches all of them in the background | 170.0 KB | 210 KB |

Run `node web/scripts/check-bundle-size.mjs --print` for the per-route list. The total budget is a proposal: the old 170 KB plus about 20% headroom for the next screens (T-042, T-043, T-050), since a cold visit pays only "first load" and the rest is a one-time background download. The next saving, if needed, is loading the `nb` messages only when that locale is active (the `problems` chunk, 11 KB gzipped, holds both languages). Raising a budget is a deliberate change to this table and the script.

## PWA: install and offline shell (T-041)
- **Manifest** `public/manifest.webmanifest` (served as `application/manifest+json`, `no-cache`): name and short name "Foodplanner" (the app name is a proper noun and is not translated, so one static manifest serves nb and en; `lang` is `en` and the description is English only), `start_url` `/`, `display` `standalone`, theme colour = the header green `#1f6f43`, white background. Icons are placeholders drawn by `scripts/generate-icons.mjs` (no external assets; rerun and commit to change them): `icon-192`, `icon-512`, `maskable-512` and `apple-touch-icon` (180) in `public/icons/`. `app/root.tsx` links the manifest, theme colour and icons.
- **Service worker** `sw/sw.js`, hand-written (about 100 lines, no dependency to pin; Workbox would add `vite-plugin-pwa` plus a dozen packages and a build-plugin that has to cooperate with React Router's two-pass SPA build, for five simple strategies). `npm run build` runs `scripts/build-sw.mjs` after `react-router build`: it writes `build/client/sw.js` with the precache list (shell, every chunk and stylesheet, icons, manifest, offline page) and a build id (hash of those files), so the browser sees a new worker exactly when the app changed.

| Request | Strategy |
|---|---|
| Navigation to any page | the precached `/index.html` (the SPA boots from it, so every route opens offline); `/offline.html` if the shell is missing. Not for `/api/` |
| `/assets/*`, icons, manifest, `offline.html` | precache (per build, old builds' caches are deleted on activate) |
| `GET /api/households/{h}/recipes/{r}/image/{version}/{size}` | CacheFirst in `fp-runtime-images`, 150 entries (the version in the url makes it immutable; the response is `private`, which is why the cache is cleared on log out) |
| `GET /api/households/{h}/recipes`, `.../recipes/{id}`, `.../tags` | NetworkFirst with a 3 s timeout in `fp-runtime-api`, 60 entries, only 2xx are stored: this is "recently viewed recipes" (the list as last seen, with its filters and page, and each opened recipe) |
| Everything else: `/api/auth/*` (login, me, antiforgery, logout), households, shopping list, plan, SignalR hubs, any POST/PUT/DELETE, other origins | not intercepted: the browser talks to the network as if there were no worker. The shopping list stays entirely with T-036's localStorage cache and queued check-offs; the worker never sees or stores it |

- **Per-user data and logout:** the runtime caches (`fp-runtime-*`) hold one user's data, so `app/pwa/clear-caches.ts` deletes them on log out, on a successful login, and when the server says the session is gone (401 from `/api/auth/me`). The precache (the app itself) holds no user data and stays. Residual risk: a request still in flight at the instant of logout could store its answer after the cleanup; the next login clears it again. Recipes are cached per url, which contains the household id.
- **Update flow:** a new worker installs and waits (no `skipWaiting` on its own). `app/pwa/use-service-worker.ts` registers `/sw.js` (production build only), sees the waiting worker and `UpdateBanner` (`app/pwa/update-banner.tsx`, in the root) shows "New version available" with a 44px "Reload" button; Reload posts `SKIP_WAITING`, and once the new worker controls the page the page reloads (never on the very first install). It checks for a new version when the tab becomes visible and every hour.
- **Offline page:** with the shell precached, an uncached route still opens; a recipe or list whose data was never fetched on this device shows the translated `OfflineNotice` (`pwa.offline*` keys) instead of a raw error (when the browser reports offline; otherwise the usual "could not reach the API"). `public/offline.html` is a static last resort that picks nb or en from the browser language.
- **nginx** (`web/nginx.conf`): `/assets/*` are content-hashed, so `public, max-age=31536000, immutable`; `index.html` (also the SPA fallback), `sw.js`, `offline.html` and the manifest are `no-cache`; icons one day. `sw.js` sits at the root, so its scope is `/` and no `Service-Worker-Allowed` header is needed. The cache header is set from a `map` on the path, so the server-level `Referrer-Policy` is kept on every response.
- **Tests:** Vitest `app/pwa/*.test.ts(x)` (update banner and flow, cache clearing, offline notice) and `routes/auth.test.tsx` (logout and login clear the runtime caches). Playwright `tests/e2e/tests/pwa.spec.ts`: manifest, icons and headers; the worker registered with the shell precached; Chrome's installability via CDP (`Page.getAppManifest` has no critical errors, `Page.getInstallabilityErrors` is empty; Lighthouse 12 dropped the PWA category); load `/recipes` and a recipe with a picture, go offline (`context.setOffline`), reload: shell, recipe, steps and picture render, the list opens, a never-opened recipe shows the offline page; the worker does not touch auth, plan or shopping requests and its caches hold only recipes, tags and pictures; T-036's offline check-off flow works with the worker on (and now also on a full offline reload); the update banner appears after a "deploy" (a small proxy in the test serves `/sw.js` with another build id, because `route()` cannot intercept the browser's own fetch of a worker script) and Reload activates it; log out empties the runtime caches. All other specs run with `serviceWorkers: "block"` (`playwright.config.ts`) so their route mocks and fresh reads are unchanged.
- First visit: the worker installs after the first page load, so requests of that very first load are not cached; recipes become available offline once opened (or the list seen) while the worker is active.

## Week planner (T-035, `/plan`)
- Code: `app/routes/plan.tsx` (screen and picker sheet), `app/planner/use-plan.ts` (week state and writes on the generated client), `avatar.ts` (initials and colour of who picked), `planner.css`. Keys are `planner.*`; the nav link is `nav.plan`.
- The week is in the URL (`?week=yyyy-MM-dd`, a Monday, default the current ISO week) and reuses `shopping/week.ts`. Day names and dates come from `Intl` in the active locale.
- Picking: the day's round "+" (56px) opens a bottom sheet with the recipe grid (search and category chips, same `useRecipeList`/`useTags` as `/recipes`); tapping a recipe POSTs the entry with the recipe's default servings and closes the sheet. That is 2 taps from the week view (Playwright "Kid can pick a meal in <=3 taps" asserts exactly 2).
- Servings are - / + steppers (1 to 50, PUT); remove is a x button. Adult and Owner may change any entry; a Child sees the stepper and remove only on their own picks (others show the servings as text), matching the API's `MealPlanEntryOwnerOrAdult` policy. 400/403/404/409 show translated messages; 404 and 409 re-read the week.
- Plan entries carry no image urls, so the screen reads each distinct recipe once for its card image (placeholder tile on failure). A plan response with image urls would remove those requests.
- "Make shopping list" POSTs generate and opens `/shopping?week=...`; "See shopping list" links there without generating.

## Recipe detail and edit (T-025, `/recipes/:id`, `/recipes/new`, `/recipes/:id/edit`)
- Detail: `routes/recipe-detail.tsx` (picture via `RecipeImage` with thumb/card/full in `srcset`, times, tags, ingredients, steps). The servings stepper asks `GET .../recipes/{id}/scaled?servings=n` (T-040, `recipes/use-scaled-ingredients.ts`) and shows the amounts it returns; the client does no scaling math and nothing is saved. At the recipe's own servings the stored lines are shown and no request is made.
- Edit/new: `routes/recipe-edit.tsx` (one component for both routes), rows in `recipes/ingredient-rows.tsx`, `step-rows.tsx`, `ingredient-picker.tsx` (searches `GET .../ingredients?search=`, creates the household's own ingredient inline), `form-parts.tsx`. Pure logic and tests: `recipes/recipe-form.ts` (client validation with the T-021 limits, request building, `mapServerErrors`).
- Role: `useActiveRole()` reads the role of the active household from `GET /api/households`. A Child gets no New/Edit/Delete/upload controls and a message on the edit routes; the API refuses their writes with 403 regardless.
- Quantity and unit come together; "to taste" is a checkbox that sends neither. No free-text ingredients (rule 5): every line points at an ingredient id.
- Validation: the client checks first (error summary with focus, `aria-invalid`, `aria-describedby`). If the server still answers 400, its `errors` keys (`ingredients[2].unitId`, `tagIds[0]`, ...) are mapped to the same fields with translated messages; the server texts are never shown.
- Picture: chosen with a file input, previewed, and sent (PUT multipart part `file`) or removed (DELETE) after the recipe is saved. The generated schema types a binary part as `string`, so the `File` is cast once in `applyPicture`. If the recipe saved but the picture failed, the page stays and the next save updates the same recipe.
- Delete asks first; `409 recipe-in-use` shows a translated message. i18n keys for this screen are in the `recipe.*` namespace.

## Cooking mode (T-040, `/recipes/:id/cook`)
- Code: `routes/recipe-cook.tsx`, `recipes/use-wake-lock.ts`, `use-scaled-ingredients.ts`, `use-recipe.ts`, `cook.css`. Keys: `cook.*`. Entered with "Start cooking" on the recipe page (which passes its chosen servings as `?servings=`, 1 to 100, else the recipe's own).
- One step at a time (28px text), "Step x of n" in a polite live region, Previous/Next buttons of 56px (Next reads "Finish" on the last step and opens the done screen, which takes focus), swipe left/right (>= 50px and clearly horizontal; `touch-action: pan-y`) and the arrow keys (ignored in form fields and with modifiers).
- Ingredients panel (toggle, `aria-expanded`) with a servings stepper (56px). Amounts come from the API: Domain `RecipeScaling` scales exactly, then `QuantityMerger.ForDisplay` rounds and picks the display unit (1500 g shows 1,5 kg); to taste is unchanged; servings 1 to 100 else 400 ProblemDetails.
- Screen Wake Lock: requested on enter, re-requested on `visibilitychange` to visible (the browser drops it when the tab is hidden), released on the done screen, exit and unmount. Unsupported or refused shows a translated hint; nothing else changes.
- Out of scope: timers (T-055) and voice (T-056).

## Internationalisation (T-038, Q-013)
All user-facing text is translatable. Code, keys and the fallback are English; locales are `nb` and `en`.

**Library choice:** a small in-house module (`app/i18n/index.ts`, no dependency), not i18next. We need only key lookup, `{name}` interpolation, one/other plurals, a language switch and Intl formatting. The module is typed end to end (a misspelled key does not compile), has nothing to pin or upgrade, and is easy to swap for i18next later because call sites are just `t("key", params)`. Dates and numbers use the platform `Intl` API.

**Files:** `app/i18n/en.ts` (source of truth for keys, fallback), `app/i18n/nb.ts` (bokmål), `app/i18n/problems.ts` (ProblemDetails `type` to message key), `app/i18n/language-switch.tsx` (44px select, in the shell header and on the login page).

**Keys and fallback:** keys are dotted English ids, `screen.thingInCamelCase` (`shopping.generate`). Lookup order: active locale, then English, then a placeholder made from the last key segment (`shopping.generateFromPlan` gives "Generate from plan"), so a forgotten translation never shows a raw key. TypeScript only accepts keys that exist in `en.ts`; a test fails if `nb.ts` lacks one or has extra keys, or if a `{placeholder}` differs between them. Plurals: define `key.one` and `key.other` with `{count}` and call `t("key", { count })`.

**Locale:** a stored choice (`localStorage` key `foodplanner.locale`, always in try/catch) wins; otherwise the first browser language that is nb/no/nn (gives nb) or en; otherwise en. `setLocale()` also sets `<html lang>`. The prerendered page is English.

### How to add text
1. Add the key to **both** `app/i18n/en.ts` and `app/i18n/nb.ts` (natural bokmål, kitchen vocabulary: "Handleliste", "Logg ut").
2. In a component: `const { t } = useI18n();` then `{t("recipes.title")}`, `aria-label={t("recipes.close")}`, `t("recipes.servings", { count: 4 })`. Outside React use `translate(getLocale(), key)`.
3. Numbers and dates: `const { formatNumber, formatDayMonth } = useI18n()`. Never `String(n)` or hand-made date strings (use `useGrouping: false` for years and week numbers).
4. API errors: map the ProblemDetails `type` with `problemKey(problemTypeOf(error))`; add new `urn:foodplanner:problem:*` codes to `problems.ts`, `en.ts` and `nb.ts`. Never show the server's `title` or `detail`. Unknown types give `problem.generic`.
5. Proper nouns that are not translated (the app name) are constants (`APP_NAME`), not literals.
6. Text a user typed, and catalog data from the API, is shown as is (catalog translation is T-039).

**Enforcement:** the local ESLint rule `foodplanner/no-literal-text` (`eslint-rules/no-literal-text.js`, run by `npm run lint` and verify.sh) fails on JSX text, string literals in JSX expressions, and literal `aria-label`, `aria-description`, `placeholder`, `title` and `alt` values in `app/` (tests are exempt). Punctuation, digits and entities are allowed. The rule has its own tests.

**Tests:** Vitest runs every suite twice, as projects `en` and `nb` (`vitest.config.ts`). Tests get texts from `app/test-i18n.ts` (`t(key)`, `n(1.5)`) instead of hard-coding English. Playwright: all specs run in en-US; project `mobile-390-nb` (browser language nb-NO) runs only `tests/e2e/tests/nb.spec.ts`.
