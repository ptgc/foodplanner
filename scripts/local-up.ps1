# Starts the local stack. Usage: .\scripts\local-up.ps1 [sql]
#   (no argument)  SQL, database init, API and web, all in containers (rebuilds images)
#   sql            only SQL Server, Azurite and the database init step (for the VS Code dev loop)
param([string]$Only = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')

& (Join-Path $PSScriptRoot 'local-secrets.ps1')

Invoke-Compose up -d --wait sql azurite
Invoke-Compose run --rm sql-init
# Schema and admin are in place before the api container starts serving. The API itself never migrates.
& (Join-Path $PSScriptRoot 'local-migrate.ps1')
& (Join-Path $PSScriptRoot 'local-seed.ps1')

if ($Only -eq 'sql') {
    Write-Host 'SQL Server is up on 127.0.0.1,1433 and Azurite (blobs) on 127.0.0.1:10000. Start the API with F5 in VS Code (README section 5).'
} else {
    Invoke-Compose up -d --build --wait
    Write-Host 'Web: http://localhost:3000   API via the web proxy: http://localhost:3000/api/health/ready   SQL: 127.0.0.1,1433   Azurite: 127.0.0.1:10000'
}
