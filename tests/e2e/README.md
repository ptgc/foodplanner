# e2e tests (Playwright)

Runs against the running docker compose stack through the web container (same-origin `/api` proxy).
Three projects: `mobile-390` (390x844, touch, en-US) and `desktop` (1280x800, en-US), plus `mobile-390-nb` (browser language nb-NO, runs only `nb.spec.ts`). Headless chromium only.
Traces are kept on failure (`trace: 'retain-on-failure'`); `test-results/` and `playwright-report/` are gitignored.

## Run locally

Start the stack first, then run the suite:

```bash
./scripts/local-up.sh
./scripts/e2e.sh                       # or: ./scripts/e2e.sh --project=mobile-390
```

```powershell
.\scripts\local-up.ps1
.\scripts\e2e.ps1                      # or: .\scripts\e2e.ps1 --project=mobile-390
```

The script runs `npm ci` and `npx playwright install chromium` (Windows and Linux; on a fresh Linux box
use `npx playwright install --with-deps chromium` once), then `npx playwright test`.
`BASE_URL` overrides the default `http://localhost:3000`.

`household.spec.ts` (T-045) also needs the seeded member (`member@foodplanner.local`, confirmed, in no household): `local-secrets` + `local-seed` create it and the scripts export `E2E_MEMBER_EMAIL` / `E2E_MEMBER_PASSWORD` from `Seed:MemberEmail` / `Seed:MemberPassword` the same way as the admin's.

Sign-in tests use the seeded admin (T-029). The scripts read `Seed:AdminEmail` / `Seed:AdminPassword` from
`dotnet user-secrets` (without printing) into `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD`; set those two variables
yourself to override. Login is rate limited (429, 10/min per email), so the suite signs in ONCE per run: the `setup`
project (`tests/auth.setup.ts`) saves the session to `.auth/admin.json` (gitignored) and the other projects depend on it.
Tests that only need a signed-in user use `test.use({ storageState: adminState })`; a second session is
`browser.newContext({ storageState: adminState })`. Only auth.spec exercises the login form (about 4 logins per run);
`signIn` waits and retries on a 429. Use `expectActiveHousehold(page, "Home")` for the household, since the admin may
have several households locally (text vs. switcher).

The service worker (T-041) is blocked for every spec (`serviceWorkers: "block"` in `playwright.config.ts`), so route mocks and fresh reads
behave as before. `pwa.spec.ts` turns it on (`test.use({ serviceWorkers: "allow" })`); it needs the web container built from the current
`web/` (`docker compose up -d --build web`), because the worker file is produced by the web build.

Open a kept trace: `npx playwright show-trace test-results/<test-dir>/trace.zip` (run in `tests/e2e`).

## verify.sh

Off by default so `verify.sh` works without the stack. `E2E=1 ./scripts/verify.sh` (or `./scripts/verify.sh --e2e`)
runs the suite as a final step; the stack must be up.

## CI wiring (for T-005)

In the CI job that runs `verify.sh`:

1. Provide `MSSQL_SA_PASSWORD` from a generated per-run value (never a repo secret) and `docker compose up -d --build --wait`.
2. `npm --prefix tests/e2e ci` and `npx --prefix tests/e2e playwright install --with-deps chromium`.
3. `E2E=1 ./scripts/verify.sh` (or `./scripts/e2e.sh`), with `CI=true` (enables one retry and `forbidOnly`).
4. Upload `tests/e2e/playwright-report` and `tests/e2e/test-results` (traces) as artifacts with `if: always()`.
5. `docker compose down -v` with `if: always()`.

## Auth fixtures

See above: `tests/fixtures.ts` has `adminState`, `signIn`, `adminCredentials`, `expectActiveHousehold`.
