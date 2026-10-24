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
- Limit: there is no service worker (PWA is T-041). Offline means "the page is open, or the app is served but the API is unreachable". A browser that is fully offline cannot load the page itself. Generate, add and remove need the API and are disabled offline.

## Recipe grid (T-024, `/recipes`, `/recipes/:id`)
- Code: `app/routes/recipes.tsx` (screen), `app/recipes/filters.ts` (filters <-> URL, pure), `use-recipes.ts` (list with `limit`/`offset` paging, tags), `recipe-image.tsx` (the only place that knows image urls), `recipes.css`; `app/routes/recipe-detail.tsx` is a minimal read-only page that T-025 replaces.
- Filters live in the URL: `?q=text&tag=<id>&tag=<id>&max=30`. Chips and "clear filters" add a history entry (back undoes them), typing replaces the entry after a 300 ms pause. Several tags are AND (T-023). Malformed values in the URL are ignored.
- Paging is a "Show more" button (offset paging, 12 per page). Two columns at 390px (cards are 2 x 173px), more on wider screens; chips, cards and buttons are at least 44px.
- `RecipeImage` shows the card/thumb image (lazy, async decode, `srcset` 160w/640w, 4:3 box so nothing shifts) or, without one (or if it fails to load), a coloured tile with the initial. Decorative (`alt=""`, the title is beside it).

### Performance budget (Lighthouse, mobile preset)
Measured 2026-10-24 with `lighthouse@12` (mobile preset: Moto G Power emulation, simulated slow 4G, 4x CPU), headless Chromium, against the Docker stack at http://localhost:3000/recipes, signed in by sending the saved Playwright session cookie (`--extra-headers`), 3 runs, 3 demo recipes in the household.

| Metric | Before gzip (3 runs) | After gzip (3 runs) | Budget |
|---|---|---|---|
| Performance score | 75-83 | 89-96 | >= 85 |
| LCP | 3.7-3.8 s | 2.4-2.5 s | <= 2.5 s |
| FCP | 3.3-3.4 s | 2.0-2.2 s | <= 2.0 s (watch) |
| TBT | 1-38 ms | 3-14 ms | <= 200 ms |
| CLS | 0.05-0.15 | 0.05-0.15 | <= 0.1 (one run in three is 0.15: the filter chips appear after the tags load; fix by reserving their space) |
| Transferred | 397 KB | 156 KB | <= 250 KB |
| JS (gzipped) | 381 KB (uncompressed!) | 146 KB | <= 170 KB, checked by `web/scripts/check-bundle-size.mjs` in verify.sh |
| Accessibility / Best practices | 100 / 100 | 100 / 100 | 100 / 100 |

The first run showed nginx sent the SPA uncompressed, so `web/nginx.conf` now enables gzip for text assets. Re-measure after T-025 and when images are many (`npx lighthouse http://localhost:3000/recipes --extra-headers=<json with Cookie> --only-categories=performance`). Raising a budget is a deliberate change to this table and the script.

## Week planner (T-035, `/plan`)
- Code: `app/routes/plan.tsx` (screen and picker sheet), `app/planner/use-plan.ts` (week state and writes on the generated client), `avatar.ts` (initials and colour of who picked), `planner.css`. Keys are `planner.*`; the nav link is `nav.plan`.
- The week is in the URL (`?week=yyyy-MM-dd`, a Monday, default the current ISO week) and reuses `shopping/week.ts`. Day names and dates come from `Intl` in the active locale.
- Picking: the day's round "+" (56px) opens a bottom sheet with the recipe grid (search and category chips, same `useRecipeList`/`useTags` as `/recipes`); tapping a recipe POSTs the entry with the recipe's default servings and closes the sheet. That is 2 taps from the week view (Playwright "Kid can pick a meal in <=3 taps" asserts exactly 2).
- Servings are - / + steppers (1 to 50, PUT); remove is a x button. Adult and Owner may change any entry; a Child sees the stepper and remove only on their own picks (others show the servings as text), matching the API's `MealPlanEntryOwnerOrAdult` policy. 400/403/404/409 show translated messages; 404 and 409 re-read the week.
- Plan entries carry no image urls, so the screen reads each distinct recipe once for its card image (placeholder tile on failure). A plan response with image urls would remove those requests.
- "Make shopping list" POSTs generate and opens `/shopping?week=...`; "See shopping list" links there without generating.

## Recipe detail and edit (T-025, `/recipes/:id`, `/recipes/new`, `/recipes/:id/edit`)
- Detail: `routes/recipe-detail.tsx` (picture via `RecipeImage` with thumb/card/full in `srcset`, times, tags, ingredients, steps). The servings stepper scales the shown amounts by the servings ratio on the client; nothing is saved.
- Edit/new: `routes/recipe-edit.tsx` (one component for both routes), rows in `recipes/ingredient-rows.tsx`, `step-rows.tsx`, `ingredient-picker.tsx` (searches `GET .../ingredients?search=`, creates the household's own ingredient inline), `form-parts.tsx`. Pure logic and tests: `recipes/recipe-form.ts` (client validation with the T-021 limits, request building, `mapServerErrors`).
- Role: `useActiveRole()` reads the role of the active household from `GET /api/households`. A Child gets no New/Edit/Delete/upload controls and a message on the edit routes; the API refuses their writes with 403 regardless.
- Quantity and unit come together; "to taste" is a checkbox that sends neither. No free-text ingredients (rule 5): every line points at an ingredient id.
- Validation: the client checks first (error summary with focus, `aria-invalid`, `aria-describedby`). If the server still answers 400, its `errors` keys (`ingredients[2].unitId`, `tagIds[0]`, ...) are mapped to the same fields with translated messages; the server texts are never shown.
- Picture: chosen with a file input, previewed, and sent (PUT multipart part `file`) or removed (DELETE) after the recipe is saved. The generated schema types a binary part as `string`, so the `File` is cast once in `applyPicture`. If the recipe saved but the picture failed, the page stays and the next save updates the same recipe.
- Delete asks first; `409 recipe-in-use` shows a translated message. i18n keys for this screen are in the `recipe.*` namespace.

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
