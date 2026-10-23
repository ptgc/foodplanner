#!/usr/bin/env bash
# Starts the local stack. Usage: ./scripts/local-up.sh [sql]
#   (no argument)  SQL, database init, API and web, all in containers (rebuilds images)
#   sql            only SQL Server and the database init step (for the VS Code dev loop)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"

"$repo_root/scripts/local-secrets.sh"

compose up -d --wait sql
compose run --rm sql-init
# Schema and admin are in place before the api container starts serving. The API itself never migrates.
"$repo_root/scripts/local-migrate.sh"
"$repo_root/scripts/local-seed.sh"

if [ "${1:-}" = sql ]; then
  echo "SQL Server is up on 127.0.0.1,1433. Start the API with F5 in VS Code (README section 5)."
else
  compose up -d --build --wait
  echo "Web: http://localhost:3000   API via the web proxy: http://localhost:3000/api/health/ready   SQL: 127.0.0.1,1433"
fi
