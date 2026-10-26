#!/usr/bin/env bash
# Generates the local SQL SA password once and stores it in .NET user secrets (Api project):
#   Sql:SaPassword             used by local-up/down/reset
#   ConnectionStrings:Blobs    UseDevelopmentStorage=true (Azurite, recipe images)
#   ConnectionStrings:Default  used by the API when it runs on the host (F5 / dotnet run). Uses 127.0.0.1 not localhost: see README troubleshooting
# Skips keys that are already set. Never prints the password.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"

existing="$(dotnet user-secrets list --project src/Api)"
has() { printf '%s\n' "$existing" | grep -q "^$1 = "; }

if has Sql:SaPassword; then
  echo "Sql:SaPassword already set, keeping it."
else
  # 24 alphanumerics with at least one upper, lower and digit, plus one '-' (SQL Server policy,
  # and no character that breaks a connection string or a shell).
  pw=""
  until [[ "$pw" =~ [A-Z] && "$pw" =~ [a-z] && "$pw" =~ [0-9] ]]; do
    pw="$(head -c 64 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')"
    pw="${pw:0:24}"
  done
  pw="${pw:0:12}-${pw:12}"
  # dotnet echoes the value it saved, so discard its output.
  dotnet user-secrets set Sql:SaPassword "$pw" --project src/Api >/dev/null
  existing="$(dotnet user-secrets list --project src/Api)"
  echo "Generated Sql:SaPassword."
fi

if has Seed:AdminEmail; then
  echo "Seed:AdminEmail already set, keeping it."
else
  dotnet user-secrets set Seed:AdminEmail admin@foodplanner.local --project src/Api >/dev/null
  echo "Set Seed:AdminEmail."
fi

if has Seed:AdminPassword; then
  echo "Seed:AdminPassword already set, keeping it."
else
  # 20 alphanumerics: well above the 10 character minimum (Auth:MinPasswordLength, T-009); no other rules apply.
  apw="$(head -c 64 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')"
  dotnet user-secrets set Seed:AdminPassword "${apw:0:20}" --project src/Api >/dev/null
  echo "Generated Seed:AdminPassword."
fi

# T-045: a second confirmed user for the e2e invite flow. local-seed creates it without any household.
if has Seed:MemberEmail; then
  echo "Seed:MemberEmail already set, keeping it."
else
  dotnet user-secrets set Seed:MemberEmail member@foodplanner.local --project src/Api >/dev/null
  echo "Set Seed:MemberEmail."
fi

if has Seed:MemberPassword; then
  echo "Seed:MemberPassword already set, keeping it."
else
  mpw="$(head -c 64 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')"
  dotnet user-secrets set Seed:MemberPassword "${mpw:0:20}" --project src/Api >/dev/null
  echo "Generated Seed:MemberPassword."
fi

if has ConnectionStrings:Default; then
  echo "ConnectionStrings:Default already set, keeping it."
else
  pw="$(secret Sql:SaPassword)"
  dotnet user-secrets set ConnectionStrings:Default \
    "Server=127.0.0.1,1433;Database=foodplanner;User Id=sa;Password=$pw;TrustServerCertificate=True" \
    --project src/Api >/dev/null
  echo "Set ConnectionStrings:Default."
fi

if has ConnectionStrings:Blobs; then
  echo "ConnectionStrings:Blobs already set, keeping it."
else
  # Azurite's documented development account: a shortcut, not a credential. The compose api container sets its own.
  dotnet user-secrets set ConnectionStrings:Blobs "UseDevelopmentStorage=true" --project src/Api >/dev/null
  echo "Set ConnectionStrings:Blobs."
fi
