#!/usr/bin/env bash
# Checks that this machine has what the orchestrator run and the local iteration need.
# Usage: ./scripts/doctor.sh        (exit 0 = ready, 1 = something to fix)
set -u
fail=0
os=$(uname -s)
ok()   { printf '  [ok]   %s\n' "$1"; }
bad()  { printf '  [FAIL] %s\n         fix: %s\n' "$1" "$2"; fail=1; }
warn() { printf '  [warn] %s\n' "$1"; }
major() { printf '%s' "${1%%.*}"; }

echo "foodplanner doctor ($os)"

# git
if command -v git >/dev/null 2>&1; then ok "git $(git --version | awk '{print $3}')"
else bad "git not found" "install Git (https://git-scm.com/downloads)"; fi

# .NET 10
if command -v dotnet >/dev/null 2>&1; then
  v=$(dotnet --version 2>/dev/null || echo 0)
  if [ "$(major "$v")" -ge 10 ] 2>/dev/null; then ok ".NET SDK $v"
  else bad ".NET SDK $v is older than 10" "install the .NET 10 SDK: macOS 'brew install --cask dotnet-sdk', Ubuntu 'sudo apt install dotnet-sdk-10.0', or https://dot.net/download"; fi
else bad ".NET SDK not found" "install the .NET 10 SDK: macOS 'brew install --cask dotnet-sdk', Ubuntu 'sudo apt install dotnet-sdk-10.0', or https://dot.net/download"; fi

# Node 22+
if command -v node >/dev/null 2>&1; then
  v=$(node --version | sed 's/^v//')
  if [ "$(major "$v")" -ge 22 ] 2>/dev/null; then ok "Node $v (npm $(npm --version 2>/dev/null || echo '?'))"
  else bad "Node $v is older than 22" "install Node 22 LTS: macOS 'brew install node@22', Linux via https://nodejs.org or nvm"; fi
else bad "Node not found" "install Node 22 LTS: macOS 'brew install node@22', Linux via https://nodejs.org or nvm"; fi

# jq (needed by the guard hook in .claude/hooks)
if command -v jq >/dev/null 2>&1; then ok "jq $(jq --version)"
else bad "jq not found (the guard hook needs it)" "macOS 'brew install jq', Ubuntu 'sudo apt install jq'"; fi

# container engine, running
engine=""
if command -v docker >/dev/null 2>&1; then
  if docker info >/dev/null 2>&1; then engine=docker; ok "docker engine running ($(docker --version | awk '{print $3}' | tr -d ,))"
  else bad "docker is installed but its engine is not running" "start Rancher Desktop (dockerd mode), Docker Desktop, or Colima ('colima start'), or on Linux/WSL 'sudo service docker start'"; fi
elif command -v podman >/dev/null 2>&1; then
  if podman info >/dev/null 2>&1; then engine=podman; ok "podman running (use 'podman compose'; see README)"
  else bad "podman is installed but not running" "on macOS/Windows run 'podman machine start'"; fi
else
  bad "no container engine found" "install Docker Engine/Docker Desktop, or Colima on macOS ('brew install colima docker docker-compose'), or Podman"
fi
if [ "$engine" = docker ]; then
  if docker compose version >/dev/null 2>&1; then ok "docker compose $(docker compose version --short 2>/dev/null)"
  else bad "docker compose plugin not found" "install the compose plugin: macOS 'brew install docker-compose', Ubuntu 'sudo apt install docker-compose-v2'"; fi
fi

# Claude Code
if command -v claude >/dev/null 2>&1; then ok "claude $(claude --version 2>/dev/null | head -1)"
else bad "Claude Code CLI not found" "install: https://code.claude.com/docs/en/setup"; fi

# repo state
if git rev-parse --git-dir >/dev/null 2>&1; then
  b=$(git branch --show-current)
  if [ "$b" = dev ]; then ok "on branch dev"; else warn "on branch '$b', the orchestrator expects 'dev' (start-orchestrator switches for you)"; fi
  [ -f .claude/settings.json ] && ok ".claude/settings.json present" || bad ".claude/settings.json missing" "git pull origin dev"
else bad "not inside the foodplanner git repo" "cd into the cloned repo"; fi

echo
if [ $fail -eq 0 ]; then echo "Ready."; else echo "Fix the [FAIL] items above, then run this again."; fi
exit $fail
