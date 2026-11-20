#!/usr/bin/env bash
# Registers the app-wide Oda OAuth client for one environment and prints only its client_id (T-053).
# Usage: ./scripts/oda-register-client.sh <web-origin> [client-name]   e.g. http://localhost:3000
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
exec node scripts/oda-register-client.mjs "$@"
