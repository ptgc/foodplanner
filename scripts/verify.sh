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

echo "==> verify: all steps passed"
