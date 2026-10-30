#!/usr/bin/env bash
# Build, lint and test everything. CI runs the same script.
# Each check is a named step; later tasks append steps (web, etc.) below.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
source ./scripts/_test-mode.sh

# T-097: locally (no CI) run low-memory by default; FP_TEST_PARALLEL=full|low overrides. Same tests, fewer at a time.
build_args=(); dotnet_test_args=(); vitest_args=()
if [ "$FP_TEST_PARALLEL" = low ]; then
  build_args=(-m:2)
  # Test projects one after another; in xunit v3 projects at most 2 test classes at once.
  dotnet_test_args=(--max-parallel-test-modules 1 --max-threads 2)
  vitest_args=(--maxWorkers=2)
fi
echo "==> verify: test parallelism ${FP_TEST_PARALLEL} (FP_TEST_PARALLEL=full|low to override)"

# The MSBuild and Roslyn build servers keep ~0.5 GB after a run; stop them however verify ends.
trap 'dotnet build-server shutdown >/dev/null 2>&1 || true' EXIT

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
step "build" dotnet build Foodplanner.slnx -c Release --no-restore "${build_args[@]}"
step "format" dotnet format Foodplanner.slnx --verify-no-changes --no-restore
step "container-engine (Api.Tests need it; start Rancher Desktop or Docker)" docker info --format "engine {{.ServerVersion}} ({{.OperatingSystem}})"
# All .NET tests in one process (T-098 fixed the Api.Tests memory growth); dotnet_test_args is empty in full mode.
step "test" dotnet test --solution Foodplanner.slnx -c Release --no-build "${dotnet_test_args[@]}"
step "web-install" npm --prefix web ci
step "web-lint" npm --prefix web run lint
step "web-typecheck" npm --prefix web run typecheck
step "web-test" npm --prefix web test -- "${vitest_args[@]}"
step "web-build" npm --prefix web run build
step "web-bundle-size" node web/scripts/check-bundle-size.mjs
step "web-api-check" npm --prefix web run api:check

# e2e needs the running stack (scripts/local-up), so it is opt-in: E2E=1 ./scripts/verify.sh or --e2e
if [ "${E2E:-0}" = 1 ] || [ "${1:-}" = --e2e ]; then
  step "e2e" ./scripts/e2e.sh
fi

echo "==> verify: all steps passed"
