# Applies all EF Core migrations to the local database (the one in ConnectionStrings:Default, user secrets).
# Explicit step only: the API never migrates at startup. Safe to repeat: applied migrations are skipped.
# SQL Server must be up (local-up does that and then runs this).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')

$cs = Get-Secret 'ConnectionStrings:Default'
if (-not $cs) { throw 'No ConnectionStrings:Default user secret yet. Run .\scripts\local-secrets.ps1 first.' }
dotnet tool restore | Out-Null
dotnet ef database update --project src/Infrastructure --startup-project src/Api --connection $cs
if ($LASTEXITCODE -ne 0) { throw 'dotnet ef database update failed' }
