#!/usr/bin/env bash
# Fails if a tracked file holds a literal SQL password. References such as
# ${MSSQL_SA_PASSWORD:?...} are fine: a value starting with $ or ? is not literal.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

q="[\"']"
pattern="((Password|Pwd)[[:space:]]*=[[:space:]]*[^\$[:space:];\"'])|((MSSQL_SA_PASSWORD|SA_PASSWORD|SQLCMDPASSWORD)[[:space:]]*[=:][[:space:]]*${q}?[^\$?[:space:]\"'])"

# Exclude this script: its own pattern line would match itself.
if git grep -nEI "$pattern" -- . ':!scripts/check-no-sql-password.sh'; then
  echo "Literal SQL password found in a tracked file (see above)." >&2
  exit 1
fi
