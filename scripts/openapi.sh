#!/usr/bin/env bash
# Regenerate the committed OpenAPI document (openapi/openapi.v1.json) from the Api project.
# Run after changing endpoints, then commit the result. verify.sh fails if it is stale.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

OPENAPI_UPDATE=1 dotnet test --project tests/Api.Tests --filter-class Foodplanner.Api.Tests.OpenApiDocumentTests
