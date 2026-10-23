# Stops and removes the containers. The SQL data volume is kept (use local-reset to delete it).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')
Invoke-Compose down
Write-Host 'Stopped. The SQL volume is kept. Use .\scripts\local-reset.ps1 to delete it.'
