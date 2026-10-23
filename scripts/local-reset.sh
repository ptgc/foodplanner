#!/usr/bin/env bash
# Stops the containers and DELETES the SQL data volume (all local data). Next local-up starts empty.
# Keeps the user secrets, so the password stays the same. To rotate it see the README.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"
compose down --volumes
echo "Stopped and deleted the SQL volume."
