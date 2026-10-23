#!/usr/bin/env bash
# Runs the Playwright e2e suite against the running local stack (start it with ./scripts/local-up.sh).
# Usage: ./scripts/e2e.sh [playwright args, e.g. --project=mobile-390]
# BASE_URL overrides the default http://localhost:3000.
# E2E_ADMIN_EMAIL / E2E_ADMIN_PASSWORD default to the seeded admin in user secrets (Seed:AdminEmail / Seed:AdminPassword);
# the password is exported to the test process only and never printed.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
if [[ -z "${E2E_ADMIN_EMAIL:-}" || -z "${E2E_ADMIN_PASSWORD:-}" ]]; then
  secrets="$(dotnet user-secrets list --project src/Api)"
  E2E_ADMIN_EMAIL="$(printf '%s\n' "$secrets" | sed -n 's/^Seed:AdminEmail[ ]=[ ]//p' | tr -d '\r')"
  E2E_ADMIN_PASSWORD="$(printf '%s\n' "$secrets" | sed -n 's/^Seed:AdminPassword[ ]=[ ]//p' | tr -d '\r')"
  unset secrets
  export E2E_ADMIN_EMAIL E2E_ADMIN_PASSWORD
fi
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test "$@"
