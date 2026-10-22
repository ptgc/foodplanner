# Starts the orchestrator in Claude Code for local iteration L1.
# Usage (Windows PowerShell 5.1 or PowerShell 7):  .\scripts\start-orchestrator.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

& "$PSScriptRoot\doctor.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

git fetch origin dev
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git switch dev
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git pull --ff-only origin dev
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$prompt = Get-Content -Raw -Encoding UTF8 docs/prompts/local-iteration-1.md
claude --agent orchestrator $prompt
exit $LASTEXITCODE
