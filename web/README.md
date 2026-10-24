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
