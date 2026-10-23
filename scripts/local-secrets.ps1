# Generates the local SQL SA password once and stores it in .NET user secrets (Api project):
#   Sql:SaPassword             used by local-up/down/reset
#   ConnectionStrings:Default  used by the API when it runs on the host (F5 / dotnet run)
# Skips keys that are already set. Never prints the password.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_local-env.ps1')

function New-SqlPassword {
    # 24 alphanumerics with at least one upper, lower and digit, plus one '-' (SQL Server policy,
    # and no character that breaks a connection string or a shell).
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    do {
        $bytes = New-Object byte[] 24
        $rng.GetBytes($bytes)
        $pw = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    } until ($pw -cmatch '[A-Z]' -and $pw -cmatch '[a-z]' -and $pw -match '[0-9]')
    return $pw.Substring(0, 12) + '-' + $pw.Substring(12)
}

if (Get-Secret 'Sql:SaPassword') {
    Write-Host 'Sql:SaPassword already set, keeping it.'
} else {
    # dotnet echoes the value it saved, so discard its output.
    dotnet user-secrets set Sql:SaPassword (New-SqlPassword) --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Generated Sql:SaPassword.'
}

if (Get-Secret 'ConnectionStrings:Default') {
    Write-Host 'ConnectionStrings:Default already set, keeping it.'
} else {
    $pw = Get-Secret 'Sql:SaPassword'
    dotnet user-secrets set ConnectionStrings:Default "Server=127.0.0.1,1433;Database=foodplanner;User Id=sa;Password=$pw;TrustServerCertificate=True" --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Set ConnectionStrings:Default.'
}
