#!/usr/bin/env bash
# Tests for guard-bash.sh. Run: .claude/hooks/test-guard-bash.sh
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
hook="$here/guard-bash.sh"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
git init -q -b dev "$tmp/dev" && git init -q -b test "$tmp/test"
fail=0

check() { # expected cwd command
  local want=$1 cwd=$2 cmd=$3 out got
  out=$(jq -n --arg c "$cmd" --arg d "$cwd" '{tool_name:"Bash",cwd:$d,tool_input:{command:$c}}' | "$hook")
  got=$(jq -r '.hookSpecificOutput.permissionDecision // "pass"' <<<"${out:-{\}}" 2>/dev/null || echo pass)
  if [ "$got" = "$want" ]; then printf 'ok    %-5s %s\n' "$want" "$cmd"
  else printf 'FAIL  want=%s got=%s  %s\n' "$want" "$got" "$cmd"; fail=1; fi
}
D="$tmp/dev"; T="$tmp/test"

# git: must be blocked
check deny $D 'git push origin prod'
check deny $D 'git push origin HEAD:test'
check deny $D 'git push origin dev:prod'
check deny $D 'git -C . push origin prod'
check deny $D "git 'push' origin test"
check deny $D "sh -c 'git push origin prod'"
check deny $D 'git push --force origin task/T-002-x'
check deny $D 'git push -f origin task/T-002-x'
check deny $D 'git push --force-with-lease origin dev'
check deny $D 'git push origin +task/T-002-x'
check deny $D 'git push origin --delete task/T-002-x'
check deny $D 'git push origin :task/T-002-x'
check deny $D 'git push --all origin'
check deny $D 'git checkout prod'
check deny $D 'git switch -c x && git switch test'
check deny $T 'git push'
check deny $T 'git commit -m x'
check deny $T 'git merge dev'
check deny $D 'echo hi && git push origin prod'
check deny $D 'cd /tmp; git push origin test'
check deny $D '(git push origin prod)'
check deny $D 'bash -c "git push --force origin dev"'
check deny $D 'echo x | gh pr merge 3'
check deny $D 'cd infra && az deployment group what-if -f main.bicep -p prod.bicepparam'
check deny $D 'true && cat .env'
check ask  $D 'cd infra && azd up'
check pass $D 'echo hi && git push origin task/T-002-x'
check deny $D 'git push origin prod;echo done'
check deny $D 'git commit -m x && git push origin prod'
check deny $D 'git commit -m "add test" && git push origin HEAD:test'
check deny $D 'git commit -m x; git push -f origin dev'
# git: must pass
check pass $D 'git commit -m "add test" && git push origin task/T-002-x'
check pass $D 'git commit -m "notes about prod" && git push -u origin task/T-063-branching'
check pass $D 'git push origin task/T-002-x && echo test prod'
check pass $D 'git push origin dev; git log origin/prod'
check pass $D 'git push origin task/T-002-solution-skeleton-and-verify-script'
check pass $D 'git push -u origin task/T-027-playwright-e2e-harness'
check pass $D 'git push origin dev'
check pass $D 'git push origin task/T-041-frontend-pwa-test-offline-shell'
check pass $D 'git commit -m "document prod and test branches"'
check pass $D 'git switch -c task/T-010-google-external-login origin/dev'
check pass $D 'git log --oneline origin/prod'
check pass $D 'git diff origin/test..origin/dev --stat'
check pass $D 'git fetch origin'
# gh
check deny $D 'gh pr merge 12'
check deny $D 'gh pr create --base prod --title x'
check deny $D 'gh secret set AZURE_KEY'
check deny $D 'gh api -X PUT repos/ptgc/foodplanner/branches/dev/protection'
check ask  $D 'gh workflow run deploy.yml'
check pass $D 'gh pr view 12'
check pass $D 'gh run list'
# azure and deploy tooling
check deny $D 'az deployment group what-if -g rg-foodplanner-prod -f infra/main.bicep'
check deny $D 'az containerapp update -n api-prod --image x'
check deny $D 'bicep build infra/main.bicep --parameters prod.bicepparam'
check ask  $D 'az deployment group create -g rg-foodplanner-dev -f infra/main.bicep'
check ask  $D 'azd up'
check ask  $D 'docker push foodplannerdevacr.azurecr.io/api:1'
check pass $D 'az deployment group what-if -g rg-foodplanner-dev -f infra/main.bicep -p infra/dev.bicepparam'
check pass $D 'az bicep build --file infra/main.bicep'
check pass $D 'bicep build infra/main.bicep'
check pass $D 'docker compose up -d'
check pass $D 'docker build -t foodplanner-api src/Api'
# secrets
check deny $D 'cat .env'
check deny $D 'cat web/.env.local'
check deny $D 'grep KEY ./.env'
check deny $D 'source .env'
check deny $D 'cat cert.pfx'
check deny $D 'cat ~/.ssh/id_ed25519'
check deny $D 'cat ~/.azure/accessTokens.json'
check pass $D 'cat .env.example'
check pass $D 'cp .env.example .env.example.bak'
check pass $D 'docker compose --env-file .env.example up'
# normal work
check pass $D './scripts/verify.sh'
check pass $D 'dotnet test tests/Domain.Tests'
check pass $D 'npm run lint && npm test'
check pass $D 'ls -la'

[ $fail -eq 0 ] && echo "ALL PASSED" || { echo "SOME FAILED"; exit 1; }
