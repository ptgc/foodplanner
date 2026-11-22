# Scores the recipe-from-photo skill on a local example set (T-118). Calls the REAL Anthropic API and costs money: run by hand only,
# never from verify.sh or CI. Usage: $env:ANTHROPIC_API_KEY='...'; ./scripts/recipe-photo-eval.ps1 --book kaptein-sabeltann --model claude-opus-5-5 [--effort medium] [--only 1,5] [--dry-run]
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
node scripts/recipe-photo-eval.mjs @args
exit $LASTEXITCODE
