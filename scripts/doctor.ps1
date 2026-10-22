# Checks that this machine has what the orchestrator run and the local iteration need.
# Usage (Windows PowerShell 5.1 or PowerShell 7):  .\scripts\doctor.ps1
$ErrorActionPreference = 'Continue'
$script:fail = 0
function Ok($m)   { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Bad($m, $fix) { Write-Host "  [FAIL] $m" -ForegroundColor Red; Write-Host "         fix: $fix"; $script:fail = 1 }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
function Has($c)  { [bool](Get-Command $c -ErrorAction SilentlyContinue) }

Write-Host "foodplanner doctor (Windows)"

if (Has git) { Ok ("git " + ((git --version) -replace 'git version ', '')) }
else { Bad "git not found" "winget install --id Git.Git -e" }

if (Has dotnet) {
    $v = (dotnet --version 2>$null)
    if ($v -and ([int]($v.Split('.')[0]) -ge 10)) { Ok ".NET SDK $v" }
    else { Bad ".NET SDK '$v' is older than 10" "winget install --id Microsoft.DotNet.SDK.10 -e" }
} else { Bad ".NET SDK not found" "winget install --id Microsoft.DotNet.SDK.10 -e" }

if (Has node) {
    $v = (node --version).TrimStart('v')
    if ([int]($v.Split('.')[0]) -ge 22) { Ok "Node $v" }
    else { Bad "Node $v is older than 22" "winget install --id OpenJS.NodeJS.LTS -e" }
} else { Bad "Node not found" "winget install --id OpenJS.NodeJS.LTS -e" }

# The guard hook and verify.sh are bash scripts and need jq. On Windows that means Git Bash (comes with Git) or WSL.
if (Has bash) { Ok "bash found (Git Bash or WSL)" }
else { Bad "bash not found (hooks and verify.sh need it)" "install Git for Windows (includes Git Bash): winget install --id Git.Git -e" }
if (Has jq) { Ok ("jq " + (jq --version)) }
else { Bad "jq not found (the guard hook needs it)" "winget install --id jqlang.jq -e" }

$engine = $null
if (Has docker) {
    docker info *> $null
    if ($LASTEXITCODE -eq 0) { $engine = 'docker'; Ok "docker engine running" }
    else { Bad "docker is installed but its engine is not running" "start Docker Desktop, or if you use Docker Engine in WSL2 run: wsl -d Ubuntu -u root service docker start" }
} elseif (Has podman) {
    podman info *> $null
    if ($LASTEXITCODE -eq 0) { $engine = 'podman'; Ok "podman running (use 'podman compose'; see README)" }
    else { Bad "podman is installed but not running" "podman machine start" }
} else { Bad "no container engine found" "install Docker Desktop (winget install --id Docker.DockerDesktop -e) or Docker Engine inside WSL2 (see README)" }
if ($engine -eq 'docker') {
    docker compose version *> $null
    if ($LASTEXITCODE -eq 0) { Ok "docker compose available" } else { Bad "docker compose plugin not found" "install Docker Desktop or the compose plugin" }
}

if (Has claude) { Ok ("claude " + ((claude --version 2>$null) | Select-Object -First 1)) }
else { Bad "Claude Code CLI not found" "see https://code.claude.com/docs/en/setup" }

git rev-parse --git-dir *> $null
if ($LASTEXITCODE -eq 0) {
    $b = git branch --show-current
    if ($b -eq 'dev') { Ok "on branch dev" } else { Warn "on branch '$b', the orchestrator expects 'dev' (start-orchestrator switches for you)" }
    if (Test-Path .claude/settings.json) { Ok ".claude/settings.json present" } else { Bad ".claude/settings.json missing" "git pull origin dev" }
} else { Bad "not inside the foodplanner git repo" "cd into the cloned repo" }

Write-Host ""
if ($script:fail -eq 0) { Write-Host "Ready." -ForegroundColor Green } else { Write-Host "Fix the [FAIL] items above, then run this again." -ForegroundColor Red }
exit $script:fail
