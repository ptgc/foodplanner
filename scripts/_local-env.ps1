# Dot-sourced by local-*.ps1 (Windows PowerShell 5.1 and PowerShell 7). Not run directly.
# Reads the SQL SA password from .NET user secrets and runs docker compose with it set for that
# one process only (never written to a file, never printed).
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Get-Secret([string]$Key) { # value of a user secret, or $null if unset
    $line = dotnet user-secrets list --project src/Api | Where-Object { $_ -like "$Key = *" } | Select-Object -First 1
    if ($line) { return $line.Substring($Key.Length + 3) }
    return $null
}

function Invoke-Compose {
    $pw = Get-Secret 'Sql:SaPassword'
    if (-not $pw) { throw 'No Sql:SaPassword user secret yet. Run .\scripts\local-secrets.ps1 first.' }
    $env:MSSQL_SA_PASSWORD = $pw
    # T-096: the local system admin allow-list (a user secret copied from Seed:AdminEmail) reaches the api container the same way.
    $supportAdmin = Get-Secret 'Support:SystemAdminEmails:0'
    if ($supportAdmin) { $env:SUPPORT_ADMIN_EMAIL = $supportAdmin }
    try { docker compose @args }
    finally {
        Remove-Item Env:\MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:\SUPPORT_ADMIN_EMAIL -ErrorAction SilentlyContinue
    }
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed (exit $LASTEXITCODE)" }
}
