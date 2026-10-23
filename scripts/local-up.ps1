# Starts the local stack. Usage: .\scripts\local-up.ps1 [sql]
#   (no argument)  SQL, database init, API and web, all in containers (rebuilds images)
#   sql            only SQL Server and the database init step (for the VS Code dev loop)
param([string]$Only = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')

& (Join-Path $PSScriptRoot 'local-secrets.ps1')

if ($Only -eq 'sql') {
    Invoke-Compose up -d --wait sql
    Invoke-Compose run --rm sql-init
    Write-Host 'SQL Server is up on 127.0.0.1,1433. Start the API with F5 in VS Code (README section 5).'
} else {
    Invoke-Compose up -d --build --wait
    Write-Host 'Web: http://localhost:3000   API via the web proxy: http://localhost:3000/api/health/ready   SQL: 127.0.0.1,1433'
}
