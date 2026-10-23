#!/usr/bin/env bash
# Runs the Playwright e2e suite against the running local stack (start it with ./scripts/local-up.sh).
# Usage: ./scripts/e2e.sh [playwright args, e.g. --project=mobile-390]
# BASE_URL overrides the default http://localhost:3000.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../tests/e2e"
npm ci
npx playwright install chromium
npx playwright test "$@"
