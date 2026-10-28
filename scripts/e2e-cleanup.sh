#!/usr/bin/env bash
# Removes the data e2e specs left in the local database (T-083). Matches e2e naming patterns only; real data is never touched.
# Usage: ./scripts/e2e-cleanup.sh [--dry-run] [--tag <run tag>]
#   --dry-run  list leftovers and delete nothing; exits 1 when any exist (the check used after e2e runs)
#   --tag      only data of one run (the teardown of scripts/e2e.sh prints the tag); default is the legacy naming patterns
# Needs the running stack (scripts/local-up.sh). Credentials come from user secrets like scripts/e2e.sh and are never printed.
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
[[ -d node_modules ]] || npm ci
node cleanup/cli.ts "$@"
