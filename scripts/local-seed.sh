#!/usr/bin/env bash
# Seeds the local admin (Seed:AdminEmail / Seed:AdminPassword from user secrets) as a confirmed Owner of "Home", and the
# member for the e2e invite flow (Seed:MemberEmail / Seed:MemberPassword): confirmed, in no household.
# Development only and idempotent. Runs the Api project as a one-shot host command; it never runs inside the
# compose api container (that one is Production). Needs the migrated schema: run local-migrate first.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"

env ASPNETCORE_ENVIRONMENT=Development Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning dotnet run --project src/Api --no-launch-profile -- seed-admin
echo "Local admin is seeded. Read the login with: dotnet user-secrets list --project src/Api"
