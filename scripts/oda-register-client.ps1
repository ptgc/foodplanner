# Registers the app-wide Oda OAuth client for one environment and prints only its client_id (T-053).
# Usage: ./scripts/oda-register-client.ps1 <web-origin> [client-name]   e.g. http://localhost:3000
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
node scripts/oda-register-client.mjs @args
exit $LASTEXITCODE
