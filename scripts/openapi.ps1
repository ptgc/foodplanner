# Regenerate the committed OpenAPI document (openapi/openapi.v1.json) from the Api project.
# Run after changing endpoints, then commit the result. verify.sh fails if it is stale.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$env:OPENAPI_UPDATE = '1'
try {
    dotnet test --project tests/Api.Tests --filter-class Foodplanner.Api.Tests.OpenApiDocumentTests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Remove-Item Env:OPENAPI_UPDATE -ErrorAction SilentlyContinue
}
