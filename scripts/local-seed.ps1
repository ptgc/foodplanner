# Seeds the local admin (Seed:AdminEmail / Seed:AdminPassword from user secrets) as a confirmed Owner of "Home".
# Development only and idempotent. Runs the Api project as a one-shot host command; it never runs inside the
# compose api container (that one is Production). Needs the migrated schema: run local-migrate first.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')

$env:ASPNETCORE_ENVIRONMENT = 'Development'
${env:Logging__LogLevel__Microsoft.EntityFrameworkCore} = 'Warning'
try { dotnet run --project src/Api --no-launch-profile -- seed-admin }
finally {
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Remove-Item 'Env:\Logging__LogLevel__Microsoft.EntityFrameworkCore' -ErrorAction SilentlyContinue
}
if ($LASTEXITCODE -ne 0) { throw 'seed-admin failed' }
Write-Host 'Local admin is seeded. Read the login with: dotnet user-secrets list --project src/Api'
