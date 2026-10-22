#!/usr/bin/env bash
# PreToolUse hook for Bash (T-062). Enforces CLAUDE.md hard rules 7 and 8 on the full
# command text, so it also catches forms plain permission rules miss, such as
# `git -C . push` or `sh -c '...'`. It is a guardrail, not a security boundary: it cannot
# see through eval, base64, variables or scripts. Real enforcement is GitHub rulesets (T-064).
# Output: JSON deny/ask decision on stdout, or nothing to leave the normal permission flow.
set -uo pipefail

input=$(cat)
cmd=$(jq -r '.tool_input.command // empty' <<<"$input")
cwd=$(jq -r '.cwd // empty' <<<"$input")
[ -z "$cmd" ] && exit 0

decide() { # $1 = deny|ask, $2 = reason
  jq -n --arg d "$1" --arg r "$2" \
    '{hookSpecificOutput:{hookEventName:"PreToolUse",permissionDecision:$d,permissionDecisionReason:$r}}'
  exit 0
}

# Drop quotes and backslashes so `git 'push'` and `sh -c "git push ..."` read as plain text.
n=$(printf '%s' "$cmd" | tr -d "'\"\\\\" | tr '\n\t' '  ' | tr -s ' ')
has() { grep -Eq -- "$1" <<<"$n"; }

SP='[[:space:]]'
SPC='[:space:]'   # for use INSIDE a bracket expression
PROT="(^|${SP}|[:/+])(test|prod)(${SP}|[;&|)]|\$)|refs/heads/(test|prod)"
current=""
[ -n "$cwd" ] && current=$(git -C "$cwd" branch --show-current 2>/dev/null || true)

# --- git: protected branches (rule 8) ---------------------------------------------------
if has "(^|[;&|(${SPC}])git${SP}"; then
  # Only the text of the push itself is checked (up to the next ; & |), so a commit message
  # such as "add test" in `git commit -m ... && git push origin task/x` is not mistaken for a branch.
  seg=$(grep -oE "(^|${SP})push(${SP}[^;&|]*)?" <<<"$n" || true)
  if [ -n "$seg" ]; then
    hs() { grep -Eq -- "$1" <<<"$seg"; }
    hs "(^|${SP})(--force[^ ]*|-[a-zA-Z]*f[a-zA-Z]*)(${SP}|\$)|${SP}\\+[^ ]" \
      && decide deny "Force-push is not allowed for agents (ADR 0009)."
    hs "(^|${SP})--(delete|all|mirror)(${SP}|\$)|(^|${SP})-d(${SP}|\$)|${SP}:[^ ]" \
      && decide deny "Deleting remote branches or pushing all refs is not allowed for agents (ADR 0009)."
    hs "$PROT" \
      && decide deny "Agents never push to test or prod; the architect promotes dev to test to prod by PR (ADR 0009)."
    case "$current" in test|prod)
      decide deny "Current branch is $current: agents never push from test or prod (ADR 0009)." ;;
    esac
  fi
  case "$current" in test|prod)
    has "(^|${SP})(commit|merge|rebase|reset|cherry-pick|pull|revert|am)(${SP}|\$)" \
      && decide deny "Current branch is $current: agents never change test or prod (ADR 0009)." ;;
  esac
  has "(^|${SP})(checkout|switch)${SP}+(-[a-zA-Z]+${SP}+)*(test|prod)(${SP}|\$)" \
    && decide deny "Agents do not work on test or prod. Use a task branch from dev (ADR 0009)."
fi

# --- gh: no merging promotion PRs, no settings or secrets (rules 7 and 8) ----------------
if has "(^|[;&|(${SPC}])gh${SP}"; then
  has "gh${SP}+pr${SP}+merge" && decide deny "Agents do not merge PRs; the architect promotes branches (ADR 0009)."
  has "gh${SP}+(pr|release)${SP}.*(--base|-B)[= ]*(test|prod)" \
    && decide deny "Agents do not open PRs into test or prod (ADR 0009)."
  has "gh${SP}+(secret|variable|ruleset|repo${SP}+(delete|edit|rename|archive))" \
    && decide deny "Agents do not change GitHub secrets, variables, rulesets or repository settings (rule 7)."
  has "gh${SP}+api${SP}.*(-X|--method)[= ]*(PUT|PATCH|POST|DELETE)" \
    && decide deny "Agents do not write to the GitHub API directly (rule 7)."
  has "gh${SP}+workflow${SP}+run" \
    && decide ask "Triggering a workflow needs the architect's approval."
fi

# --- Azure and deploy tooling: nothing may target prod (rule 7) --------------------------
if has "(^|[;&|(${SPC}/])(az|azd|bicep|terraform|kubectl|helm|func)${SP}"; then
  has "(^|[^[:alnum:]])prod(uction)?([^[:alnum:]]|\$)" \
    && decide deny "Never configure or deploy production (rule 7)."
  has "(^|[;&|(${SPC}/])azd${SP}+(up|deploy|provision|down)" \
    && decide ask "azd deploys need the architect's approval; CD deploys dev (ADR 0006)."
  if has "(^|[;&|(${SPC}/])az${SP}" && has "${SP}(create|delete|update|set|deploy|apply|add|remove|purge|import|login)(${SP}|\$)"; then
    has "what-if|validate" || decide ask "Changing Azure resources needs the architect's approval; CD deploys dev (ADR 0006)."
  fi
fi
has "(^|[;&|(${SPC}])docker${SP}+(push|login)" \
  && decide ask "Pushing images or logging in to a registry needs the architect's approval."

# --- Secrets and key material (rule 7) ---------------------------------------------------
READERS='(cat|head|tail|less|more|bat|nl|tac|xxd|od|strings|base64|grep|rg|awk|sed|cp|mv|scp|curl|source)'
n2=$(sed -E 's/\.env\.(example|sample|template)//g' <<<"$n")
if grep -Eq "(^|[;&|(${SPC}])${READERS}${SP}" <<<"$n2" || grep -Eq "(^|[;&|(])${SP}*\\.${SP}+[^ ]" <<<"$n2"; then
  grep -Eq "(^|${SP}|[/=<>])\\.env(${SP}|\$|\\.[A-Za-z0-9_.-]+)" <<<"$n2" \
    && decide deny "Reading .env files is not allowed. Use .env.example for variable names (rule 7)."
  has "\\.(pem|pfx|p12|key)(${SP}|\$)|id_rsa|id_ed25519|\\.ssh/|\\.azure/|\\.git-credentials|\\.netrc" \
    && decide deny "Reading key material and credential stores is not allowed (rule 7)."
fi

exit 0
