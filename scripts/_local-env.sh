# Sourced by local-*.sh. Not run directly.
# Reads the SQL SA password from .NET user secrets and runs docker compose with it set for that
# one process only (never written to a file, never printed).
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

secret() { # secret <key>: prints the value of a user secret, or nothing if unset
  dotnet user-secrets list --project src/Api | sed -n "s/^$1 = //p"
}

compose() {
  local pw
  pw="$(secret Sql:SaPassword)"
  if [ -z "$pw" ]; then
    echo "No Sql:SaPassword user secret yet. Run ./scripts/local-secrets.sh first." >&2
    return 1
  fi
  MSSQL_SA_PASSWORD="$pw" docker compose "$@"
}
