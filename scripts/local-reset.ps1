# Stops the containers and DELETES the SQL data volume (all local data). Next local-up starts empty.
# Keeps the user secrets, so the password stays the same. To rotate it see the README.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')
Invoke-Compose down --volumes
Write-Host 'Stopped and deleted the SQL volume.'
