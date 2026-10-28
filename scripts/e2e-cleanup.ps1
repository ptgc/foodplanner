# Removes the data e2e specs left in the local database (T-083). Matches e2e naming patterns only; real data is never touched.
# Usage: .\scripts\e2e-cleanup.ps1 [--dry-run] [--tag <run tag>]
#   --dry-run  list leftovers and delete nothing; exits 1 when any exist
#   --tag      only data of one run; default is the legacy naming patterns
# Needs the running stack (scripts\local-up.ps1). Credentials come from user secrets and are never printed.
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
if (-not $env:E2E_ADMIN_EMAIL -or -not $env:E2E_ADMIN_PASSWORD) {
    Push-Location $root
    try { $secrets = dotnet user-secrets list --project src/Api } finally { Pop-Location }
    foreach ($line in $secrets) {
        if ($line -like 'Seed:AdminEmail[ ]=[ ]*')    { $env:E2E_ADMIN_EMAIL    = $line.Substring('Seed:AdminEmail'.Length + 3) }
        if ($line -like 'Seed:AdminPassword[ ]=[ ]*') { $env:E2E_ADMIN_PASSWORD = $line.Substring('Seed:AdminPassword'.Length + 3) }
    }
    Remove-Variable secrets
}
Push-Location (Join-Path $root 'tests\e2e')
try {
    if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
    node cleanup/cli.ts @args; exit $LASTEXITCODE
} finally { Pop-Location }
