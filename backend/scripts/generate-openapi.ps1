# Regenerates backend/openapi/zladovce-v1.json from the API code (build-time generation, no database needed).
# Run after changing endpoints or DTOs and commit the result; the frontend generates its models from this file.
#
#   pwsh backend/scripts/generate-openapi.ps1

$ErrorActionPreference = 'Stop'

$backend = Split-Path -Parent $PSScriptRoot
$project = Join-Path $backend 'src/Zladovce.Api/Zladovce.Api.csproj'
$output = Join-Path $backend 'openapi/zladovce-v1.json'

# The document generator starts the application's host builder; Development supplies a complete local configuration.
$previous = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
try {
    dotnet build $project --nologo -v quiet -p:GenerateOpenApiDocument=true
    if ($LASTEXITCODE -ne 0) { throw "OpenAPI generation failed (dotnet build exit code $LASTEXITCODE)." }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previous
}

if (-not (Test-Path $output)) { throw "Expected $output to be generated." }
Write-Host "OpenAPI document written to $output"
