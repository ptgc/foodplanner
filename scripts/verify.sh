#!/usr/bin/env bash
# Build, lint and test everything. CI runs the same script.
# Each check is a named step; later tasks append steps (web, etc.) below.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

step() {
  local name="$1"; shift
  echo "==> verify: ${name}"
  if ! "$@"; then
    echo "!! verify FAILED at step: ${name}" >&2
    exit 1
  fi
}

step "no-sql-password" ./scripts/check-no-sql-password.sh
step "restore" dotnet restore Foodplanner.slnx
step "build" dotnet build Foodplanner.slnx -c Release --no-restore
step "format" dotnet format Foodplanner.slnx --verify-no-changes --no-restore
step "test" dotnet test --solution Foodplanner.slnx -c Release --no-build
step "web-install" npm --prefix web ci
step "web-lint" npm --prefix web run lint
step "web-typecheck" npm --prefix web run typecheck
step "web-test" npm --prefix web test
step "web-build" npm --prefix web run build
step "web-api-check" npm --prefix web run api:check

# e2e needs the running stack (scripts/local-up), so it is opt-in: E2E=1 ./scripts/verify.sh or --e2e
if [ "${E2E:-0}" = 1 ] || [ "${1:-}" = --e2e ]; then
  step "e2e" ./scripts/e2e.sh
fi

echo "==> verify: all steps passed"
