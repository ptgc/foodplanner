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
  # T-096: the local system admin allow-list (a user secret copied from Seed:AdminEmail) reaches the api container the same way.
  # T-133: the Claude key and the on/off switches (user secrets, default off) reach it the same way. The key is never printed.
  MSSQL_SA_PASSWORD="$pw" SUPPORT_ADMIN_EMAIL="$(secret Support:SystemAdminEmails:0)" \
    AI_IMPORT_API_KEY="$(secret AiImport:ApiKey)" AI_IMPORT_ENABLED="$(secret AiImport:Enabled)" \
    AI_PHOTO_IMPORT_ENABLED="$(secret AiPhotoImport:Enabled)" docker compose "$@"
}
