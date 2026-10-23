# Runs the Playwright e2e suite against the running local stack (start it with .\scripts\local-up.ps1).
# Usage: .\scripts\e2e.ps1 [playwright args, e.g. --project=mobile-390]
# BASE_URL overrides the default http://localhost:3000.
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..\tests\e2e')
try {
    npm ci;                          if ($LASTEXITCODE) { exit $LASTEXITCODE }
    npx playwright install chromium; if ($LASTEXITCODE) { exit $LASTEXITCODE }
    npx playwright test @args;       exit $LASTEXITCODE
} finally { Pop-Location }
