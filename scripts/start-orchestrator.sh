#!/usr/bin/env bash
# Starts the orchestrator in Claude Code for local iteration L1.
# Usage: ./scripts/start-orchestrator.sh
set -euo pipefail
cd "$(dirname "$0")/.."

./scripts/doctor.sh

git fetch origin dev
git switch dev
git pull --ff-only origin dev

exec claude --agent orchestrator "$(cat docs/prompts/local-iteration-1.md)"
