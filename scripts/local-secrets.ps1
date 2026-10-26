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

if (Get-Secret 'Seed:AdminEmail') {
    Write-Host 'Seed:AdminEmail already set, keeping it.'
} else {
    dotnet user-secrets set Seed:AdminEmail admin@foodplanner.local --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Set Seed:AdminEmail.'
}

if (Get-Secret 'Seed:AdminPassword') {
    Write-Host 'Seed:AdminPassword already set, keeping it.'
} else {
    # 20 alphanumerics: well above the 10 character minimum (Auth:MinPasswordLength, T-009); no other rules apply.
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $bytes = New-Object byte[] 20
    $rng.GetBytes($bytes)
    $apw = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    dotnet user-secrets set Seed:AdminPassword $apw --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Generated Seed:AdminPassword.'
}

# T-045: a second confirmed user for the e2e invite flow. local-seed creates it without any household.
if (Get-Secret 'Seed:MemberEmail') {
    Write-Host 'Seed:MemberEmail already set, keeping it.'
} else {
    dotnet user-secrets set Seed:MemberEmail member@foodplanner.local --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Set Seed:MemberEmail.'
}

if (Get-Secret 'Seed:MemberPassword') {
    Write-Host 'Seed:MemberPassword already set, keeping it.'
} else {
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $bytes = New-Object byte[] 20
    $rng.GetBytes($bytes)
    $mpw = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    dotnet user-secrets set Seed:MemberPassword $mpw --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Generated Seed:MemberPassword.'
}

if (Get-Secret 'ConnectionStrings:Default') {
    Write-Host 'ConnectionStrings:Default already set, keeping it.'
} else {
    $pw = Get-Secret 'Sql:SaPassword'
    dotnet user-secrets set ConnectionStrings:Default "Server=127.0.0.1,1433;Database=foodplanner;User Id=sa;Password=$pw;TrustServerCertificate=True" --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Set ConnectionStrings:Default.'
}

if (Get-Secret 'ConnectionStrings:Blobs') {
    Write-Host 'ConnectionStrings:Blobs already set, keeping it.'
} else {
    # Azurite's documented development account: a shortcut, not a credential. The compose api container sets its own.
    dotnet user-secrets set ConnectionStrings:Blobs 'UseDevelopmentStorage=true' --project src/Api | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets set failed' }
    Write-Host 'Set ConnectionStrings:Blobs.'
}
