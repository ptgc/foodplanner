#!/usr/bin/env bash
# Applies all EF Core migrations to the local database (the one in ConnectionStrings:Default, user secrets).
# Explicit step only: the API never migrates at startup. Safe to repeat: applied migrations are skipped.
# SQL Server must be up (local-up does that and then runs this).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"

cs="$(secret ConnectionStrings:Default)"
if [ -z "$cs" ]; then
  echo "No ConnectionStrings:Default user secret yet. Run ./scripts/local-secrets.sh first." >&2
  exit 1
fi
dotnet tool restore >/dev/null
dotnet ef database update --project src/Infrastructure --startup-project src/Api --connection "$cs"
