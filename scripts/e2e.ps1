# Runs the Playwright e2e suite against the running local stack (start it with .\scripts\local-up.ps1).
# Usage: .\scripts\e2e.ps1 [playwright args, e.g. --project=mobile-390]
# BASE_URL overrides the default http://localhost:3000.
# E2E_ADMIN_EMAIL / E2E_ADMIN_PASSWORD default to the seeded admin in user secrets (Seed:AdminEmail / Seed:AdminPassword);
# the password is set for the test process only and never printed.
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
    npm ci;                          if ($LASTEXITCODE) { exit $LASTEXITCODE }
    npx playwright install chromium; if ($LASTEXITCODE) { exit $LASTEXITCODE }
    npx playwright test @args;       exit $LASTEXITCODE
} finally { Pop-Location }
