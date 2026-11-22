#!/usr/bin/env bash
# Scores the recipe-from-photo skill on a local example set (T-118). Calls the REAL Anthropic API and costs money: run by hand only,
# never from verify.sh or CI. Usage: ANTHROPIC_API_KEY=... ./scripts/recipe-photo-eval.sh --book kaptein-sabeltann --model claude-opus-5-5 [--effort medium] [--only 1,5] [--dry-run]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
exec node scripts/recipe-photo-eval.mjs "$@"
