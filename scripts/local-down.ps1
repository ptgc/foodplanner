# Stops and removes the containers. The SQL and Azurite data volumes are kept (use local-reset to delete it).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')
Invoke-Compose down
Write-Host 'Stopped. The SQL and Azurite volumes are kept. Use .\scripts\local-reset.ps1 to delete it.'
