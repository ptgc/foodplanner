---
name: frontend-screen
description: Build or change a screen in /web. Use when a task adds a route or a visible part of a page. Covers the 390px mobile-first layout, 44px touch targets, loading, empty and error states, accessibility checks, translated text, and the Vitest and Playwright test patterns.
---

# Build a frontend screen

Reference examples:
- `/pantry` (T-093): `web/app/routes/pantry.tsx`, `web/app/routes/pantry.test.tsx`, `tests/e2e/tests/pantry.spec.ts`.
- The recipe grid (T-024), for a skeleton while loading: `web/app/routes/recipes.tsx`, `web/app/routes/recipes.test.tsx`,
  `tests/e2e/tests/recipes.spec.ts`.

Read `web/README.md` ("Internationalisation", "Bundle budget") and the section of the nearest screen first.

## Structure

1. **Route.** Add it in `web/app/routes.ts` inside `layout("routes/shell.tsx", [...])`; the file is `web/app/routes/<name>.tsx`
   with a default export. Keep shared parts in a feature folder (`web/app/household/`, `web/app/shopping/`).
2. **Session and household.** `const { session } = useSession();` (`web/app/session.tsx`); render nothing until
   `session.status === "signedIn"`, find the active household, and give the inner screen `key={household.id}` so a household
   switch starts clean (`Pantry` / `PantryScreen`). Show or hide controls with `powers(role)` (`web/app/household/roles.ts`).
   That is only a UI hint: the API enforces the same rules.
3. **Data.** Use the generated client only (`api` from `web/app/api/client.ts`, types from `components["schemas"]`); see the
   regenerate-api-client skill. Wrap calls in `call()` (`web/app/household/call.ts`), which turns ProblemDetails and network
   failures into a message key, and load lists with `useLoaded(key, run)` (`web/app/household/use-loaded.ts`).
   Never show the server's `title` or `detail`; map a new problem code in `web/app/i18n/problems.ts`.
   If the screen shows catalog names (ingredients, units, tags), put the locale in the fetch dependencies.
4. **Bundle.** A screen only some users open should be a lazy chunk (`lazyWithMessages`, `web/app/i18n/lazy.ts`).
   Check with `node web/scripts/check-bundle-size.mjs --print`; `verify.sh` fails over budget.

## States: every screen has all four

5. **Loading:** `<p role="status">{t("pantry.loading")}</p>`, or a skeleton for a grid: `aria-busy="true"`, a visually hidden
   `role="status"` text and the skeleton `aria-hidden`, sized like the real content so nothing shifts (`Skeleton` in `recipes.tsx`).
6. **Empty:** its own text that says what to do next, with a test id (`data-testid="pantry-empty"`, `recipes-empty`).
   A filtered list that matches nothing has a different text and a way out ("clear filters").
7. **Error:** `<p role="alert" className="form-error">{t(loadError)}</p>`, with a retry where a retry makes sense. A failed
   load keeps the data already shown. Offline: `OfflineNotice` (`web/app/pwa/offline-notice.tsx`).
8. **Content:** a list with stable keys and a test id per row (`pantry-row`).

## Layout: 390px first (CLAUDE.md rule 10)

9. Base styles target a 390px phone (`web/app/app.css`, first line). Wider screens only add `@media (min-width: ...)` rules;
   the shell caps the width. Nothing may scroll sideways at 390px.
10. Reuse the existing classes before adding CSS: `button`, `button-outline`, `household-card`, `household-list`,
    `household-row` (`web/app/app.css`, `web/app/household/household.css`, `web/app/recipes/recipes.css`).
11. Every touch target is at least 44px: `min-height: var(--touch)` (and `min-width: var(--touch)` for icon buttons);
    `--touch: 44px` is in `app.css`. Links styled as buttons, chips, select boxes and checkboxes count too.

## Text and accessibility

12. All text goes through `t()` from `useI18n()`. Add every key to both `web/app/i18n/en.ts` and `web/app/i18n/nb.ts`.
    `npm --prefix web run lint` fails on literal JSX text and literal `aria-label`, `placeholder`, `title` and `alt`
    (`web/eslint-rules/no-literal-text.js`).
13. Accessibility checks:
    - one `<h1>`, and the screen is a `<section aria-labelledby="...">`;
    - every input has a label (`<label htmlFor>` with `useId()`);
    - a button that acts on one row names that row: `aria-label={t("pantry.removeFor", { name: entry.name })}`;
    - status text has `role="status"`, errors `role="alert"`; decorative images have `alt=""`;
    - confirmations use `ConfirmDialog` (`web/app/household/confirm-dialog.tsx`); everything works with the keyboard.
    `eslint-plugin-jsx-a11y` runs in `npm run lint`. Tests find elements by role and accessible name, so a missing name fails
    a test. There is no axe dependency; adding one is a question for the architect.

## Vitest (`web/app/**/*.test.tsx`, next to the screen)

14. Copy `web/app/routes/pantry.test.tsx`:
    - `beforeEach`: `resetAntiforgeryToken()`, then `vi.stubGlobal("fetch", vi.fn(async (request: Request) => ...))` that answers
      by path: `/api/auth/me`, `/api/auth/antiforgery`, `/api/households` (with the `role` under test), then the feature's
      routes; it records method, path, query and body; anything else throws `unexpected ...`;
    - `afterEach`: `vi.unstubAllGlobals()` and `vi.restoreAllMocks()`;
    - render in `<MemoryRouter initialEntries={["/pantry"]}><SessionProvider>...</SessionProvider></MemoryRouter>`;
    - type the fixtures with `components["schemas"]`; read texts with `t()` from `web/app/test-i18n.ts` (every suite runs in
      `en` and `nb`, see `web/vitest.config.ts`), never hard-coded English.
15. Cover: loading then content, the empty state, a ProblemDetails failure showing the translated message, an unreachable
    API, the request sent (method, path, body), and what a Child sees (`role = "Child"`). Pantry shows the empty, request and
    Child cases; `recipes.test.tsx` shows the skeleton, the ProblemDetails message and the unreachable API.
    Use `userEvent` and query by role or label.
    Run `npm --prefix web test`.

## Playwright (`tests/e2e/tests/<feature>.spec.ts`)

16. Copy `tests/e2e/tests/pantry.spec.ts`:
    - `import { adminState, expect, test, uniqueStamp } from "./fixtures";` and `test.use({ storageState: adminState });`
    - create test data through the API with the `X-XSRF-TOKEN` header (`csrf()` helper), with names from
      `uniqueStamp(testInfo)` so the run teardown (T-083) finds leftovers; delete it in `finally`;
    - a test that clears or deletes data works in its own household, never the seeded admin's;
    - find elements by role and name, as a user would.
17. Specs run in the `mobile-390` and `desktop` projects (`tests/e2e/playwright.config.ts`). On mobile check
    `noHorizontalScroll(page)` and that the main controls' `boundingBox()` height is at least 44 (`touchTarget` in pantry.spec).
    Catalog names on screen: add a case to `tests/e2e/tests/language-switch.spec.ts`.
18. Run against the stack: `./scripts/local-up.sh` (rebuilds the containers), then
    `./scripts/e2e.sh --workers=1` (always one worker locally; add `tests/<feature>.spec.ts` to run one spec).

## Finish

19. Add a short section for the screen to `web/README.md` (route, files, behaviour), like "Recipe grid (T-024 ...)".
20. Run `./scripts/verify.sh` (lint, typecheck, Vitest, build, bundle size, `api:check`) and finish with the finish-task skill.
