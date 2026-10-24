#!/usr/bin/env bash
# Stops the containers and DELETES the SQL and Azurite data volumes (all local data, including uploaded images). Next local-up starts empty.
# Keeps the user secrets, so the password stays the same. To rotate it see the README.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_local-env.sh"
compose down --volumes
echo "Stopped and deleted the SQL and Azurite (blob) volumes."
