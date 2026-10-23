#!/usr/bin/env bash
# Stops and removes the containers. The SQL data volume is kept (use local-reset to delete it).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"
compose down
echo "Stopped. The SQL volume is kept. Use ./scripts/local-reset.sh to delete it."
