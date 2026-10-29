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
if [ "$FP_TEST_PARALLEL" = low ]; then
  # Api.Tests memory grows with the number of tests run in one process (about 1.6 GB for all of them), whatever the
  # parallelism. So low mode runs it in three processes: pass 1 is everything except the two groups below (so a new
  # namespace is run there), pass 2 and 3 are exactly those groups. Each pass starts its own SQL Server container.
  api_group_a=(Foodplanner.Api.Tests.Recipes Foodplanner.Api.Tests.Catalog Foodplanner.Api.Tests.Import)
  api_group_b=(Foodplanner.Api.Tests.TenantIsolation Foodplanner.Api.Tests.Persistence Foodplanner.Api.Tests.Households)
  not_args=(); a_args=(); b_args=()
  for ns in "${api_group_a[@]}"; do not_args+=(--filter-not-namespace "$ns*"); a_args+=(--filter-namespace "$ns*"); done
  for ns in "${api_group_b[@]}"; do not_args+=(--filter-not-namespace "$ns*"); b_args+=(--filter-namespace "$ns*"); done
  step "test (1/3: Domain, Architecture, remaining Api.Tests)" dotnet test --solution Foodplanner.slnx -c Release --no-build "${dotnet_test_args[@]}" "${not_args[@]}"
  step "test (2/3: Api.Tests recipes, catalog, import)" dotnet test --project tests/Api.Tests -c Release --no-build "${dotnet_test_args[@]}" "${a_args[@]}"
  step "test (3/3: Api.Tests tenant isolation, persistence, households)" dotnet test --project tests/Api.Tests -c Release --no-build "${dotnet_test_args[@]}" "${b_args[@]}"
else
  step "test" dotnet test --solution Foodplanner.slnx -c Release --no-build
fi
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
